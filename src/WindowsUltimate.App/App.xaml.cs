using System.Text;
using System.Windows;
using System.Windows.Threading;
using WindowsUltimate.App.Localization;
using WindowsUltimate.App.Services;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // OEM code pages for DISM/chkdsk output
        // WPF bindings format with en-US unless told otherwise; follow the Windows regional format instead.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag)));
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Log.Info($"Starting Windows Ultimate {typeof(App).Assembly.GetName().Version} (elevated: {Elevation.IsElevated})");
        AppServices.Initialize();
        Loc.Instance.SetLanguage(AppServices.Settings.EffectiveLanguage);

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        _ = Updater.CheckAtStartupAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.Shutdown();
        Log.Info("Exited");
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception: " + e.Exception); // full stack: these are bugs
        Notifier.Error(e.Exception.Message);
        e.Handled = true;
    }
}
