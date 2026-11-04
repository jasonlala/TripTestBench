// ============================================================================
// 文件：ModbusTcpClient.cs
// 作用：Modbus TCP 主站（客户端）实现，面向汇川 EASY320。
//       只封装项目用到的功能码：
//         0x01 读线圈           —— M 区（主回路 / 启动 / 停止 / 完成标志）
//         0x02 读离散输入       —— X 区（急停 / 告警 / 充电完成）
//         0x03 读保持寄存器     —— D 区（实时电流 / 脱扣电流 / 运行时间）
//         0x05 写单个线圈       —— 单点控制（M0 / M1 / M3 等）
//         0x06 写单个寄存器     —— 单个设定值
//         0x0F 写多个线圈
//         0x10 写多个寄存器
// 帧格式（MBAP 头 7 字节 + PDU）：
//   事务号(2B) | 协议号(2B，固定 0) | 长度(2B，= 单元号 + PDU 长度) | 单元号(1B) | 功能码(1B) | 数据
// 地址约定（沿用现场点位表，不做 0/1 基换算）：
//   · 保持寄存器地址 = D 编号（D1520 → 地址 1520）
//   · 线圈地址       = M 编号（M0 → 地址 0）
//   · 离散输入地址   = X 编号（X10 → 地址 16）
// 说明：所有读写共用一把锁串行执行，保证一问一答不交叉（Modbus 本身是半双工请求应答）。
// ============================================================================

using System.IO;                 // IOException：连接断开异常
using System.Net.Sockets;        // TcpClient：TCP 连接

namespace TripTestBench.Services.Plc;

/// <summary>
/// Modbus TCP 客户端：负责组帧、收发、校验事务号与异常码。
/// </summary>
public sealed class ModbusTcpClient : IDisposable
{
    private readonly TcpClient _tcp;          // TCP 连接对象
    private readonly NetworkStream _stream;   // 网络数据流
    private readonly object _sync = new();    // 收发锁：一问一答不交叉
    private ushort _transactionId;            // 事务号：每次请求自增，用于匹配响应
    private bool _disposed;                   // 是否已释放

    /// <summary>Modbus 从站单元号（EASY320 默认 1）。</summary>
    public byte UnitId { get; }

    /// <summary>
    /// 建立 TCP 连接。
    /// </summary>
    /// <param name="ip">PLC 的 IP 地址</param>
    /// <param name="port">Modbus TCP 端口（默认 502）</param>
    /// <param name="unitId">从站单元号（默认 1）</param>
    /// <param name="timeoutMs">连接与读写超时（毫秒）</param>
    public ModbusTcpClient(string ip, int port, byte unitId = 1, int timeoutMs = 2000)
    {
        UnitId = unitId;

        // 关闭 Nagle 算法，减少小报文延迟（工控实时性优先）
        _tcp = new TcpClient
        {
            NoDelay = true,
            ReceiveTimeout = timeoutMs,
            SendTimeout = timeoutMs
        };

        // 异步连接 + 超时等待：避免 PLC 不在线时界面卡死
        var connectTask = _tcp.ConnectAsync(ip, port);
        if (!connectTask.Wait(timeoutMs))
        {
            _tcp.Dispose();

            // 超时放弃连接后，挂接观察续接：ConnectAsync 会在 Dispose 时以
            // “I/O 操作已中止”异常结束，若无人观察将成为 UnobservedTaskException，
            // 进程可能因此崩溃退出（现场“PLC 未连接就闪退”的根源）
            _ = connectTask.ContinueWith(
                t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted);

            throw new TimeoutException($"连接 {ip}:{port} 超时");
        }

        // 传递真实异常（拒绝连接 / 网络不可达等），便于日志定位
        connectTask.GetAwaiter().GetResult();

        _stream = _tcp.GetStream();
        _stream.ReadTimeout = timeoutMs;
        _stream.WriteTimeout = timeoutMs;
    }

    /// <summary>连接是否可用。</summary>
    public bool IsConnected => !_disposed && _tcp.Connected;

    // ---------------------------------------------------------------------
    // 读操作
    // ---------------------------------------------------------------------

