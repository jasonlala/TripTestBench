// ============================================================================
// 文件：PrintService.cs
// 作用：把试验记录打印到打印机（系统打印对话框 + A4 横向报表）。
//       报表 = 标题 + 副标题 + 11 列数据 + 最后一列“曲线图”截图。
//       实现方式：用 FlowDocument 生成表格文档，交给 WPF 的 PrintDialog 打印，
//       不依赖界面控件的可视状态，窗口被遮挡或最小化也能正常打印。
// ============================================================================

using System.IO;                        // MemoryStream：从 PNG 字节解码图片
using System.Printing;                  // PageOrientation / PageMediaSize
using System.Windows;                   // Thickness / FlowDocument 排版
using System.Windows.Controls;          // PrintDialog / Image
using System.Windows.Documents;         // FlowDocument / Table / Paragraph / BlockUIContainer
using System.Windows.Media;             // Brush / FontFamily
using System.Windows.Media.Imaging;     // BitmapImage：解码数据库中保存的 PNG
using TripTestBench.Models;             // TestRecord

namespace TripTestBench.Services;

/// <summary>试验记录打印服务。</summary>
public static class PrintService
{
    /// <summary>各列宽度（DIU，约等于 1/96 英寸）；与导出列顺序一一对应。</summary>
    private static readonly double[] ColumnWidths = { 118, 120, 110, 70, 52, 78, 74, 78, 72, 70, 56 };

    /// <summary>曲线截图列宽（DIU）。</summary>
    private const double ChartColumnWidth = 120;

    /// <summary>截图在报表中的显示尺寸（DIU）：宽高比与界面图表一致，缩略图不拉伸变形。</summary>
    private const double ChartImageWidth = 112;
    private const double ChartImageHeight = 63;

    /// <summary>
    /// 弹出打印对话框并打印当前筛选结果。
    /// </summary>
    /// <param name="records">要打印的记录</param>
    /// <param name="headers">表头（按当前语言）</param>
    /// <param name="title">报表标题</param>
    /// <param name="subtitle">副标题（记录数、打印时间等）</param>
    /// <param name="chartProvider">曲线截图提供者（返回 PNG 字节）；为 null 时不打印截图列</param>
    /// <param name="chartHeader">曲线截图列的表头文字</param>
    /// <returns>true 表示已发送到打印机；false 表示用户取消</returns>
    public static bool PrintRecords(
        IReadOnlyList<TestRecord> records,
        IReadOnlyList<string> headers,
        string title,
        string subtitle,
        Func<TestRecord, byte[]?>? chartProvider = null,
        string? chartHeader = null)
    {
        // 1) 先构造文档：排版若有问题，在弹出打印对话框之前就能暴露
        var document = BuildDocument(records, headers, title, subtitle, chartProvider, chartHeader, out var chartCount);

        var columnCount = headers.Count + (chartProvider is null ? 0 : 1);
        Logger.Info($"打印文档已生成：{records.Count} 条记录，{columnCount} 列"
                    + (chartProvider is null ? string.Empty : $"，内嵌曲线截图 {chartCount} 张"));

        var dialog = new PrintDialog();

        // 工业现场报表多为横向 A4：先尝试设置，打印机不支持时忽略
        try
        {
            if (dialog.PrintTicket is not null)
            {
                dialog.PrintTicket.PageOrientation = PageOrientation.Landscape;
                dialog.PrintTicket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("设置打印纸张参数失败（将使用打印机默认值）", ex);
        }

        // 2) 用户在系统打印对话框点“取消”时直接返回，不消耗纸张
        if (dialog.ShowDialog() != true)
        {
            Logger.Info("操作员取消了打印");
            return false;
        }

        // 3) 按所选打印机的可打印区域排版，避免右侧内容被裁掉
        try
        {
            if (dialog.PrintableAreaWidth > 0 && dialog.PrintableAreaHeight > 0)
            {
                document.PageWidth = dialog.PrintableAreaWidth;
                document.PageHeight = dialog.PrintableAreaHeight;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("读取可打印区域失败（使用默认页面尺寸）", ex);
        }

        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, title);
        Logger.Info($"已发送打印任务：{title}，共 {records.Count} 条记录");
        return true;
    }

    /// <summary>构造打印文档：标题 + 副标题 + 数据表格（可选曲线截图列）。</summary>
    private static FlowDocument BuildDocument(
        IReadOnlyList<TestRecord> records,
        IReadOnlyList<string> headers,
        string title,
        string subtitle,
        Func<TestRecord, byte[]?>? chartProvider,
        string? chartHeader,
        out int chartCount)
    {
        chartCount = 0;

        var hasChart = chartProvider is not null;
        var columnCount = headers.Count + (hasChart ? 1 : 0);

        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 9.5,
            PagePadding = new Thickness(24),
            ColumnWidth = double.PositiveInfinity // 单栏排版，表格才不会被拆成两栏
        };

        // 1) 标题
        document.Blocks.Add(new Paragraph(new Run(title))
        {
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 2)
        });

