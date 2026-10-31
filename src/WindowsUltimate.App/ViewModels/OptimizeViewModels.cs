using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using WindowsUltimate.App.Dialogs;
using WindowsUltimate.App.Localization;
using WindowsUltimate.App.Mvvm;
using WindowsUltimate.App.Services;
using WindowsUltimate.Core.Backup;
using WindowsUltimate.Core.Debloat;
using WindowsUltimate.Core.Diagnostics;
using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Monitoring;
using WindowsUltimate.Core.Optimization;
using WindowsUltimate.Core.Privacy;
using WindowsUltimate.Core.Startup;

namespace WindowsUltimate.App.ViewModels;

// ───────────────────────── Gaming ─────────────────────────

public sealed class ProfileCardViewModel(GamingProfile profile, bool isActive, ICommand activate)
{
    public GamingProfile Profile => profile;
    public string Name => profile.Name;
    public string Description => profile.Description;
    public bool IsActive => isActive;
    public bool IsSmart => profile.IsSmart;
    public int Count => profile.TweakIds.Count;
    public string CountText => string.Format(Loc.Get("ProfileChanges"), profile.TweakIds.Count);
    public ICommand ActivateCommand => activate;
}

public sealed class GamingViewModel : PageViewModel
{
    public override string Key => "gaming";

    public GamingViewModel()
    {
        RefreshCommand = new AsyncCommand(() => Busy(Loc.Get("Analyzing"), AnalyzeAsync));
        RestorePreviousCommand = new AsyncCommand(RestorePreviousAsync, () => AppServices.Profiles.State.ActiveSessionId is not null);
        ActivateCommand = new AsyncCommand(p => ActivateAsync((GamingProfile)p!));
        AppServices.ConfigurationChanged += () => OnUi(async () => await AnalyzeAsync());
    }

    public ObservableCollection<CheckItem> Checks { get; } = [];
    public ObservableCollection<CheckItem> Overlays { get; } = [];
    public ObservableCollection<ProfileCardViewModel> Profiles { get; } = [];
    public TweakCollection Tweaks { get; } = new(t => t.Category is TweakCategory.Gaming or TweakCategory.Power or TweakCategory.Input);
    public ICommand RefreshCommand { get; }
    public ICommand RestorePreviousCommand { get; }
    public ICommand ActivateCommand { get; }
    public string ActiveProfileText { get; private set; } = "";
    public bool HasOverlays => Overlays.Count > 0;

    protected override Task LoadAsync() => Busy(Loc.Get("Analyzing"), AnalyzeAsync);

    private async Task AnalyzeAsync()
    {
        var p = await AppServices.ProfileTask;
        var (checks, overlays) = await Task.Run(() => (BuildChecks(p), DetectOverlays()));
        Checks.Clear();
        foreach (var c in checks) Checks.Add(c);
        Overlays.Clear();
        foreach (var o in overlays) Overlays.Add(o);

        Profiles.Clear();
        var state = AppServices.Profiles.State;
        foreach (var prof in AppServices.Profiles.Profiles(p))
            Profiles.Add(new ProfileCardViewModel(prof, prof.Id == state.ActiveProfileId, ActivateCommand));
        var active = Profiles.FirstOrDefault(x => x.IsActive);
        ActiveProfileText = active is null ? Loc.Get("NoProfileActive") : string.Format(Loc.Get("ProfileActiveSince"), active.Name, state.ActivatedAt?.ToString("g"));
        OnPropertiesChanged(nameof(ActiveProfileText), nameof(HasOverlays));
        await Tweaks.RefreshAsync();
    }

