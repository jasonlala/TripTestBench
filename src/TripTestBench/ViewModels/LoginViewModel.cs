// ============================================================================
// 文件：LoginViewModel.cs
// 作用：登录界面的视图模型。
//       负责接收用户名 / 密码、调用账户服务校验、显示错误提示；
//       校验通过后通过回调通知 MainWindow 切换到主界面。
// ============================================================================

using System.Windows.Input;              // ICommand
using TripTestBench.Localization;        // 多语言
using TripTestBench.Models;              // UserAccount
using TripTestBench.Mvvm;                // ObservableObject / RelayCommand
using TripTestBench.Services;            // UserService

namespace TripTestBench.ViewModels;

/// <summary>登录界面视图模型。</summary>
public sealed class LoginViewModel : ObservableObject
{
    private readonly UserService _userService;            // 账户服务
    private readonly Action<UserAccount> _onSuccess;      // 登录成功回调（切换到主界面）

    private string _userName = string.Empty;              // 用户名输入
    private string _password = string.Empty;              // 密码输入（PasswordBox 通过后台同步）
    private string? _errorKey;                            // 当前错误提示的语言键（切换语言时重新翻译）
    private string _errorMessage = string.Empty;          // 当前错误提示文本

    /// <summary>构造登录视图模型。</summary>
    public LoginViewModel(UserService userService, Action<UserAccount> onSuccess)
    {
        _userService = userService;
        _onSuccess = onSuccess;

        LoginCommand = new RelayCommand(Login, null, "登录");
        LanguageManager.LanguageChanged += OnLanguageChanged;
    }

    public string UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    /// <summary>错误提示文本：为空时界面隐藏提示条。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError)); // 联动提示条可见性
            }
        }
    }

    /// <summary>是否有错误（绑定到提示条 Visibility）。</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>登录命令（按钮 / 回车触发）。</summary>
    public ICommand LoginCommand { get; }

    /// <summary>标题（用于窗口标题，随语言切换刷新）。</summary>
    public string AppTitle => LanguageManager.Get("L.App.Title");

    /// <summary>
    /// 执行登录：依次校验用户名、密码是否为空，再调用账户服务校验。
    /// </summary>
    private void Login()
    {
        if (string.IsNullOrWhiteSpace(UserName))
        {
            SetError("L.Login.EmptyUser");
            return;
        }

        if (string.IsNullOrEmpty(Password))
        {
            SetError("L.Login.EmptyPassword");
            return;
        }

        if (_userService.Validate(UserName, Password, out var user) && user is not null)
        {
            SetError(null);

            // 登录成功后立即清空内存中的密码，避免残留在界面
            Password = string.Empty;
            _onSuccess(user);
            return;
        }

        SetError("L.Login.Error");
    }

    /// <summary>设置错误提示（传 null 表示清除）。</summary>
    private void SetError(string? key)
    {
        _errorKey = key;
        ErrorMessage = key is null ? string.Empty : LanguageManager.Get(key);
    }

    /// <summary>语言切换：重新翻译当前错误提示与标题。</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_errorKey is not null)
        {
            ErrorMessage = LanguageManager.Get(_errorKey);
        }

        OnPropertyChanged(nameof(AppTitle));
    }
}
