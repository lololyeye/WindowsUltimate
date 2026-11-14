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

/// <summary>One page. Advanced pages are hidden in Simple mode (but still reachable from a recommendation).</summary>
public sealed record NavItem(string Key, Func<PageViewModel> Factory, bool Advanced = false)
{
    public string Title => Loc.Get($"Page_{Key}");
}

/// <summary>A sidebar entry. Sections with several pages show them as tabs under the page header.</summary>
public sealed record NavSection(string Key, string Glyph, IReadOnlyList<NavItem> Pages)
{
    public string Title => Loc.Get($"Nav_{Key}");
    public IEnumerable<NavItem> VisiblePages => Pages.Where(p => !p.Advanced || UiMode.Instance.IsAdvanced);
}

/// <summary>A tab of the current section.</summary>
public sealed record SubTab(string Key, string Title, bool IsSelected);

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
    private readonly Dictionary<string, string> _lastInSection = new();
    private PageViewModel? _current;
    private string _currentKey = "";
    private string _currentSection = "";

    public MainViewModel()
    {
        Sections =
        [
            new("overview", "", [new("dashboard", () => new DashboardViewModel())]),
            new("windows", "",
            [
                new("optimize", () => new OptimizeViewModel()),
                new("startup", () => new StartupViewModel()),
                new("services", () => new ServicesViewModel(), Advanced: true),
                new("privacy", () => new PrivacyViewModel()),
                new("responsiveness", () => new ResponsivenessViewModel(), Advanced: true),
                new("debloat", () => new DebloatViewModel(), Advanced: true),
            ]),
            new("network", "", [new("network", () => new NetworkViewModel())]),
            new("security", "", [new("security", () => new SecurityViewModel())]),
            new("cleanup", "", [new("storage", () => new StorageViewModel())]),
            new("gaming", "", [new("gaming", () => new GamingViewModel())]),
            new("diagnostics", "",
            [
                new("monitor", () => new MonitorViewModel()),
                new("benchmark", () => new BenchmarkViewModel()),
                new("repair", () => new RepairViewModel()),
                new("drivers", () => new HardwareViewModel(), Advanced: true),
            ]),
        ];
        Footer =
        [
            new("history", "", [new("history", () => new HistoryViewModel())]),
            new("settings", "", [new("settings", () => new SettingsViewModel())]),
        ];

        NavigateCommand = new RelayCommand(p => Navigate((string)p!));
        NavigateSectionCommand = new RelayCommand(p => NavigateSection((string)p!));
        RestartElevatedCommand = new RelayCommand(RestartElevated, () => !IsElevated);
        DismissToastCommand = new RelayCommand(p => Toasts.Remove((Toast)p!));
        SetModeCommand = new RelayCommand(p => UiMode.Instance.Set(p as string == "advanced"));

        Notifier.Raised += (message, kind) => OnUi(() => ShowToast(message, kind));
        Navigator.Requested += key => OnUi(() => Navigate(key));
        UiMode.Instance.Changed += OnModeChanged;
        ((INotifyPropertyChanged)Loc.Instance).PropertyChanged += (_, e) =>
        {
            OnPropertiesChanged(nameof(Sections), nameof(Footer));
            RebuildTabs();
            if (e.PropertyName == nameof(Loc.Language)) DropPagesForLanguageChange();
        };
        Navigate("dashboard");
    }

    public IReadOnlyList<NavSection> Sections { get; }
    public IReadOnlyList<NavSection> Footer { get; }
    public ObservableCollection<SubTab> SubTabs { get; } = [];
    public bool HasSubTabs => SubTabs.Count > 1;
    public ObservableCollection<Toast> Toasts { get; } = [];
    public ICommand NavigateCommand { get; }
    public ICommand NavigateSectionCommand { get; }
    public ICommand RestartElevatedCommand { get; }
    public ICommand DismissToastCommand { get; }
    public ICommand SetModeCommand { get; }
    public bool IsAdvanced => UiMode.Instance.IsAdvanced;

    public bool IsElevated => Elevation.IsElevated;
    public string Version => "v" + Core.Updates.UpdateService.CurrentVersion.ToString(3);

    public PageViewModel? Current { get => _current; private set => Set(ref _current, value); }
    public string CurrentKey { get => _currentKey; private set => Set(ref _currentKey, value); }
    public string CurrentSection { get => _currentSection; private set => Set(ref _currentSection, value); }

    private IEnumerable<NavSection> AllSections => Sections.Concat(Footer);

    public void NavigateSection(string sectionKey)
    {
        var section = AllSections.FirstOrDefault(s => s.Key == sectionKey);
        if (section is null) return;
        var visible = section.VisiblePages.ToList();
        var target = _lastInSection.TryGetValue(sectionKey, out var last) && visible.Any(p => p.Key == last) ? last : visible.FirstOrDefault()?.Key;
        if (target is not null) Navigate(target);
    }

    public async void Navigate(string key)
    {
        if (key == CurrentKey && Current is not null) return;
        var section = AllSections.FirstOrDefault(s => s.Pages.Any(p => p.Key == key));
        var item = section?.Pages.First(p => p.Key == key);
        if (section is null || item is null) return;
        Current?.OnDeactivated();
        if (!_pages.TryGetValue(key, out var page)) _pages[key] = page = item.Factory();
        _lastInSection[section.Key] = key;
        CurrentSection = section.Key;
        CurrentKey = key;
        RebuildTabs();
        Current = page;
        await page.ActivateAsync();
    }

    private void RebuildTabs()
    {
        SubTabs.Clear();
        var section = AllSections.FirstOrDefault(s => s.Key == CurrentSection);
        if (section is not null)
            foreach (var p in section.Pages.Where(p => !p.Advanced || UiMode.Instance.IsAdvanced || p.Key == CurrentKey))
                SubTabs.Add(new SubTab(p.Key, p.Title, p.Key == CurrentKey));
        OnPropertyChanged(nameof(HasSubTabs));
    }

    private void OnModeChanged()
    {
        OnPropertyChanged(nameof(IsAdvanced));
        var section = AllSections.FirstOrDefault(s => s.Key == CurrentSection);
        if (section is not null && !section.VisiblePages.Any(p => p.Key == CurrentKey)) NavigateSection(section.Key);
        else RebuildTabs();
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
