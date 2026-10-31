using System.Collections.ObjectModel;
using System.Windows.Input;
using WindowsUltimate.App.Dialogs;
using WindowsUltimate.App.Localization;
using WindowsUltimate.App.Mvvm;
using WindowsUltimate.App.Services;
using WindowsUltimate.Core.Diagnostics;
using WindowsUltimate.Core.Drivers;
using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Network;
using WindowsUltimate.Core.Optimization;
using WindowsUltimate.Core.Repair;
using WindowsUltimate.Core.Storage;

namespace WindowsUltimate.App.ViewModels;

// ───────────────────────── Storage ─────────────────────────

public sealed class CleanupItemViewModel(CleanupCategory category, Action changed) : ObservableObject
{
    private bool _isSelected = category.SelectedByDefault && category.Available;
    public CleanupCategory Category => category;
    public string Name => category.Name;
    public string Description => category.Description;
    public string? Note => category.Note;
    public bool IsCaution => category.Safety == CleanupSafety.Caution;
    public bool Available => category.Available;
    public string SizeText => category.Bytes > 0 ? Format.Bytes(category.Bytes) : "—";
    public string FilesText => category.IsRecycleBin ? $"{category.FileCount} item(s)" : $"{category.FileCount} file(s)";
    public bool IsSelected { get => _isSelected; set { if (Set(ref _isSelected, value && Available)) changed(); } }
    public void Refresh() => OnPropertiesChanged(nameof(SizeText), nameof(FilesText), nameof(Note));
}

public sealed class DuplicateGroupViewModel(DuplicateGroup group)
{
    public DuplicateGroup Group => group;
    public string Header => $"{group.Files.Count} × {Format.Bytes(group.FileSize)} — {Format.Bytes(group.Wasted)} wasted";
    public List<LargeFile> Files => group.Files;
}

public sealed class StorageViewModel : PageViewModel
{
    private string _scanRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string _cleanupSummary = "";
    private long _selectedBytes;
    private CancellationTokenSource? _cts;
    public override string Key => "storage";

    public StorageViewModel()
    {
        ScanCleanupCommand = new AsyncCommand(() => Busy(Loc.Get("ScanningCleanup"), ScanCleanupAsync));
        CleanCommand = new AsyncCommand(CleanAsync, () => _selectedBytes > 0 && !IsBusy);
        FindLargeCommand = new AsyncCommand(FindLargeAsync);
        FindDuplicatesCommand = new AsyncCommand(FindDuplicatesAsync);
        FindFoldersCommand = new AsyncCommand(FindFoldersAsync);
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        RevealCommand = new RelayCommand(p => { if (p is string s) ProcessRunner.RevealInExplorer(s); });
        RecycleCommand = new AsyncCommand(p => RecycleAsync((LargeFile)p!));
        OpenAppsSettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:appsfeatures"));
        OpenStorageSenseCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:storagesense"));
    }

    public ObservableCollection<DriveUsage> Drives { get; } = [];
    public ObservableCollection<CleanupItemViewModel> Cleanup { get; } = [];
    public ObservableCollection<LargeFile> LargeFiles { get; } = [];
    public ObservableCollection<DuplicateGroupViewModel> Duplicates { get; } = [];
    public ObservableCollection<FolderSize> Folders { get; } = [];
    public ObservableCollection<InstalledApp> Apps { get; } = [];
    public ObservableCollection<CleanupHistoryEntry> History { get; } = [];

    public string ScanRoot { get => _scanRoot; set => Set(ref _scanRoot, value); }
    public string CleanupSummary { get => _cleanupSummary; private set => Set(ref _cleanupSummary, value); }
    public string SelectedText => Format.Bytes(_selectedBytes);
    public bool HasLargeFiles => LargeFiles.Count > 0;
    public bool HasDuplicates => Duplicates.Count > 0;

    public ICommand ScanCleanupCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand FindLargeCommand { get; }
    public ICommand FindDuplicatesCommand { get; }
    public ICommand FindFoldersCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand RecycleCommand { get; }
    public ICommand OpenAppsSettingsCommand { get; }
    public ICommand OpenStorageSenseCommand { get; }

