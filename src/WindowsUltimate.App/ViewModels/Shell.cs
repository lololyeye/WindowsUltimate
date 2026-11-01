using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using WindowsUltimate.App.Localization;
using WindowsUltimate.App.Mvvm;
using WindowsUltimate.App.Services;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.App.ViewModels;

/// <summary>Base for every page: localized title/subtitle, busy state, lifecycle hooks.</summary>
public abstract class PageViewModel : ObservableObject
{
    private bool _loaded;
    private bool _isBusy;
    private string _busyText = "";

    protected PageViewModel()
    {
        // Weak subscription: pages dropped after a language change must not be kept alive by Loc.
        PropertyChangedEventManager.AddHandler(Loc.Instance, (_, _) => OnPropertiesChanged(nameof(Title), nameof(Subtitle)), "");
    }

    public abstract string Key { get; }
    public string Title => Loc.Get($"Page_{Key}");
    public string Subtitle => Loc.Get($"Page_{Key}_Sub");

    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }
    public string BusyText { get => _busyText; set => Set(ref _busyText, value); }

    public async Task ActivateAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            try { await LoadAsync(); }
            catch (Exception ex)
            {
                Log.Error($"Loading page {Key} failed", ex);
                Notifier.Error(ex.Message);
            }
        }
        OnActivated();
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;
    public virtual void OnActivated() { }
    public virtual void OnDeactivated() { }

    protected async Task Busy(string text, Func<Task> work)
    {
        IsBusy = true;
        BusyText = text;
        try { await work(); }
        finally { IsBusy = false; BusyText = ""; }
    }

    protected IProgress<string> BusyProgress => new Progress<string>(s => BusyText = s);
}

public sealed record NavItem(string Key, string Glyph, Func<PageViewModel> Factory);

public sealed record NavGroup(string HeaderKey, IReadOnlyList<NavItem> Items)
{
    public string Header => Loc.Get(HeaderKey);
}

public sealed class Toast
{
    public required string Message { get; init; }
    public required Notifier.Kind Kind { get; init; }
    public string Glyph => Kind switch
    {
        Notifier.Kind.Success => "",
        Notifier.Kind.Warning => "",
        Notifier.Kind.Error => "",
        _ => "",
    };
    public string Severity => Kind switch
    {
        Notifier.Kind.Success => "good",
        Notifier.Kind.Warning => "warn",
        Notifier.Kind.Error => "bad",
        _ => "info",
    };
}

public sealed class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, PageViewModel> _pages = new();
    private PageViewModel? _current;
    private string _currentKey = "";

    public MainViewModel()
    {
        Groups =
        [
            new("NavOverview",
            [
                new("dashboard", "", () => new DashboardViewModel()),
                new("monitor", "", () => new MonitorViewModel()),
                new("benchmark", "", () => new BenchmarkViewModel()),
            ]),
            new("NavOptimize",
            [
                new("gaming", "", () => new GamingViewModel()),
                new("responsiveness", "", () => new ResponsivenessViewModel()),
                new("privacy", "", () => new PrivacyViewModel()),
                new("debloat", "", () => new DebloatViewModel()),
                new("startup", "", () => new StartupViewModel()),
            ]),
            new("NavMaintain",
            [
                new("storage", "", () => new StorageViewModel()),
                new("network", "", () => new NetworkViewModel()),
                new("repair", "", () => new RepairViewModel()),
                new("drivers", "", () => new HardwareViewModel()),
            ]),
        ];
        Footer =
        [
            new("history", "", () => new HistoryViewModel()),
            new("settings", "", () => new SettingsViewModel()),
        ];

        NavigateCommand = new RelayCommand(p => Navigate((string)p!));
        RestartElevatedCommand = new RelayCommand(RestartElevated, () => !IsElevated);
        DismissToastCommand = new RelayCommand(p => Toasts.Remove((Toast)p!));

        Notifier.Raised += (message, kind) => OnUi(() => ShowToast(message, kind));
        Navigator.Requested += key => OnUi(() => Navigate(key));
        ((INotifyPropertyChanged)Loc.Instance).PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(Groups));
            if (e.PropertyName == nameof(Loc.Language)) DropPagesForLanguageChange();
        };
        Navigate("dashboard");
    }

    public IReadOnlyList<NavGroup> Groups { get; }
    public IReadOnlyList<NavItem> Footer { get; }
    public ObservableCollection<Toast> Toasts { get; } = [];
    public ICommand NavigateCommand { get; }
    public ICommand RestartElevatedCommand { get; }
    public ICommand DismissToastCommand { get; }

    public bool IsElevated => Elevation.IsElevated;
    public string Version => "v" + Core.Updates.UpdateService.CurrentVersion.ToString(3);

    public PageViewModel? Current { get => _current; private set => Set(ref _current, value); }
    public string CurrentKey { get => _currentKey; private set => Set(ref _currentKey, value); }

    public async void Navigate(string key)
    {
        if (key == CurrentKey && Current is not null) return;
        var item = Groups.SelectMany(g => g.Items).Concat(Footer).FirstOrDefault(i => i.Key == key);
        if (item is null) return;
        Current?.OnDeactivated();
        if (!_pages.TryGetValue(key, out var page)) _pages[key] = page = item.Factory();
        CurrentKey = key;
        Current = page;
        await page.ActivateAsync();
    }

    /// <summary>
    /// Engine texts (tweaks, findings, catalogs) are translated when they are read, so cached pages are rebuilt
    /// in the new language the next time they are opened. The current page and busy pages are kept.
    /// </summary>
    private void DropPagesForLanguageChange()
    {
        foreach (var (key, page) in _pages.ToList())
            if (page != Current && !page.IsBusy) _pages.Remove(key);
    }

    private void ShowToast(string message, Notifier.Kind kind)
    {
        var toast = new Toast { Message = message, Kind = kind };
        Toasts.Add(toast);
        while (Toasts.Count > 3) Toasts.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == Notifier.Kind.Error ? 12 : 6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Toasts.Remove(toast);
        };
        timer.Start();
    }

    private void RestartElevated()
    {
        if (Elevation.RestartElevated()) System.Windows.Application.Current.Shutdown();
        else Notifier.Warning(Loc.Get("ElevationCancelled"));
    }
}
