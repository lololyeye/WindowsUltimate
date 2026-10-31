using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.App.Views;

public partial class MonitorView : System.Windows.Controls.UserControl
{
    public MonitorView()
    {
        InitializeComponent();
        DiskChart.ScaleFormatter = NetChart.ScaleFormatter = v => Format.Rate(v);
    }
}
