// ============================================================================
// 文件：ModbusPlcService.cs
// 作用：真实 PLC 的通讯服务（基汇川 EASY320，Modbus TCP 协议）。
//       1) 后台轮询线程按配置周期（默认 200ms）读取实时电流 / 脱扣电流 / 运行时间 / 位信号；
//       2) 把读到的数据封装成 PlcSnapshot，通过事件抛给界面层；
//       3) 提供主回路、开始、停止、设定值写入等控制指令；
//       4) 通讯异常时自动断开并在 2 秒后重连，界面指示灯变红闪烁。
// 地址映射（沿用现场点位表编号，不做换算）：
//   · D 区 → 保持寄存器 0x03（读）/ 0x06、0x10（写）
//   · M 区 → 线圈 0x01（读）/ 0x05、0x0F（写）
//   · X 区 → 离散输入 0x02（只读）
// 说明：界面曲线的 1 秒采样由 ViewModel 负责，本层只保证“数据尽可能新”。
// ============================================================================

using System.Diagnostics;        // Stopwatch：统计指令下发耗时
using System.IO;                 // IOException：未连接时的写入异常
using TripTestBench.Models;      // PlcOptions / PlcSnapshot / PlcPoints

namespace TripTestBench.Services.Plc;

/// <summary>
/// 真实 PLC 服务（Modbus TCP）：后台轮询 + 指令写入 + 断线自动重连。
/// </summary>
public sealed class ModbusPlcService : IPlcService
{
    // 读写锁：轮询读与指令写不能同时占用同一个 socket
    private readonly SemaphoreSlim _io = new(1, 1);

    private ModbusTcpClient? _client;               // Modbus TCP 客户端
    private CancellationTokenSource? _cts;          // 轮询取消令牌
    private Task? _pollTask;                        // 轮询后台任务
    private PlcOptions _options = new();            // 当前通讯参数
    private volatile bool _connected;               // 连接状态（跨线程可见）
    private double _peak;                           // 本次试验峰值电流（软件侧统计）

    public bool IsConnected => _connected;

    public PlcOptions Options => _options;

    /// <summary>每读到一帧数据触发一次（后台线程）。</summary>
    public event EventHandler<PlcSnapshot>? SnapshotReceived;

