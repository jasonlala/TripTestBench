// ============================================================================
// 文件：ChartPreviewWindow.xaml.cs
// 作用：曲线截图预览窗口。
//       数据查询界面双击记录（或点“查看曲线”）时，从数据库读出 PNG 并在此显示。
// ============================================================================

using System.IO;                            // MemoryStream
using System.Windows;                       // Window / RoutedEventArgs
using System.Windows.Input;                 // MouseButtonEventArgs
using System.Windows.Media.Imaging;         // BitmapImage
using TripTestBench.Services;               // Logger

namespace TripTestBench.Views;

/// <summary>曲线截图预览窗口。</summary>
public partial class ChartPreviewWindow : Window
{
    /// <summary>构造窗口：加载 XAML。</summary>
    public ChartPreviewWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 弹出预览窗口。
    /// </summary>
    /// <param name="png">数据库里保存的 PNG 字节</param>
    /// <param name="title">窗口标题（产品名称 / 编号 + 时间）</param>
    /// <param name="owner">父窗口，未指定时用主窗口</param>
    public static void ShowPreview(byte[] png, string title, Window? owner = null)
    {
        try
        {
            var window = new ChartPreviewWindow
            {
                Owner = owner ?? Application.Current?.MainWindow
            };

            window.TitleText.Text = title;
            window.PreviewImage.Source = LoadImage(png);
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            // 记录完整异常（含 InnerException），便于现场排查 XAML / 图片解码问题
            Logger.Error("打开曲线截图预览失败：" + ex, ex);
        }
    }

    /// <summary>把 PNG 字节解码为可显示的位图（OnLoad 保证流释放后依然可用）。</summary>
    private static BitmapImage? LoadImage(byte[] bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // 立即解码，随后即可释放流
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();                                // 跨线程共享安全
        return image;
    }

    /// <summary>标题栏拖动窗口。</summary>
    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 拖动过程中释放鼠标可能抛异常，忽略
            }
        }
    }

    /// <summary>关闭预览。</summary>
    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
