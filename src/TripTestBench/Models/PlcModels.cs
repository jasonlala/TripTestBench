// ============================================================================
// 文件：PlcModels.cs
// 作用：PLC 相关的数据模型与点位常量。
//       · PlcSnapshot —— 一次轮询得到的实时数据帧（电流 / 时间 / 各类信号）；
//       · TestState    —— 试验流程状态枚举（驱动界面状态文字与按钮可用性）；
//       · PlcOptions   —— 通讯参数（IP / 端口 / 轮询周期）；
//       · PlcPoints    —— 现场点位表（D / M / X 区地址与比例系数）。
// 说明：点位表来自现场《PLC相关.xls》，修改地址只需要改本文件。
// ============================================================================

namespace TripTestBench.Models;

/// <summary>PLC 轮询得到的一帧实时数据。</summary>
public sealed record PlcSnapshot
{
    /// <summary>该帧数据是否有效（false 表示通讯中断）。</summary>
    public bool Connected { get; init; }

    /// <summary>实时电流 (kA)，对应 D1520 / 1000。</summary>
    public double CurrentKa { get; init; }

    /// <summary>运行时间 (s)，对应 D1600 / 1000。</summary>
    public double RunTimeSeconds { get; init; }

    /// <summary>峰值电流 (kA)：软件侧对实时电流的最大值统计。</summary>
    public double PeakCurrentKa { get; init; }

    /// <summary>完成信号 M80：PLC 判定本次试验已结束。</summary>
    public bool Completed { get; init; }

    /// <summary>充电完成 X3：电容充电到位，允许开始测试。</summary>
    public bool ChargeComplete { get; init; }

    /// <summary>告警 X4~X7（常闭触点）：任一为 OFF 即视为告警。</summary>
    public bool Alarm { get; init; }

    /// <summary>
    /// 急停（常闭触点）：X0 / X1 任一为 OFF 即表示急停按下，立即中止试验。
    /// </summary>
    public bool Emergency { get; init; }

    /// <summary>工作标志 M0：主回路是否处于输出状态。</summary>
    public bool Working { get; init; }

    /// <summary>该帧的采集时间。</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

/// <summary>试验流程状态：界面状态文字与按钮可用性都由它驱动。</summary>
public enum TestState
{
    /// <summary>待机：可充电、可查看历史。</summary>
    Idle,

    /// <summary>充电中：主回路已合闸，等待充电完成信号 X3。</summary>
    Charging,

    /// <summary>就绪：充电完成，可开始测试。</summary>
    Ready,

    /// <summary>测试中：已下发启动命令，正在 1 秒采样并倒计时。</summary>
    Testing,

    /// <summary>完成：倒计时结束或收到 M80，已生成试验记录。</summary>
    Completed,

    /// <summary>已停止：操作员手动停止或超时保护触发。</summary>
    Stopped,

    /// <summary>告警：PLC 告警信号有效（X4~X7 任一 OFF），已停止输出并锁定操作。</summary>
    Alarm,

    /// <summary>急停：安全回路动作（X0 / X1 任一断开），立即断流并锁定操作。</summary>
    Emergency
}

/// <summary>PLC 通讯参数（可写入 appsettings.json，现场可改）。</summary>
public sealed class PlcOptions
{
    /// <summary>PLC IP 地址（默认 192.168.1.88）。</summary>
    public string Ip { get; set; } = "192.168.1.88";

    /// <summary>Modbus TCP 端口（标准 502）。</summary>
    public int Port { get; set; } = 502;

    /// <summary>Modbus 从站单元号（EASY320 默认 1）。</summary>
    public byte UnitId { get; set; } = 1;

    /// <summary>轮询周期（毫秒）：决定实时电流数字刷新速度，默认 200ms。</summary>
    public int PollIntervalMs { get; set; } = 200;

