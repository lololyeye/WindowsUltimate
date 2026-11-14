using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using WindowsUltimate.App.Dialogs;
using WindowsUltimate.App.Localization;
using WindowsUltimate.App.Mvvm;
using WindowsUltimate.App.Services;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Security;

namespace WindowsUltimate.App.ViewModels;

public sealed class SecurityFindingViewModel : ObservableObject
{
    private bool _expanded;

    public SecurityFindingViewModel(SecurityFinding f, SecurityViewModel owner)
    {
        Finding = f;
        RemedyCommand = new AsyncCommand(() => owner.RemedyAsync(this), () => HasRemedy);
        ScanFileCommand = new AsyncCommand(() => owner.ScanFileAsync(this), () => FileExists);
        LookupCommand = new RelayCommand(() => owner.Lookup(this), () => f.Sha256 is { Length: 64 });
        RevealCommand = new RelayCommand(Reveal, () => f.FilePath is not null || f.RemedyData.ContainsKey("folder"));
        TrustCommand = new RelayCommand(() => owner.Trust(this));
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public SecurityFinding Finding { get; }
    public string Title => Finding.Title;
    public string Location => Finding.Location;
    public string Method => Finding.Method;
    public string Evidence => Finding.Evidence;
    public string Recommendation => Finding.Recommendation;
    public string TimeText => Finding.Time.ToString("g");
    public string Sha256 => Finding.Sha256 ?? "";
    public string SeverityKey => Finding.Severity switch
    {
        ThreatSeverity.Critical or ThreatSeverity.High => "bad",
        ThreatSeverity.Medium => "warn",
        ThreatSeverity.Low => "info",
        _ => "",
    };
    public string SeverityText => Loc.Get("Sev_" + Finding.Severity);
    public string ConfidenceText => string.Format(Loc.Get("ConfidenceIs"), Loc.Get("Conf_" + Finding.Confidence));
    public string CategoryText => Loc.Get("ThreatCat_" + Finding.Category);
    public bool FileExists => Finding.FilePath is { } p && File.Exists(p);
    public bool HasRemedy => Finding.Remedy is not RemedyKind.None;
    public string RemedyLabel => Loc.Get("Remedy_" + Finding.Remedy);
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }

    public ICommand RemedyCommand { get; }
    public ICommand ScanFileCommand { get; }
    public ICommand LookupCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand TrustCommand { get; }
    public ICommand ToggleCommand { get; }

    private void Reveal()
    {
        if (Finding.FilePath is { } p && File.Exists(p)) ProcessRunner.RevealInExplorer(p);
        else if (Finding.RemedyData.GetValueOrDefault("folder") is { } dir && Directory.Exists(dir)) ProcessRunner.ShellOpen(dir);
    }
}

/// <summary>
/// Security Center. Microsoft Defender gives the verdicts (scans, detections, removal); Windows Ultimate adds a
/// heuristic review of the places malware uses to persist or steal data. Indicators are not verdicts, and every
/// action asks first. This page does not replace an antivirus.
/// </summary>
public sealed class SecurityViewModel : PageViewModel
{
    private DefenderStatus? _defender;
    private SecurityScanResult? _scan;
    private CancellationTokenSource? _cts;
    private string _scanLog = "";
    private bool _defenderScanning;
    public override string Key => "security";

