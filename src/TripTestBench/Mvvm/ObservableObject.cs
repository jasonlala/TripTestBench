// ============================================================================
// 文件：ObservableObject.cs
// 作用：MVVM 的基类，实现 INotifyPropertyChanged。
//       所有 ViewModel 继承本类后，属性赋值通过 SetProperty 完成即可自动刷新界面。
// ============================================================================

using System.ComponentModel;      // INotifyPropertyChanged
using System.Runtime.CompilerServices; // CallerMemberName：自动获取属性名

namespace TripTestBench.Mvvm;

/// <summary>可观察对象基类：提供属性变更通知的通用实现。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>属性变更事件（WPF 绑定引擎监听此事件刷新界面）。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>触发属性变更通知（不传参数时自动使用调用方属性名）。</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 赋值并在值确实变化时通知界面。
    /// </summary>
    /// <returns>true = 值发生变化（调用方可据此联动其他属性）。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        // 值相同则不通知，避免无意义的界面重绘
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
