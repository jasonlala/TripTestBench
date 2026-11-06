// ============================================================================
// 文件：RecordRepository.cs
// 作用：试验记录的持久化（SQLite）与导出（CSV / Excel）。
//       · 存储：%LOCALAPPDATA%\TripTestBench\triptestbench.db（SQLite）
//               每条记录最后两列是 Remark 与 ChartImage（曲线截图 PNG）
//       · 迁移：首次启动若发现旧的 records.json，自动导入数据库并改名备份
//       · 导出：桌面下生成 CSV（UTF-8 BOM）或 Excel（.xlsx，可内嵌曲线截图）
// ============================================================================

using System.Globalization;             // 固定格式输出（不受系统区域影响）
using System.IO;                        // 文件读写
using System.Text;                      // StringBuilder / UTF8Encoding
using System.Text.Encodings.Web;        // 中文不转义
using System.Text.Json;                 // 旧 JSON 记录迁移
using ClosedXML.Excel;                  // Excel 导出
using TripTestBench.Models;             // TestRecord

namespace TripTestBench.Services;

/// <summary>试验记录仓库：内存缓存 + SQLite 持久化 + 导出。</summary>
public sealed class RecordRepository
{
    /// <summary>旧记录文件的反序列化选项（只用于迁移）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _sync = new();      // 读写锁：保存与查询并发安全
    private readonly RecordDatabase _database;  // SQLite 数据库
    private List<TestRecord> _records = new();  // 内存中的全部记录（不含截图二进制）

    /// <summary>默认构造：使用默认数据库文件。</summary>
    public RecordRepository()
        : this(new RecordDatabase())
    {
    }

    /// <summary>指定数据库构造（便于测试）。</summary>
    public RecordRepository(RecordDatabase database)
    {
        _database = database;
    }

    /// <summary>数据库文件路径（界面提示用）。</summary>
    public string DatabasePath => _database.FilePath;

    /// <summary>加载历史记录：自动迁移旧 JSON，再从 SQLite 读取。</summary>
    public void Load()
    {
        try
        {
            AppPaths.EnsureDataDir();
            MigrateLegacyJsonIfNeeded();

            _records = _database.QueryAll();
            Logger.Info($"已从 SQLite 加载 {_records.Count} 条历史记录：{_database.FilePath}");
        }
        catch (Exception ex)
        {
            Logger.Error("加载历史记录失败", ex);
            _records = new List<TestRecord>();
        }
    }

    /// <summary>获取全部记录（按试验时间倒序，最新在前；不含截图二进制）。</summary>
    public IReadOnlyList<TestRecord> GetAll()
    {
        lock (_sync)
        {
            return _records.OrderByDescending(r => r.TestTime).ToList();
        }
    }

    /// <summary>
    /// 新增一条记录并立即写入数据库。
    /// </summary>
    /// <param name="record">试验记录</param>
    /// <param name="chartImage">本次试验的曲线截图（PNG 字节），存入数据库最后一列</param>
    public void Add(TestRecord record, byte[]? chartImage = null)
    {
        lock (_sync)
        {
            // 同步内存标记，保证保存后界面立刻能显示“查看曲线”
            record.ChartImage = chartImage;
            record.HasChartImage = chartImage is { Length: > 0 };

            _database.Insert(record, chartImage);
            _records.Add(record);

            Logger.Info($"记录已写入 SQLite：{record.ProductName} / {record.ProductCode}"
                        + $"（截图 {(record.HasChartImage ? $"{chartImage!.Length / 1024} KB" : "无")}）");
        }
    }

