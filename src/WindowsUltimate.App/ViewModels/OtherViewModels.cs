using System.Collections.ObjectModel;
using System.Windows.Input;
using WindowsUltimate.App.Dialogs;
using WindowsUltimate.App.Localization;
using WindowsUltimate.App.Mvvm;
using WindowsUltimate.App.Services;
using WindowsUltimate.Core.Backup;
using WindowsUltimate.Core.Benchmark;
using WindowsUltimate.Core.Diagnostics;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Monitoring;

namespace WindowsUltimate.App.ViewModels;

// ───────────────────────── Live monitor ─────────────────────────

public sealed class MonitorViewModel : PageViewModel
{
    private IDisposable? _subscription;
    private readonly ProcessMonitor _processes = new();
    private int _tick;
    private PerfSample? _last;
    public override string Key => "monitor";

    public MonitorViewModel()
    {
        Intervals = [500, 1000, 2000, 5000];
    }

    public IReadOnlyList<int> Intervals { get; }
    public int Interval
    {
        get => AppServices.Settings.MonitorIntervalMs;
        set
        {
            AppServices.Settings.MonitorIntervalMs = value;
            AppServices.Settings.Save();
            AppServices.Monitor.SetInterval(TimeSpan.FromMilliseconds(value));
            OnPropertyChanged();
        }
    }

    public PerfSample? Last => _last;
    public IReadOnlyList<double> Cpu => Series(s => s.CpuPercent);
    public IReadOnlyList<double> Gpu => Series(s => s.GpuPercent ?? 0);
    public IReadOnlyList<double> Ram => Series(s => s.RamPercent);
    public IReadOnlyList<double> DiskRead => Series(s => s.DiskReadBps);
    public IReadOnlyList<double> DiskWrite => Series(s => s.DiskWriteBps);
    public IReadOnlyList<double> NetRx => Series(s => s.NetRxBps);
    public IReadOnlyList<double> NetTx => Series(s => s.NetTxBps);
    public IReadOnlyList<double> CpuTemp => Series(s => s.CpuTempC ?? 0);
    public IReadOnlyList<double> GpuTemp => Series(s => s.GpuTempC ?? 0);
    public string CpuText => _last is null ? "—" : $"{_last.CpuPercent:0} %";
    public string GpuText => _last?.GpuPercent is { } g ? $"{g:0} %" : "—";
    public string RamText => _last is null ? "—" : $"{_last.RamPercent:0} % · {Format.Bytes(_last.RamUsedBytes)}";
    public string DiskText => _last is null ? "—" : $"R {Format.Rate(_last.DiskReadBps)} · W {Format.Rate(_last.DiskWriteBps)}{(_last.DiskActivePercent is { } a ? $" · {a:0}% active" : "")}";
    public string NetText => _last is null ? "—" : $"↓ {Format.Rate(_last.NetRxBps)} · ↑ {Format.Rate(_last.NetTxBps)}";
    public string TempText => _last is null ? "—" :
        $"CPU {(_last.CpuTempC is { } c ? $"{c:0}°C" : "n/a")} · GPU {(_last.GpuTempC is { } g ? $"{g:0}°C" : "n/a")}";
    public ObservableCollection<ProcessSample> TopProcesses { get; } = [];

    private IReadOnlyList<double> Series(Func<PerfSample, double> f) => AppServices.Monitor.History.TakeLast(120).Select(f).ToArray();

    protected override Task LoadAsync()
    {
        _processes.Sample();
        return Task.CompletedTask;
    }

    public override void OnActivated()
    {
        _subscription ??= AppServices.Monitor.Subscribe();
        AppServices.Monitor.Sampled += OnSample;
    }

    public override void OnDeactivated()
    {
        AppServices.Monitor.Sampled -= OnSample;
        _subscription?.Dispose();
        _subscription = null;
    }

    private void OnSample(object? sender, PerfSample s)
    {
        _last = s;
        // Process enumeration is the expensive part; refresh it every third sample.
        List<ProcessSample>? top = null;
        if (_tick++ % 3 == 0) top = _processes.Sample().OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.WorkingSet).Take(12).ToList();
        OnUi(() =>
        {
            OnPropertyChanged(string.Empty);
            if (top is null) return;
            TopProcesses.Clear();
            foreach (var p in top) TopProcesses.Add(p);
        });
    }
}

// ───────────────────────── Benchmark ─────────────────────────

