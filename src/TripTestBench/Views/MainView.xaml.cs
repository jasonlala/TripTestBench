// ============================================================================
// 文件：MainView.xaml.cs
// 作用：主界面视图的代码后置（Code-Behind）。
//       主界面采用纯 MVVM 绑定，业务逻辑全部在 MainViewModel 中，
//       这里只保留两件事：
//       1) XAML 初始化；
//       2) 把“电流曲线”区域渲染为 PNG 字节流（保存记录时存入数据库）。
// ============================================================================

using System.Globalization;               // InvariantCulture 数值解析
using System.IO;                          // MemoryStream
using System.Windows.Controls;            // UserControl / TextBox
using System.Windows.Input;               // TextCompositionEventArgs / TextChangedEventArgs
using System.Windows.Media;               // VisualTreeHelper / PixelFormats
using System.Windows.Media.Imaging;       // RenderTargetBitmap / PngBitmapEncoder

namespace TripTestBench.Views;

/// <summary>主界面（试验台）视图。</summary>
public partial class MainView : UserControl
{
    /// <summary>时间框自动补冒号期间置 true，避免 TextChanged 递归触发。</summary>
    private bool _suppressTimeFormat;

    /// <summary>构造视图：加载 XAML 定义的控件树。</summary>
    public MainView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 设定电流输入限定：只允许数字与一个小数点、最多两位小数，
    /// 且数值不得超过 10（含 10）；非法按键直接被拦截，不进入文本框。
    /// </summary>
    private void SetCurrentBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;
        var caret = box.CaretIndex;
        var candidate = box.Text[..caret] + e.Text + box.Text[caret..];

        // 合法写法：整数最多 2 位（如 0~10）、或小数（整体最多两位小数）
        var patternOk = System.Text.RegularExpressions.Regex.IsMatch(
            candidate, @"^(\d{1,2}(\.\d{0,2})?|\.\d{0,2})$");

        var valueOk = !double.TryParse(
            candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value <= 10; // 能解析成数字时不得超过 10（含 10）

        e.Handled = !patternOk || !valueOk;
    }

    /// <summary>设定时间输入限定：只允许数字，冒号由自动格式化补齐。</summary>
    private void SetTimeBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(c => !char.IsDigit(c));

    /// <summary>
    /// 设定时间自动格式化：抽取已输入数字（最多 6 位）重排为 hh:mm:ss。
    /// 冒号始终由本方法补齐，因此用户无法删除冒号；光标保持在原数字位置。
    /// </summary>
    private void SetTimeBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTimeFormat)
        {
            return;
        }

        var box = (TextBox)sender;

        // 记录光标前的数字个数，重排后把光标放回相同数字位置
        var digitsBeforeCaret = box.Text[..Math.Min(box.CaretIndex, box.Text.Length)].Count(char.IsDigit);

        var digits = new string(box.Text.Where(char.IsDigit).ToArray());
        if (digits.Length > 6)
        {
            digits = digits[..6]; // 最多 hh:mm:ss 六位
        }

        var formatted = digits.Length switch
        {
            0 => string.Empty,
            <= 2 => digits,                            // hh
            <= 4 => digits.Insert(2, ":"),             // hh:mm
            _ => digits.Insert(4, ":").Insert(2, ":")  // hh:mm:ss
        };

        _suppressTimeFormat = true;
        box.Text = formatted;
        _suppressTimeFormat = false;

        var index = 0;
        while (index < formatted.Length && digitsBeforeCaret > 0)
        {
            if (char.IsDigit(formatted[index]))
            {
                digitsBeforeCaret--;
            }

            index++;
        }

        box.CaretIndex = index;
    }

    /// <summary>
    /// 把电流曲线卡片（标题 + 峰值电流 + 图表区域）渲染为 PNG 图片字节流。
    /// 由 MainViewModel 在“保存数据”时调用，结果写入数据库最后一列 ChartImage。
    /// </summary>
    /// <returns>PNG 字节；控件尚未布局完成时返回 null</returns>
    public byte[]? CaptureChartImage()
    {
        if (ChartCard is null)
        {
            return null;
        }

        var width = ChartCard.ActualWidth;
        var height = ChartCard.ActualHeight;
        if (width < 1 || height < 1)
        {
            return null; // 界面还没布局（例如从未显示过主界面）
        }

        // 按屏幕 DPI 渲染，保证在高分屏上截图也清晰
        var dpi = VisualTreeHelper.GetDpi(ChartCard);
        var pixelWidth = (int)Math.Round(width * dpi.DpiScaleX);
        var pixelHeight = (int)Math.Round(height * dpi.DpiScaleY);

        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);

        // 用 VisualBrush 把卡片重绘到一个与卡片同尺寸的矩形上再渲染：
        // 直接 Render(卡片) 会受窗口布局裁剪/偏移影响，导致截图缺一块；
        // VisualBrush 方式总是从控件自身 (0,0) 开始完整绘制。
        var paint = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = height,
            Fill = new VisualBrush(ChartCard)
        };
        paint.Arrange(new System.Windows.Rect(0, 0, width, height));
        bitmap.Render(paint);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
