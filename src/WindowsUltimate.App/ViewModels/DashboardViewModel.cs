using System.Collections.ObjectModel;
using System.Windows.Input;
using WindowsUltimate.App.Dialogs;
using WindowsUltimate.App.Localization;
using WindowsUltimate.App.Mvvm;
using WindowsUltimate.App.Services;
using WindowsUltimate.Core.Diagnostics;
using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Monitoring;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.App.ViewModels;

public sealed class DashboardViewModel : PageViewModel
{
    private IDisposable? _monitor;
    private readonly List<double> _cpu = [], _gpu = [], _ram = [];
    private SystemProfile? _profile;
    private ScanReport? _report;
    private List<TweakEvaluation> _safe = [];
    private string _powerPlan = "—";
    private int _startupCount;
    private int _processCount;
    private bool _showMethodology;

    public override string Key => "dashboard";

    public DashboardViewModel()
    {
        QuickScanCommand = new AsyncCommand(() => ScanAsync(false), () => !IsBusy);
        FullScanCommand = new AsyncCommand(() => ScanAsync(true), () => !IsBusy);
        ApplySafeCommand = new AsyncCommand(ApplySafeAsync, () => _safe.Count > 0 && !IsBusy);
        ToggleMethodologyCommand = new RelayCommand(() => ShowMethodology = !ShowMethodology);
        AppServices.ConfigurationChanged += () => OnUi(async () => await RefreshRecommendationsAsync());
    }

    public ICommand QuickScanCommand { get; }
    public ICommand FullScanCommand { get; }
    public ICommand ApplySafeCommand { get; }
    public ICommand ToggleMethodologyCommand { get; }

