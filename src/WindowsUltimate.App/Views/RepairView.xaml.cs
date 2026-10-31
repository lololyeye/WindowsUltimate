namespace WindowsUltimate.App.Views;

public partial class RepairView : System.Windows.Controls.UserControl
{
    public RepairView() => InitializeComponent();

    private void OnConsoleChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ConsoleBox.ScrollToEnd();
}
