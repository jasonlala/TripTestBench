// ============================================================================
// 文件：RelayCommand.cs
// 作用：提供两种 ICommand 实现，供按钮绑定：
//       1) RelayCommand       —— 同步命令（导航、退出登录等）；
//       2) AsyncRelayCommand  —— 异步命令（充电、开始、停止、保存），执行期间自动禁用按钮，
//                                防止操作员连点造成重复下发指令。
//       两者都支持传入“命令名称”：每次点击写入 USER 日志，命令抛异常时写 ERROR 日志，
//       现场拿到日志即可还原“谁在什么时候点了什么按钮、结果如何”。
// ============================================================================

using System.Windows.Input;   // ICommand
using TripTestBench.Services; // Logger：用户操作与命令异常统一记录

namespace TripTestBench.Mvvm;

/// <summary>同步命令：把委托包装成 ICommand。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;          // 执行逻辑
    private readonly Func<object?, bool>? _canExecute;  // 可用性判断
    private readonly string? _name;                     // 命令名称（写日志用，可空）

    /// <summary>无参构造：适合界面直接绑定。</summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null, string? name = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute(), name)
    {
    }

    /// <summary>带参构造：可接收 CommandParameter。</summary>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null, string? name = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _name = name;
    }

    /// <summary>可用性变化事件（由 RaiseCanExecuteChanged 手动触发）。</summary>
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <summary>执行命令：先写用户操作日志，异常记录堆栈后继续抛出（由全局异常处理弹窗）。</summary>
    public void Execute(object? parameter)
    {
        if (_name is not null)
        {
            Logger.User($"点击「{_name}」");
        }

        try
        {
            _execute(parameter);
        }
        catch (Exception ex)
        {
            Logger.Error($"命令「{_name ?? "未命名"}」执行失败", ex);
            throw;
        }
    }

    /// <summary>通知界面重新计算按钮可用性（界面自动变灰 / 恢复）。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>异步命令：执行期间自动禁用自身，防止重复触发。</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;      // 异步执行逻辑
    private readonly Func<object?, bool>? _canExecute;  // 可用性判断
    private readonly string? _name;                     // 命令名称（写日志用，可空）
    private bool _isRunning;                            // 是否正在执行

    /// <summary>无参构造：适合界面直接绑定。</summary>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, string? name = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute(), name)
    {
    }

    /// <summary>带参构造：可接收 CommandParameter。</summary>
    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null, string? name = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _name = name;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _isRunning;

    /// <summary>执行中一律不可用，避免连点重复下发 PLC 指令。</summary>
    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    /// <summary>执行命令：记录用户操作，内部捕获异常保证界面线程不因异常退出。</summary>
    public async void Execute(object? parameter)
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();

        if (_name is not null)
        {
            Logger.User($"点击「{_name}」");
        }

        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            Logger.Error($"命令「{_name ?? "未命名"}」执行失败", ex);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
