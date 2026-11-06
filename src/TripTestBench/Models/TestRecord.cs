// ============================================================================
// 文件：TestRecord.cs
// 作用：定义“一次过流脱扣试验”的完整数据记录。
//       该对象在试验完成时生成，操作员确认后保存到本地历史库（records.json），
//       并可在“数据查询”界面检索、导出 CSV。
// ============================================================================

namespace TripTestBench.Models;

/// <summary>一次过流脱扣试验的完整记录。</summary>
public sealed class TestRecord
{
    /// <summary>记录唯一编号（GUID），用于去重与导出定位。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>试验时间（本地时间，精确到秒）。</summary>
    public DateTime TestTime { get; set; } = DateTime.Now;

    /// <summary>产品名称（操作员输入）。</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>产品编号（操作员输入）。</summary>
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>试验员（默认取登录账号，可修改）。</summary>
    public string Tester { get; set; } = string.Empty;

    /// <summary>设定电流 (kA)。</summary>
    public double SetCurrentKa { get; set; }

    /// <summary>设定时间 (s)。</summary>
    public double SetTimeSeconds { get; set; }

    /// <summary>峰值电流 (kA)。</summary>
    public double PeakCurrentKa { get; set; }

    /// <summary>运行时间 (s)：从下发启动命令到结束的时间。</summary>
    public double RunTimeSeconds { get; set; }

    /// <summary>备注（预留字段，可用于记录异常或复检说明）。</summary>
    public string Remark { get; set; } = string.Empty;

    /// <summary>
    /// 本次试验的电流曲线截图（PNG 原始字节，数据库最后一列 ChartImage）。
    /// 列表查询时为空（避免一次性把大量图片读进内存），需要时按 Id 单独读取。
    /// </summary>
    public byte[]? ChartImage { get; set; }

    /// <summary>数据库里是否保存了曲线截图（列表界面显示“查看曲线”用）。</summary>
    public bool HasChartImage { get; set; }
}