    protected override async Task LoadAsync()
    {
        RefreshDrives();
        foreach (var c in CleanupService.Categories()) Cleanup.Add(new CleanupItemViewModel(c, UpdateSelected));
        LoadHistory();
        var apps = await Task.Run(StorageAnalyzer.InstalledApps);
        foreach (var a in apps.OrderByDescending(a => a.SizeBytes)) Apps.Add(a);
        await Busy(Loc.Get("ScanningCleanup"), ScanCleanupAsync);
    }

    private void RefreshDrives()
    {
        Drives.Clear();
        foreach (var d in StorageAnalyzer.Drives()) Drives.Add(d);
    }

    private void LoadHistory()
    {
        History.Clear();
        foreach (var h in CleanupService.History().OrderByDescending(h => h.Time).Take(20)) History.Add(h);
    }

    private void UpdateSelected()
    {
        _selectedBytes = Cleanup.Where(c => c.IsSelected).Sum(c => c.Category.Bytes);
        OnPropertyChanged(nameof(SelectedText));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ScanCleanupAsync()
    {
        var cats = Cleanup.Where(c => c.Available).Select(c => c.Category).ToList();
        await CleanupService.ScanAsync(cats, BusyProgress);
        foreach (var c in Cleanup) c.Refresh();
        var total = cats.Sum(c => c.Bytes);
        CleanupSummary = string.Format(Loc.Get("CleanupFound"), Format.Bytes(total));
        UpdateSelected();
    }

    private async Task CleanAsync()
    {
        var selected = Cleanup.Where(c => c.IsSelected).ToList();
        var details = selected.Select(c => $"{c.Name}: {c.SizeText} ({c.FilesText}){(c.IsCaution ? " — " + Loc.Get("Caution") + ": " + c.Description : "")}");
        if (!ConfirmDialog.Show(Loc.Get("CleanupConfirmTitle"), string.Format(Loc.Get("CleanupConfirmMessage"), SelectedText), details, Loc.Get("CleanNow"),
                acknowledge: selected.Any(c => c.IsCaution) ? Loc.Get("CleanupCautionAck") : null))
            return;
        List<CleanupResult> results = [];
        await Busy(Loc.Get("Cleaning"), async () =>
        {
            results = await CleanupService.CleanAsync(selected.Select(c => c.Category), BusyProgress);
            await CleanupService.ScanAsync(selected.Select(c => c.Category));
        });
        foreach (var c in Cleanup) c.Refresh();
        UpdateSelected();
        RefreshDrives();
        LoadHistory();
        var freed = results.Sum(r => r.FreedBytes);
        var skipped = results.Sum(r => r.Skipped);
        Notifier.Success(string.Format(Loc.Get("CleanupDone"), Format.Bytes(freed), skipped));
        Log.Audit($"Cleanup freed {freed} bytes, skipped {skipped} in-use files");
    }

    private async Task FindLargeAsync()
    {
        if (!Directory.Exists(ScanRoot)) { Notifier.Warning(Loc.Get("FolderNotFound")); return; }
        _cts = new CancellationTokenSource();
        await Busy(Loc.Get("SearchingLargeFiles"), async () =>
        {
            var files = await StorageAnalyzer.LargeFilesAsync(ScanRoot, 100L * 1024 * 1024, 200, null, _cts.Token);
            LargeFiles.Clear();
            foreach (var f in files) LargeFiles.Add(f);
        });
        OnPropertyChanged(nameof(HasLargeFiles));
    }

    private async Task FindFoldersAsync()
    {
        if (!Directory.Exists(ScanRoot)) { Notifier.Warning(Loc.Get("FolderNotFound")); return; }
        _cts = new CancellationTokenSource();
        await Busy(Loc.Get("MeasuringFolders"), async () =>
        {
            var folders = await StorageAnalyzer.TopFoldersAsync(ScanRoot, BusyProgress, _cts.Token);
            Folders.Clear();
            foreach (var f in folders) Folders.Add(f);
        });
    }

    private async Task FindDuplicatesAsync()
    {
        if (!Directory.Exists(ScanRoot)) { Notifier.Warning(Loc.Get("FolderNotFound")); return; }
        _cts = new CancellationTokenSource();
        await Busy(Loc.Get("SearchingDuplicates"), async () =>
        {
            var groups = await StorageAnalyzer.DuplicatesAsync(ScanRoot, 1024 * 1024, BusyProgress, _cts.Token);
            Duplicates.Clear();
            foreach (var g in groups.OrderByDescending(g => g.Wasted).Take(100)) Duplicates.Add(new DuplicateGroupViewModel(g));
        });
        OnPropertyChanged(nameof(HasDuplicates));
    }

    /// <summary>Sends a file the user picked to the Recycle Bin (recoverable). Never permanently deletes.</summary>
    private async Task RecycleAsync(LargeFile file)
    {
        var group = Duplicates.FirstOrDefault(g => g.Files.Contains(file));
        if (group is not null && group.Files.Count(f => File.Exists(f.Path)) <= 1)
        {
            Notifier.Warning(Loc.Get("KeepOneCopy"));
            return;
        }
        if (!ConfirmDialog.Show(Loc.Get("RecycleTitle"), Loc.Get("RecycleMessage"), [file.Path, Format.Bytes(file.Bytes)], Loc.Get("MoveToRecycleBin")))
            return;
        try
        {
            await Task.Run(() => StorageAnalyzer.Recycle(file.Path));
            Log.Audit($"Moved to Recycle Bin at user request: {file.Path}");
            LargeFiles.Remove(file);
            if (group is not null)
            {
                group.Files.Remove(file);
                if (group.Files.Count <= 1) Duplicates.Remove(group);
                else { var i = Duplicates.IndexOf(group); Duplicates[i] = new DuplicateGroupViewModel(group.Group); }
            }
            Notifier.Success(Loc.Get("Recycled"));
            RefreshDrives();
        }
        catch (Exception ex) { Notifier.Error(ex.Message); }
    }
}

// ───────────────────────── Network ─────────────────────────

public sealed class NetworkViewModel : PageViewModel
{
    private PingStats? _gateway, _internet;
    private string _diagnosis = "";
    private List<double> _pingSeries = [];
    private CancellationTokenSource? _cts;
    public override string Key => "network";

