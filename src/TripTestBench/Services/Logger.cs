// ============================================================================
// 文件：Logger.cs
// 作用：轻量级文件日志（现场排障的主要依据）。
//       · 目录：软件当前目录下的 log\（不可写时自动回退到 %LOCALAPPDATA%\TripTestBench\log）
//       · 切分：按天分文件 app-yyyyMMdd.log
//       · 分类：USER（用户操作）/ PLC（下发指令）/ TX·RX（协议帧，可开关）/
//               INFO / WARN / ERROR / SESSION（启动环境快照）
//       · 级别：由 appsettings.json 的 log.minLevel 控制（默认 Info）
//       日志本身失败绝不影响业务逻辑（全部 try/catch 吞掉）。
// ============================================================================

using System.IO;      // 目录 / 文件追加
using System.Text;    // Encoding.UTF8 / StringBuilder

namespace TripTestBench.Services;

/// <summary>日志级别：数值越大越严重。</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

/// <summary>线程安全的简易日志记录器。</summary>
public static class Logger
{
    private static readonly object SyncRoot = new(); // 写日志锁：多线程安全
    private static string? _directory;               // 实际使用的日志目录（首次写入时确定）

    /// <summary>实际生效的日志目录。</summary>
    public static string LogDirectory
    {
        get
        {
            lock (SyncRoot)
            {
                return _directory ??= ResolveDirectory();
            }
        }
    }

    /// <summary>当前的日志文件完整路径（按天命名）。</summary>
    public static string CurrentLogFile => Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>是否记录 PLC 协议帧（appsettings.json → log.plcFrames，默认关闭）。</summary>
    public static bool PlcFramesEnabled => AppConfig.Instance.Settings.Log?.PlcFrames == true;

    /// <summary>最低输出级别（appsettings.json → log.minLevel）。</summary>
    private static LogLevel MinLevel
    {
        get
        {
            var text = AppConfig.Instance.Settings.Log?.MinLevel;
            return Enum.TryParse<LogLevel>(text, ignoreCase: true, out var level) ? level : LogLevel.Info;
        }
    }

    // ---------------------------------------------------------------------
    // 分级写入
    // ---------------------------------------------------------------------

    /// <summary>调试细节（协议帧、寄存器原始值等，默认不输出）。</summary>
    public static void Debug(string message) => Write(LogLevel.Debug, "DEBUG", message);

    /// <summary>一般信息（连接、流程节点、结果）。</summary>
    public static void Info(string message) => Write(LogLevel.Info, "INFO", message);

    /// <summary>警告（通讯重连、信号异常等，不影响运行）。</summary>
    public static void Warn(string message) => Write(LogLevel.Warn, "WARN", message);

    /// <summary>错误（异常堆栈一并写入，便于定位）。</summary>
    public static void Error(string message, Exception? exception = null)
        => Write(
            LogLevel.Error,
            "ERROR",
            exception is null
                ? message
                : $"{message} :: {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");

    /// <summary>用户操作轨迹（点击了什么按钮、改了什么参数）。</summary>
    public static void User(string message) => Write(LogLevel.Info, "USER", message);

    /// <summary>向 PLC 下发的指令（含地址、数值、耗时、结果）。</summary>
    public static void Plc(string message) => Write(LogLevel.Info, "PLC", message);

    /// <summary>PLC 协议帧（十六进制），仅当 log.plcFrames = true 时输出。</summary>
    public static void PlcFrame(bool outgoing, ReadOnlySpan<byte> frame)
    {
        if (!PlcFramesEnabled)
        {
            return;
        }

        Write(LogLevel.Debug, outgoing ? "TX" : "RX", Hex(frame));
    }

    // ---------------------------------------------------------------------
    // 会话与环境
    // ---------------------------------------------------------------------

    /// <summary>
    /// 启动时写一段分隔线 + 运行环境快照，并清理过期日志。
    /// 现场把日志发回来时，这段信息能直接定位软件版本、系统与配置。
    /// </summary>
    /// <param name="version">程序版本号</param>
    public static void BeginSession(string? version)
    {
        try
        {
            var settings = AppConfig.Instance.Settings;
            var plc = settings.Plc ?? new Models.PlcOptions();

            var builder = new StringBuilder();
            builder.AppendLine(new string('=', 78));
            builder.AppendLine($"启动会话 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"  程序版本  {version ?? "未知"}");
            builder.AppendLine($"  操作系统  {Environment.OSVersion.VersionString} / {(Environment.Is64BitOperatingSystem ? "64 位" : "32 位")}");
            builder.AppendLine($"  运行框架  .NET {Environment.Version}");
            builder.AppendLine($"  计算机名  {Environment.MachineName} / 用户 {Environment.UserName}");
            builder.AppendLine($"  程序目录  {AppContext.BaseDirectory}");
            builder.AppendLine($"  日志文件  {CurrentLogFile}");
            builder.AppendLine($"  界面语言  {settings.Language}");
            builder.AppendLine($"  PLC 参数  {plc.Ip}:{plc.Port}（Modbus TCP，从站号 {plc.UnitId}），轮询 {plc.PollIntervalMs} ms");
            builder.AppendLine($"  日志级别  {MinLevel}{(PlcFramesEnabled ? "（含协议帧）" : string.Empty)}");
            builder.AppendLine(new string('=', 78));

            Write(LogLevel.Info, "SESSION", builder.ToString().TrimEnd());
            CleanupOldLogs(settings.Log?.RetainDays ?? 30);
        }
        catch
        {
            // 环境信息写失败不影响启动
        }
    }

    /// <summary>删除超过保留天数的历史日志（默认 30 天），避免磁盘被占满。</summary>
    private static void CleanupOldLogs(int retainDays)
    {
        try
        {
            if (retainDays <= 0)
            {
                return;
            }

            var deadline = DateTime.Now.AddDays(-retainDays);
            foreach (var file in Directory.GetFiles(LogDirectory, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < deadline)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败忽略（不影响写日志）
        }
    }

    // ---------------------------------------------------------------------
    // 内部实现
    // ---------------------------------------------------------------------

    /// <summary>字节数组转十六进制字符串（"50 00 FF 03 ..."）。</summary>
    public static string Hex(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return "(空)";
        }

        var builder = new StringBuilder(data.Length * 3);
        foreach (var b in data)
        {
            builder.Append(b.ToString("X2")).Append(' ');
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>实际写文件：自动创建目录，按天分文件，UTF-8 追加。</summary>
    private static void Write(LogLevel level, string category, string message)
    {
        try
        {
            if (level < MinLevel)
            {
                return;
            }

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category,-5}] {message}{Environment.NewLine}";

            lock (SyncRoot)
            {
                var dir = _directory ??= ResolveDirectory();
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log"),
                    line,
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不应影响业务（例如磁盘满、权限不足）
        }
    }

    /// <summary>
    /// 选择日志目录：优先“软件当前目录\log”；
    /// 安装在 Program Files 等无写权限的位置时回退到用户数据目录。
    /// </summary>
    private static string ResolveDirectory()
    {
        if (TryPrepare(AppPaths.LogDir))
        {
            return AppPaths.LogDir;
        }

        TryPrepare(AppPaths.FallbackLogDir);
        return AppPaths.FallbackLogDir;
    }

    /// <summary>目录是否可创建并且可写（创建探针文件后立即删除）。</summary>
    private static bool TryPrepare(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var probe = Path.Combine(directory, ".writable");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
