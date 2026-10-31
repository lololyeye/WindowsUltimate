using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WindowsUltimate.App.Controls;

public enum HoverMode { None, Glow, Lift }

/// <summary>
/// Motion helpers of the design system. Styles freeze their brushes, so animations work on per-element clones
/// created here: <c>Glow</c> brightens a panel's luminous edge, <c>Lift</c> also raises the element by a pixel or two
/// and lights its fill. Everything runs once per interaction; nothing loops.
/// </summary>
public static class Fx
{
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(180));
    public static readonly Duration Base = new(TimeSpan.FromMilliseconds(240));
    public static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    private const double RestOpacity = 0.55;

    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached("Hover", typeof(HoverMode), typeof(Fx),
        new PropertyMetadata(HoverMode.None, OnHoverChanged));

    public static HoverMode GetHover(DependencyObject d) => (HoverMode)d.GetValue(HoverProperty);
    public static void SetHover(DependencyObject d, HoverMode value) => d.SetValue(HoverProperty, value);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(HoverState), typeof(Fx));

    private sealed class HoverState
    {
        public Brush? Edge;
        public Brush? Fill;
        public TranslateTransform? Lift;
    }

    private static void OnHoverChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border b) return;
        b.MouseEnter -= OnEnter;
        b.MouseLeave -= OnLeave;
        b.Loaded -= OnLoaded;
        if ((HoverMode)e.NewValue == HoverMode.None) return;
        b.MouseEnter += OnEnter;
        b.MouseLeave += OnLeave;
        b.Loaded += OnLoaded;
        if (b.IsLoaded) Prepare(b);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Prepare((Border)sender);

    /// <summary>Swaps the style's frozen brushes for animatable clones (once per element).</summary>
    private static void Prepare(Border b)
    {
        if (b.GetValue(StateProperty) is HoverState) return;
        var state = new HoverState();
        if (b.BorderBrush is { } edge && !BindingOperations.IsDataBound(b, Border.BorderBrushProperty))
        {
            state.Edge = edge.CloneCurrentValue();
            state.Edge.Opacity = RestOpacity;
            b.BorderBrush = state.Edge;
        }
        if (GetHover(b) == HoverMode.Lift)
        {
            if (b.Background is { } fill && !BindingOperations.IsDataBound(b, Border.BackgroundProperty))
            {
                state.Fill = fill.CloneCurrentValue();
                state.Fill.Opacity = RestOpacity;
                b.Background = state.Fill;
            }
            if (b.RenderTransform is not TranslateTransform)
            {
                state.Lift = new TranslateTransform();
                b.RenderTransform = state.Lift;
            }
        }
        b.SetValue(StateProperty, state);
    }

    private static void OnEnter(object sender, MouseEventArgs e) => Animate((Border)sender, hovered: true);
    private static void OnLeave(object sender, MouseEventArgs e) => Animate((Border)sender, hovered: false);

    private static void Animate(Border b, bool hovered)
    {
        Prepare(b);
        if (b.GetValue(StateProperty) is not HoverState s) return;
        var opacity = hovered ? 1 : RestOpacity;
        s.Edge?.BeginAnimation(Brush.OpacityProperty, To(opacity, Fast));
        s.Fill?.BeginAnimation(Brush.OpacityProperty, To(opacity, Fast));
        s.Lift?.BeginAnimation(TranslateTransform.YProperty, To(hovered ? -1.5 : 0, Base));
    }

    public static DoubleAnimation To(double value, Duration duration) => new(value, duration) { EasingFunction = Ease };

    /// <summary>Entrance used for page changes and toasts: fade in while settling from <paramref name="offsetY"/> to 0.</summary>
    public static void Enter(UIElement element, double offsetY = 10, double offsetX = 0)
    {
        if (element.RenderTransform is not TranslateTransform t || t.IsFrozen)
        {
            t = new TranslateTransform();
            element.RenderTransform = t;
        }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Base) { EasingFunction = Ease });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, Base) { EasingFunction = Ease });
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offsetX, 0, Base) { EasingFunction = Ease });
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

/// <summary>Loading placeholder with a soft shimmer. The shimmer only runs while the skeleton is visible.</summary>
public sealed class Skeleton : Border
{
    private readonly TranslateTransform _shift = new();
    private readonly LinearGradientBrush _shine;

    public Skeleton()
    {
        CornerRadius = new CornerRadius(6);
        Background = new SolidColorBrush(Color.FromArgb(0x12, 255, 255, 255));
        _shine = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            RelativeTransform = _shift,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.3),
                new GradientStop(Color.FromArgb(0x16, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.7),
            },
        };
        var shine = new Border { Background = _shine };
        shine.SetBinding(CornerRadiusProperty, new Binding(nameof(CornerRadius)) { Source = this });
        Child = shine;
        IsVisibleChanged += (_, _) => Run(IsVisible);
    }

    private void Run(bool on)
    {
        if (!on)
        {
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }
        _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, new Duration(TimeSpan.FromSeconds(1.6)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }
}
