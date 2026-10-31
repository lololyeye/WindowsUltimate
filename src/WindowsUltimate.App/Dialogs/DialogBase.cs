using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WindowsUltimate.App.Controls;

namespace WindowsUltimate.App.Dialogs;

/// <summary>Frameless glass dialog window shared by all confirmation dialogs. Dims its owner and scales in.</summary>
public class DialogBase : Window
{
    public DialogBase()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var owner = Application.Current?.MainWindow;
        if (owner is not null && owner.IsVisible && owner != this) Owner = owner;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; }
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) DragMove();
        };
        Loaded += (_, _) =>
        {
            (Owner as MainWindow)?.ShowScrim(true);
            if (Content is FrameworkElement root) Appear(root);
        };
        Closed += (_, _) => (Owner as MainWindow)?.ShowScrim(false);
    }

    /// <summary>Modal entrance: fade in while scaling from 96 % and settling from slightly below.</summary>
    private static void Appear(FrameworkElement root)
    {
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(0.96, 0.96);
        var shift = new TranslateTransform(0, 8);
        root.RenderTransform = new TransformGroup { Children = { scale, shift } };
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
        root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Fx.Fast));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, Fx.Base) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, Fx.Base) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Fx.Base) { EasingFunction = Fx.Ease });
    }
}
