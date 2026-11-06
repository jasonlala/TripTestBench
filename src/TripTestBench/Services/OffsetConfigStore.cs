// ============================================================================
// 文件：OffsetConfigStore.cs
// 作用：读写程序目录下的 offset.json（电流补偿区间配置）。
//       文件不存在时自动创建默认示例；每次查询都重新读文件，
//       现场用记事本增删区间后无需重启软件，下一次开始测试即生效。
//       文件损坏 / 区间非法时回退“不补偿”（系数 1.0），保证试验可继续。
// ============================================================================

using System.IO;                            // File
using System.Text.Encodings.Web;            // 中文不转义
using System.Text.Json;                     // JSON 序列化
using TripTestBench.Models;                 // OffsetConfig

namespace TripTestBench.Services;

/// <summary>电流补偿配置存取单例。</summary>
public sealed class OffsetConfigStore
{
    /// <summary>全局唯一实例。</summary>
    public static OffsetConfigStore Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private OffsetConfigStore()
    {
    }

    /// <summary>加载配置：文件不存在时创建带示例区间的默认文件。</summary>
    public OffsetConfig LoadOrCreate()
    {
        try
        {
            if (!File.Exists(AppPaths.OffsetConfigFile))
            {
                var def = DefaultConfig();
                File.WriteAllText(AppPaths.OffsetConfigFile, JsonSerializer.Serialize(def, JsonOptions));
                Logger.Info($"已创建默认电流补偿配置 {AppPaths.OffsetConfigFile}");
                return def;
            }

            return JsonSerializer.Deserialize<OffsetConfig>(
                       File.ReadAllText(AppPaths.OffsetConfigFile), JsonOptions) ?? new OffsetConfig();
        }
        catch (Exception ex)
        {
            Logger.Error("加载电流补偿配置失败，本次按不补偿（系数 1.0）处理", ex);
            return new OffsetConfig();
        }
    }

    /// <summary>
    /// 查询设定电流命中的补偿系数：重新读取文件（现场改配置即时生效）。
    /// 匹配规则：MinKa ≤ 电流 &lt; MaxKa（左闭右开），命中第一组即返回。
    /// 未命中任何区间 / 配置异常时返回 1.0（不补偿）。
    /// </summary>
    public double GetFactor(double setKa)
    {
        var config = LoadOrCreate();
        foreach (var range in config.Ranges)
        {
            if (range.Factor <= 0)
            {
                continue; // 非法系数（≤0）忽略，防止把写入值清零
            }

            if (setKa >= range.MinKa && setKa < range.MaxKa)
            {
                return range.Factor;
            }
        }

        return 1.0;
    }

    /// <summary>默认示例配置（0~0.5 → 0.5；0.5~0.9 → 0.8），用户可自行增删。</summary>
    private static OffsetConfig DefaultConfig() => new()
    {
        Ranges =
        [
            new CurrentOffsetRange { MinKa = 0.0, MaxKa = 0.5, Factor = 0.5 },
            new CurrentOffsetRange { MinKa = 0.5, MaxKa = 0.9, Factor = 0.8 }
        ]
    };
}
