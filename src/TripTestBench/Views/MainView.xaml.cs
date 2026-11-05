// ============================================================================
// 文件：MainView.xaml.cs
// 作用：主界面视图的代码后置（Code-Behind）。
//       主界面采用纯 MVVM 绑定，业务逻辑全部在 MainViewModel 中，
//       这里只保留两件事：
//       1) XAML 初始化；
//       2) 把“电流曲线”区域渲染为 PNG 字节流（保存记录时存入数据库）。
// ============================================================================

using System.IO;                          // MemoryStream
using System.Windows.Controls;            // UserControl
using System.Windows.Media;               // VisualTreeHelper / PixelFormats
using System.Windows.Media.Imaging;       // RenderTargetBitmap / PngBitmapEncoder

namespace TripTestBench.Views;

/// <summary>主界面（试验台）视图。</summary>
public partial class MainView : UserControl
{
    /// <summary>构造视图：加载 XAML 定义的控件树。</summary>
    public MainView()
    {
        InitializeComponent();
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
