// ============================================================================
// 文件：CurrentChart.cs
// 作用：实时电流曲线控件（零第三方依赖，纯 WPF DrawingContext 绘制）。
//       特性：
//       1) 自动 Y 轴量程（1 / 1.2 / 1.5 / 2 / 2.5 / 3 / 4 / 5 / 6 / 8 / 10 档“漂亮刻度”）；
//       2) X 轴自适应：数据少时按 5 秒窗口放大，数据多时按设定窗口（30 秒）滚动；
//       3) 曲线下方渐变填充 + 主线发光 + 最新点标记 + 最新值气泡；
//       4) 中英文切换、尺寸变化、数据变化时自动重绘。
// 使用：在 XAML 中绑定 Samples（ObservableCollection&lt;ChartSample&gt;）即可。
// ============================================================================

using System.Collections.ObjectModel;   // ObservableCollection：采样点集合
using System.Collections.Specialized;   // 集合变更通知
using System.Globalization;             // CultureInfo：数字与文字格式化
using System.Windows;                   // FrameworkElement / Rect / Point
using System.Windows.Media;             // DrawingContext / Brush / Pen / Geometry
using TripTestBench.Localization;       // 多语言：无数据提示文字
using TripTestBench.Models;             // ChartSample：曲线采样点

namespace TripTestBench.Controls;

