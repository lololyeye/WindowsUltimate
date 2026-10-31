using System.Windows;
using WindowsUltimate.App.Localization;

namespace WindowsUltimate.App.Dialogs;

public partial class ConfirmDialog : DialogBase
{
    private ConfirmDialog() => InitializeComponent();

    /// <summary>
    /// Generic confirmation. <paramref name="acknowledge"/> adds a required checkbox — the "second confirmation"
    /// used for risky or irreversible operations.
    /// </summary>
    public static bool Show(string title, string message, IEnumerable<string>? details = null, string? confirmText = null,
        bool danger = false, string? acknowledge = null, bool infoOnly = false)
    {
        var d = new ConfirmDialog();
        d.TitleText.Text = title;
        d.MessageText.Text = message;
        var lines = details?.ToList();
        if (lines is { Count: > 0 })
        {
            d.DetailsHost.Visibility = Visibility.Visible;
            d.DetailsText.Text = string.Join("\n", lines);
        }
        d.ConfirmButton.Content = confirmText ?? Loc.Get("Continue");
        if (danger)
        {
            d.ConfirmButton.Style = (Style)Application.Current.Resources["Btn.Danger"];
            d.IconHost.Background = (System.Windows.Media.Brush)Application.Current.Resources["B.WarningSoft"];
            d.Glyph.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["B.Warning"];
            d.Glyph.Text = "";
        }
        if (acknowledge is not null)
        {
            d.AckBox.Visibility = Visibility.Visible;
            d.AckText.Text = acknowledge;
            d.ConfirmButton.IsEnabled = false;
        }
        if (infoOnly)
        {
            d.CancelButton.Visibility = Visibility.Collapsed;
            d.ConfirmButton.Content = confirmText ?? Loc.Get("Close");
        }
        return d.ShowDialog() == true;
    }

    private void OnAck(object sender, RoutedEventArgs e) => ConfirmButton.IsEnabled = AckBox.IsChecked == true;
    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
