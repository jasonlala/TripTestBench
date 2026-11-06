// ============================================================================
// 文件：OffsetConfig.cs
// 作用：电流补偿区间配置模型（对应程序目录下的 offset.json）。
//       每组配置包含：设定电流下限、上限、补偿系数；
//       设定电流落在 [下限, 上限) 区间内时，写入 PLC 前乘以补偿系数。
// ============================================================================

namespace TripTestBench.Models;

/// <summary>一组电流补偿区间：MinKa ≤ 设定电流 &lt; MaxKa 时，写入值 = 设定值 × Factor。</summary>
public sealed class CurrentOffsetRange
{
    /// <summary>区间下限（kA，含）。</summary>
    public double MinKa { get; set; }

    /// <summary>区间上限（kA，不含；最后一组含）。</summary>
    public double MaxKa { get; set; }

    /// <summary>电流补偿系数（写入 PLC 前 ×该系数，默认 1.0 = 不补偿）。</summary>
    public double Factor { get; set; } = 1.0;
}

/// <summary>offset.json 的根对象：补偿区间列表（用户可自由增删）。</summary>
public sealed class OffsetConfig
{
    /// <summary>补偿区间组（按顺序匹配，命中第一组即生效）。</summary>
    public List<CurrentOffsetRange> Ranges { get; set; } = new();
}
