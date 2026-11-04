// ============================================================================
// 文件：MessageDialog.xaml.cs
// 作用：通用深色确认 / 提示对话框的代码后置。
//       支持两种模式：确认模式（确定 + 取消）、提示模式（仅确定）；
//       无系统边框，标题区域可拖动。
// ============================================================================

using System.Windows;              // Window / Visibility / RoutedEventArgs
using System.Windows.Input;        // MouseButtonEventArgs

namespace TripTestBench.Views;

/// <summary>通用消息对话框。</summary>
public partial class MessageDialog : Window
{
    /// <summary>
    /// 构造对话框。
    /// </summary>
    /// <param name="title">标题（同时用于任务栏与 UI 自动化识别）</param>
    /// <param name="message">正文内容</param>
    /// <param name="showCancel">是否显示“取消”按钮</param>
    public MessageDialog(string title, string message, bool showCancel)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>点击“确定”：返回 true。</summary>
    private void Ok_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>点击“取消”：返回 false。</summary>
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>拖动标题区域移动窗口。</summary>
    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