    private static List<CheckItem> BuildChecks(SystemProfile p)
    {
        var list = new List<CheckItem>();
        var gpu = p.PrimaryGpu;
        if (gpu is not null)
        {
            var age = gpu.DriverDate is { } d ? (DateTime.Now - d).TotalDays : 0;
            list.Add(new(Loc.Get("CheckGpuDriver"), $"{gpu.FriendlyDriverVersion}{(gpu.DriverDate is { } dd ? $" ({dd:d})" : "")}",
                age > 365 ? "warn" : "good", age > 365 ? Loc.Get("CheckGpuDriverOld") + " " + DriverAnalyzerSource(gpu.Vendor) : gpu.Name));
        }
        foreach (var disp in p.Displays)
            list.Add(new(string.Format(Loc.Get("CheckRefresh"), disp.MonitorName.Length > 0 ? disp.MonitorName : disp.DeviceName),
                $"{disp.CurrentHz} Hz", disp.BelowMaxRefresh ? "bad" : "good",
                disp.BelowMaxRefresh ? string.Format(Loc.Get("CheckRefreshLow"), disp.MaxHzAtCurrentResolution) : ""));

        var gm = SystemChecks.GameModeEnabled();
        list.Add(new(Loc.Get("CheckGameMode"), gm == false ? Loc.Get("Off") : Loc.Get("On"), gm == false ? "warn" : "good", Loc.Get("CheckGameModeDetail")));
        var hags = SystemChecks.HagsEnabled();
        list.Add(new(Loc.Get("CheckHags"), hags is null ? Loc.Get("NotSupported") : hags.Value ? Loc.Get("On") : Loc.Get("Off"), "info", Loc.Get("CheckHagsDetail")));
        var rec = SystemChecks.BackgroundRecording();
        list.Add(new(Loc.Get("CheckRecording"), rec ? Loc.Get("On") : Loc.Get("Off"), rec ? "warn" : "good", Loc.Get("CheckRecordingDetail")));
        var plan = SystemChecks.PowerPlan();
        list.Add(new(Loc.Get("PowerPlan"), plan?.Name ?? "—", plan?.Id == PowerPlanAction.PowerSaver ? "bad" : "good", p.IsLaptop ? Loc.Get("CheckPowerLaptop") : ""));
        var dx = SystemChecks.DirectXGlobalSettings();
        list.Add(new(Loc.Get("CheckWindowedOpt"), dx.GetValueOrDefault("SwapEffectUpgradeEnable") == "1" ? Loc.Get("On") : Loc.Get("Default"), "info", Loc.Get("CheckWindowedOptDetail")));
        if (p.Memory.Modules.Count == 1)
            list.Add(new(Loc.Get("CheckDualChannel"), Loc.Get("SingleModule"), "warn", Loc.Get("CheckDualChannelDetail")));
        var rated = p.Memory.Modules.Select(m => m.RatedMhz).DefaultIfEmpty(0).Max();
        if (p.Memory.ConfiguredMhz > 0)
            list.Add(new(Loc.Get("CheckMemorySpeed"), $"{p.Memory.ConfiguredMhz} MT/s", rated > p.Memory.ConfiguredMhz + 200 ? "warn" : "good",
                rated > p.Memory.ConfiguredMhz + 200 ? string.Format(Loc.Get("CheckMemorySpeedLow"), rated) : ""));
        return list;
    }

    private static string DriverAnalyzerSource(HardwareVendor v) => Core.Drivers.DriverAnalyzer.OfficialDriverSource(v);

    private static List<CheckItem> DetectOverlays()
    {
        var names = Process.GetProcesses().Select(pr => { try { return pr.ProcessName; } catch { return ""; } finally { pr.Dispose(); } })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ProcessMonitor.KnownOverlays.Where(o => names.Contains(o.Process))
            .Select(o => new CheckItem(o.Name, Loc.Get("Running"), "info", o.Note)).ToList();
    }