    /// <summary>连接状态变化时触发（后台线程）。</summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <summary>启动后台轮询（立即返回，不阻塞界面）。</summary>
    public Task StartAsync(PlcOptions options)
    {
        _options = options.Clone();
        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));

        Logger.Info($"PLC 服务启动：{_options.Ip}:{_options.Port}（Modbus TCP，从站号 {_options.UnitId}），"
                    + $"轮询周期 {_options.PollIntervalMs} ms，超时 2000 ms");
        return Task.CompletedTask;
    }

    /// <summary>停止轮询并关闭连接。</summary>
    public async Task StopAsync()
    {
        Logger.Info("PLC 服务停止：取消轮询并关闭连接");

        var cts = _cts;
        _cts = null;
        cts?.Cancel();

        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.ConfigureAwait(false);
            }
            catch
            {
                // 停止过程中的异常可忽略（连接已主动关闭）
            }

            _pollTask = null;
        }

        cts?.Dispose();
        CloseClient();
        SetConnected(false);
    }

    /// <summary>
    /// 轮询主循环：连接 → 读数据 → 抛事件 → 按周期休眠；异常则断开并 2 秒后重连。
    /// </summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 1) 确保连接可用（断线重连在这里完成）
                await EnsureClientAsync(ct).ConfigureAwait(false);

                // 2) 读取一帧数据
                var snapshot = await ReadSnapshotAsync(ct).ConfigureAwait(false);

                // 3) 通知界面：连接正常 + 最新数据
                SetConnected(true);
                SnapshotReceived?.Invoke(this, snapshot);

                // 4) 按配置周期休眠（默认 200ms，最低 50ms）
                await Task.Delay(Math.Max(50, _options.PollIntervalMs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常取消（退出程序 / 重新连接）
                break;
            }
            catch (Exception ex)
            {
                // 通讯异常：记录日志 → 标记断开（指示灯红色闪烁）→ 关闭连接 → 2 秒后重试
                Logger.Warn($"PLC 通讯异常：{ex.Message}");
                SetConnected(false);
                CloseClient();
                try
                {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>确保客户端已连接（双重检查 + 加锁，避免重复创建连接）。</summary>
    private async Task EnsureClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true })
        {
            return;
        }

        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 拿到锁后再判断一次，防止其他线程已经建好连接
            if (_client is { IsConnected: true })
            {
                return;
            }

            _client?.Dispose();

            // 建立 TCP 连接放到线程池执行，避免阻塞 UI 调度线程
            _client = await Task.Run(
                () => new ModbusTcpClient(_options.Ip, _options.Port, _options.UnitId, 2000), ct)
                .ConfigureAwait(false);

            Logger.Info($"已连接 PLC {_options.Ip}:{_options.Port}（从站号 {_options.UnitId}）");
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>
    /// 读取一帧完整数据：一次批量读 D 区 81 个寄存器 + M 区 81 个线圈 + X 区 17 个离散输入。
    /// 说明：批量读取可以显著降低通讯次数，提高实时性。
    /// </summary>
    private Task<PlcSnapshot> ReadSnapshotAsync(CancellationToken ct) => Task.Run(() =>
    {
        _io.Wait(ct);
        try
        {
            var client = _client ?? throw new ModbusException("PLC 未连接");

            // D1520 连续读取 82 个寄存器：实时电流 D1520(int32) / 脱扣电流 D1550 / 运行时间 D1600(int32)
            var words = client.ReadHoldingRegisters(PlcPoints.D_RealtimeCurrent, 82);

            // M0 ~ M80：主回路、开始、停止、完成等标志
            var m = client.ReadCoils(PlcPoints.M_MainCircuit, 81);

            // X0 ~ X16：急停、充电完成（X 区 Modbus 起始地址 63488）
            var x = client.ReadDiscreteInputs(PlcPoints.X_BaseAddress, 17);

            // 寄存器原始值 ÷ 1000 还原工程量（D1520 / D1600 为 32 位 int，合并高低字）
            var current = ModbusTcpClient.ToInt32(words[0], words[1]) / PlcPoints.CurrentScale;
            var runTime = ModbusTcpClient.ToInt32(
                words[PlcPoints.D_RunTime - PlcPoints.D_RealtimeCurrent],
                words[PlcPoints.D_RunTime - PlcPoints.D_RealtimeCurrent + 1]) / PlcPoints.TimeScale;

            // 软件侧同步统计峰值（即使 PLC 未提供峰值寄存器）
            if (current > _peak)
            {
                _peak = current;
            }

            // 组装数据帧
            return new PlcSnapshot
            {
                Connected = true,
                CurrentKa = current,
                RunTimeSeconds = runTime,
                PeakCurrentKa = _peak,
                Completed = m[PlcPoints.M_Completed],                     // M80 完成信号
                ChargeComplete = x[PlcPoints.X_ChargeComplete],           // X3 充电完成
                // X4 ~ X7 为常闭告警触点：任一 OFF 即视为告警
                Alarm = !(x[PlcPoints.X_AlarmStart] && x[PlcPoints.X_AlarmStart + 1]
                          && x[PlcPoints.X_AlarmStart + 2] && x[PlcPoints.X_AlarmStart + 3]),
                // X0 / X1 均为常闭急停触点：任一 OFF 即视为急停按下
                Emergency = !x[PlcPoints.X_Emergency] || !x[PlcPoints.X_Emergency2],
                Working = m[PlcPoints.M_MainCircuit],                     // M0 主回路
                Timestamp = DateTime.Now
            };
        }
        finally
        {
            _io.Release();
        }
    }, ct);

    /// <summary>
    /// 写入当前工位的设定电流与设定时间（界面值 × 1000 后写入 D 区）。
    /// 同时同步工位选择继电器 M60 / M61。
    /// </summary>
    public Task WriteTestSettingsAsync(int station, double currentKa, double timeSeconds)
    {
        var (timeRegister, currentRegister) = PlcPoints.GetSettingRegisters(station);
        var currentValue = ToRegisterValue(currentKa);   // int32（×1000，占 2 个寄存器）
        var timeValue = ToRegisterValue(timeSeconds);    // int32（×1000，占 2 个寄存器）

        var description =
            $"设定值 工位 {station}：电流 {currentKa:F2} kA → D{currentRegister}={currentValue}（int32）；"
            + $"时间 {timeSeconds:F2} s → D{timeRegister}={timeValue}（int32）";

        return WriteAsync(client =>
        {
            // 设定时间 / 电流均为 32 位 int：按高低字拆分后用 0x10 写入连续两个寄存器
            client.WriteRegisters(timeRegister, ModbusTcpClient.ToWords(timeValue));
            client.WriteRegisters(currentRegister, ModbusTcpClient.ToWords(currentValue));
        }, description);
    }

    /// <summary>主回路启动 / 停止：M0 = ON / OFF。</summary>
    public Task SetMainCircuitAsync(bool on) => WriteAsync(
        client => client.WriteCoil(PlcPoints.M_MainCircuit, on),
        $"主回路 M{PlcPoints.M_MainCircuit} = {(on ? "ON（开始充电）" : "OFF（断开主回路）")}");

    /// <summary>
    /// 开始测试电平（M1）：电平模式，ON 启动 / OFF 撤销，不自动复位。
    /// </summary>
    public Task SetStartTestAsync(bool on) => WriteAsync(
        client => client.WriteCoil(PlcPoints.M_StartTest, on),
        $"开始测试 M{PlcPoints.M_StartTest} = {OnOff(on)}（电平）");

    /// <summary>
    /// 停止测试电平（M3）：电平模式，ON 停止 / OFF 撤销，不自动复位。
    /// </summary>
    public Task SetStopTestAsync(bool on) => WriteAsync(
        client => client.WriteCoil(PlcPoints.M_StopTest, on),
        $"停止测试 M{PlcPoints.M_StopTest} = {OnOff(on)}（电平）");

    /// <summary>清零软件侧峰值统计（每次开始测试前调用）。</summary>
    public void ResetPeak() => _peak = 0;

    /// <summary>ON / OFF 文本，用于日志。</summary>
    private static string OnOff(bool value) => value ? "ON" : "OFF";

    /// <summary>
    /// 串行执行一次写操作（与轮询读共用一把锁），并把指令内容、耗时、结果写入日志。
    /// 现场只要看日志就能知道“什么时候向哪个地址写了什么值、是否成功”。
    /// </summary>
    private async Task WriteAsync(Action<ModbusTcpClient> action, string description)
    {
        if (!_connected)
        {
            Logger.Warn($"PLC 未连接，指令未下发：{description}");
            throw new IOException("PLC 未连接");
        }

        await _io.WaitAsync().ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        try
        {
            var client = _client ?? throw new ModbusException("PLC 未连接");
            await Task.Run(() => action(client)).ConfigureAwait(false);

            watch.Stop();
            Logger.Plc($"下发 {description} → 成功，耗时 {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            watch.Stop();
            Logger.Error($"下发 {description} → 失败，耗时 {watch.ElapsedMilliseconds} ms", ex);
            throw;
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>工程量 → 寄存器值：×1000 后四舍五入为 32 位 int（D1000 / D1002 为 int 类型，占 2 个寄存器）。</summary>
    private static int ToRegisterValue(double value)
        => (int)Math.Round(value * 1000d);

    /// <summary>更新连接状态并通知订阅者（仅在状态变化时触发）。</summary>
    private void SetConnected(bool connected)
    {
        if (_connected == connected)
        {
            return;
        }

        _connected = connected;

        if (!connected)
        {
            Logger.Warn($"PLC 连接中断：{_options.Ip}:{_options.Port}，将在 2 秒后自动重连");
        }

        ConnectionChanged?.Invoke(this, connected);
    }

    /// <summary>关闭并释放当前连接（加锁，避免与正在进行的读写冲突）。</summary>
    private void CloseClient()
    {
        try
        {
            _io.Wait(1000);
            try
            {
                _client?.Dispose();
                _client = null;
            }
            finally
            {
                _io.Release();
            }
        }
        catch
        {
            // 关闭连接失败可忽略（进程即将重连）
        }
    }

    /// <summary>
    /// 释放资源：取消轮询 → 等轮询任务退出 → 关闭连接 → 释放信号量。
    /// 必须先等轮询任务退出再释放信号量与连接，否则后台任务会在半路收到
    /// “I/O 操作已中止 / 信号量已释放”异常且无人观察，进程可能因此崩溃。
    /// </summary>
    public void Dispose()
    {
        _cts?.Cancel();

        // 有界等待轮询任务退出（最多 3 秒）；任务内的异常已在轮询循环中记录，此处吞掉重抛
        try
        {
            _pollTask?.Wait(3000);
        }
        catch
        {
            // 轮询循环自身已捕获并记录过异常，这里只保证不向外传播
        }

        CloseClient();
        _cts?.Dispose();
        _io.Dispose();
    }
}
