using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsUltimate.App.Dialogs;

/// <summary>Frameless dark dialog window shared by all confirmation dialogs.</summary>
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
    }
}