    private async Task ActivateAsync(GamingProfile profile)
    {
        var system = await AppServices.ProfileTask;
        var hasActive = AppServices.Profiles.State.ActiveSessionId is not null;
        var all = profile.TweakIds.Select(AppServices.Engine.Find).OfType<Tweak>().ToList();
        var blocked = new List<(Tweak, string)>();
        var tweaks = new List<Tweak>();
        foreach (var t in all)
        {
            var c = t.CheckCompatibility(system);
            if (!c.IsCompatible) blocked.Add((t, c.Reason ?? ""));
            else if (t.RequiresAdmin && !Elevation.IsElevated) blocked.Add((t, Loc.Get("NeedsAdmin")));
            else tweaks.Add(t);
        }

        if (tweaks.Count == 0)
        {
            // Profiles such as "Normal desktop" only restore what the previous profile changed.
            if (!ConfirmDialog.Show(profile.Name, hasActive ? Loc.Get("ProfileRestoreOnly") : Loc.Get("ProfileNothing"), confirmText: Loc.Get("Continue"), infoOnly: !hasActive))
                return;
            if (hasActive) await RestorePreviousCoreAsync();
            return;
        }

        var consent = ConsentDialog.Ask(string.Format(Loc.Get("ActivateProfileTitle"), profile.Name) + (hasActive ? " · " + Loc.Get("PreviousWillBeRestored") : ""), tweaks, blocked);
        if (consent is null) return;
        await Busy(Loc.Get("Applying"), async () =>
        {
            if (consent.CreateRestorePoint)
            {
                var (ok, msg) = await RestorePoint.CreateAsync("Windows Ultimate: " + profile.Name);
                if (!ok) Notifier.Warning(msg);
            }
            var summary = await AppServices.Profiles.ActivateAsync(profile, system, BusyProgress);
            TweakApplier.Report(summary);
        });
        AppServices.NotifyConfigurationChanged();
    }

    private async Task RestorePreviousAsync()
    {
        if (!ConfirmDialog.Show(Loc.Get("RestorePrevious"), Loc.Get("RestorePreviousMessage"), confirmText: Loc.Get("Restore"))) return;
        await RestorePreviousCoreAsync();
    }

    private async Task RestorePreviousCoreAsync()
    {
        var r = await AppServices.Profiles.RestorePreviousAsync();
        if (r is not null) TweakApplier.ReportRollback(r);
        AppServices.NotifyConfigurationChanged();
    }
}

// ───────────────────────── Responsiveness ─────────────────────────

public sealed class ResponsivenessViewModel : PageViewModel
{
    public override string Key => "responsiveness";

    public ResponsivenessViewModel()
    {
        RefreshCommand = new AsyncCommand(() => Busy(Loc.Get("Analyzing"), AnalyzeAsync));
    }

    public ObservableCollection<CheckItem> Checks { get; } = [];
    public TweakCollection Tweaks { get; } = new(t => t.Category is TweakCategory.Responsiveness or TweakCategory.Visual);
    public IReadOnlyList<Myth> Myths { get; } = Core.Optimization.Myths.All;
    public ICommand RefreshCommand { get; }

    protected override Task LoadAsync() => Busy(Loc.Get("Analyzing"), AnalyzeAsync);