    // ── System summary ──
    public SystemProfile? Profile { get => _profile; private set { Set(ref _profile, value); OnPropertiesChanged(nameof(CpuName), nameof(GpuName), nameof(MemoryText), nameof(WindowsText), nameof(BoardText), nameof(BiosText), nameof(StorageText), nameof(DisplayText)); } }
    public string CpuName => _profile?.Cpu.Name ?? "…";
    public string GpuName => _profile?.PrimaryGpu?.Name ?? "…";
    public string MemoryText => _profile is null ? "…" :
        $"{Format.Bytes(_profile.Memory.TotalBytes)} {_profile.Memory.Type}{(_profile.Memory.ConfiguredMhz > 0 ? $" · {_profile.Memory.ConfiguredMhz} MT/s" : "")} · {_profile.Memory.Modules.Count} module(s)";
    public string WindowsText => _profile is null ? "…" : $"{_profile.Os.Caption} {_profile.Os.DisplayVersion} · build {_profile.Os.BuildString}{(_profile.Os.IsInsider ? " · Insider" : "")}";
    public string BoardText => _profile is null ? "…" : $"{_profile.Board.Manufacturer} {_profile.Board.Product}".Trim();
    public string BiosText => _profile is null ? "…" : $"{_profile.Board.BiosVersion}{(_profile.Board.BiosDate is { } d ? $" ({d:d})" : "")}";
    public string DisplayText => _profile?.Displays.FirstOrDefault(d => d.IsPrimary) is { } d ? $"{d.Width}×{d.Height} @ {d.CurrentHz} Hz" : "—";
    public string StorageText
    {
        get
        {
            try
            {
                var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
                return $"{Format.Bytes(sys.AvailableFreeSpace)} free of {Format.Bytes(sys.TotalSize)}";
            }
            catch { return "—"; }
        }
    }
    public double StorageUsedPercent
    {
        get
        {
            try
            {
                var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
                return 100.0 * (sys.TotalSize - sys.AvailableFreeSpace) / sys.TotalSize;
            }
            catch { return 0; }
        }
    }
    public string PowerPlan { get => _powerPlan; private set => Set(ref _powerPlan, value); }
    public int StartupCount { get => _startupCount; private set => Set(ref _startupCount, value); }
    public int ProcessCount { get => _processCount; private set => Set(ref _processCount, value); }
    public string Uptime => Format.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64));

    // ── Live ──
    public double CpuPercent { get; private set; }
    public double? GpuPercent { get; private set; }
    public double RamPercent { get; private set; }
    public string RamUsedText { get; private set; } = "";
    public string CpuTemp { get; private set; } = "";
    public string GpuTemp { get; private set; } = "";
    public string VramText { get; private set; } = "";
    public IReadOnlyList<double> CpuHistory => _cpu.ToArray();
    public IReadOnlyList<double> GpuHistory => _gpu.ToArray();
    public IReadOnlyList<double> RamHistory => _ram.ToArray();

    // ── Scan ──
    public ScanReport? Report { get => _report; private set { Set(ref _report, value); OnPropertiesChanged(nameof(HasReport), nameof(Score), nameof(ScoreLabel), nameof(ScanSummary), nameof(Penalties), nameof(Findings), nameof(IsFullScan)); } }
    public bool HasReport => _report is not null;
    public int Score => _report?.Score ?? 0;
    public string ScoreLabel => _report is null ? Loc.Get("NotScanned") : Loc.Get("Score" + _report.ScoreLabel.Replace(" ", ""));
    public bool IsFullScan => _report?.IsFull ?? false;
    public string ScanSummary => _report is null ? "" :
        string.Format(Loc.Get("ScanSummary"), _report.IsFull ? Loc.Get("FullAnalysis") : Loc.Get("QuickScan"), _report.Time.ToString("t"), _report.Duration.TotalSeconds.ToString("0.0"));
    public IEnumerable<Finding> Penalties => _report?.Penalties ?? [];
    public ObservableCollection<FindingViewModel> Findings { get; } = [];
    public string Methodology => ScanReport.Methodology;
    public bool ShowMethodology { get => _showMethodology; set => Set(ref _showMethodology, value); }

    public int SafeCount => _safe.Count;
    public string SafeSummary => _safe.Count == 0 ? Loc.Get("NoSafeRecommendations") : string.Join(" · ", _safe.Take(4).Select(s => s.Tweak.Name)) + (_safe.Count > 4 ? $" +{_safe.Count - 4}" : "");

    protected override async Task LoadAsync()
    {
        Profile = await AppServices.ProfileTask;
        _ = Task.Run(() =>
        {
            var plan = SystemChecks.PowerPlan()?.Name ?? "—";
            var processes = SystemChecks.Memory().Processes;
            OnUi(() => { PowerPlan = plan; ProcessCount = processes; });
        });
        await ScanAsync(false);
    }

    public override void OnActivated()
    {
        _monitor ??= AppServices.Monitor.Subscribe();
        AppServices.Monitor.Sampled += OnSample;
        OnPropertyChanged(nameof(Uptime));
    }

    public override void OnDeactivated()
    {
        AppServices.Monitor.Sampled -= OnSample;
        _monitor?.Dispose();
        _monitor = null;
    }

    private void OnSample(object? sender, PerfSample s) => OnUi(() =>
    {
        Push(_cpu, s.CpuPercent);
        Push(_ram, s.RamPercent);
        if (s.GpuPercent is { } g) Push(_gpu, g);
        CpuPercent = s.CpuPercent;
        GpuPercent = s.GpuPercent;
        RamPercent = s.RamPercent;
        RamUsedText = $"{Format.Bytes(s.RamUsedBytes)} / {Format.Bytes(s.RamTotalBytes)}";
        CpuTemp = s.CpuTempC is { } ct ? $"{ct:0}°C ACPI" : "";
        GpuTemp = s.GpuTempC is { } gt ? $"{gt:0}°C" : "";
        VramText = s.VramUsedBytes is { } v ? $"VRAM {Format.Bytes(v)}{(s.VramTotalBytes is { } vt ? " / " + Format.Bytes(vt) : "")}" : "";
        OnPropertiesChanged(nameof(CpuPercent), nameof(GpuPercent), nameof(RamPercent), nameof(RamUsedText), nameof(CpuTemp), nameof(GpuTemp), nameof(VramText),
            nameof(CpuHistory), nameof(GpuHistory), nameof(RamHistory));
    });

    private static void Push(List<double> list, double v)
    {
        list.Add(v);
        if (list.Count > 60) list.RemoveAt(0);
    }

    private async Task ScanAsync(bool full)
    {
        await Busy(full ? Loc.Get("RunningFullAnalysis") : Loc.Get("RunningQuickScan"), async () =>
        {
            var report = await AppServices.RunScanAsync(full, BusyProgress);
            Findings.Clear();
            foreach (var f in report.Findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.ScorePenalty))
                Findings.Add(new FindingViewModel(f));
            Report = report;
            StartupCount = AppServices.LastScanContext?.EnabledStartup ?? 0;
            await RefreshRecommendationsAsync();
        });
    }

    private async Task RefreshRecommendationsAsync()
    {
        var profile = await AppServices.ProfileTask;
        _safe = await Task.Run(() => AppServices.Engine.Rank(profile).Where(e => e.IsSafeRecommendation && !e.Tweak.RequiresAdmin || e.IsSafeRecommendation && Elevation.IsElevated).ToList());
        OnPropertiesChanged(nameof(SafeCount), nameof(SafeSummary));
    }

    private async Task ApplySafeAsync()
    {
        var summary = await TweakApplier.ApplyAsync(_safe.Select(s => s.Tweak).ToList(), Loc.Get("ApplySafeTitle"), "dashboard:safe");
        if (summary is not null) await ScanAsync(_report?.IsFull ?? false);
    }
}