    public NetworkViewModel()
    {
        PingCommand = new AsyncCommand(RunPingAsync, () => !IsBusy);
        DnsCommand = new AsyncCommand(CompareDnsAsync, () => !IsBusy);
        FlushDnsCommand = new AsyncCommand(FlushDnsAsync);
        ResetCommand = new AsyncCommand(ResetStackAsync);
        RefreshCommand = new RelayCommand(LoadAdapters);
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        OpenDnsSettingsCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:network-status"));
    }

    public ObservableCollection<AdapterInfo> Adapters { get; } = [];
    public ObservableCollection<WifiStatus> Wifi { get; } = [];
    public ObservableCollection<CheckItem> WifiAdvice { get; } = [];
    public ObservableCollection<DnsResult> Dns { get; } = [];
    public PingStats? Gateway { get => _gateway; private set => Set(ref _gateway, value); }
    public PingStats? Internet { get => _internet; private set => Set(ref _internet, value); }
    public string Diagnosis { get => _diagnosis; private set => Set(ref _diagnosis, value); }
    public IReadOnlyList<double> PingSeries => _pingSeries.ToArray();
    public string DnsNote => Loc.Get("DnsNote");

    public ICommand PingCommand { get; }
    public ICommand DnsCommand { get; }
    public ICommand FlushDnsCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenDnsSettingsCommand { get; }

    protected override Task LoadAsync()
    {
        LoadAdapters();
        return Task.CompletedTask;
    }

    private void LoadAdapters()
    {
        Adapters.Clear();
        foreach (var a in NetworkDiagnostics.Adapters()) Adapters.Add(a);
        Wifi.Clear();
        WifiAdvice.Clear();
        foreach (var w in WifiInfo.Query())
        {
            Wifi.Add(w);
            var advice = WifiInfo.Advice(w);
            WifiAdvice.Add(new CheckItem($"{w.Ssid} · {w.Band} · {Loc.Get("Channel")} {w.Channel}", $"{w.SignalQuality} %{(w.Rssi is { } r ? $" ({r} dBm)" : "")}",
                w.SignalQuality >= 70 ? "good" : w.SignalQuality >= 45 ? "warn" : "bad", advice));
        }
    }