    /// <summary>读保持寄存器（功能码 0x03）：D 区，最多 125 个。</summary>
    public ushort[] ReadHoldingRegisters(int startAddress, int count) => ReadRegisters(0x03, startAddress, count);

    /// <summary>读输入寄存器（功能码 0x04），最多 125 个。</summary>
    public ushort[] ReadInputRegisters(int startAddress, int count) => ReadRegisters(0x04, startAddress, count);

    /// <summary>读线圈（功能码 0x01）：M 区，最多 2000 点。</summary>
    public bool[] ReadCoils(int startAddress, int count) => ReadBits(0x01, startAddress, count);

    /// <summary>读离散输入（功能码 0x02）：X 区，最多 2000 点。</summary>
    public bool[] ReadDiscreteInputs(int startAddress, int count) => ReadBits(0x02, startAddress, count);

    // ---------------------------------------------------------------------
    // 写操作
    // ---------------------------------------------------------------------

    /// <summary>写单个线圈（功能码 0x05）：M0 主回路 / M1 开始 / M3 停止。</summary>
    public void WriteCoil(int address, bool value)
    {
        ValidateAddress(address);

        var request = new byte[5];
        request[0] = 0x05;
        request[1] = (byte)(address >> 8);
        request[2] = (byte)(address & 0xFF);
        request[3] = value ? (byte)0xFF : (byte)0x00;   // ON = 0xFF00，OFF = 0x0000
        request[4] = 0x00;

        var response = Execute(request);
        if (response.Length < 4)
        {
            throw new ModbusException($"写线圈响应长度异常（{response.Length + 1} 字节）");
        }

        // 回显地址必须与请求一致，防止响应错位（PDU 已去掉功能码，前 2 字节即地址）
        var echoAddress = (response[0] << 8) | response[1];
        if (echoAddress != address)
        {
            throw new ModbusException($"写线圈回显地址不一致：请求 {address}，响应 {echoAddress}");
        }
    }

    /// <summary>写单个寄存器（功能码 0x06）：设定值写入 D 区。</summary>
    public void WriteRegister(int address, ushort value)
    {
        ValidateAddress(address);

        var request = new byte[5];
        request[0] = 0x06;
        request[1] = (byte)(address >> 8);
        request[2] = (byte)(address & 0xFF);
        request[3] = (byte)(value >> 8);
        request[4] = (byte)(value & 0xFF);

        var response = Execute(request);
        if (response.Length < 4)
        {
            throw new ModbusException($"写寄存器响应长度异常（{response.Length + 1} 字节）");
        }

        var echoAddress = (response[0] << 8) | response[1];
        if (echoAddress != address)
        {
            throw new ModbusException($"写寄存器回显地址不一致：请求 {address}，响应 {echoAddress}");
        }
    }