public sealed record MetricRow(string Name, string Unit, string Explanation, string Before, string After, string Delta, string Verdict, string Status);

public sealed class BenchmarkViewModel : PageViewModel
{
    private BenchmarkResult? _before, _after;
    private string _label = "";
    private double _progress;
    public override string Key => "benchmark";

    public BenchmarkViewModel()
    {
        RunCommand = new AsyncCommand(RunAsync, () => !IsBusy);
        DeleteCommand = new RelayCommand(p =>
        {
            var r = (BenchmarkResult)p!;
            if (!ConfirmDialog.Show(Loc.Get("DeleteResult"), r.Label + " · " + r.Time.ToString("g"), confirmText: Loc.Get("Delete"), danger: true)) return;
            BenchmarkRunner.Delete(r.Id);
            LoadHistory();
        });
    }

    public ObservableCollection<BenchmarkResult> History { get; } = [];
    public ObservableCollection<MetricRow> Rows { get; } = [];
    public string Label { get => _label; set => Set(ref _label, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public BenchmarkResult? Before { get => _before; set { if (Set(ref _before, value)) Compare(); } }
    public BenchmarkResult? After { get => _after; set { if (Set(ref _after, value)) Compare(); } }
    public bool CanCompare => History.Count >= 1;
    public string Notes => After is null ? "" : string.Join(Environment.NewLine, After.Notes);
    public ICommand RunCommand { get; }
    public ICommand DeleteCommand { get; }

    protected override Task LoadAsync()
    {
        LoadHistory();
        return Task.CompletedTask;
    }

    private void LoadHistory()
    {
        History.Clear();
        foreach (var r in BenchmarkRunner.History().OrderByDescending(r => r.Time)) History.Add(r);
        _after = History.FirstOrDefault();
        _before = History.Skip(1).FirstOrDefault();
        OnPropertiesChanged(nameof(Before), nameof(After), nameof(CanCompare));
        Compare();
    }

    private async Task RunAsync()
    {
        if (!ConfirmDialog.Show(Loc.Get("RunBenchmark"), Loc.Get("BenchmarkIntro"), [Loc.Get("BenchmarkTip1"), Loc.Get("BenchmarkTip2"), Loc.Get("BenchmarkTip3")], Loc.Get("Start")))
            return;
        var label = string.IsNullOrWhiteSpace(Label) ? $"{Loc.Get("Run")} {DateTime.Now:g}" : Label.Trim();
        await Busy(Loc.Get("BenchmarkRunning"), async () =>
        {
            var progress = new Progress<(string Step, double Fraction)>(p => { BusyText = p.Step; Progress = p.Fraction * 100; });
            var result = await BenchmarkRunner.RunAsync(label, progress);
            BenchmarkRunner.Save(result);
        });
        Progress = 0;
        Label = "";
        LoadHistory();
    }

    private void Compare()
    {
        Rows.Clear();
        foreach (var m in BenchmarkRunner.Metrics)
        {
            string fmt(BenchmarkResult? r) => r is null ? "—" : m.Value(r).ToString(m.Value(r) >= 100 ? "0" : "0.0#");
            if (Before is not null && After is not null && Before.Id != After.Id)
            {
                var (delta, verdict) = BenchmarkRunner.Compare(m, Before, After);
                var status = verdict.StartsWith("Improved") ? "good" : verdict.StartsWith("Worse") ? "bad" : "";
                Rows.Add(new(m.Name, m.Unit, m.Explanation, fmt(Before), fmt(After), $"{delta:+0.0;-0.0;0} %", verdict, status));
            }
            else Rows.Add(new(m.Name, m.Unit, m.Explanation, fmt(Before), fmt(After), "", "", ""));
        }
        OnPropertyChanged(nameof(Notes));
    }
}

// ───────────────────────── History & rollback ─────────────────────────

public sealed class EntryViewModel(JournalEntry entry, ICommand undo)
{
    public JournalEntry Entry => entry;
    public string Name => entry.TweakName;
    public string Description => entry.Description;
    public string Change => entry.OldValue is null && entry.NewValue is null ? "" : $"{entry.OldValue ?? "—"} → {entry.NewValue ?? "—"}";
    public string Status => Loc.Get("Entry" + entry.Status);
    public string Severity => entry.Status switch
    {
        EntryStatus.Applied => "good",
        EntryStatus.RolledBack => "",
        EntryStatus.Irreversible => "warn",
        _ => "bad",
    };
    public string? Error => entry.Error;
    public string? RestoreHint => entry.RestoreHint;
    public bool CanUndo => entry.CanRollBack;
    public ICommand UndoCommand => undo;
}

public sealed class SessionViewModel(JournalSession session, ICommand undoSession, ICommand undoEntry)
{
    public JournalSession Session => session;
    public string Title => session.Title;
    public string Time => session.StartedUtc.ToLocalTime().ToString("g");
    public string Meta => $"{session.Origin}{(session.Elevated ? " · admin" : "")}{(session.RestorePoint is { } rp ? " · " + rp : "")}";
    public List<EntryViewModel> Entries { get; } = session.Entries.Select(e => new EntryViewModel(e, undoEntry)).ToList();
    public bool CanUndo => session.HasRollbackableEntries;
    public ICommand UndoCommand => undoSession;
}

public sealed class HistoryViewModel : PageViewModel
{
    public override string Key => "history";

    public HistoryViewModel()
    {
        UndoSessionCommand = new AsyncCommand(p => UndoSessionAsync((SessionViewModel)p!));
        UndoEntryCommand = new AsyncCommand(p => UndoEntryAsync((EntryViewModel)p!));
        UndoAllCommand = new AsyncCommand(UndoAllAsync, () => ActiveCount > 0);
        ExportCommand = new RelayCommand(Export);
        RestorePointCommand = new AsyncCommand(CreateRestorePointAsync);
        OpenLogsCommand = new RelayCommand(() => ProcessRunner.ShellOpen(AppPaths.Logs));
        OpenSystemRestoreCommand = new RelayCommand(() => ProcessRunner.ShellOpen(ProcessRunner.System32("rstrui.exe")));
        AppServices.Journal.Changed += (_, _) => OnUi(Load);
    }

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];
    public int ActiveCount { get; private set; }
    public string ActiveText => string.Format(Loc.Get("ActiveChanges"), ActiveCount);
    public ICommand UndoSessionCommand { get; }
    public ICommand UndoEntryCommand { get; }
    public ICommand UndoAllCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand RestorePointCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenSystemRestoreCommand { get; }

    protected override Task LoadAsync()
    {
        Load();
        return Task.CompletedTask;
    }

    private void Load()
    {
        Sessions.Clear();
        foreach (var s in AppServices.Journal.Sessions.OrderByDescending(s => s.StartedUtc))
            Sessions.Add(new SessionViewModel(s, UndoSessionCommand, UndoEntryCommand));
        ActiveCount = AppServices.Journal.ActiveEntries().Count;
        OnPropertiesChanged(nameof(ActiveCount), nameof(ActiveText));
    }

    private static bool NeedsAdminFor(IEnumerable<JournalEntry> entries) =>
        !Elevation.IsElevated && entries.Any(e => e.ActionKey.StartsWith("reg:HKLM", StringComparison.OrdinalIgnoreCase) || e.ActionKey.StartsWith("task:") || e.ActionKey.StartsWith("power"));

    private async Task UndoSessionAsync(SessionViewModel s)
    {
        var entries = s.Session.Entries.Where(e => e.CanRollBack).ToList();
        var details = entries.Select(e => $"{e.TweakName}: {e.Description}").ToList();
        if (NeedsAdminFor(entries)) details.Insert(0, Loc.Get("UndoMayNeedAdmin"));
        if (!ConfirmDialog.Show(Loc.Get("UndoSessionTitle"), string.Format(Loc.Get("UndoSessionMessage"), s.Title), details, Loc.Get("Undo"))) return;
        await Busy(Loc.Get("Restoring"), async () => TweakApplier.ReportRollback(await AppServices.Engine.RollbackSessionAsync(s.Session.Id)));
        AppServices.NotifyConfigurationChanged();
    }

    private async Task UndoEntryAsync(EntryViewModel e)
    {
        if (!ConfirmDialog.Show(Loc.Get("UndoTitle"), string.Format(Loc.Get("UndoTweakMessage"), e.Name), [e.Description, e.Change], Loc.Get("Undo"))) return;
        await Busy(Loc.Get("Restoring"), async () => TweakApplier.ReportRollback(await AppServices.Engine.RollbackEntryAsync(e.Entry.Id)));
        AppServices.NotifyConfigurationChanged();
    }

    private async Task UndoAllAsync()
    {
        var active = AppServices.Journal.ActiveEntries();
        var details = active.Select(a => $"{a.Entry.TweakName}: {a.Entry.Description}").ToList();
        if (NeedsAdminFor(active.Select(a => a.Entry))) details.Insert(0, Loc.Get("UndoMayNeedAdmin"));
        if (!ConfirmDialog.Show(Loc.Get("UndoAllTitle"), string.Format(Loc.Get("UndoAllMessage"), active.Count), details, Loc.Get("Continue"), danger: true)) return;
        if (!ConfirmDialog.Show(Loc.Get("UndoAllTitle"), Loc.Get("UndoAllConfirm2"), confirmText: Loc.Get("UndoAll"), danger: true)) return;
        await Busy(Loc.Get("Restoring"), async () =>
        {
            TweakApplier.ReportRollback(await AppServices.Engine.RollbackAllAsync());
            await AppServices.Profiles.RestorePreviousAsync(); // clears profile state; its entries were already restored
        });
        AppServices.NotifyConfigurationChanged();
    }

    private void Export()
    {
        try
        {
            var path = AppServices.Journal.Export();
            ProcessRunner.RevealInExplorer(path);
            Notifier.Success(string.Format(Loc.Get("Exported"), path));
        }
        catch (Exception ex) { Notifier.Error(ex.Message); }
    }

    private async Task CreateRestorePointAsync()
    {
        if (!Elevation.IsElevated) { Notifier.Warning(Loc.Get("NeedsAdmin")); return; }
        await Busy(Loc.Get("CreatingRestorePoint"), async () =>
        {
            var (ok, message) = await RestorePoint.CreateAsync("Windows Ultimate: manual restore point");
            if (ok) Notifier.Success(message); else Notifier.Warning(message);
        });
    }
}

// ───────────────────────── Settings ─────────────────────────

public sealed class SettingsViewModel : PageViewModel
{
    public override string Key => "settings";

