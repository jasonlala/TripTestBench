// ============================================================================
// 文件：App.xaml.cs
// 作用：应用入口与全局异常兜底。
//       启动顺序：加载配置 → 初始化日志 → 应用语言 → 显示主窗口（登录界面）。
//       全局异常统一写入日志，界面异常弹窗提示但不退出，保证工控机连续运行。
// ============================================================================

using System.Windows;                    // Application / MessageBox
using System.Windows.Threading;          // DispatcherUnhandledException
using TripTestBench.Localization;        // 语言管理
using TripTestBench.Services;            // 配置 / 日志

namespace TripTestBench;

/// <summary>应用主类。</summary>
public partial class App : Application
{
    /// <summary>启动流程。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1) 兜底捕获非 UI 线程异常（写日志，避免静默崩溃）
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("AppDomain unhandled exception", args.ExceptionObject as Exception);

        // 1.1) 兜底捕获未被观察的后台任务异常（fire-and-forget 任务抛出时，
        //      标记为已观察，保证进程不会因此终止）
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Unobserved task exception", args.Exception);
            args.SetObserved(); // 标记已处理，防止默认策略终止进程
        };

        // 2) 兜底捕获 UI 线程异常（提示 + 写日志，不退出程序）
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 3) 加载 appsettings.json（语言、PLC 参数）
        AppConfig.Instance.Load();

        // 3.1) 开启日志会话：输出软件版本、系统、程序目录、日志路径、PLC 参数
        Logger.BeginSession(typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        Logger.Info("Application starting");

        // 4) 应用上次选择的语言（默认中文）
        LanguageManager.Apply(AppConfig.Instance.Settings.Language);

        // 5) 显示主窗口（内部先展示登录界面）
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>UI 线程未处理异常：记录堆栈并提示操作员。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Dispatcher unhandled exception", e.Exception);
        MessageBox.Show(e.Exception.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // 标记已处理，避免程序退出影响生产
    }

    /// <summary>退出时记录日志。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("Application exiting");
        base.OnExit(e);
    }
}
