// ============================================================================
// 文件：AppPaths.cs
// 作用：集中管理程序使用到的所有路径，避免各模块散落硬编码。
//       数据目录：%LOCALAPPDATA%\TripTestBench（每用户独立、可写，无需管理员权限）
//       · records.json —— 试验历史记录
//       · users.json   —— 登录账户
//       · logs\        —— 按天切分的运行日志
//       配置文件：程序目录下 appsettings.json（便于现场直接修改）
// ============================================================================

using System.IO; // Path / Directory

namespace TripTestBench.Services;

/// <summary>应用路径常量。</summary>
public static class AppPaths
{
    /// <summary>数据根目录：%LOCALAPPDATA%\TripTestBench。</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TripTestBench");

    /// <summary>旧的 JSON 记录文件（仅用于首次启动时迁移到 SQLite）。</summary>
    public static string RecordsFile => Path.Combine(DataDir, "records.json");

    /// <summary>
    /// SQLite 试验数据库文件：试验记录 + 曲线截图。
    /// 与 exe 同目录（publish 目录），现场备份数据更直观。
    /// </summary>
    public static string DatabaseFile => Path.Combine(AppContext.BaseDirectory, "triptestbench.db");

    /// <summary>旧版数据库文件位置：%LOCALAPPDATA%\TripTestBench\triptestbench.db（仅用于迁移）。</summary>
    public static string LegacyDatabaseFile => Path.Combine(DataDir, "triptestbench.db");

    /// <summary>登录账户文件。</summary>
    public static string UsersFile => Path.Combine(DataDir, "users.json");

    /// <summary>
    /// 日志目录：软件当前目录下的 log 文件夹。
    /// 现场排障时可直接在程序目录里找到日志，无需再去 AppData 翻找。
    /// </summary>
    public static string LogDir => Path.Combine(AppContext.BaseDirectory, "log");

    /// <summary>
    /// 日志回退目录：程序装在 Program Files 等受保护目录、当前目录不可写时使用。
    /// </summary>
    public static string FallbackLogDir => Path.Combine(DataDir, "log");

    /// <summary>配置文件：与 exe 同目录，方便现场用记事本修改。</summary>
    public static string ConfigFile => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>电流补偿区间配置文件：与 exe 同目录（现场可直接增删区间组）。</summary>
    public static string OffsetConfigFile => Path.Combine(AppContext.BaseDirectory, "offset.json");

    /// <summary>导出目录：优先桌面；没有桌面目录（如服务账户）时退回数据目录。</summary>
    public static string ExportDir
    {
        get
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return string.IsNullOrWhiteSpace(desktop) ? DataDir : desktop;
        }
    }

    /// <summary>确保数据目录存在（首次运行时创建）。</summary>
    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}
