using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WindowsUltimate.App.Controls;

/// <summary>
/// Lightweight area/line chart drawn directly with StreamGeometry (no charting dependency).
/// Assign a new array to <see cref="Values"/> to redraw. Charts step once per sample, like Task Manager: a slide-in
/// animation on every sample made WPF compose the whole window ~13 times a second for each live chart.
/// </summary>
public sealed class SparkChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(SparkChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SecondaryValuesProperty = DependencyProperty.Register(nameof(SecondaryValues), typeof(IReadOnlyList<double>), typeof(SparkChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SparkChart),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(nameof(AutoScale), typeof(bool), typeof(SparkChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(SparkChart),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SecondaryStrokeProperty = DependencyProperty.Register(nameof(SecondaryStroke), typeof(Brush), typeof(SparkChart),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(nameof(ShowGrid), typeof(bool), typeof(SparkChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(nameof(Capacity), typeof(int), typeof(SparkChart),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<double>? SecondaryValues { get => (IReadOnlyList<double>?)GetValue(SecondaryValuesProperty); set => SetValue(SecondaryValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool AutoScale { get => (bool)GetValue(AutoScaleProperty); set => SetValue(AutoScaleProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush SecondaryStroke { get => (Brush)GetValue(SecondaryStrokeProperty); set => SetValue(SecondaryStrokeProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    public Func<double, string> ScaleFormatter { get; set; } = v => v.ToString("0");

    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)), 1));
    private static readonly Pen GridPenMinor = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(9, 255, 255, 255)), 1) { DashStyle = new DashStyle([2, 4], 0) });
    private static readonly Brush ScaleBrush = Freeze(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)));
    private static readonly Typeface ScaleFace = new("Segoe UI Variable Small, Segoe UI");

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 2 || h < 2) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable background
        dc.PushClip(new RectangleGeometry(new Rect(0, -2, w, h + 4)));

        if (ShowGrid)
        {
            for (var i = 1; i < 4; i++)
            {
                var y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
            }
            for (var i = 1; i < 6; i++)
            {
                var x = Math.Round(w * i / 6) + 0.5;
                dc.DrawLine(GridPenMinor, new Point(x, 0), new Point(x, h));
            }
        }

        var max = Maximum;
        if (AutoScale)
        {
            var all = (Values ?? []).Concat(SecondaryValues ?? []).DefaultIfEmpty(0).Max();
            max = Math.Max(1, NiceCeiling(all * 1.15));
        }

        if (SecondaryValues is { Count: > 1 } sec) DrawSeries(dc, sec, max, w, h, SecondaryStroke, primary: false);
        if (Values is { Count: > 1 } values) DrawSeries(dc, values, max, w, h, Stroke, primary: true);
        dc.Pop();

        if (AutoScale && ShowGrid)
        {
            var label = new FormattedText(ScaleFormatter(max), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                ScaleFace, 10, ScaleBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(w - label.Width - 2, 0));
        }
    }

    private static double NiceCeiling(double v)
    {
        if (v <= 0) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var n = v / mag;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag;
    }

    private static Color ColorOf(Brush b) => b switch
    {
        SolidColorBrush s => s.Color,
        GradientBrush { GradientStops.Count: > 0 } g => g.GradientStops[0].Color,
        _ => Colors.DodgerBlue,
    };

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private void DrawSeries(DrawingContext dc, IReadOnlyList<double> values, double max, double w, double h, Brush stroke, bool primary)
    {
        var cap = Math.Max(Capacity, values.Count);
        var step = w / (cap - 1);
        var offset = (cap - values.Count) * step;
        Point P(int i) => new(offset + i * step, h - Math.Clamp(values[i] / max, 0, 1) * (h - 4) - 2);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            for (var i = 1; i < values.Count; i++) ctx.LineTo(P(i), true, true);
        }
        line.Freeze();

        var color = ColorOf(stroke);
        if (primary)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(offset, h), true, true);
                for (var i = 0; i < values.Count; i++) ctx.LineTo(P(i), true, true);
                ctx.LineTo(new Point(offset + (values.Count - 1) * step, h), true, true);
            }
            area.Freeze();
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(WithAlpha(color, 84), 0),
                    new GradientStop(WithAlpha(color, 26), 0.6),
                    new GradientStop(WithAlpha(color, 0), 1),
                },
            };
            gradient.Freeze();
            dc.DrawGeometry(gradient, null, area);

            // Glow: a wide, faint stroke under the line (cheaper than a blur effect).
            var glow = new Pen(new SolidColorBrush(WithAlpha(color, 40)), 6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            glow.Freeze();
            dc.DrawGeometry(null, glow, line);
        }

        var pen = new Pen(stroke, primary ? 1.8 : 1.4) { LineJoin = PenLineJoin.Round };
        if (!primary) pen.DashStyle = new DashStyle([3, 2], 0);
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        if (primary)
        {
            // Live point: halo + core on the newest sample.
            var last = P(values.Count - 1);
            dc.DrawEllipse(new SolidColorBrush(WithAlpha(color, 50)), null, last, 6, 6);
            dc.DrawEllipse(Brushes.White, new Pen(stroke, 1.5), last, 2.6, 2.6);
        }
    }
}