        // 2) 副标题：记录数 / 打印时间
        document.Blocks.Add(new Paragraph(new Run(subtitle))
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            Margin = new Thickness(0, 0, 0, 10)
        });

        // 3) 数据表格
        var table = new Table { CellSpacing = 0 };

        for (var i = 0; i < columnCount; i++)
        {
            GridLength width;

            if (hasChart && i == headers.Count)
            {
                width = new GridLength(ChartColumnWidth);   // 截图列固定宽度
            }
            else if (headers.Count == ColumnWidths.Length)
            {
                width = new GridLength(ColumnWidths[i]);    // 列数与预设宽度一致时用固定宽度
            }
            else
            {
                width = new GridLength(1, GridUnitType.Star); // 否则等分
            }

            table.Columns.Add(new TableColumn { Width = width });
        }

        // 3.1 表头行：深蓝底 + 白字
        var headerGroup = new TableRowGroup();
        var headerRow = new TableRow();
        foreach (var header in headers)
        {
            headerRow.Cells.Add(CreateCell(header, isHeader: true, passed: true));
        }

        if (hasChart)
        {
            headerRow.Cells.Add(CreateCell(chartHeader ?? "Chart", isHeader: true, passed: true));
        }

        headerGroup.Rows.Add(headerRow);
        table.RowGroups.Add(headerGroup);

        // 3.2 数据行：结果列用绿 / 红区分，最后一列为曲线截图
        var bodyGroup = new TableRowGroup();
        foreach (var record in records)
        {
            var row = new TableRow();
            row.Cells.Add(CreateCell(record.TestTime.ToString("yyyy-MM-dd HH:mm:ss"), false, true));
            row.Cells.Add(CreateCell(record.ProductName, false, true));
            row.Cells.Add(CreateCell(record.ProductCode, false, true));
            row.Cells.Add(CreateCell(record.Tester, false, true));
            row.Cells.Add(CreateCell(record.Station.ToString(), false, true));
            row.Cells.Add(CreateCell(record.SetCurrentKa.ToString("F3"), false, true));
            row.Cells.Add(CreateCell(record.SetTimeSeconds.ToString("F2"), false, true));
            row.Cells.Add(CreateCell(record.TripCurrentKa.ToString("F3"), false, true));
            row.Cells.Add(CreateCell(record.PeakCurrentKa.ToString("F3"), false, true));
            row.Cells.Add(CreateCell(record.RunTimeSeconds.ToString("F2"), false, true));
            row.Cells.Add(CreateCell(record.Passed ? "PASS" : "FAIL", false, record.Passed));

            if (hasChart)
            {
                byte[]? png = null;
                try
                {
                    png = chartProvider!(record);
                }
                catch (Exception ex)
                {
                    // 单条记录取图失败不影响整份报表
                    Logger.Error($"打印时读取曲线截图失败：{record.ProductCode}", ex);
                }

                if (png is { Length: > 0 })
                {
                    chartCount++;
                }

                row.Cells.Add(CreateChartCell(png));
            }

            bodyGroup.Rows.Add(row);
        }

        table.RowGroups.Add(bodyGroup);
        document.Blocks.Add(table);
        return document;
    }

    /// <summary>创建曲线截图单元格：有图则显示缩略图，无图显示短横线占位。</summary>
    private static TableCell CreateChartCell(byte[]? png)
    {
        var cell = new TableCell
        {
            Padding = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xDD, 0xE4)),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Background = Brushes.White
        };

        if (png is not { Length: > 0 })
        {
            cell.Blocks.Add(new Paragraph(new Run("-"))
            {
                Margin = new Thickness(0),
                TextAlignment = TextAlignment.Center
            });
            return cell;
        }

        var image = new Image
        {
            Source = LoadBitmap(png),
            Width = ChartImageWidth,
            Height = ChartImageHeight,
            Stretch = Stretch.Uniform     // 等比缩放，保持曲线比例
        };

        cell.Blocks.Add(new BlockUIContainer(image) { Margin = new Thickness(0) });
        return cell;
    }

    /// <summary>把 PNG 字节解码为可打印的位图（OnLoad 立即解码，流可安全释放）。</summary>
    private static BitmapSource LoadBitmap(byte[] png)
    {
        using var stream = new MemoryStream(png);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();                  // 冻结后可跨线程使用，且不再依赖原流
        return bitmap;
    }

    /// <summary>创建表格单元格（统一边框与内边距）。</summary>
    private static TableCell CreateCell(string text, bool isHeader, bool passed)
    {
        var paragraph = new Paragraph(new Run(text ?? string.Empty))
        {
            Margin = new Thickness(0),
            TextAlignment = isHeader ? TextAlignment.Center : TextAlignment.Center
        };

        if (isHeader)
        {
            paragraph.FontWeight = FontWeights.Bold;
            paragraph.Foreground = Brushes.White;
        }
        else if (!passed)
        {
            paragraph.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
        }

        return new TableCell(paragraph)
        {
            Padding = new Thickness(4, 3, 4, 3),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xDD, 0xE4)),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Background = isHeader
                ? new SolidColorBrush(Color.FromRgb(0x1F, 0x3A, 0x5F))
                : Brushes.White
        };
    }
}