    /// <summary>深拷贝：避免运行中修改配置对象影响其他逻辑。</summary>
    public PlcOptions Clone() => new()
    {
        Ip = Ip,
        Port = Port,
        UnitId = UnitId,
        PollIntervalMs = PollIntervalMs
    };
}

/// <summary>
/// Modbus 地址表（依据现场点位表；地址编号与 PLC 软元件编号一致，不做 0/1 基换算）。
/// </summary>
public static class PlcPoints
{
    // ---------------- 保持寄存器（D 区：0x03 读 / 0x06、0x10 写） ----------------
    /// <summary>实时电流 D1520（32 位 int，占 D1520~D1521，原始值 A，÷1000 = kA）。</summary>
    public const int D_RealtimeCurrent = 1520;

    /// <summary>运行时间 D1600（32 位 int，占 D1600~D1601，原始值 ms，÷1000 = s）。</summary>
    public const int D_RunTime = 1600;

    /// <summary>工位 1 设定时间 D1000（32 位 int，占 D1000~D1001，写入值 = 秒 ×1000）。</summary>
    public const int D_SetTime1 = 1000;

    /// <summary>工位 1 设定电流 D1002（32 位 int，占 D1002~D1003，写入值 = kA ×1000）。</summary>
    public const int D_SetCurrent1 = 1002;

    /// <summary>工位 2 设定时间 D1006（32 位 int，占 D1006~D1007）。</summary>
    public const int D_SetTime2 = 1006;

    /// <summary>工位 2 设定电流 D1008（32 位 int，占 D1008~D1009）。</summary>
    public const int D_SetCurrent2 = 1008;

    /// <summary>工位 3 设定时间 D1012（32 位 int，占 D1012~D1013）。</summary>
    public const int D_SetTime3 = 1012;

    /// <summary>工位 3 设定电流 D1014（32 位 int，占 D1014~D1015）。</summary>
    public const int D_SetCurrent3 = 1014;

    // ---------------- 线圈（M 区：0x01 读 / 0x05、0x0F 写） ----------------
    /// <summary>主回路控制 M0：ON 启动 / OFF 停止。</summary>
    public const int M_MainCircuit = 0;

    /// <summary>开始测试 M1：电平模式，ON 启动 / OFF 撤销。</summary>
    public const int M_StartTest = 1;

    /// <summary>停止测试 M3：电平模式，ON 停止 / OFF 撤销。</summary>
    public const int M_StopTest = 3;

    /// <summary>完成信号 M80：PLC 判定本次试验完成。</summary>
    public const int M_Completed = 80;

    // ---------------- 离散输入（X 区：0x02 只读，Modbus 起始地址 63488） ----------------
    /// <summary>X 区 Modbus 起始地址：X0 → 63488，即离散输入地址 = 63488 + X 编号。</summary>
    public const int X_BaseAddress = 63488;

    /// <summary>急停输入 X0（常闭触点）：ON 正常 / OFF 急停按下，按下时立即中止试验并锁定操作。</summary>
    public const int X_Emergency = 0;

    /// <summary>急停输入 X1（常闭触点）：与 X0 并联，任一 OFF 即视为急停。</summary>
    public const int X_Emergency2 = 1;

    /// <summary>告警输入起始 X4（常闭触点）：X4 ~ X7 共 4 点，任一 OFF 即视为告警。</summary>
    public const int X_AlarmStart = 4;

    /// <summary>告警输入点数：X4 ~ X7。</summary>
    public const int X_AlarmCount = 4;

    /// <summary>充电完成 X3（即第 3 点）。</summary>
    public const int X_ChargeComplete = 3;

    // ---------------- 换算比例 ----------------
    /// <summary>电流比例：寄存器值 ÷1000 = kA。</summary>
    public const double CurrentScale = 1000d;

    /// <summary>时间比例：寄存器值 ÷1000 = 秒。</summary>
    public const double TimeScale = 1000d;

    /// <summary>按工位取“设定时间 / 设定电流”两个寄存器地址。</summary>
    public static (int Time, int Current) GetSettingRegisters(int station) => station switch
    {
        2 => (D_SetTime2, D_SetCurrent2),
        3 => (D_SetTime3, D_SetCurrent3),
        _ => (D_SetTime1, D_SetCurrent1)
    };
}
