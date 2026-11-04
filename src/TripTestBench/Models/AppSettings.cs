// ============================================================================
// 文件：AppSettings.cs
// 作用：定义应用配置、登录账户与曲线采样点三个基础数据结构。
//       · AppSettings —— 对应 appsettings.json（语言、PLC 通讯参数）；
//       · UserAccount —— 登录账户（密码以盐值 + SHA256 存储，不保存明文）；
//       · ChartSample —— 曲线上的一个点（时间 + 电流值）。
// ============================================================================

namespace TripTestBench.Models;

/// <summary>应用配置：随程序目录部署的 appsettings.json。</summary>
public sealed class AppSettings
{
    /// <summary>界面语言：zh-CN / en-US。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>PLC 通讯参数（IP / 端口 / 轮询周期）。</summary>
    public PlcOptions Plc { get; set; } = new();

    /// <summary>日志配置（级别 / 是否记录协议帧 / 保留天数）。</summary>
    public LogOptions Log { get; set; } = new();

    /// <summary>深拷贝：避免运行时修改影响已保存的配置。</summary>
    public AppSettings Clone() => new()
    {
        Language = Language,
        Plc = Plc.Clone(),
        Log = Log.Clone()
    };
}

/// <summary>日志配置：对应 appsettings.json 的 log 节点。</summary>
public sealed class LogOptions
{
    /// <summary>最低日志级别：Debug / Info / Warn / Error。</summary>
    public string MinLevel { get; set; } = "Info";

    /// <summary>
    /// 是否记录 PLC 协议帧（十六进制）。
    /// 默认关闭：轮询周期 200ms、每帧 3 次请求，开启后日志量很大，仅在排查协议问题时打开。
    /// </summary>
    public bool PlcFrames { get; set; }

    /// <summary>日志保留天数：启动时自动删除更早的文件（0 表示不清理）。</summary>
    public int RetainDays { get; set; } = 30;

    /// <summary>深拷贝。</summary>
    public LogOptions Clone() => new()
    {
        MinLevel = MinLevel,
        PlcFrames = PlcFrames,
        RetainDays = RetainDays
    };
}

/// <summary>登录账户信息（保存在用户目录 users.json）。</summary>
public sealed class UserAccount
{
    /// <summary>登录用户名。</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>显示名称（界面展示用，可留空）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>角色：Admin / Operator（预留权限控制）。</summary>
    public string Role { get; set; } = "Operator";

    /// <summary>密码盐值（随机字符串，防彩虹表）。</summary>
    public string Salt { get; set; } = string.Empty;

    /// <summary>密码哈希（SHA256(盐值 + 明文)），不保存明文密码。</summary>
    public string PasswordHash { get; set; } = string.Empty;
}

/// <summary>曲线采样点：Time 为时间轴秒数，Value 为电流值（kA）。</summary>
public readonly record struct ChartSample(double Time, double Value);