    private async Task RunPingAsync()
    {
        _cts = new CancellationTokenSource();
        var adapter = Adapters.FirstOrDefault(a => a.Gateways.Count > 0);
        var wireless = adapter?.IsWireless ?? false;
        _pingSeries = [];
        await Busy(Loc.Get("PingRunning"), async () =>
        {
            if (adapter?.Gateways.FirstOrDefault() is { } gw)
                Gateway = await NetworkDiagnostics.PingAsync(gw, Loc.Get("Router"), 30, TimeSpan.FromMilliseconds(200), null, _cts.Token);
            var progress = new Progress<PingStats>(s =>
            {
                _pingSeries = s.Samples.Select(x => x ?? 0).ToList();
                OnPropertyChanged(nameof(PingSeries));
                BusyText = $"{Loc.Get("PingRunning")} {s.Sent}/60";
            });
            Internet = await NetworkDiagnostics.PingAsync("1.1.1.1", "Internet (1.1.1.1)", 60, TimeSpan.FromMilliseconds(250), progress, _cts.Token);
            Diagnosis = NetworkDiagnostics.Diagnose(Gateway, Internet, wireless);
        });
    }

    private async Task CompareDnsAsync()
    {
        _cts = new CancellationTokenSource();
        await Busy(Loc.Get("DnsRunning"), async () =>
        {
            var results = await NetworkDiagnostics.CompareDnsAsync(BusyProgress, _cts.Token);
            Dns.Clear();
            foreach (var r in results.OrderBy(r => r.MedianMs ?? double.MaxValue)) Dns.Add(r);
        });
    }

    private static async Task FlushDnsAsync()
    {
        var r = await NetworkDiagnostics.FlushDnsAsync();
        if (r.Success) Notifier.Success(Loc.Get("DnsFlushed"));
        else Notifier.Error(r.StdOut + r.StdErr);
    }

    private async Task ResetStackAsync()
    {
        if (!Elevation.IsElevated) { Notifier.Warning(Loc.Get("NeedsAdmin")); return; }
        if (!ConfirmDialog.Show(Loc.Get("ResetNetworkTitle"), Loc.Get("ResetNetworkMessage"), [Loc.Get("ResetNetworkDetail1"), Loc.Get("ResetNetworkDetail2"), Loc.Get("ResetNetworkDetail3")],
                Loc.Get("Continue"), danger: true))
            return;
        if (!ConfirmDialog.Show(Loc.Get("ResetNetworkTitle"), Loc.Get("ResetNetworkConfirm2"), confirmText: Loc.Get("ResetNetwork"), danger: true,
                acknowledge: Loc.Get("ResetNetworkAck")))
            return;
        await Busy(Loc.Get("ResettingNetwork"), async () =>
        {
            var (ok, output) = await NetworkDiagnostics.ResetStackAsync();
            AppServices.Journal.RecordIrreversible(Loc.Get("ResetNetworkTitle"), "network",
                [("Winsock and TCP/IP reset", "netsh winsock reset; netsh int ip reset", "Reconfigure static IP/DNS/VPN settings manually if you had any.")]);
            if (ok) Notifier.Success(Loc.Get("NetworkResetDone"));
            else Notifier.Error(output);
        });
    }
}

// ───────────────────────── Repair ─────────────────────────

public sealed class RepairViewModel : PageViewModel
{
    private string _console = "";
    private string _running = "";
    private CancellationTokenSource? _cts;
    public override string Key => "repair";

    public RepairViewModel()
    {
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _cts is not null);
        RunToolCommand = new AsyncCommand(p => RunToolAsync((RepairTool)p!), _ => _cts is null);
        CheckUpdatesCommand = new AsyncCommand(CheckUpdatesAsync);
        OpenWindowsUpdateCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:windowsupdate"));
        OpenTroubleshootCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:troubleshoot"));
        OpenReliabilityCommand = new RelayCommand(() => ProcessRunner.ShellOpen(ProcessRunner.System32("perfmon.exe"), "/rel"));
        ScanEventsCommand = new AsyncCommand(() => Busy(Loc.Get("ReadingEvents"), ScanEventsAsync));
    }

    public IReadOnlyList<RepairTool> Tools { get; } = RepairTools.All;
    public ObservableCollection<ServiceState> Services { get; } = [];
    public ObservableCollection<DiskReliability> Disks { get; } = [];
    public ObservableCollection<EventCategory> Events { get; } = [];
    public ObservableCollection<string> PendingUpdates { get; } = [];
    public ObservableCollection<CheckItem> Status { get; } = [];
    public string Console { get => _console; private set => Set(ref _console, value); }
    public string Running { get => _running; private set => Set(ref _running, value); }
    public bool IsRunning => _cts is not null;
    public string UpdatesText { get; private set; } = "";