    /// <summary>按记录 Id 读取曲线截图（PNG 字节）。</summary>
    public byte[]? GetChartImage(string recordId)
    {
        try
        {
            return _database.GetChartImage(recordId);
        }
        catch (Exception ex)
        {
            Logger.Error("读取曲线截图失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 导出 CSV：表头由界面按当前语言传入，数据行固定列顺序。
    /// </summary>
    /// <returns>导出的完整文件路径</returns>
    public string ExportCsv(IEnumerable<TestRecord> records, IReadOnlyList<string> headers)
    {
        var list = records.ToList();
        var builder = new StringBuilder();

        // 表头（按当前语言）
        builder.AppendLine(string.Join(",", headers.Select(Escape)));

        foreach (var r in list)
        {
            builder.AppendLine(string.Join(",", new[]
            {
                r.TestTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                r.ProductName,
                r.ProductCode,
                r.Tester,
                r.SetCurrentKa.ToString("F3", CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(r.SetTimeSeconds).ToString(@"hh\:mm\:ss"),
                r.PeakCurrentKa.ToString("F3", CultureInfo.InvariantCulture),
                r.RunTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)
            }.Select(Escape)));
        }

        var path = Path.Combine(AppPaths.ExportDir, $"试验记录_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        Logger.Info($"导出记录 {list.Count} 条到 {path}");
        return path;
    }

    /// <summary>
    /// 导出 Excel（.xlsx）：表头样式化、首行冻结、自动筛选；
    /// includeChart 为 true 时把每条记录的曲线截图嵌入最后一列。
    /// </summary>
    /// <param name="records">要导出的记录</param>
    /// <param name="headers">表头（最后一列应为“曲线图”）</param>
    /// <param name="sheetName">工作表名称</param>
    /// <param name="includeChart">是否嵌入曲线截图列</param>
    /// <param name="passText">合格文字（跟随界面语言）</param>
    /// <param name="failText">不合格文字（跟随界面语言）</param>
    /// <param name="targetPath">目标文件路径（界面“另存为”选择）；为空时默认保存到桌面</param>
    /// <returns>导出的完整文件路径</returns>
    public string ExportExcel(
        IEnumerable<TestRecord> records,
        IReadOnlyList<string> headers,
        string sheetName,
        bool includeChart,
        string passText,
        string failText,
        string? targetPath = null)
    {
        var list = records.ToList();
        var pictureStreams = new List<MemoryStream>(); // 图片流需保留到 SaveAs 之后

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SanitizeSheetName(sheetName));

        // 1) 表头：深色底 + 白字 + 居中
        for (var column = 0; column < headers.Count; column++)
        {
            var cell = sheet.Cell(1, column + 1);
            cell.Value = headers[column];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F3A5F");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        sheet.Row(1).Height = 22;

        // 2) 数据行
        var rowIndex = 2;
        var dataColumnCount = includeChart ? headers.Count - 1 : headers.Count;

        foreach (var record in list)
        {
            var values = new List<XLCellValue>
            {
                record.TestTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                record.ProductName,
                record.ProductCode,
                record.Tester,
                Math.Round(record.SetCurrentKa, 3),
                TimeSpan.FromSeconds(record.SetTimeSeconds).ToString(@"hh\:mm\:ss"),
                Math.Round(record.PeakCurrentKa, 3),
                Math.Round(record.RunTimeSeconds, 2)
            };

            for (var column = 0; column < values.Count && column < dataColumnCount; column++)
            {
                var cell = sheet.Cell(rowIndex, column + 1);
                cell.Value = values[column];
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            // 3) 曲线截图列：读取 BLOB 后贴到对应行最后一列
            if (includeChart)
            {
                var png = record.HasChartImage ? _database.GetChartImage(record.Id) : null;
                if (png is { Length: > 0 })
                {
                    var stream = new MemoryStream(png);
                    pictureStreams.Add(stream);

                    var picture = sheet.AddPicture(stream).MoveTo(sheet.Cell(rowIndex, headers.Count));
                    picture.WithSize(150, 84);      // 约 4cm × 2.2cm，行高 66 磅可完整显示
                    sheet.Row(rowIndex).Height = 66;
                }
            }

            rowIndex++;
        }

        // 4) 列宽：按单元格内容显示宽度手动计算（中文/全角按 2 个字符宽计），
        //    不依赖 AdjustToContents（其对中文与合并样式的测量在现场环境不稳定），
        //    保证导出的 Excel 列宽始终与内容匹配。
        for (var column = 1; column <= dataColumnCount; column++)
        {
            var maxUnits = 6d; // 最小宽度下限
            for (var row = 1; row <= Math.Max(1, rowIndex - 1); row++)
            {
                maxUnits = Math.Max(maxUnits, MeasureDisplayUnits(sheet.Cell(row, column).GetString()));
            }

            sheet.Column(column).Width = Math.Min(maxUnits + 3, 60); // 留 3 字符边距，封顶 60
        }
        if (includeChart)
        {
            sheet.Column(headers.Count).Width = 24;
        }

        // 5) 冻结表头 + 自动筛选，方便现场按产品编号过滤
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(1, rowIndex - 1), headers.Count).SetAutoFilter();

        // 6) 目标文件：优先使用“另存为”对话框选择的路径，否则默认保存到桌面
        string path;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            AppPaths.EnsureDataDir();
            path = Path.Combine(AppPaths.ExportDir, $"试验记录_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        }
        else
        {
            path = targetPath;

            // 用户可能输入了尚不存在的子目录
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        try
        {
            workbook.SaveAs(path);
        }
        finally
        {
            foreach (var stream in pictureStreams)
            {
                stream.Dispose();
            }
        }

        Logger.Info($"导出 Excel（{list.Count} 条，{(includeChart ? "含" : "不含")}曲线截图）到 {path}");
        return path;
    }

    /// <summary>
    /// 把“当前这一次试验”的完整数据（试验信息 + 1 Hz 电流曲线）保存为独立 CSV 文件。
    /// 输出目录为桌面（无桌面时退回数据目录），UTF-8 带 BOM，Excel 双击打开中文不乱码。
    /// </summary>
    /// <param name="record">本次试验记录（同时用于生成文件名）</param>
    /// <param name="samples">本次试验的曲线采样点（时间 s / 电流 kA）</param>
    /// <param name="infoItems">试验信息键值对（标签由调用方按当前语言提供）</param>
    /// <param name="curveTimeLabel">曲线表头：时间列</param>
    /// <param name="curveCurrentLabel">曲线表头：电流列</param>
    /// <param name="fileNamePrefix">文件名前缀（如“试验数据”）</param>
    /// <returns>保存后的完整文件路径</returns>
    public string SaveTestDataCsv(
        TestRecord record,
        IReadOnlyList<ChartSample> samples,
        IReadOnlyList<(string Label, string Value)> infoItems,
        string curveTimeLabel,
        string curveCurrentLabel,
        string fileNamePrefix)
    {
        var builder = new StringBuilder();

        // 1) 试验信息段：每行“标签,值”
        foreach (var item in infoItems)
        {
            builder.AppendLine(string.Join(",", new[] { Escape(item.Label), Escape(item.Value) }));
        }

        // 2) 空行分隔两段，Excel 打开时层次清晰
        builder.AppendLine();

        // 3) 曲线数据段：时间(s) / 电流(kA)，每行一个采样点
        builder.AppendLine(string.Join(",", new[] { Escape(curveTimeLabel), Escape(curveCurrentLabel) }));
        foreach (var sample in samples)
        {
            builder.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0:F1},{1:F3}",
                sample.Time,
                sample.Value));
        }

        AppPaths.EnsureDataDir();

        // 4) 文件名：前缀 + 产品编号 + 时间戳，避免多次保存互相覆盖
        var code = SanitizeFileName(record.ProductCode);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var name = string.IsNullOrWhiteSpace(code)
            ? $"{fileNamePrefix}_{stamp}.csv"
            : $"{fileNamePrefix}_{code}_{stamp}.csv";
        var path = Path.Combine(AppPaths.ExportDir, name);

        // 5) UTF-8 带 BOM 写入
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        Logger.Info($"当前试验数据已保存到 {path}（曲线 {samples.Count} 点）");
        return path;
    }

    /// <summary>首次启动迁移：数据库为空且存在 records.json 时，把旧记录导入 SQLite。</summary>
    private void MigrateLegacyJsonIfNeeded()
    {
        if (!File.Exists(AppPaths.RecordsFile))
        {
            return;
        }

        if (_database.Count() > 0)
        {
            return; // 数据库已有数据，不再重复导入
        }

        var json = File.ReadAllText(AppPaths.RecordsFile);
        var legacy = JsonSerializer.Deserialize<List<TestRecord>>(json, JsonOptions);
        if (legacy is null || legacy.Count == 0)
        {
            return;
        }

        foreach (var record in legacy)
        {
            _database.Insert(record, null); // 旧数据没有截图
        }

        // 迁移完成：改名备份，避免下次启动重复判断
        File.Move(AppPaths.RecordsFile, AppPaths.RecordsFile + ".migrated", true);
        Logger.Info($"已将 {legacy.Count} 条旧 JSON 记录迁移到 SQLite，原文件改名 .migrated");
    }

    /// <summary>清理文件名中的非法字符（路径分隔符、冒号、通配符等），避免写文件失败。</summary>
    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    /// <summary>清理 Excel 工作表名：去掉禁用字符并截断到 31 字符。</summary>
    private static string SanitizeSheetName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Sheet1";
        }

        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var chars = value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars);
        return name.Length > 31 ? name[..31] : name;
    }

    /// <summary>
    /// 测量文本的显示宽度（单位≈半角字符数）：中文 / 全角字符按 2 计，
    /// 用于导出 Excel 时按内容计算列宽。
    /// </summary>
    private static double MeasureDisplayUnits(string text)
    {
        var units = 0d;
        foreach (var c in text)
        {
            units += c > 0xFF ? 2 : 1; // CJK / 全角符号宽度加倍
        }

        return units;
    }

    /// <summary>CSV 字段转义：包含逗号 / 引号 / 换行时用双引号包裹并转义内部引号。</summary>
    private static string Escape(string value)
    {
        value ??= string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
