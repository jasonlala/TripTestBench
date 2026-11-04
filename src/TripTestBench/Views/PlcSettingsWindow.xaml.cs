// ============================================================================
// 文件：PlcSettingsWindow.xaml.cs
// 作用：PLC 通讯设置窗口的代码后置。
//       负责读取用户输入（IP / 端口 / 从站号 / 轮询周期）、做合法性校验、
//       提供“测试连接”按钮，并把最终结果通过 Result 属性返回给主窗口。
// ============================================================================

using System.Net;                 // IPAddress：IP 地址格式校验
using System.Windows;             // Window / RoutedEventArgs
using System.Windows.Input;       // MouseButtonEventArgs
using TripTestBench.Localization; // 多语言文案
using TripTestBench.Models;       // PlcOptions
using TripTestBench.Services;     // Logger：记录用户修改通讯参数
using TripTestBench.Services.Plc; // PlcServiceFactory：测试连接

namespace TripTestBench.Views;

/// <summary>PLC 通讯设置对话框。</summary>
public partial class PlcSettingsWindow : Window
{
    /// <summary>构造窗口：把当前配置回填到输入框。</summary>
    public PlcSettingsWindow(PlcOptions current)
    {
        InitializeComponent();

        Title = LanguageManager.Get("L.Settings.Title");
        IpBox.Text = current.Ip;
        PortBox.Text = current.Port.ToString();
        UnitBox.Text = current.UnitId.ToString();
        PollBox.Text = current.PollIntervalMs.ToString();

        // 先复制一份，取消时保持原配置不变
        Result = current.Clone();
    }

    /// <summary>用户确认后的配置（点击“应用并重连”后有效）。</summary>
    public PlcOptions Result { get; private set; }

    /// <summary>点击“测试连接”：用当前输入参数尝试握手。</summary>
    private async void Test_OnClick(object sender, RoutedEventArgs e)
    {
        var options = Collect(out var error);
        if (options is null)
        {
            StatusText.Text = error;
            return;
        }

        TestButton.IsEnabled = false;
        StatusText.Text = LanguageManager.Get("L.Settings.Testing");
        Logger.User($"测试 PLC 连接：{options.Ip}:{options.Port}（从站号 {options.UnitId}）");

        try
        {
            await PlcServiceFactory.TestConnectionAsync(options);
            StatusText.Text = LanguageManager.Get("L.Msg.ConnectionOk");
            Logger.Info($"PLC 连接测试成功：{options.Ip}:{options.Port}（从站号 {options.UnitId}）");
        }
        catch (Exception ex)
        {
            StatusText.Text = LanguageManager.Format("L.Msg.ConnectionFail", ex.Message);
            Logger.Warn($"PLC 连接测试失败：{options.Ip}:{options.Port}（从站号 {options.UnitId}） → {ex.Message}");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>点击“应用并重连”：校验通过后保存结果并关闭窗口。</summary>
    private void Apply_OnClick(object sender, RoutedEventArgs e)
    {
        var options = Collect(out var error);
        if (options is null)
        {
            StatusText.Text = error;
            return;
        }

        Result = options;
        Logger.User($"修改 PLC 通讯参数：{options.Ip}:{options.Port}，从站号 {options.UnitId}，"
                    + $"轮询 {options.PollIntervalMs} ms");
        DialogResult = true;
    }

    /// <summary>点击“取消”：不修改配置。</summary>
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 读取并校验输入框内容。
    /// </summary>
    /// <param name="error">校验失败时的提示文字</param>
    /// <returns>校验通过返回配置对象，否则返回 null</returns>
    private PlcOptions? Collect(out string error)
    {
        error = string.Empty;

        // 1) IP 地址格式校验
        var ip = IpBox.Text.Trim();
        if (!IPAddress.TryParse(ip, out _))
        {
            error = LanguageManager.Get("L.Settings.InvalidIp");
            return null;
        }

        // 2) 端口范围校验（1 ~ 65535）
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            error = LanguageManager.Get("L.Settings.InvalidPort");
            return null;
        }

        // 3) 从站号：非法或越界时回退默认 1（不阻塞保存）
        if (!byte.TryParse(UnitBox.Text.Trim(), out var unitId))
        {
            unitId = 1;
        }

        // 4) 轮询周期：非法或越界时回退默认 200ms（不阻塞保存）
        if (!int.TryParse(PollBox.Text.Trim(), out var poll) || poll is < 50 or > 5000)
        {
            poll = 200;
        }

        return new PlcOptions
        {
            Ip = ip,
            Port = port,
            UnitId = unitId,
            PollIntervalMs = poll
        };
    }

    /// <summary>拖动标题区域移动窗口。</summary>
    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