    public SettingsViewModel()
    {
        OpenDataCommand = new RelayCommand(() => ProcessRunner.ShellOpen(AppPaths.Root));
        CheckUpdatesCommand = new AsyncCommand(() => Updater.CheckAsync(interactive: true));
        OpenReleasesCommand = new RelayCommand(() => ProcessRunner.ShellOpen(Core.Updates.UpdateService.ReleasesPage), () => Core.Updates.UpdateService.IsConfigured);
    }

    public bool CheckForUpdates
    {
        get => AppServices.Settings.CheckForUpdates;
        set { AppServices.Settings.CheckForUpdates = value; AppServices.Settings.Save(); OnPropertyChanged(); }
    }

    public bool Translucency
    {
        get => AppServices.Settings.Translucency;
        set
        {
            AppServices.Settings.Translucency = value;
            AppServices.Settings.Save();
            (System.Windows.Application.Current.MainWindow as MainWindow)?.SetTranslucency(value);
            OnPropertiesChanged(nameof(Translucency), nameof(TranslucencyState));
        }
    }

    /// <summary>Explains why the backdrop is off when it was requested but Windows cannot provide it.</summary>
    public string TranslucencyState => (System.Windows.Application.Current.MainWindow as MainWindow)?.IsTranslucent == true
        ? Loc.Get("TranslucencyOn")
        : Translucency ? Loc.Get("TranslucencyUnavailable") : Loc.Get("TranslucencyOff");

    public string UpdateSource => Core.Updates.UpdateService.IsConfigured ? "github.com/" + Core.Updates.UpdateService.Repository : "—";
    public ICommand CheckUpdatesCommand { get; }
    public ICommand OpenReleasesCommand { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Languages { get; } =
        [new("en", "English"), new("fr", "Français")];

    public string Language
    {
        get => AppServices.Settings.Language;
        set
        {
            if (value == AppServices.Settings.Language) return;
            AppServices.Settings.Language = value;
            AppServices.Settings.Save();
            Loc.Instance.SetLanguage(value);
            OnPropertyChanged(string.Empty);
            Notifier.Info(Loc.Get("LanguageChanged"));
        }
    }

    public bool CreateRestorePoint
    {
        get => AppServices.Settings.CreateRestorePointByDefault;
        set { AppServices.Settings.CreateRestorePointByDefault = value; AppServices.Settings.Save(); OnPropertyChanged(); }
    }

    public string DataFolder => AppPaths.Root;
    public string Methodology => ScanReport.Methodology;
    public string Version => "v" + Core.Updates.UpdateService.CurrentVersion.ToString(3);
    public bool IsElevated => Elevation.IsElevated;
    public ICommand OpenDataCommand { get; }
}
