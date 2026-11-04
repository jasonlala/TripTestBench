// ============================================================================
// 文件：DialogService.cs
// 作用：统一封装深色风格的确认框 / 提示框，供 ViewModel 直接调用。
//       好处：ViewModel 不直接依赖 Window 类型，且所有弹窗样式保持一致。
// ============================================================================

using System.Windows;                 // Application：取主窗口作为父窗口
using TripTestBench.Localization;     // LanguageManager：多语言标题与正文
using TripTestBench.Views;            // MessageDialog：自定义深色对话框

namespace TripTestBench.Services;

/// <summary>统一确认 / 提示对话框服务。</summary>
public static class DialogService
{
    /// <summary>
    /// 弹出“确定 / 取消”确认框（危险操作前调用，如充电、开始、停止、退出）。
    /// </summary>
    /// <param name="messageKey">语言资源键（如 L.Msg.ConfirmStart）</param>
    /// <returns>true = 用户点击确定</returns>
    public static bool Confirm(string messageKey)
    {
        var dialog = new MessageDialog(
            LanguageManager.Get("L.Msg.ConfirmTitle"),
            LanguageManager.Get(messageKey),
            showCancel: true)
        {
            Owner = Application.Current?.MainWindow
        };

        return dialog.ShowDialog() == true;
    }

    /// <summary>弹出只有“确定”的提示框。</summary>
    public static void Info(string message, string? title = null)
    {
        var dialog = new MessageDialog(
            title ?? LanguageManager.Get("L.Msg.ConfirmTitle"),
            message,
            showCancel: false)
        {
            Owner = Application.Current?.MainWindow
        };

        dialog.ShowDialog();
    }
}
