using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;
using WindowsUltimate.App.Controls;
using WindowsUltimate.App.Services;
using WindowsUltimate.App.ViewModels;

namespace WindowsUltimate.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private WindowState _lastState = WindowState.Normal;
    private int _scrimDepth;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        SourceInitialized += (_, _) =>
        {
            ApplyDwmAttributes();
            SetTranslucency(AppServices.Settings.Translucency);
        };
        StateChanged += OnStateChanged;
        _vm.PropertyChanged += OnVmChanged;
    }

    /// <summary>True when the native acrylic backdrop is active (Windows 11 22H2+, transparency effects on).</summary>
    public bool IsTranslucent { get; private set; }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // With WindowChrome, a maximized window overhangs the screen by the resize border.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";

        // Nothing needs to sample or animate while the window is minimized.
        if (WindowState == WindowState.Minimized) _vm.Current?.OnDeactivated();
        else if (_lastState == WindowState.Minimized) _vm.Current?.OnActivated();
        _lastState = WindowState;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentKey)) SyncNav(this);
        if (e.PropertyName == nameof(MainViewModel.Current))
        {
            // Page transition: header and page settle in together once the new view is built.
            Dispatcher.BeginInvoke(() =>
            {
                Fx.Enter(PageHost, offsetY: 12);
                Fx.Enter(PageHeader, offsetY: 6);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Keeps the sidebar selection in sync when navigation is triggered from code (e.g. a finding's action).</summary>
    private void SyncNav(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
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

    private void OnToastLoaded(object sender, RoutedEventArgs e) => Fx.Enter((UIElement)sender, offsetY: 0, offsetX: 40);

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Dims the window behind a modal dialog. Calls nest (a dialog opened from a dialog).</summary>
    public void ShowScrim(bool show)
    {
        _scrimDepth = Math.Max(0, _scrimDepth + (show ? 1 : -1));
        var visible = _scrimDepth > 0;
        if (visible) Scrim.Visibility = Visibility.Visible;
        var fade = Fx.To(visible ? 1 : 0, Fx.Base);
        if (!visible) fade.Completed += (_, _) => { if (_scrimDepth == 0) Scrim.Visibility = Visibility.Collapsed; };
        Scrim.BeginAnimation(OpacityProperty, fade);
    }

    private void ApplyDwmAttributes()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    /// <summary>
    /// Turns the native acrylic backdrop on or off. Needs Windows 11 22H2 (build 22621) and the system
    /// "Transparency effects" setting; otherwise — or if DWM refuses — the window keeps its opaque atmosphere.
    /// </summary>
    public void SetTranslucency(bool requested)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var source = HwndSource.FromHwnd(hwnd);
        var chrome = WindowChrome.GetWindowChrome(this);
        var enable = requested && Environment.OSVersion.Version.Build >= 22621 && SystemTransparencyEnabled();

        if (enable)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            var type = DWMSBT_TRANSIENTWINDOW;
            enable = DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0
                     && DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0;
        }

        if (enable)
        {
            chrome.GlassFrameThickness = new Thickness(-1); // keeps WindowChrome from collapsing the extended frame
            Background = Brushes.Transparent;
            Atmosphere.Fill = (Brush)FindResource("B.L1");
        }
        else
        {
            var none = DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
            chrome.GlassFrameThickness = new Thickness(0);
            var caption = 0x000F0A08; // COLORREF 0x00BBGGRR for #080A0F
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            Background = (Brush)FindResource("B.Window");
            Atmosphere.Fill = (Brush)FindResource("B.L1Opaque");
        }
        IsTranslucent = enable;
    }

    private static bool SystemTransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int v || v != 0;
        }
        catch { return true; }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // acrylic

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
