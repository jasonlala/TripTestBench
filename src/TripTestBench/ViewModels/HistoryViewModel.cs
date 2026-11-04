// ============================================================================
// 文件：HistoryViewModel.cs
// 作用：数据查询（历史记录）界面的视图模型。
//       功能：
//       1) 从 SQLite 读取全部试验记录（按时间倒序）；
//       2) 按时间范围（全部 / 今天 / 近 7 天 / 近 30 天）筛选；
//       3) 按关键字（产品名称 / 产品编号 / 试验员）模糊搜索；
//       4) 统计总记录、合格数、不合格数；
//       5) 导出当前筛选结果为 Excel（.xlsx，可内嵌曲线截图）；
//       6) 把当前筛选结果发送到打印机；
//       7) 查看单条记录保存的曲线截图。
// ============================================================================

using System.Collections.ObjectModel;    // 绑定到 DataGrid 的记录集合
using System.Windows;                    // Application：另存为对话框的 owner
using System.Windows.Input;              // ICommand
using Microsoft.Win32;                   // SaveFileDialog：导出 Excel 时由操作员选择保存位置
using TripTestBench.Localization;        // 多语言
using TripTestBench.Models;              // TestRecord
using TripTestBench.Mvvm;                // MVVM 基类与命令
using TripTestBench.Services;            // 记录仓库 / 打印 / 日志
using TripTestBench.Views;               // 曲线截图预览窗口

namespace TripTestBench.ViewModels;

