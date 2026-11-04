// ============================================================================
// 文件：LoginView.xaml.cs
// 作用：登录界面视图的代码后置。
//       WPF 的 PasswordBox 出于安全考虑不支持绑定 Password 属性，
//       因此这里通过 PasswordChanged 事件把密码同步到 ViewModel。
// ============================================================================

using System.Windows;               // RoutedEventArgs
using System.Windows.Controls;      // UserControl
using TripTestBench.ViewModels;     // LoginViewModel

namespace TripTestBench.Views;

/// <summary>登录界面视图。</summary>
public partial class LoginView : UserControl
{
    /// <summary>构造视图：初始化控件，并在显示后自动聚焦用户名输入框。</summary>
    public LoginView()
    {
        InitializeComponent();
        Loaded += (_, _) => UserNameBox.Focus();
    }

    /// <summary>密码框内容变化 → 同步到 ViewModel（用于登录校验）。</summary>
    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel viewModel)
        {
            viewModel.Password = PasswordBox.Password;
        }
    }
}
