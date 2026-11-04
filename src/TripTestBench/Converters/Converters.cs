// ============================================================================
// 文件：Converters.cs
// 作用：XAML 绑定用的值转换器集合。
//       把 ViewModel 中的布尔值 / 数字转换成界面需要的文字、颜色与可见性，
//       避免在 ViewModel 里出现界面元素类型（保持 MVVM 分层清晰）。
// ============================================================================

using System.Globalization;   // CultureInfo：转换器签名要求
using System.Windows;         // Visibility
using System.Windows.Data;    // IValueConverter
using System.Windows.Media;   // Brush
using TripTestBench.Localization; // 多语言文案

namespace TripTestBench.Converters;

/// <summary>bool → 合格 / 不合格文本（跟随当前语言）。</summary>
public sealed class PassedToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? LanguageManager.Get("L.Msg.Pass") : LanguageManager.Get("L.Msg.Fail");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>bool → 合格绿色 / 不合格红色。</summary>
public sealed class PassedToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? FindBrush("B.Success") : FindBrush("B.Danger");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    /// <summary>从主题资源字典取画刷，取不到时回退为灰色（避免绑定崩溃）。</summary>
    internal static Brush FindBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

/// <summary>bool → Visibility；参数传 "invert" 时取反。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;

        // 支持取反：例如“未连接时才显示提示”
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>工位编号 → “工位 1” / “Sta. 1”（跟随语言）。</summary>
public sealed class StationToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => LanguageManager.Format("L.Field.StationN", value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>用于分段按钮：当前值与参数相等时返回 true（选中）。</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 只处理“被选中”的情况，取消选中时不回写，避免把值改回 0
        if (value is true && parameter is not null && int.TryParse(parameter.ToString(), out var result))
        {
            return result;
        }

        return Binding.DoNothing;
    }
}

/// <summary>错误标记 → 提示文字颜色（红色 / 次要灰）。</summary>
public sealed class ErrorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? PassedToBrushConverter.FindBrush("B.Danger")
            : PassedToBrushConverter.FindBrush("B.TextSecondary");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
