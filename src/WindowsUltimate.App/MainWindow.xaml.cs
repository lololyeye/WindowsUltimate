using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using WindowsUltimate.App.ViewModels;

namespace WindowsUltimate.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        SourceInitialized += (_, _) => ApplyDwmAttributes();
        StateChanged += (_, _) =>
        {
            // With WindowChrome, a maximized window overhangs the screen by the resize border.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };
        _vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentKey)) SyncNav(this);
    }

    /// <summary>Keeps the sidebar selection in sync when navigation is triggered from code (e.g. a finding's action).</summary>
    private void SyncNav(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton { GroupName: "nav" } rb) rb.IsChecked = (string)rb.Tag == _vm.CurrentKey;
            else SyncNav(child);
        }
    }

    private void OnNavItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb) rb.IsChecked = (string)rb.Tag == _vm.CurrentKey;
    }

    /// <summary>Navigation follows the checked item, so mouse, keyboard arrows and UI Automation all behave the same.</summary>
    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key }) _vm.Navigate(key);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void ApplyDwmAttributes()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2, caption = 0x000F0B09; // COLORREF 0x00BBGGRR for #090B0F
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));  // DWMWA_WINDOW_CORNER_PREFERENCE = round
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