    private async Task AnalyzeAsync()
    {
        var idle = await SystemChecks.SampleCpuAsync(TimeSpan.FromSeconds(2));
        var checks = await Task.Run(() =>
        {
            var list = new List<CheckItem>();
            var mem = SystemChecks.Memory();
            list.Add(new(Loc.Get("CheckBackgroundCpu"), $"{idle:0.0} %", idle > 15 ? "bad" : idle > 6 ? "warn" : "good", Loc.Get("CheckBackgroundCpuDetail")));
            list.Add(new(Loc.Get("CheckCommit"), $"{mem.CommitPercent:0} %", mem.CommitPercent > 90 ? "bad" : mem.CommitPercent > 80 ? "warn" : "good", Loc.Get("CheckCommitDetail")));
            list.Add(new(Loc.Get("CheckMemoryLoad"), $"{mem.MemoryLoadPercent:0} % · {Format.Bytes(mem.AvailableBytes)} {Loc.Get("Free")}", mem.MemoryLoadPercent > 90 ? "bad" : mem.MemoryLoadPercent > 80 ? "warn" : "good", ""));
            list.Add(new(Loc.Get("Processes"), $"{mem.Processes} · {mem.Threads} threads", mem.Processes > 300 ? "warn" : "good", ""));
            var startup = StartupManager.EnabledCount();
            list.Add(new(Loc.Get("StartupApps"), startup.ToString(), startup > 15 ? "warn" : "good", Loc.Get("CheckStartupDetail")));
            if (EventLogScanner.LastBootDuration() is { } boot)
                list.Add(new(Loc.Get("CheckBootTime"), Format.Duration(boot), boot.TotalSeconds > 90 ? "warn" : "good", Loc.Get("CheckBootTimeDetail")));
            var sys = AppServices.Profile?.Disks.FirstOrDefault();
            if (AppServices.Profile?.Disks.Any(d => d.MediaType == "HDD") == true)
                list.Add(new(Loc.Get("CheckHdd"), "HDD", "warn", Loc.Get("CheckHddDetail")));
            var reboot = SystemChecks.PendingReboot();
            if (reboot.ComponentServicing || reboot.WindowsUpdate)
                list.Add(new(Loc.Get("CheckPendingReboot"), Loc.Get("Yes"), "warn", Loc.Get("CheckPendingRebootDetail")));
            var plan = SystemChecks.PowerPlan();
            list.Add(new(Loc.Get("PowerPlan"), plan?.Name ?? "—", plan?.Id == PowerPlanAction.PowerSaver ? "bad" : "good", ""));
            return list;
        });
        Checks.Clear();
        foreach (var c in checks) Checks.Add(c);
        await Tweaks.RefreshAsync();
    }
}

// ───────────────────────── Privacy ─────────────────────────

public sealed class PrivacyViewModel : PageViewModel
{
    public override string Key => "privacy";

    public PrivacyViewModel()
    {
        OpenCommand = new RelayCommand(p => ProcessRunner.ShellOpen((string)p!));
    }

    public TweakCollection Tweaks { get; } = new(t => t.Category is TweakCategory.Privacy);
    public ObservableCollection<PermissionStatus> Permissions { get; } = [];
    public ICommand OpenCommand { get; }

    protected override Task LoadAsync() => Busy(Loc.Get("Analyzing"), async () =>
    {
        await Tweaks.RefreshAsync();
        var perms = await Task.Run(PermissionReview.Review);
        foreach (var p in perms) Permissions.Add(p);
    });
}

// ───────────────────────── Debloat ─────────────────────────

public sealed class AppxItemViewModel(AppxPackageInfo package, Action changed) : ObservableObject
{
    private bool _isSelected;
    public AppxPackageInfo Package => package;
    public string Name => package.FriendlyName;
    public string PackageName => package.Name;
    public string Version => package.Version;
    public string WhatItDoes => package.Catalog?.WhatItDoes ?? "";
    public string Consequences => package.Catalog?.Consequences ?? "";
    public bool Protected => package.Protected;
    public bool CanSelect => !package.Protected;
    public bool IsSelected { get => _isSelected; set { if (Set(ref _isSelected, value && CanSelect)) changed(); } }
}

public sealed class AppxGroupViewModel(DebloatCategory category, string title, string description, string severity, List<AppxItemViewModel> items)
{
    public DebloatCategory Category => category;
    public string Title => title;
    public string Description => description;
    public string Severity => severity;
    public List<AppxItemViewModel> Items => items;
    public int Count => items.Count;
    public bool Selectable => category != DebloatCategory.DoNotRemove;
}

public sealed class DebloatViewModel : PageViewModel
{
    private int _selectedCount;
    private string _status = "";
    public override string Key => "debloat";