    public ICommand CancelCommand { get; }
    public ICommand RunToolCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand OpenWindowsUpdateCommand { get; }
    public ICommand OpenTroubleshootCommand { get; }
    public ICommand OpenReliabilityCommand { get; }
    public ICommand ScanEventsCommand { get; }

    protected override async Task LoadAsync()
    {
        var (services, disks, reboot) = await Task.Run(() => (UpdateHealth.ServiceStates(), UpdateHealth.Reliability(), SystemChecks.PendingReboot()));
        foreach (var s in services) Services.Add(s);
        foreach (var d in disks) Disks.Add(d);
        Status.Add(new(Loc.Get("CheckPendingReboot"), reboot.ComponentServicing || reboot.WindowsUpdate ? Loc.Get("Yes") : Loc.Get("No"),
            reboot.ComponentServicing || reboot.WindowsUpdate ? "warn" : "good", Loc.Get("CheckPendingRebootDetail")));
        if (AppServices.LastScanContext?.Events is { } ev) foreach (var e in ev) Events.Add(e);
    }

    private async Task ScanEventsAsync()
    {
        var events = await Task.Run(() => EventLogScanner.Scan());
        Events.Clear();
        foreach (var e in events) Events.Add(e);
    }

    private async Task RunToolAsync(RepairTool tool)
    {
        if (tool.RequiresAdmin && !Elevation.IsElevated) { Notifier.Warning(Loc.Get("NeedsAdmin")); return; }
        var details = new List<string> { tool.WhatHappens, "> " + tool.CommandLine };
        if (tool.Duration.Length > 0) details.Add(Loc.Get("Duration") + ": " + tool.Duration);
        if (!ConfirmDialog.Show(tool.Name, tool.Description, details, Loc.Get("Run"), acknowledge: tool.ModifiesSystem ? Loc.Get("RepairAck") : null))
            return;

        _cts = new CancellationTokenSource();
        Running = tool.Name;
        OnPropertyChanged(nameof(IsRunning));
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("> " + tool.CommandLine).AppendLine();
        Console = sb.ToString();
        var pending = 0;
        try
        {
            Log.Audit($"Running repair tool {tool.Id}: {tool.CommandLine}");
            var result = await RepairTools.RunAsync(tool, line =>
            {
                lock (sb)
                {
                    // DISM/SFC redraw progress on one line with \r; keep only the latest state of that line.
                    sb.AppendLine(line.TrimEnd());
                }
                if (Interlocked.Exchange(ref pending, 1) == 0)
                    OnUi(() => { pending = 0; lock (sb) Console = sb.ToString(); });
            }, _cts.Token);
            lock (sb) sb.AppendLine().AppendLine($"Exit code {result.ExitCode}");
            Console = sb.ToString();
            if (result.Success) Notifier.Success(string.Format(Loc.Get("ToolFinished"), tool.Name));
            else Notifier.Warning(string.Format(Loc.Get("ToolFinishedCode"), tool.Name, result.ExitCode));
        }
        catch (OperationCanceledException) { Console += Environment.NewLine + Loc.Get("Cancelled"); }
        catch (Exception ex) { Console += Environment.NewLine + ex.Message; }
        finally
        {
            _cts = null;
            Running = "";
            OnPropertyChanged(nameof(IsRunning));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task CheckUpdatesAsync()
    {
        await Busy(Loc.Get("CheckingUpdates"), async () =>
        {
            var (titles, error) = await UpdateHealth.PendingUpdatesAsync(CancellationToken.None);
            PendingUpdates.Clear();
            foreach (var t in titles) PendingUpdates.Add(t);
            UpdatesText = error ?? (titles.Count == 0 ? Loc.Get("NoPendingUpdates") : string.Format(Loc.Get("PendingUpdatesCount"), titles.Count));
            OnPropertyChanged(nameof(UpdatesText));
        });
    }
}

// ───────────────────────── Hardware & drivers ─────────────────────────

public sealed class HardwareViewModel : PageViewModel
{
    private bool _showAllDrivers;
    private List<DriverInfo> _drivers = [];
    public override string Key => "drivers";

    public HardwareViewModel()
    {
        OpenCommand = new RelayCommand(p => ProcessRunner.ShellOpen((string)p!));
        OpenDeviceManagerCommand = new RelayCommand(() => ProcessRunner.ShellOpen(ProcessRunner.System32("devmgmt.msc")));
        OpenOptionalUpdatesCommand = new RelayCommand(() => ProcessRunner.ShellOpen("ms-settings:windowsupdate-optionalupdates"));
    }

    public SystemProfile? Profile { get; private set; }
    public ObservableCollection<CheckItem> Summary { get; } = [];
    public ObservableCollection<DriverInfo> Drivers { get; } = [];
    public ObservableCollection<ProblemDevice> Problems { get; } = [];
    public ObservableCollection<CheckItem> Sources { get; } = [];
    public bool HasProblems => Problems.Count > 0;
    public ICommand OpenCommand { get; }
    public ICommand OpenDeviceManagerCommand { get; }
    public ICommand OpenOptionalUpdatesCommand { get; }

    public bool ShowAllDrivers
    {
        get => _showAllDrivers;
        set { if (Set(ref _showAllDrivers, value)) FillDrivers(); }
    }

    protected override Task LoadAsync() => Busy(Loc.Get("ReadingDrivers"), async () =>
    {
        var p = await AppServices.ProfileTask;
        Profile = p;
        OnPropertyChanged(nameof(Profile));
        Summary.Add(new(Loc.Get("Processor"), p.Cpu.Name, "info", $"{p.Cpu.Cores} cores · {p.Cpu.Threads} threads · {p.Cpu.MaxClockMhz} MHz · {p.Cpu.Socket}"));
        foreach (var g in p.Gpus)
            Summary.Add(new(Loc.Get("Graphics"), g.Name, "info", $"{(g.VramBytes > 0 ? Format.Bytes(g.VramBytes) + " · " : "")}driver {g.FriendlyDriverVersion}{(g.DriverDate is { } d ? $" ({d:d})" : "")}{(g.IsIntegrated ? " · integrated" : "")}"));
        foreach (var m in p.Memory.Modules)
            Summary.Add(new(Loc.Get("Memory") + " " + m.Slot, Format.Bytes(m.CapacityBytes), "info", $"{m.Type} · {m.ConfiguredMhz}/{m.RatedMhz} MT/s · {m.Manufacturer} {m.PartNumber}".Trim()));
        foreach (var disk in p.Disks)
            Summary.Add(new(disk.FriendlyName, Format.Bytes(disk.SizeBytes), disk.Health == "Healthy" ? "good" : disk.Health == "Unknown" ? "info" : "bad",
                $"{disk.MediaType} · {disk.BusType} · {disk.Health} · firmware {disk.FirmwareVersion}"));
        Summary.Add(new(Loc.Get("Motherboard"), $"{p.Board.Manufacturer} {p.Board.Product}".Trim(), "info", $"BIOS {p.Board.BiosVersion}{(p.Board.BiosDate is { } bd ? $" ({bd:d})" : "")} · {p.Board.BiosVendor}"));
        foreach (var a in p.AudioDevices) Summary.Add(new(Loc.Get("Audio"), a, "info"));

        var (drivers, problems) = await Task.Run(() => (DriverAnalyzer.Drivers(), DriverAnalyzer.ProblemDevices()));
        _drivers = drivers;
        FillDrivers();
        foreach (var pr in problems) Problems.Add(pr);
        OnPropertyChanged(nameof(HasProblems));

        foreach (var vendor in new[] { p.Cpu.Vendor }.Concat(p.Gpus.Select(g => g.Vendor)).Distinct())
        {
            var url = DriverAnalyzer.OfficialDriverSource(vendor);
            if (url.StartsWith("http")) Sources.Add(new(vendor.ToString(), url, "info"));
        }
        if (p.Board.Manufacturer.Length > 0)
            Sources.Add(new(p.Board.Manufacturer, Loc.Get("BoardSupportSite"), "info", Loc.Get("BiosNote")));
    });

    private void FillDrivers()
    {
        Drivers.Clear();
        foreach (var d in _drivers.Where(d => ShowAllDrivers || d.IsKeyClass || d.Flag.Length > 0).OrderByDescending(d => d.Flag.Length > 0).ThenBy(d => d.DeviceClass).ThenBy(d => d.DeviceName))
            Drivers.Add(d);
    }
}
