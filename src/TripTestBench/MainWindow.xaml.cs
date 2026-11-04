// ============================================================================
// 文件：MainWindow.xaml.cs
// 作用：主窗口（外壳）逻辑。
//       1) 承载三个界面：登录 / 主界面 / 数据查询（ContentControl 切换）；
//       2) 自定义标题栏：拖动、双击最大化、最小化、关闭；
//       3) 标题栏语言切换按钮（中文 ⇄ English），并把选择写入配置；
//       4) 关闭前安全确认：测试进行中必须先确认，避免带载断电。
// ============================================================================

using System.ComponentModel;         // CancelEventArgs：关闭前拦截
using System.Windows;                // Window / RoutedEventArgs
using System.Windows.Input;          // MouseButtonEventArgs / MouseButtonState
using TripTestBench.Localization;    // 语言管理
using TripTestBench.Models;          // UserAccount
using TripTestBench.Services;        // 用户 / 记录 / 配置
using TripTestBench.ViewModels;      // 三个界面的 ViewModel
using TripTestBench.Views;           // 三个界面与设置窗口

namespace TripTestBench;

/// <summary>主窗口：负责界面切换与窗口外观控制。</summary>
public partial class MainWindow : Window
{
    private readonly UserService _userService = new();   // 用户服务（登录校验）
    private readonly RecordRepository _records = new();  // 试验记录仓库

    private MainViewModel? _mainViewModel;               // 主界面 VM（缓存，返回时保留状态）
    private MainView? _mainView;                         // 主界面视图（缓存，避免重复创建）

    /// <summary>构造主窗口：加载数据 → 适配屏幕 → 应用语言 → 显示登录界面。</summary>
    public MainWindow()
    {
        InitializeComponent();

        // 1) 加载本地数据（账户与历史记录）
        _userService.Load();
        _records.Load();

        // 2) 根据屏幕分辨率调整窗口尺寸（防止工控机小屏超出）
        FitToScreen();

        // 3) 刷新标题栏语言按钮文字与窗口标题
        UpdateLanguageUi();
        LanguageManager.LanguageChanged += OnLanguageChanged;

        // 4) 默认进入登录界面
        ShowLogin();
    }

    /// <summary>窗口尺寸不超过当前屏幕工作区（1366×768 等小屏也能完整显示）。</summary>
    private void FitToScreen()
    {
        var work = SystemParameters.WorkArea;
        if (Width > work.Width)
        {
            Width = Math.Max(MinWidth, work.Width);
        }

        if (Height > work.Height)
        {
            Height = Math.Max(MinHeight, work.Height);
        }
    }

    /// <summary>显示登录界面：释放主界面资源，创建新的登录 VM。</summary>
    private void ShowLogin()
    {
        DisposeMain();

        var view = new LoginView
        {
            DataContext = new LoginViewModel(_userService, OnLoginSuccess)
        };

        RootContent.Content = view;
    }

    /// <summary>登录成功回调：进入主界面。</summary>
    private void OnLoginSuccess(UserAccount user)
    {
        ShowMain();
    }

    /// <summary>显示主界面：首次创建 VM/视图，之后复用（保留曲线与参数）。</summary>
    private void ShowMain()
    {
        _mainViewModel ??= new MainViewModel(
            _userService,
            _records,
            ShowHistory,   // 打开数据查询
            ShowLogin,     // 退出登录
            ShowSettings); // 打开通讯设置

        _mainView ??= new MainView { DataContext = _mainViewModel };

        // 把“曲线截图”能力交给 VM：保存记录时由视图把图表渲染成 PNG
        _mainViewModel.ChartCaptureProvider ??= _mainView.CaptureChartImage;

        RootContent.Content = _mainView;
        _mainViewModel.OnActivated(); // 首次激活时才连接 PLC
    }

    /// <summary>显示数据查询界面（每次新建，保证读到最新记录）。</summary>
    private void ShowHistory()
    {
        RootContent.Content = new HistoryView
        {
            DataContext = new HistoryViewModel(_records, ShowMain)
        };
    }

    /// <summary>打开 PLC 通讯设置窗口，确认后保存配置并重连。</summary>
    private void ShowSettings()
    {
        var dialog = new PlcSettingsWindow(AppConfig.Instance.Settings.Plc)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 保存新参数到 appsettings.json
        AppConfig.Instance.Settings.Plc = dialog.Result;
        AppConfig.Instance.Save();

        // 立即按新参数重连 PLC
        if (_mainViewModel is not null)
        {
            _ = _mainViewModel.ReconnectAsync();
        }
    }

    /// <summary>释放主界面 VM（停止 PLC、停掉定时器）。</summary>
    private void DisposeMain()
    {
        _mainViewModel?.Dispose();
        _mainViewModel = null;
        _mainView = null;
    }

    /// <summary>语言切换按钮：中文 ⇄ English，并持久化选择。</summary>
    private void LanguageButton_OnClick(object sender, RoutedEventArgs e)
    {
        LanguageManager.Toggle();
        AppConfig.Instance.Settings.Language = LanguageManager.Current;
        AppConfig.Instance.Save();
    }

    /// <summary>语言变化：刷新按钮文字与窗口标题。</summary>
    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateLanguageUi();

    /// <summary>更新标题栏语言按钮文字（当前中文则显示 EN，反之显示中文）。</summary>
    private void UpdateLanguageUi()
    {
        LanguageButton.Content = LanguageManager.IsEnglish ? "中文" : "EN";
        Title = LanguageManager.Get("L.App.Title");
    }

    /// <summary>标题栏拖动 / 双击最大化。</summary>
    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 双击最大化瞬间 DragMove 可能抛异常，忽略即可
            }
        }
    }

    /// <summary>在最大化与还原之间切换。</summary>
    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>最小化窗口。</summary>
    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>关闭窗口（走 OnClosing 的安全确认）。</summary>
    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 关闭前拦截：
    /// 1) 测试进行中先确认停止试验，避免带载断电造成设备损坏；
    /// 2) 任何情况下点击关闭按钮都要弹出退出确认框，用户确认后才退出程序。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_mainViewModel is { IsTesting: true } && !DialogService.Confirm("L.Msg.ConfirmStopShutdown"))
        {
            e.Cancel = true;
            return;
        }

        // 通用退出确认：确认后才真正关闭窗口（程序随之退出）
        if (!DialogService.Confirm("L.Msg.ConfirmExit"))
        {
            e.Cancel = true;
            return;
        }

        Logger.User("用户确认退出程序");
        _mainViewModel?.Dispose();
        LanguageManager.LanguageChanged -= OnLanguageChanged;
        base.OnClosing(e);
    }
}
