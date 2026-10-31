using WindowsUltimate.Core.Backup;
using WindowsUltimate.Core.Diagnostics;
using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Monitoring;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.App.Services;

public sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public int MonitorIntervalMs { get; set; } = 1000;
    public bool CreateRestorePointByDefault { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public bool Translucency { get; set; } = true;

    public static AppSettings Load() => JsonStore.Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
    public void Save() => JsonStore.Save(AppPaths.SettingsFile, this);
}

/// <summary>Application-wide services. Created once at startup; hardware detection runs in the background.</summary>
public static class AppServices
{
    public static AppSettings Settings { get; private set; } = new();
    public static ChangeJournal Journal { get; private set; } = null!;
    public static OptimizationEngine Engine { get; private set; } = null!;
    public static ProfileManager Profiles { get; private set; } = null!;
    public static PerformanceMonitor Monitor { get; private set; } = null!;
    public static Task<SystemProfile> ProfileTask { get; private set; } = null!;
    public static SystemProfile? Profile => ProfileTask.IsCompletedSuccessfully ? ProfileTask.Result : null;

    public static ScanReport? LastScan { get; private set; }
    public static SystemAnalyzer.Context? LastScanContext { get; private set; }
    public static event Action? ScanCompleted;
    public static event Action? ConfigurationChanged;

    public static void Initialize()
    {
        Settings = AppSettings.Load();
        Journal = new ChangeJournal();
        Engine = new OptimizationEngine(Journal, TweakCatalog.Create());
        Profiles = new ProfileManager(Engine);
        Monitor = new PerformanceMonitor();
        Monitor.SetInterval(TimeSpan.FromMilliseconds(Settings.MonitorIntervalMs));
        ProfileTask = HardwareDetector.DetectAsync();
        _ = ProfileTask.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) Monitor.VramTotalBytes = t.Result.PrimaryGpu?.VramBytes ?? 0;
        }, TaskScheduler.Default);
    }

    public static async Task<ScanReport> RunScanAsync(bool full, IProgress<string>? progress = null)
    {
        var profile = await ProfileTask;
        var (report, ctx) = await SystemAnalyzer.ScanAsync(profile, full, progress);
        LastScan = report;
        LastScanContext = ctx;
        ScanCompleted?.Invoke();
        return report;
    }

    /// <summary>Raised after any change to the system configuration so pages can refresh their state.</summary>
    public static void NotifyConfigurationChanged() => ConfigurationChanged?.Invoke();

    public static void Shutdown()
    {
        Monitor.Dispose();
    }
}

/// <summary>In-app notifications (toast area of the main window).</summary>
public static class Notifier
{
    public enum Kind { Info, Success, Warning, Error }

    public static event Action<string, Kind>? Raised;

    public static void Info(string message) => Raised?.Invoke(message, Kind.Info);
    public static void Success(string message) => Raised?.Invoke(message, Kind.Success);
    public static void Warning(string message) => Raised?.Invoke(message, Kind.Warning);
    public static void Error(string message) => Raised?.Invoke(message, Kind.Error);
}

/// <summary>Cross-page navigation requests (e.g. a dashboard finding pointing to the Storage page).</summary>
public static class Navigator
{
    public static event Action<string>? Requested;
    public static void Go(string pageKey) => Requested?.Invoke(pageKey);
}