    public DebloatViewModel()
    {
        RemoveCommand = new AsyncCommand(RemoveAsync, () => SelectedCount > 0 && !IsBusy);
        RefreshCommand = new AsyncCommand(() => Busy(Loc.Get("LoadingApps"), LoadAppsAsync));
        SelectSafeCommand = new RelayCommand(() =>
        {
            foreach (var g in Groups) foreach (var i in g.Items) i.IsSelected = g.Category == DebloatCategory.SafeToRemove;
        });
        OpenStoreCommand = new RelayCommand(p => ProcessRunner.ShellOpen(((AppxItemViewModel)p!).Package.StoreLink));
        OpenFeaturesCommand = new RelayCommand(OptionalFeatures.OpenWindowsFeaturesDialog);
    }

    public ObservableCollection<AppxGroupViewModel> Groups { get; } = [];
    public ObservableCollection<OptionalFeature> Features { get; } = [];
    public TweakCollection Tweaks { get; } = new(t => t.Category is TweakCategory.ConsumerFeatures);
    public int SelectedCount { get => _selectedCount; private set => Set(ref _selectedCount, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string FeaturesError { get; private set; } = "";
    public ICommand RemoveCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SelectSafeCommand { get; }
    public ICommand OpenStoreCommand { get; }
    public ICommand OpenFeaturesCommand { get; }

    protected override Task LoadAsync() => Busy(Loc.Get("LoadingApps"), async () =>
    {
        await Tweaks.RefreshAsync();
        await LoadAppsAsync();
        var (features, error) = await OptionalFeatures.ListAsync();
        foreach (var f in features.Where(f => f.IsNotable)) Features.Add(f);
        FeaturesError = error ?? "";
        OnPropertyChanged(nameof(FeaturesError));
    });

    private async Task LoadAppsAsync()
    {
        List<AppxPackageInfo> packages;
        try { packages = await AppxService.ListAsync(); }
        catch (Exception ex) { Status = ex.Message; return; }

        void Changed() => SelectedCount = Groups.Sum(g => g.Items.Count(i => i.IsSelected));
        Groups.Clear();
        var defs = new (DebloatCategory Cat, string Sev)[]
        {
            (DebloatCategory.SafeToRemove, "good"), (DebloatCategory.Optional, "info"), (DebloatCategory.Advanced, "warn"),
            (DebloatCategory.Unclassified, ""), (DebloatCategory.DoNotRemove, "bad"),
        };
        foreach (var (cat, sev) in defs)
        {
            var items = packages.Where(p => p.Category == cat && !(cat == DebloatCategory.DoNotRemove && p.IsFramework))
                .OrderBy(p => p.FriendlyName).Select(p => new AppxItemViewModel(p, Changed)).ToList();
            if (items.Count == 0) continue;
            Groups.Add(new AppxGroupViewModel(cat, Loc.Get("Debloat_" + cat), Loc.Get("Debloat_" + cat + "_Desc"), sev, items));
        }
        var frameworks = packages.Count(p => p.IsFramework);
        Status = string.Format(Loc.Get("AppsFound"), packages.Count, frameworks);
        SelectedCount = 0;
    }

    private async Task RemoveAsync()
    {
        var selected = Groups.SelectMany(g => g.Items).Where(i => i.IsSelected && !i.Protected).ToList();
        var details = selected.Select(i => $"{i.Name} ({i.PackageName}){(i.Consequences.Length > 0 ? " — " + i.Consequences : "")}");
        if (!ConfirmDialog.Show(Loc.Get("RemoveAppsTitle"), string.Format(Loc.Get("RemoveAppsMessage"), selected.Count), details, Loc.Get("Continue"), danger: true))
            return;
        // Second confirmation: removal cannot be rolled back by the app (reinstall comes from the Microsoft Store).
        if (!ConfirmDialog.Show(Loc.Get("RemoveAppsTitle"), Loc.Get("RemoveAppsIrreversible"), confirmText: string.Format(Loc.Get("RemoveN"), selected.Count),
                danger: true, acknowledge: Loc.Get("RemoveAppsAcknowledge")))
            return;

        var removed = new List<(string, string, string?)>();
        var failed = new List<string>();
        await Busy(Loc.Get("RemovingApps"), async () =>
        {
            foreach (var item in selected)
            {
                BusyText = item.Name;
                var (ok, msg) = await AppxService.RemoveAsync(item.Package);
                if (ok) removed.Add((item.Name, $"Removed app package {item.Package.FullName} for the current user", "Reinstall from the Microsoft Store: " + item.Package.StoreLink));
                else failed.Add($"{item.Name}: {msg}");
            }
        });
        if (removed.Count > 0) AppServices.Journal.RecordIrreversible(Loc.Get("RemoveAppsTitle"), "debloat", removed);
        if (failed.Count == 0) Notifier.Success(string.Format(Loc.Get("AppsRemoved"), removed.Count));
        else Notifier.Error(string.Format(Loc.Get("AppsRemovedWithFailures"), removed.Count, failed.Count) + " " + string.Join(" ", failed));
        await Busy(Loc.Get("LoadingApps"), LoadAppsAsync);
    }
}

// ───────────────────────── Startup & processes ─────────────────────────

public sealed class StartupItemViewModel(StartupEntry entry, Func<StartupItemViewModel, bool, Task> toggle) : ObservableObject
{
    public StartupEntry Entry => entry;
    public string Name => entry.Name;
    public string Publisher => entry.Publisher.Length > 0 ? entry.Publisher : entry.Signer.Length > 0 ? entry.Signer : Loc.Get("UnknownPublisher");
    public string Signer => entry.Signer;
    public string Command => entry.Command;
    public string Source => entry.SourceLabel;
    public string Impact => entry.EstimatedImpact;
    public bool FileMissing => !entry.FileExists;
    public bool IsMicrosoft => entry.IsMicrosoft;
    public bool AdminBlocked => entry.RequiresAdmin && !Elevation.IsElevated;
    public bool CanToggle => !AdminBlocked;
    public bool Enabled
    {
        get => entry.Enabled;
        set { if (value != entry.Enabled) _ = toggle(this, value); }
    }
    public void Refresh() => OnPropertyChanged(nameof(Enabled));
}

public sealed class TaskItemViewModel(ScheduledTaskInfo task, Func<TaskItemViewModel, bool, Task> toggle) : ObservableObject
{
    public ScheduledTaskInfo Task => task;
    public string Name => task.Name;
    public string Path => task.Path;
    public string Author => task.Author;
    public string Actions => task.Actions;
    public string Triggers => task.Triggers;
    public string LastRun => task.LastRun?.ToString("g") ?? "—";
    public bool IsMicrosoft => task.IsMicrosoft;
    public bool CanToggle => Elevation.IsElevated || !task.IsMicrosoft;
    public bool Enabled
    {
        get => task.Enabled;
        set { if (value != task.Enabled) _ = toggle(this, value); }
    }
    public void Refresh() => OnPropertyChanged(nameof(Enabled));
}

public sealed class StartupViewModel : PageViewModel
{
    private readonly ProcessMonitor _processes = new();
    private bool _includeMicrosoftTasks;
    private string _processFilter = "";
    private List<ProcessSample> _allProcesses = [];
    public override string Key => "startup";

    public StartupViewModel()
    {
        RefreshCommand = new AsyncCommand(() => Busy(Loc.Get("Loading"), LoadAllAsync));
        RefreshProcessesCommand = new AsyncCommand(RefreshProcessesAsync);
        EndProcessCommand = new AsyncCommand(p => EndProcessAsync((ProcessSample)p!));
        RevealCommand = new RelayCommand(p => { if (p is string s && File.Exists(s)) ProcessRunner.RevealInExplorer(s); });
    }

    public ObservableCollection<StartupItemViewModel> Startup { get; } = [];
    public ObservableCollection<TaskItemViewModel> Tasks { get; } = [];
    public ObservableCollection<ProcessSample> Processes { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand RefreshProcessesCommand { get; }
    public ICommand EndProcessCommand { get; }
    public ICommand RevealCommand { get; }
    public int EnabledCount => Startup.Count(s => s.Enabled);

    public bool IncludeMicrosoftTasks
    {
        get => _includeMicrosoftTasks;
        set { if (Set(ref _includeMicrosoftTasks, value)) _ = LoadTasksAsync(); }
    }

    public string ProcessFilter
    {
        get => _processFilter;
        set { if (Set(ref _processFilter, value)) FillProcesses(); }
    }

    protected override async Task LoadAsync()
    {
        _processes.Sample(); // prime the CPU deltas
        await Busy(Loc.Get("Loading"), LoadAllAsync);
    }

    private async Task LoadAllAsync()
    {
        await RefreshProcessesAsync();
        var running = _allProcesses;
        var entries = await System.Threading.Tasks.Task.Run(() => StartupManager.List(running));
        Startup.Clear();
        foreach (var e in entries.OrderByDescending(e => e.Enabled).ThenBy(e => e.Name))
            Startup.Add(new StartupItemViewModel(e, ToggleStartupAsync));
        OnPropertyChanged(nameof(EnabledCount));
        await LoadTasksAsync();
    }

    private async Task LoadTasksAsync()
    {
        var include = IncludeMicrosoftTasks;
        var tasks = await System.Threading.Tasks.Task.Run(() => ScheduledTasks.List(include));
        Tasks.Clear();
        foreach (var t in tasks.OrderBy(t => t.IsMicrosoft).ThenBy(t => t.Path)) Tasks.Add(new TaskItemViewModel(t, ToggleTaskAsync));
    }

    private async Task RefreshProcessesAsync()
    {
        _allProcesses = await System.Threading.Tasks.Task.Run(_processes.Sample);
        FillProcesses();
    }

    private void FillProcesses()
    {
        Processes.Clear();
        var f = ProcessFilter.Trim();
        foreach (var p in _allProcesses.Where(p => f.Length == 0 || p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Company.Contains(f, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.WorkingSet).Take(150))
            Processes.Add(p);
    }

    private async Task ToggleStartupAsync(StartupItemViewModel item, bool enable)
    {
        var tweak = StartupManager.CreateToggle(item.Entry, enable);
        AppServices.Engine.Register(tweak);
        var summary = await TweakApplier.ApplyAsync([tweak], tweak.Name, "startup");
        if (summary is { AppliedCount: > 0 }) item.Entry.Enabled = enable;
        item.Refresh();
        OnPropertyChanged(nameof(EnabledCount));
    }

    private async Task ToggleTaskAsync(TaskItemViewModel item, bool enable)
    {
        var tweak = ScheduledTasks.CreateToggle(item.Task, enable);
        AppServices.Engine.Register(tweak);
        var summary = await TweakApplier.ApplyAsync([tweak], tweak.Name, "tasks");
        if (summary is { AppliedCount: > 0 }) item.Task.Enabled = enable;
        item.Refresh();
    }

    private async Task EndProcessAsync(ProcessSample p)
    {
        if (p.Kind != ProcessKind.ThirdParty)
        {
            Notifier.Warning(Loc.Get("CannotEndSystemProcess"));
            return;
        }
        if (!ConfirmDialog.Show(Loc.Get("EndTaskTitle"), string.Format(Loc.Get("EndTaskMessage"), p.Name, p.Company.Length > 0 ? p.Company : Loc.Get("UnknownPublisher")),
                [p.Path ?? ""], Loc.Get("EndTask"), danger: true))
            return;
        try
        {
            ProcessMonitor.Terminate(p);
            Log.Audit($"Terminated process {p.Name} ({p.Pid}) at user request");
            Notifier.Success(string.Format(Loc.Get("ProcessEnded"), p.Name));
        }
        catch (Exception ex) { Notifier.Error(ex.Message); }
        await System.Threading.Tasks.Task.Delay(400);
        await RefreshProcessesAsync();
    }
}