    /// <summary>写多个线圈（功能码 0x0F）。</summary>
    public void WriteCoils(int startAddress, IReadOnlyList<bool> values)
    {
        if (values.Count is <= 0 or > 1968)
        {
            throw new ArgumentOutOfRangeException(nameof(values), "点数必须在 1 ~ 1968 之间");
        }

        // 只有 1 个点时用 0x05，兼容性更好（部分小型 PLC 不支持 0x0F）
        if (values.Count == 1)
        {
            WriteCoil(startAddress, values[0]);
            return;
        }

        ValidateAddress(startAddress);

        var byteCount = (values.Count + 7) / 8;
        var request = new byte[6 + byteCount];
        request[0] = 0x0F;
        request[1] = (byte)(startAddress >> 8);
        request[2] = (byte)(startAddress & 0xFF);
        request[3] = (byte)(values.Count >> 8);
        request[4] = (byte)(values.Count & 0xFF);
        request[5] = (byte)byteCount;

        // 位按低位在前打包
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i])
            {
                request[6 + i / 8] |= (byte)(1 << (i % 8));
            }
        }

        var response = Execute(request);
        if (response.Length < 4)
        {
            throw new ModbusException($"写多线圈响应长度异常（{response.Length + 1} 字节）");
        }
    }

    /// <summary>写多个寄存器（功能码 0x10）。</summary>
    public void WriteRegisters(int startAddress, IReadOnlyList<ushort> values)
    {
        if (values.Count is <= 0 or > 123)
        {
            throw new ArgumentOutOfRangeException(nameof(values), "点数必须在 1 ~ 123 之间");
        }

        // 只有 1 个点时用 0x06
        if (values.Count == 1)
        {
            WriteRegister(startAddress, values[0]);
            return;
        }

        ValidateAddress(startAddress);

        var request = new byte[6 + values.Count * 2];
        request[0] = 0x10;
        request[1] = (byte)(startAddress >> 8);
        request[2] = (byte)(startAddress & 0xFF);
        request[3] = (byte)(values.Count >> 8);
        request[4] = (byte)(values.Count & 0xFF);
        request[5] = (byte)(values.Count * 2);

        for (var i = 0; i < values.Count; i++)
        {
            request[6 + i * 2] = (byte)(values[i] >> 8);        // Modbus 为大端序
            request[7 + i * 2] = (byte)(values[i] & 0xFF);
        }

        var response = Execute(request);
        if (response.Length < 4)
        {
            throw new ModbusException($"写多寄存器响应长度异常（{response.Length + 1} 字节）");
        }
    }

    // ---------------------------------------------------------------------
    // 组帧与解析
    // ---------------------------------------------------------------------

    /// <summary>读寄存器公共实现（0x03 / 0x04）。</summary>
    private ushort[] ReadRegisters(byte function, int startAddress, int count)
    {
        ValidateCount(count, 125, "寄存器");
        ValidateAddress(startAddress);

        var request = new byte[5];
        request[0] = function;
        request[1] = (byte)(startAddress >> 8);
        request[2] = (byte)(startAddress & 0xFF);
        request[3] = (byte)(count >> 8);
        request[4] = (byte)(count & 0xFF);

        var data = Execute(request);
        if (data.Length < 1)
        {
            throw new ModbusException("响应数据为空");
        }

        var byteCount = data[0];
        if (byteCount != count * 2 || data.Length < 1 + byteCount)
        {
            throw new ModbusException($"寄存器数据长度异常：期望 {count * 2} 字节，实际 {byteCount} 字节");
        }

        var result = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = (ushort)((data[1 + i * 2] << 8) | data[2 + i * 2]);   // 大端序
        }

        return result;
    }

    /// <summary>读位公共实现（0x01 / 0x02）。</summary>
    private bool[] ReadBits(byte function, int startAddress, int count)
    {
        ValidateCount(count, 2000, "位");
        ValidateAddress(startAddress);

        var request = new byte[5];
        request[0] = function;
        request[1] = (byte)(startAddress >> 8);
        request[2] = (byte)(startAddress & 0xFF);
        request[3] = (byte)(count >> 8);
        request[4] = (byte)(count & 0xFF);

        var data = Execute(request);
        if (data.Length < 1)
        {
            throw new ModbusException("响应数据为空");
        }

        var byteCount = data[0];
        var expected = (count + 7) / 8;
        if (byteCount != expected || data.Length < 1 + byteCount)
        {
            throw new ModbusException($"位数据长度异常：期望 {expected} 字节，实际 {byteCount} 字节");
        }

        var result = new bool[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = (data[1 + i / 8] & (1 << (i % 8))) != 0;   // 低位在前
        }

        return result;
    }

    /// <summary>
    /// 发送请求 → 接收响应 → 校验事务号 / 异常码 → 返回 PDU 数据区（不含功能码）。
    /// </summary>
    private byte[] Execute(byte[] request)
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            // ---------- 1. 组装 MBAP 头 + PDU ----------
            var transactionId = ++_transactionId;
            var length = request.Length + 1;                 // 单元号 + PDU
            var frame = new byte[7 + request.Length];

            frame[0] = (byte)(transactionId >> 8);           // 事务号（高字节）
            frame[1] = (byte)(transactionId & 0xFF);         // 事务号（低字节）
            frame[2] = 0x00;                                 // 协议号固定 0
            frame[3] = 0x00;
            frame[4] = (byte)(length >> 8);                  // 长度（高字节）
            frame[5] = (byte)(length & 0xFF);                // 长度（低字节）
            frame[6] = UnitId;                               // 单元号
            Buffer.BlockCopy(request, 0, frame, 7, request.Length);

            // ---------- 2. 发送 ----------
            Logger.PlcFrame(outgoing: true, frame);
            _stream.Write(frame, 0, frame.Length);
            _stream.Flush();

            // ---------- 3. 读取 MBAP 头（7 字节） ----------
            var header = new byte[7];
            ReadExactly(header, 0, header.Length);

            var responseTransaction = (ushort)((header[0] << 8) | header[1]);
            if (responseTransaction != transactionId)
            {
                throw new ModbusException($"事务号不匹配：请求 {transactionId}，响应 {responseTransaction}");
            }

            var protocolId = (header[2] << 8) | header[3];
            if (protocolId != 0)
            {
                throw new ModbusException($"协议标识异常：{protocolId}（应为 0）");
            }

            var responseLength = (header[4] << 8) | header[5];
            if (responseLength is < 2 or > 260)
            {
                throw new ModbusException($"响应长度异常：{responseLength}");
            }

            // ---------- 4. 读取 PDU（长度含单元号，需减 1） ----------
            var body = new byte[responseLength - 1];
            ReadExactly(body, 0, body.Length);

            if (Logger.PlcFramesEnabled)
            {
                var response = new byte[header.Length + body.Length];
                Buffer.BlockCopy(header, 0, response, 0, header.Length);
                Buffer.BlockCopy(body, 0, response, header.Length, body.Length);
                Logger.PlcFrame(outgoing: false, response);
            }

            // ---------- 5. 异常码检查（功能码最高位置 1 表示异常响应） ----------
            var function = body[0];
            if ((function & 0x80) != 0)
            {
                var code = body.Length > 1 ? body[1] : (byte)0x00;
                throw new ModbusException($"PLC 返回异常码 0x{code:X2}{DescribeExceptionCode(code)}");
            }

            var data = new byte[body.Length - 1];
            Buffer.BlockCopy(body, 1, data, 0, data.Length);
            return data;
        }
    }

    /// <summary>把常用 Modbus 异常码翻译成中文，便于现场排障。</summary>
    private static string DescribeExceptionCode(byte code) => code switch
    {
        0x01 => "（非法功能码：PLC 不支持该功能）",
        0x02 => "（非法数据地址：地址或点数超出 PLC 范围）",
        0x03 => "（非法数据值：写入值超出允许范围）",
        0x04 => "（从站设备故障）",
        0x05 => "（确认：命令正在处理，需稍后重试）",
        0x06 => "（从站设备忙）",
        0x08 => "（存储奇偶校验错误）",
        0x0A => "（网关路径不可用）",
        0x0B => "（网关目标设备无响应）",
        _ => string.Empty
    };

    /// <summary>点数合法性校验。</summary>
    private static void ValidateCount(int count, int max, string what)
    {
        if (count <= 0 || count > max)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"{what}点数必须在 1 ~ {max} 之间");
        }
    }

    /// <summary>地址合法性校验（Modbus 地址为 0 ~ 65535）。</summary>
    private static void ValidateAddress(int address)
    {
        if (address is < 0 or > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(address), "Modbus 地址必须在 0 ~ 65535 之间");
        }
    }

    /// <summary>循环读取直到读满指定字节数（TCP 可能分包到达）。</summary>
    private void ReadExactly(byte[] buffer, int offset, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = _stream.Read(buffer, offset + read, count - read);
            if (n <= 0)
            {
                throw new IOException("PLC 连接已断开");
            }

            read += n;
        }
    }

    /// <summary>已释放则抛出异常，避免使用失效连接。</summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ModbusTcpClient));
        }
    }

    /// <summary>释放网络资源（关闭流与 TCP 连接）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // 关闭流失败可忽略（连接已断）
        }

        _tcp.Dispose();
    }
}

/// <summary>Modbus TCP 通讯异常（协议错误 / 异常码 / 未连接）。</summary>
public sealed class ModbusException : Exception
{
    public ModbusException(string message) : base(message)
    {
    }
}