/// <summary>
/// 实时电流曲线控件：自动坐标、滚动时间窗口、渐变填充与最新点标记。
/// 数据由 <see cref="Samples"/> 驱动，本项目按 1 秒一个点更新。
/// </summary>
public sealed class CurrentChart : FrameworkElement
{
    // ---------------- 字体与配色（全部冻结，避免每帧重复创建对象） ----------------
    private static readonly Typeface LabelTypeface =
        new(new FontFamily("Consolas, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x1B, 0x2C, 0x3E)));       // 网格线颜色
    private static readonly Brush AxisTextBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x5E, 0x78, 0x93)));    // 坐标文字颜色
    private static readonly Brush HintTextBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x46, 0x5C, 0x72)));    // 提示文字颜色
    private static readonly Pen GridPen = Freeze(new Pen(GridBrush, 1));                                                   // 实线（坐标轴 / 边框）
    private static readonly Pen DashPen = Freeze(new Pen(GridBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 5 }, 0) }); // 虚线（内部网格）

    private static readonly Color AccentColor = Color.FromRgb(0x00, 0xC8, 0xFF); // 曲线主色（青色）

    private bool _subscribed; // 是否已订阅语言切换事件（防止重复订阅）

    /// <summary>构造控件：启用像素对齐，并在加载/卸载时维护语言事件订阅。</summary>
    public CurrentChart()
    {
        SnapsToDevicePixels = true;

        // 控件显示时订阅语言切换（切换中英文后重绘“无数据”提示）
        Loaded += (_, _) =>
        {
            if (!_subscribed)
            {
                LanguageManager.LanguageChanged += OnLanguageChanged;
                _subscribed = true;
            }
        };

        // 控件卸载时取消订阅，避免内存泄漏
        Unloaded += (_, _) =>
        {
            if (_subscribed)
            {
                LanguageManager.LanguageChanged -= OnLanguageChanged;
                _subscribed = false;
            }
        };
    }

    // =====================================================================
    // 依赖属性：采样点集合
    // =====================================================================
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples),
        typeof(ObservableCollection<ChartSample>),
        typeof(CurrentChart),
        new PropertyMetadata(null, OnSamplesChanged));

    /// <summary>曲线数据源：每加入一个点，控件自动重绘。</summary>
    public ObservableCollection<ChartSample>? Samples
    {
        get => (ObservableCollection<ChartSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    // =====================================================================
    // 依赖属性：X 轴窗口长度（秒）
    // =====================================================================
    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds),
        typeof(double),
        typeof(CurrentChart),
        new PropertyMetadata(30d, (d, _) => ((CurrentChart)d).InvalidateVisual()));

    /// <summary>X 轴最大显示窗口（秒）：默认 30 秒，数据不足时自动缩小。</summary>
    public double WindowSeconds
    {
        get => (double)GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    /// <summary>数据源切换：解绑旧集合、绑定新集合，并立即重绘。</summary>
    private static void OnSamplesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (CurrentChart)d;

        if (e.OldValue is ObservableCollection<ChartSample> oldCollection)
        {
            oldCollection.CollectionChanged -= chart.OnCollectionChanged;
        }

        if (e.NewValue is ObservableCollection<ChartSample> newCollection)
        {
            newCollection.CollectionChanged += chart.OnCollectionChanged;
        }

        chart.InvalidateVisual();
    }

    /// <summary>采样点变化（新增 / 删除）→ 请求重绘。</summary>
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    /// <summary>语言切换 → 重绘（“无数据”提示文字需要跟随语言）。</summary>
    private void OnLanguageChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>控件尺寸变化 → 重绘，保证曲线铺满可用区域。</summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    /// <summary>
    /// 核心绘制流程：背景 → 计算坐标范围 → 画网格 → 画渐变填充 → 画曲线 → 画最新点与数值。
    /// </summary>
    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;

        // 尺寸过小时不绘制（避免除零与无效几何）
        if (width < 20 || height < 20)
        {
            return;
        }

        // 背景透明（由外层卡片提供深色底）
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        // ---------------- 1. 计算绘图区（左侧留 Y 轴刻度，底部留 X 轴刻度） ----------------
        const double marginLeft = 50;
        const double marginRight = 16;
        const double marginTop = 14;
        const double marginBottom = 30;

        var plot = new Rect(
            marginLeft,
            marginTop,
            Math.Max(10, width - marginLeft - marginRight),
            Math.Max(10, height - marginTop - marginBottom));

        // ---------------- 2. 统计数据范围（最大时间 / 最大电流） ----------------
        var samples = Samples;
        var hasData = samples is { Count: > 0 };

        double latestTime = 0;
        double maxValue = 0;
        if (hasData)
        {
            foreach (var s in samples!)
            {
                if (s.Time > latestTime)
                {
                    latestTime = s.Time;
                }

                if (s.Value > maxValue)
                {
                    maxValue = s.Value;
                }
            }
        }

        // Y 轴取“漂亮”步长，固定 5 段，避免出现 3.8 这类刻度
        var step = NiceStep(Math.Max(2d, maxValue * 1.15 / 5d));
        var yMax = step * 5;

        // X 轴：数据不足一个窗口时自适应缩放，数据变多后按窗口滚动
        var window = Math.Max(5d, WindowSeconds);
        var dataSpan = Math.Max(latestTime * 1.05, 5d);
        var span = Math.Min(window, Math.Max(5d, Math.Ceiling(dataSpan / 5d) * 5d));
        var endTime = Math.Max(span, latestTime);
        var startTime = endTime - span;

        // ---------------- 3. 画网格与坐标文字 ----------------
        DrawGrid(dc, plot, yMax, startTime, span, step);

        // 没有数据：显示居中提示
        if (!hasData)
        {
            DrawCenteredHint(dc, plot, LanguageManager.Get("L.Chart.NoData"));
            return;
        }

        // ---------------- 4. 取可见点（保留窗口左侧一个点，保证曲线从边界外连进来） ----------------
        var visible = new List<ChartSample>(samples!.Count);
        ChartSample? previous = null;
        foreach (var sample in samples)
        {
            if (sample.Time < startTime)
            {
                previous = sample;
                continue;
            }

            if (previous is not null)
            {
                visible.Add(previous.Value);
                previous = null;
            }

            visible.Add(sample);
        }

        if (visible.Count == 0)
        {
            DrawCenteredHint(dc, plot, LanguageManager.Get("L.Chart.NoData"));
            return;
        }

        // ---------------- 5. 坐标映射函数：时间 → 像素 X，电流 → 像素 Y ----------------
        double MapX(double t) => plot.Left + (t - startTime) / span * plot.Width;
        double MapY(double v) => plot.Bottom - Math.Clamp(v / yMax, 0, 1.05) * plot.Height;

        // 裁剪到绘图区，避免曲线溢出到坐标文字上
        dc.PushClip(new RectangleGeometry(plot));

        // ---------------- 6. 曲线下方渐变填充 ----------------
        var fillGeometry = new StreamGeometry();
        using (var ctx = fillGeometry.Open())
        {
            ctx.BeginFigure(new Point(MapX(visible[0].Time), plot.Bottom), true, true); // 从左下角起笔
            foreach (var sample in visible)
            {
                ctx.LineTo(new Point(MapX(sample.Time), MapY(sample.Value)), true, false);
            }

            ctx.LineTo(new Point(MapX(visible[^1].Time), plot.Bottom), true, false);    // 回到右下角闭合
        }

        fillGeometry.Freeze();

        var fillBrush = new LinearGradientBrush(
            Color.FromArgb(0x55, AccentColor.R, AccentColor.G, AccentColor.B), // 顶部：半透明青
            Color.FromArgb(0x00, AccentColor.R, AccentColor.G, AccentColor.B), // 底部：全透明
            new Point(0, 0),
            new Point(0, 1));
        fillBrush.Freeze();
        dc.DrawGeometry(fillBrush, null, fillGeometry);

        // ---------------- 7. 曲线主线（外发光 + 实线） ----------------
        var lineGeometry = new StreamGeometry();
        using (var ctx = lineGeometry.Open())
        {
            var started = false;
            foreach (var sample in visible)
            {
                var point = new Point(MapX(sample.Time), MapY(sample.Value));
                if (!started)
                {
                    ctx.BeginFigure(point, false, false);
                    started = true;
                }
                else
                {
                    ctx.LineTo(point, true, false);
                }
            }
        }

        lineGeometry.Freeze();

        var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(0x33, AccentColor.R, AccentColor.G, AccentColor.B)), 6)
        {
            LineJoin = PenLineJoin.Round
        };
        glowPen.Freeze();

        var linePen = new Pen(new SolidColorBrush(AccentColor), 2) { LineJoin = PenLineJoin.Round };
        linePen.Freeze();

        dc.DrawGeometry(null, glowPen, lineGeometry);  // 外发光
        dc.DrawGeometry(null, linePen, lineGeometry);  // 主线条

        // ---------------- 8. 最新点标记（光晕 + 实心点） ----------------
        var latest = visible[^1];
        var latestPoint = new Point(MapX(latest.Time), MapY(latest.Value));
        var markerBrush = new SolidColorBrush(AccentColor);
        markerBrush.Freeze();
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(0x33, AccentColor.R, AccentColor.G, AccentColor.B)),
            null,
            latestPoint,
            9,
            9);
        dc.DrawEllipse(markerBrush, null, latestPoint, 3.2, 3.2);

        dc.Pop();

        // ---------------- 9. 最新值气泡（显示在点旁边） ----------------
        DrawLatestValue(dc, plot, latestPoint, latest.Value);
    }

    /// <summary>
    /// 绘制网格与坐标文字：
    /// Y 轴 5 段（电流值），X 轴 5 段（时间秒数），单位 kA 水印放在绘图区内。
    /// </summary>
    private void DrawGrid(DrawingContext dc, Rect plot, double yMax, double startTime, double window, double step)
    {
        // ---------- 水平网格 + Y 轴刻度 ----------
        const int horizontalSteps = 5;
        var format = step >= 10 ? "F0" : step >= 1 ? "F1" : "F2"; // 根据步长决定小数位

        for (var i = 0; i <= horizontalSteps; i++)
        {
            var value = yMax * i / horizontalSteps;
            var y = plot.Bottom - plot.Height * i / horizontalSteps;

            // 最底部用实线，其余用虚线
            dc.DrawLine(i == 0 ? GridPen : DashPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var text = BuildText(value.ToString(format, CultureInfo.InvariantCulture));
            dc.DrawText(text, new Point(plot.Left - text.Width - 8, y - text.Height / 2));
        }

        // ---------- 垂直网格 + X 轴刻度 ----------
        const int verticalSteps = 5;
        for (var i = 0; i <= verticalSteps; i++)
        {
            var x = plot.Left + plot.Width * i / verticalSteps;
            dc.DrawLine(i == verticalSteps ? GridPen : DashPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            var seconds = startTime + window * i / verticalSteps;
            var text = BuildText(seconds.ToString("F0", CultureInfo.InvariantCulture) + "s");

            // 首尾标签贴边对齐，避免超出绘图区被裁剪
            var textX = x - text.Width / 2;
            if (i == 0)
            {
                textX = plot.Left;
            }
            else if (i == verticalSteps)
            {
                textX = plot.Right - text.Width;
            }

            dc.DrawText(text, new Point(textX, plot.Bottom + 8));
        }

        // 单位水印放在绘图区内，避免与顶部刻度重叠
        var unit = BuildText("kA");
        dc.DrawText(unit, new Point(plot.Left + 6, plot.Top + 2));
    }

    // “漂亮”步长候选值：让刻度落在 1 / 1.2 / 1.5 / 2 / 2.5 / 3 / 4 / 5 / 6 / 8 / 10 上
    private static readonly double[] NiceSteps = { 1d, 1.2d, 1.5d, 2d, 2.5d, 3d, 4d, 5d, 6d, 8d, 10d };

    /// <summary>把任意步长规整为“漂亮”步长（例如 2.31 → 2.5）。</summary>
    private static double NiceStep(double raw)
    {
        var exponent = Math.Floor(Math.Log10(Math.Max(raw, 1e-6))); // 数量级
        var magnitude = Math.Pow(10, exponent);                     // 10 的幂
        var normalized = raw / magnitude;                           // 归一化到 1 ~ 10

        foreach (var candidate in NiceSteps)
        {
            if (normalized <= candidate + 1e-9)
            {
                return candidate * magnitude;
            }
        }

        return 10d * magnitude;
    }

    /// <summary>在最新点旁边绘制电流数值气泡（靠近右边界时自动翻到左侧）。</summary>
    private void DrawLatestValue(DrawingContext dc, Rect plot, Point latestPoint, double value)
    {
        var text = BuildText(value.ToString("F3", CultureInfo.InvariantCulture) + " kA", true);

        // 默认画在点的右侧；右侧放不下则画到左侧
        var x = latestPoint.X + 12;
        if (x + text.Width > plot.Right)
        {
            x = latestPoint.X - text.Width - 12;
        }

        // 垂直方向限制在绘图区内
        var y = Math.Clamp(latestPoint.Y - text.Height - 10, plot.Top + 2, plot.Bottom - text.Height - 2);
        dc.DrawText(text, new Point(x, y));
    }

    /// <summary>在绘图区正中绘制提示文字（例如“暂无数据”）。</summary>
    private void DrawCenteredHint(DrawingContext dc, Rect plot, string message)
    {
        var text = new FormattedText(
            message,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            12,
            HintTextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point(plot.Left + (plot.Width - text.Width) / 2, plot.Top + (plot.Height - text.Height) / 2));
    }

    /// <summary>创建坐标文字（highlight=true 时用青色高亮，用于最新值显示）。</summary>
    private FormattedText BuildText(string value, bool highlight = false)
    {
        return new FormattedText(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            highlight ? 13 : 11,
            highlight ? new SolidColorBrush(AccentColor) : AxisTextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    /// <summary>冻结画刷 / 画笔，提升渲染性能（避免每帧复制）。</summary>
    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze)
        {
            freezable.Freeze();
        }

        return freezable;
    }
}
