using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace WindowsUltimate.App.Controls;

/// <summary>
/// Lightweight area/line chart drawn directly with StreamGeometry (no charting dependency).
/// Assign a new array to <see cref="Values"/> to redraw.
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

    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1) { DashStyle = new DashStyle([3, 4], 0) });

    private static Pen Freeze(Pen p)
    {
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 2 || h < 2) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable background

        if (ShowGrid)
        {
            for (var i = 1; i < 4; i++)
            {
                var y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
            }
        }

        var max = Maximum;
        if (AutoScale)
        {
            var all = (Values ?? []).Concat(SecondaryValues ?? []).DefaultIfEmpty(0).Max();
            max = Math.Max(1, NiceCeiling(all * 1.15));
        }

        if (SecondaryValues is { Count: > 1 } sec) DrawSeries(dc, sec, max, w, h, SecondaryStroke, fill: false);
        if (Values is { Count: > 1 } values) DrawSeries(dc, values, max, w, h, Stroke, fill: true);

        if (AutoScale && ShowGrid)
        {
            var label = new FormattedText(FormatScale(max), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(w - label.Width - 2, 0));
        }
    }

    public Func<double, string> ScaleFormatter { get; set; } = v => v.ToString("0");

    private string FormatScale(double v) => ScaleFormatter(v);

    private static double NiceCeiling(double v)
    {
        if (v <= 0) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var n = v / mag;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag;
    }

    private void DrawSeries(DrawingContext dc, IReadOnlyList<double> values, double max, double w, double h, Brush stroke, bool fill)
    {
        var cap = Math.Max(Capacity, values.Count);
        var step = w / (cap - 1);
        var offset = (cap - values.Count) * step;
        Point P(int i) => new(offset + i * step, h - Math.Clamp(values[i] / max, 0, 1) * (h - 2) - 1);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            for (var i = 1; i < values.Count; i++) ctx.LineTo(P(i), true, true);
        }
        line.Freeze();

        if (fill && stroke is SolidColorBrush s)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(offset, h), true, true);
                for (var i = 0; i < values.Count; i++) ctx.LineTo(P(i), true, true);
                ctx.LineTo(new Point(offset + (values.Count - 1) * step, h), true, true);
            }
            area.Freeze();
            var gradient = new LinearGradientBrush(Color.FromArgb(70, s.Color.R, s.Color.G, s.Color.B), Color.FromArgb(0, s.Color.R, s.Color.G, s.Color.B), 90);
            gradient.Freeze();
            dc.DrawGeometry(gradient, null, area);
        }

        var pen = new Pen(stroke, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>Circular gauge with a rounded progress arc (used for the health score and usage dials).</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(nameof(Sweep), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(270.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size / 2 - Thickness / 2;
        var start = 90 + (360 - Sweep) / 2; // gap at the bottom

        var track = new Pen(TrackBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(center, r, start, Sweep));

        var fraction = Math.Clamp(Maximum <= 0 ? 0 : Value / Maximum, 0, 1);
        if (fraction > 0.001)
        {
            var pen = new Pen(Brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Arc(center, r, start, Sweep * fraction));
        }
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