    public SecurityViewModel()
    {
        QuickScanCommand = new AsyncCommand(() => DefenderScanAsync("quick"), CanDefender);
        FullScanCommand = new AsyncCommand(() => DefenderScanAsync("full"), CanDefender);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _cts is not null);
        UpdateSignaturesCommand = new AsyncCommand(UpdateSignaturesAsync, CanDefender);
        OfflineScanCommand = new AsyncCommand(OfflineScanAsync, CanDefender);
        RemoveThreatsCommand = new AsyncCommand(RemoveThreatsAsync, () => Threats.Any(t => t.IsActive));
        OpenWindowsSecurityCommand = new RelayCommand(() => ProcessRunner.ShellOpen("windowsdefender:"));
        RunCheckCommand = new AsyncCommand(RunCheckAsync, () => !IsBusy);
        ExportCommand = new RelayCommand(Export, () => _scan is not null);
        RefreshCommand = new AsyncCommand(RefreshDefenderAsync);
        ClearTrustCommand = new RelayCommand(() => { TrustList.Clear(); Notifier.Info(Loc.Get("TrustCleared")); });
    }

    public ObservableCollection<DefenderThreat> Threats { get; } = [];
    public ObservableCollection<SecurityFindingViewModel> Findings { get; } = [];
    public ObservableCollection<PersistenceItem> Persistence { get; } = [];
    public ObservableCollection<BrowserExtensionInfo> Extensions { get; } = [];
    public ObservableCollection<string> Limitations { get; } = [];

    public ICommand QuickScanCommand { get; }
    public ICommand FullScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand UpdateSignaturesCommand { get; }
    public ICommand OfflineScanCommand { get; }
    public ICommand RemoveThreatsCommand { get; }
    public ICommand OpenWindowsSecurityCommand { get; }
    public ICommand RunCheckCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ClearTrustCommand { get; }

    public bool IsElevated => Elevation.IsElevated;
    public DefenderStatus? Defender { get => _defender; private set { Set(ref _defender, value); OnDefenderChanged(); } }
    public bool DefenderAvailable => _defender?.Available == true;
    public bool DefenderScanning { get => _defenderScanning; private set => Set(ref _defenderScanning, value); }
    public string ScanLog { get => _scanLog; private set => Set(ref _scanLog, value); }

    public string ProtectionText => _defender switch
    {
        null => "—",
        { Available: false } => _defender.OtherAntivirus.Count > 0
            ? string.Format(Loc.Get("ProtectedByOther"), string.Join(", ", _defender.OtherAntivirus.Select(a => a.Name)))
            : Loc.Get("DefenderUnavailable"),
        { IsActive: true, RealTimeProtection: true } => Loc.Get("DefenderActive"),
        { IsActive: true } => Loc.Get("DefenderRealtimeOff"),
        _ => _defender.OtherAntivirus.Count > 0
            ? string.Format(Loc.Get("ProtectedByOther"), string.Join(", ", _defender.OtherAntivirus.Select(a => a.Name)))
            : Loc.Get("DefenderInactive"),
    };
    public string ProtectionSeverity => _defender switch
    {
        null => "",
        { IsActive: true, RealTimeProtection: true, SignaturesOutOfDate: false } => "good",
        { IsActive: true } => "warn",
        _ when _defender.OtherAntivirus.Any(a => a.Enabled) => "good",
        _ => "bad",
    };
    public string SignaturesText => _defender?.SignatureAgeDays is { } age
        ? string.Format(Loc.Get(age < 1 ? "SignaturesToday" : "SignaturesDaysOld"), Math.Floor(age), _defender.SignatureVersion)
        : "—";
    public string SignaturesSeverity => _defender?.SignatureAgeDays is { } age ? age < 3 ? "good" : age < 7 ? "warn" : "bad" : "";
    public string LastQuickText => _defender?.LastQuickScan?.ToString("g") ?? Loc.Get("Never");
    public string LastFullText => _defender?.LastFullScan?.ToString("g") ?? Loc.Get("Never");
    public string TamperText => _defender?.TamperProtected switch { true => Loc.Get("On"), false => Loc.Get("Off"), _ => "—" };
    public string RealtimeText => _defender is null ? "—" : _defender.RealTimeProtection ? Loc.Get("On") : Loc.Get("Off");

    public bool HasScan => _scan is not null;
    public bool HasFindings => Findings.Count > 0;
    public bool NoFindings => _scan is not null && Findings.Count == 0;
    public bool HasThreats => Threats.Count > 0;
    public string ScanSummary => _scan is null ? Loc.Get("SecurityNotRunYet")
        : string.Format(Loc.Get("SecuritySummary"), _scan.ItemsReviewed, Findings.Count, _scan.Duration.TotalSeconds, _scan.Time.ToString("t"))
          + (_scan.Ignored > 0 ? " " + string.Format(Loc.Get("SecurityTrustedHidden"), _scan.Ignored) : "");

    private bool CanDefender() => DefenderAvailable && _cts is null;

    protected override Task LoadAsync() => RefreshDefenderAsync();

    private async Task RefreshDefenderAsync()
    {
        var (status, threats) = await Task.Run(() => (DefenderClient.Status(), DefenderClient.Threats()));
        Defender = status;
        Threats.Clear();
        foreach (var t in threats.OrderByDescending(t => t.IsActive).ThenByDescending(t => t.Detected)) Threats.Add(t);
        OnPropertyChanged(nameof(HasThreats));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnDefenderChanged() => OnPropertiesChanged(nameof(DefenderAvailable), nameof(ProtectionText), nameof(ProtectionSeverity), nameof(SignaturesText),
        nameof(SignaturesSeverity), nameof(LastQuickText), nameof(LastFullText), nameof(TamperText), nameof(RealtimeText));

    // ───────── Defender ─────────

    private async Task DefenderScanAsync(string type)
    {
        if (type == "full" && !ConfirmDialog.Show(Loc.Get("FullScanTitle"), Loc.Get("FullScanMessage"), confirmText: Loc.Get("StartScan")))
            return;
        _cts = new CancellationTokenSource();
        DefenderScanning = true;
        ScanLog = Loc.Get(type == "quick" ? "QuickScanRunning" : "FullScanRunning");
        var started = DateTime.Now;
        try
        {
            Log.Audit($"Defender {type} scan started");
            var r = await DefenderClient.ScanAsync(type, null, _cts.Token);
            ScanLog = !r.Completed ? Loc.Get("ScanCancelled")
                : r.ThreatsFound ? string.Format(Loc.Get("ScanThreatsFound"), r.Threats.Count) + (r.Threats.Count > 0 ? "\n" + string.Join("\n", r.Threats) : "")
                : string.Format(Loc.Get("ScanClean"), (DateTime.Now - started).TotalMinutes);
            if (r.ThreatsFound) Notifier.Warning(ScanLog.Split('\n')[0]);
            else if (r.Completed) Notifier.Success(ScanLog);
        }
        finally
        {
            _cts = null;
            DefenderScanning = false;
            CommandManager.InvalidateRequerySuggested();
            await RefreshDefenderAsync();
        }
    }

    private async Task UpdateSignaturesAsync()
    {
        await Busy(Loc.Get("UpdatingSignatures"), async () =>
        {
            var (ok, output) = await DefenderClient.UpdateSignaturesAsync();
            if (ok) Notifier.Success(Loc.Get("SignaturesUpdated"));
            else Notifier.Warning(Loc.Get("SignaturesUpdateFailed") + (output.Length > 0 ? "\n" + output.Trim() : ""));
            await RefreshDefenderAsync();
        });
    }

    private async Task OfflineScanAsync()
    {
        if (!Elevation.IsElevated) { Notifier.Warning(Loc.Get("NeedsAdmin")); return; }
        if (!ConfirmDialog.Show(Loc.Get("OfflineScanTitle"), Loc.Get("OfflineScanMessage"), [Loc.Get("OfflineScanDetail1"), Loc.Get("OfflineScanDetail2")],
                Loc.Get("RestartAndScan"), danger: true, acknowledge: Loc.Get("OfflineScanAck")))
            return;
        Log.Audit("Defender offline scan requested by the user");
        var (ok, output) = await DefenderClient.StartOfflineScanAsync();
        if (!ok) Notifier.Error(output);
    }

    private async Task RemoveThreatsAsync()
    {
        if (!Elevation.IsElevated) { Notifier.Warning(Loc.Get("NeedsAdmin")); return; }
        var active = Threats.Where(t => t.IsActive).ToList();
        if (!ConfirmDialog.Show(Loc.Get("RemoveThreatsTitle"), Loc.Get("RemoveThreatsMessage"),
                active.Select(t => $"{t.Name} — {t.ResourcesText.Split('\n').FirstOrDefault()}"), Loc.Get("RemoveThreats"), danger: true))
            return;
        await Busy(Loc.Get("RemovingThreats"), async () =>
        {
            Log.Audit($"Defender remediation requested for {active.Count} threat(s): {string.Join(", ", active.Select(t => t.Name))}");
            var (ok, output) = await DefenderClient.RemoveActiveThreatsAsync();
            if (ok) Notifier.Success(Loc.Get("ThreatsRemoved"));
            else Notifier.Error(output);
            await RefreshDefenderAsync();
        });
    }

    // ───────── Windows Ultimate check ─────────

    private async Task RunCheckAsync()
    {
        _cts = new CancellationTokenSource();
        try
        {
            await Busy(Loc.Get("SecurityChecking"), async () =>
            {
                var result = await SecurityScanner.ScanAsync(BusyProgress, _cts.Token);
                Apply(result);
            });
        }
        finally
        {
            _cts = null;
            CommandManager.InvalidateRequerySuggested();
        }
        await RefreshDefenderAsync();
    }

    private void Apply(SecurityScanResult result)
    {
        _scan = result;
        Findings.Clear();
        foreach (var f in result.Findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Confidence))
            Findings.Add(new SecurityFindingViewModel(f, this));
        Persistence.Clear();
        foreach (var p in result.Persistence.OrderByDescending(p => p.Flagged).ThenBy(p => p.Kind)) Persistence.Add(p);
        Extensions.Clear();
        foreach (var e in result.Extensions.OrderByDescending(e => e.PowerfulPermissions.Count)) Extensions.Add(e);
        Limitations.Clear();
        foreach (var l in result.Limitations) Limitations.Add(l);
        OnPropertiesChanged(nameof(HasScan), nameof(HasFindings), nameof(NoFindings), nameof(ScanSummary));
    }

    private void RemoveFinding(SecurityFindingViewModel f)
    {
        Findings.Remove(f);
        OnPropertiesChanged(nameof(HasFindings), nameof(NoFindings), nameof(ScanSummary));
    }

    internal async Task RemedyAsync(SecurityFindingViewModel item)
    {
        var f = item.Finding;
        switch (f.Remedy)
        {
            case RemedyKind.OpenSettings:
                if (f.RemedyData.GetValueOrDefault("uri") is { } uri) ProcessRunner.ShellOpen(uri);
                return;
            case RemedyKind.DefenderRemove:
                await RemoveThreatsAsync();
                return;
        }

        // A running process must be stopped before its file can be moved. Ask separately: ending a process loses its unsaved work.
        if (f.Remedy == RemedyKind.Quarantine && int.TryParse(f.RemedyData.GetValueOrDefault("pid"), out var pid))
        {
            Process? p = null;
            try { p = Process.GetProcessById(pid); } catch { /* already gone */ }
            if (p is not null)
            {
                if (!ConfirmDialog.Show(Loc.Get("EndProcessTitle"), string.Format(Loc.Get("EndProcessMessage"), p.ProcessName, pid), [f.FilePath ?? ""],
                        Loc.Get("EndProcess"), danger: true))
                    return;
                try
                {
                    p.Kill(entireProcessTree: false);
                    await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
                    Log.Audit($"Ended process {p.ProcessName} ({pid}) before quarantine of {f.FilePath}");
                }
                catch (Exception ex)
                {
                    Notifier.Error(string.Format(Loc.Get("EndProcessFailed"), ex.Message));
                    return;
                }
            }
        }

        var tweak = await Task.Run(() => SecurityRemedies.CreateTweak(f));
        if (tweak is null) { Notifier.Info(Loc.Get("RemedyNotApplicable")); return; }
        AppServices.Engine.Register(tweak);
        var summary = await TweakApplier.ApplyAsync([tweak], tweak.Name, "security");
        if (summary is { AppliedCount: > 0 }) RemoveFinding(item);
    }

    internal async Task ScanFileAsync(SecurityFindingViewModel item)
    {
        if (item.Finding.FilePath is not { } file) return;
        await Busy(Loc.Get("DefenderScanningFile"), async () =>
        {
            var r = await DefenderClient.ScanAsync("file", file);
            if (!r.Completed) Notifier.Warning(Loc.Get("ScanCancelled"));
            else if (r.ThreatsFound) Notifier.Warning(string.Format(Loc.Get("DefenderFileThreat"), Path.GetFileName(file), string.Join(", ", r.Threats)));
            else Notifier.Success(string.Format(Loc.Get("DefenderFileClean"), Path.GetFileName(file)));
        });
    }

    /// <summary>Opens VirusTotal's page for the hash in the browser. Only the hash leaves the PC; the file is never uploaded.</summary>
    internal void Lookup(SecurityFindingViewModel item)
    {
        if (item.Finding.Sha256 is not { Length: 64 } hash) return;
        if (!ConfirmDialog.Show(Loc.Get("LookupTitle"), Loc.Get("LookupMessage"), [hash], Loc.Get("OpenInBrowser")))
            return;
        ProcessRunner.ShellOpen("https://www.virustotal.com/gui/file/" + hash);
    }

    internal void Trust(SecurityFindingViewModel item)
    {
        if (!ConfirmDialog.Show(Loc.Get("TrustTitle"), Loc.Get("TrustMessage"), [item.Finding.FilePath ?? item.Title], Loc.Get("TrustIt")))
            return;
        TrustList.Add(item.Finding.TrustKey);
        Log.Audit($"User marked as trusted: {item.Finding.TrustKey}");
        RemoveFinding(item);
    }

    private void Export()
    {
        if (_scan is null) return;
        var path = Path.Combine(AppPaths.Exports, $"security-report-{DateTime.Now:yyyyMMdd-HHmm}.txt");
        File.WriteAllText(path, SecurityScanner.ExportText(_scan, _defender));
        ProcessRunner.RevealInExplorer(path);
        Notifier.Success(Loc.Get("ReportExported"));
    }
}