/// <summary>
/// Circular gauge with a luminous rounded arc (health score and usage dials). The arc eases to each new value;
/// the glow, tick ring and inner light are drawn once per value, there is no idle animation.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, OnValueChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(nameof(Sweep), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(270.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowTicksProperty = DependencyProperty.Register(nameof(ShowTicks), typeof(bool), typeof(RingGauge),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The value actually drawn; animated towards <see cref="Value"/>.</summary>
    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(nameof(DisplayValue), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }
    public bool ShowTicks { get => (bool)GetValue(ShowTicksProperty); set => SetValue(ShowTicksProperty, value); }
    public double DisplayValue { get => (double)GetValue(DisplayValueProperty); set => SetValue(DisplayValueProperty, value); }

    private static readonly Pen TickPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)), 1));
    private static readonly Pen TickPenMajor = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1.2));

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (RingGauge)d;
        var target = (double)e.NewValue;
        if (!g.IsLoaded)
        {
            // Animate from zero the first time the gauge is shown.
            g.Loaded -= g.OnFirstShow;
            g.Loaded += g.OnFirstShow;
            return;
        }
        if (!g.IsVisible)
        {
            g.BeginAnimation(DisplayValueProperty, null);
            g.DisplayValue = target;
            return;
        }
        // Live dials change every second: changes too small to see are set directly, and the easing stays short,
        // so a busy dial does not keep the renderer animating between samples.
        var delta = Math.Abs(target - g.DisplayValue) / Math.Max(1, g.Maximum);
        if (delta < 0.005)
        {
            g.BeginAnimation(DisplayValueProperty, null);
            g.DisplayValue = target;
            return;
        }
        var ms = Math.Clamp(delta * 1400, 220, 600);
        g.BeginAnimation(DisplayValueProperty, new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void OnFirstShow(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstShow;
        BeginAnimation(DisplayValueProperty, new DoubleAnimation(0, Value, new Duration(TimeSpan.FromMilliseconds(900)))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static Color ColorOf(Brush b) => b switch
    {
        SolidColorBrush s => s.Color,
        GradientBrush { GradientStops.Count: > 0 } g => g.GradientStops[^1].Color,
        _ => Colors.DodgerBlue,
    };

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Lighten(Color c, double t) =>
        Color.FromArgb(c.A, (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var t = Thickness;
        if (size <= t * 2) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var tickRoom = ShowTicks ? t * 0.9 + 6 : 0;
        var r = size / 2 - t / 2 - tickRoom;
        var start = 90 + (360 - Sweep) / 2; // gap at the bottom
        var fraction = Math.Clamp(Maximum <= 0 ? 0 : DisplayValue / Maximum, 0, 1);
        var color = ColorOf(Brush);

        // Inner light: a faint disc of the arc colour, stronger as the score rises.
        if (ShowTicks)
        {
            var inner = new RadialGradientBrush(WithAlpha(color, (byte)(18 + 22 * fraction)), WithAlpha(color, 0));
            inner.Freeze();
            dc.DrawEllipse(inner, null, center, r - t, r - t);
        }

        if (ShowTicks)
        {
            var outer = size / 2 - 1;
            for (var i = 0; i <= 50; i++)
            {
                var deg = (start + Sweep * i / 50) * Math.PI / 180;
                var major = i % 5 == 0;
                var len = major ? 6 : 3;
                var p1 = new Point(center.X + outer * Math.Cos(deg), center.Y + outer * Math.Sin(deg));
                var p2 = new Point(center.X + (outer - len) * Math.Cos(deg), center.Y + (outer - len) * Math.Sin(deg));
                dc.DrawLine(major ? TickPenMajor : TickPen, p1, p2);
            }
        }

        var track = new Pen(TrackBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(center, r, start, Sweep));

        if (fraction <= 0.001) return;
        var arc = Arc(center, r, start, Sweep * fraction);

        // Glow: two wide translucent passes beneath the arc.
        var glowWide = new Pen(new SolidColorBrush(WithAlpha(color, 22)), t * 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var glowNear = new Pen(new SolidColorBrush(WithAlpha(color, 46)), t * 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, glowWide, arc);
        dc.DrawGeometry(null, glowNear, arc);

        // Arc: deeper at the start, lighter towards the head, like light travelling along it.
        var gradient = new LinearGradientBrush(WithAlpha(color, 210), Lighten(color, 0.28), new Point(0, 1), new Point(1, 0));
        gradient.Freeze();
        var pen = new Pen(gradient, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, arc);

        // Specular highlight along the arc's outer edge.
        var sheen = new Pen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), Math.Max(1, t * 0.18)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, sheen, Arc(center, r + t * 0.28, start, Sweep * fraction));

        // Head: a bright point at the current value.
        var headDeg = (start + Sweep * fraction) * Math.PI / 180;
        var head = new Point(center.X + r * Math.Cos(headDeg), center.Y + r * Math.Sin(headDeg));
        dc.DrawEllipse(new SolidColorBrush(WithAlpha(Lighten(color, 0.6), 235)), null, head, t * 0.24, t * 0.24);
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        sweepDeg = Math.Min(sweepDeg, 359.99);
        Point At(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(startDeg), false, false);
            ctx.ArcTo(At(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