/// <summary>数据查询界面视图模型。</summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly RecordRepository _repository; // 记录仓库（SQLite）
    private readonly Action _back;                 // 返回主界面回调
    private readonly List<TestRecord> _all = new(); // 全量记录缓存（筛选前）

    private string _keyword = string.Empty;        // 关键字
    private int _rangeIndex;                       // 时间范围索引：0 全部 / 1 今天 / 2 近 7 天 / 3 近 30 天
    private int _totalCount;                       // 当前筛选结果总数
    private int _passedCount;                      // 合格数
    private int _failedCount;                      // 不合格数
    private string _statusMessage = string.Empty;  // 底部状态提示（导出 / 打印结果）
    private bool _statusIsError;                   // 状态提示是否为错误
    private TestRecord? _selectedRecord;           // 当前选中行（双击查看曲线用）

    /// <summary>构造：初始化命令、订阅语言变化并立即加载数据。</summary>
    public HistoryViewModel(RecordRepository repository, Action back)
    {
        _repository = repository;
        _back = back;

        // 命令名称会写入 USER 日志：现场可据此还原“谁在什么时候导出了什么”
        SearchCommand = new RelayCommand(Refresh, null, "查询记录");                      // 查询
        ResetCommand = new RelayCommand(Reset, null, "重置筛选");                          // 重置
        ExportCommand = new RelayCommand(ExportCsv, null, "导出 CSV");                     // 导出 CSV（保留能力）
        ExportExcelCommand = new RelayCommand(ExportExcel, null, "导出 Excel");            // 导出 Excel（含截图）
        PrintCommand = new RelayCommand(Print, null, "打印记录");                          // 打印
        ViewChartCommand = new RelayCommand(
            parameter => ViewChart(parameter as TestRecord), null, "查看曲线截图");        // 查看曲线
        BackCommand = new RelayCommand(() => _back(), null, "返回主界面");                 // 返回主界面

        LanguageManager.LanguageChanged += OnLanguageChanged;
        Reload();
    }

    /// <summary>当前筛选结果（绑定到 DataGrid）。</summary>
    public ObservableCollection<TestRecord> Items { get; } = new();

    /// <summary>当前选中行（双击 DataGrid 时查看曲线）。</summary>
    public TestRecord? SelectedRecord
    {
        get => _selectedRecord;
        set => SetProperty(ref _selectedRecord, value);
    }

    /// <summary>关键字输入（产品名称 / 编号 / 试验员）。</summary>
    public string Keyword
    {
        get => _keyword;
        set => SetProperty(ref _keyword, value);
    }

    /// <summary>时间范围索引：切换后立即重新筛选。</summary>
    public int RangeIndex
    {
        get => _rangeIndex;
        set
        {
            if (SetProperty(ref _rangeIndex, value))
            {
                Refresh();
            }
        }
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    public int PassedCount
    {
        get => _passedCount;
        private set => SetProperty(ref _passedCount, value);
    }

    public int FailedCount
    {
        get => _failedCount;
        private set => SetProperty(ref _failedCount, value);
    }

    /// <summary>结果为空（界面显示“暂无符合条件的记录”）。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>底部状态提示文本（导出路径 / 打印结果 / 失败原因）。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    /// <summary>是否有状态提示（控制提示条可见性）。</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>状态提示是否为错误（决定文字颜色）。</summary>
    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public ICommand SearchCommand { get; }

    public ICommand ResetCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand ExportExcelCommand { get; }

    public ICommand PrintCommand { get; }

    public ICommand ViewChartCommand { get; }

    public ICommand BackCommand { get; }

    /// <summary>重新从数据库加载全部记录，再套用当前筛选条件。</summary>
    public void Reload()
    {
        _all.Clear();
        _all.AddRange(_repository.GetAll());
        Refresh();
    }

    /// <summary>
    /// 按时间范围 + 关键字筛选，并刷新统计数字。
    /// </summary>
    public void Refresh()
    {
        // 1) 计算时间起点（今天 0 点 / 近 7 天 / 近 30 天）
        var from = RangeIndex switch
        {
            1 => DateTime.Today,
            2 => DateTime.Today.AddDays(-6),
            3 => DateTime.Today.AddDays(-29),
            _ => DateTime.MinValue
        };

        var keyword = Keyword?.Trim() ?? string.Empty;

        // 2) 时间过滤
        var query = _all.Where(r => r.TestTime.Date >= from.Date);

        // 3) 关键字过滤：产品名称 / 产品编号 / 试验员 任一命中即可
        if (!string.IsNullOrEmpty(keyword))
        {
            query = query.Where(r =>
                r.ProductName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                r.ProductCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                r.Tester.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        // 4) 按时间倒序，最新的记录排在最前
        var result = query.OrderByDescending(r => r.TestTime).ToList();

        // 5) 更新可观察集合（DataGrid 自动刷新）
        Items.Clear();
        foreach (var record in result)
        {
            Items.Add(record);
        }

        // 6) 刷新统计与空状态
        TotalCount = result.Count;
        PassedCount = result.Count(r => r.Passed);
        FailedCount = TotalCount - PassedCount;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>重置筛选条件：清空关键字、回到“全部”时间范围。</summary>
    private void Reset()
    {
        Keyword = string.Empty;
        RangeIndex = 0;
        StatusMessage = string.Empty;
        Refresh();
    }

    /// <summary>导出当前筛选结果为 CSV（保留的兼容能力）。</summary>
    private void ExportCsv()
    {
        if (Items.Count == 0)
        {
            SetStatus(LanguageManager.Get("L.Msg.NoExportData"), true);
            return;
        }

        try
        {
            var path = _repository.ExportCsv(Items, BuildDataHeaders());
            SetStatus(LanguageManager.Format("L.Msg.Exported", path), false);
        }
        catch (Exception ex)
        {
            Logger.Error("导出 CSV 失败", ex);
            SetStatus(LanguageManager.Format("L.Msg.ExportFailed", ex.Message), true);
        }
    }

    /// <summary>导出当前筛选结果为 Excel（含曲线截图列），弹出“另存为”对话框选择保存位置。</summary>
    private void ExportExcel()
    {
        if (Items.Count == 0)
        {
            SetStatus(LanguageManager.Get("L.Msg.NoExportData"), true);
            return;
        }

        // 1) 默认保存到桌面，操作员可自行改目录与文件名
        var dialog = new SaveFileDialog
        {
            Title = LanguageManager.Get("L.Dialog.SaveExcel"),
            Filter = LanguageManager.Get("L.Dialog.ExcelFilter"),
            DefaultExt = ".xlsx",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = AppPaths.ExportDir,
            FileName = $"试验记录_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };

        var owner = Application.Current?.MainWindow;
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true)
        {
            Logger.User("操作员取消导出 Excel");
            return;
        }

        try
        {
            // 表头 = 11 个数据列 + 1 个“曲线图”列（图片贴在最后一列）
            var headers = BuildDataHeaders().ToList();
            headers.Add(LanguageManager.Get("L.History.Col.Chart"));

            var path = _repository.ExportExcel(
                Items,
                headers,
                LanguageManager.Get("L.History.SheetName"),
                includeChart: true,
                passText: LanguageManager.Get("L.Msg.Pass"),
                failText: LanguageManager.Get("L.Msg.Fail"),
                targetPath: dialog.FileName);

            SetStatus(LanguageManager.Format("L.Msg.ExportedExcel", path), false);
        }
        catch (Exception ex)
        {
            Logger.Error("导出 Excel 失败", ex);
            SetStatus(LanguageManager.Format("L.Msg.ExportFailed", ex.Message), true);
        }
    }

    /// <summary>把当前筛选结果发送到打印机（A4 横向报表）。</summary>
    private void Print()
    {
        if (Items.Count == 0)
        {
            SetStatus(LanguageManager.Get("L.Msg.NoExportData"), true);
            return;
        }

        try
        {
            var subtitle = LanguageManager.Format(
                "L.Print.Subtitle",
                Items.Count,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            // 打印时把每条记录的曲线截图一并输出（列表里未加载 BLOB，按 Id 现取）
            var printed = PrintService.PrintRecords(
                Items.ToList(),
                BuildDataHeaders(),
                LanguageManager.Get("L.Print.Title"),
                subtitle,
                record => record.ChartImage ?? _repository.GetChartImage(record.Id),
                LanguageManager.Get("L.History.Col.Chart"));

            // 用户在打印对话框点“取消”时不提示错误
            if (printed)
            {
                SetStatus(LanguageManager.Get("L.Msg.PrintDone"), false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("打印失败", ex);
            SetStatus(LanguageManager.Format("L.Msg.PrintFailed", ex.Message), true);
        }
    }

    /// <summary>查看某条记录的曲线截图（双击行或点“查看”按钮）。</summary>
    private void ViewChart(TestRecord? record)
    {
        record ??= SelectedRecord;
        if (record is null)
        {
            return;
        }

        // 优先用内存里已加载的（刚保存的记录），否则按 Id 从数据库读取
        var png = record.ChartImage ?? _repository.GetChartImage(record.Id);
        if (png is not { Length: > 0 })
        {
            SetStatus(LanguageManager.Get("L.Msg.NoChartImage"), true);
            return;
        }

        var title = $"{record.ProductName} / {record.ProductCode}  ·  {record.TestTime:yyyy-MM-dd HH:mm:ss}";
        ChartPreviewWindow.ShowPreview(png, title);
    }

    /// <summary>11 个数据列的表头（顺序与 RecordRepository 导出一致）。</summary>
    private static List<string> BuildDataHeaders()
    {
        return new List<string>
        {
            LanguageManager.Get("L.History.Col.Time"),
            LanguageManager.Get("L.History.Col.ProductName"),
            LanguageManager.Get("L.History.Col.ProductCode"),
            LanguageManager.Get("L.History.Col.Tester"),
            LanguageManager.Get("L.History.Col.Station"),
            LanguageManager.Get("L.History.Col.SetCurrent"),
            LanguageManager.Get("L.History.Col.SetTime"),
            LanguageManager.Get("L.History.Col.TripCurrent"),
            LanguageManager.Get("L.Readout.PeakCurrent") + " (kA)",
            LanguageManager.Get("L.History.Col.RunTime"),
            LanguageManager.Get("L.History.Col.Result")
        };
    }

    /// <summary>设置底部提示（同时记录是否为错误）。</summary>
    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    /// <summary>语言切换：重新筛选（结果文字随语言变化）并清空旧提示。</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Refresh();
        StatusMessage = string.Empty;
    }
}
