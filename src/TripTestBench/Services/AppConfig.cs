// ============================================================================
// 文件：AppConfig.cs
// 作用：读写程序目录下的 appsettings.json（语言 + PLC 通讯参数）。
//       工控机现场可以直接用记事本修改该文件，无需重新编译。
//       配置读取失败时自动回退默认值，保证软件始终能启动。
// ============================================================================

using System.IO;                            // 文件读写
using System.Text.Encodings.Web;            // 中文不转义（配置文件可读性）
using System.Text.Json;                     // JSON 序列化
using TripTestBench.Models;                 // AppSettings

namespace TripTestBench.Services;

/// <summary>应用配置单例：负责 appsettings.json 的加载与保存。</summary>
public sealed class AppConfig
{
    /// <summary>全局唯一实例（界面各处直接访问）。</summary>
    public static AppConfig Instance { get; } = new();

    /// <summary>JSON 序列化选项：缩进、忽略大小写、属性名小驼峰、中文不转义。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _sync = new(); // 写文件锁：防止并发保存损坏文件

    /// <summary>当前生效的配置。</summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>
    /// 加载配置：文件不存在时创建默认配置并落盘；解析异常时回退默认值。
    /// </summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFile))
            {
                Settings = new AppSettings();
                Save();
                return;
            }

            var json = File.ReadAllText(AppPaths.ConfigFile);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Logger.Error("加载配置失败，使用默认配置", ex);
            Settings = new AppSettings();
        }
    }

    /// <summary>保存配置到程序目录（失败只记录日志，不影响运行）。</summary>
    public void Save()
    {
        try
        {
            lock (_sync)
            {
                File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(Settings, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            Logger.Error("保存配置失败", ex);
        }
    }
}
