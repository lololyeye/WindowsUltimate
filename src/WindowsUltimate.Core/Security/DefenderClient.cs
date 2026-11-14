using WindowsUltimate.Core.Diagnostics;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Security;

public sealed class DefenderStatus
{
    public bool Available { get; init; }
    public string RunningMode { get; init; } = "";
    public bool AntivirusEnabled { get; init; }
    public bool RealTimeProtection { get; init; }
    public bool BehaviorMonitor { get; init; }
    public bool? TamperProtected { get; init; }
    public bool SignaturesOutOfDate { get; init; }
    public string SignatureVersion { get; init; } = "";
    public DateTime? SignaturesUpdated { get; init; }
    public DateTime? LastQuickScan { get; init; }
    public DateTime? LastFullScan { get; init; }
    public List<AntivirusStatus> OtherAntivirus { get; init; } = [];

    /// <summary>Defender is the active antivirus (not passive because another product is registered).</summary>
    public bool IsActive => Available && AntivirusEnabled && RunningMode is "Normal" or "";

    public double? SignatureAgeDays => SignaturesUpdated is { } d ? (DateTime.Now - d).TotalDays : null;
}

public sealed class DefenderThreat
{
    public string Name { get; init; } = "";
    public string Severity { get; init; } = "";
    public ThreatSeverity Level { get; init; }
    public string Category { get; init; } = "";
    public string Status { get; init; } = "";
    public bool IsActive { get; init; }
    public bool DidExecute { get; init; }
    public DateTime? Detected { get; init; }
    public List<string> Resources { get; init; } = [];
    public string ResourcesText => string.Join("\n", Resources.Select(r => r.Replace("file:_", "")).Take(5));
}

public sealed record DefenderScanResult(bool Completed, bool ThreatsFound, string Output, IReadOnlyList<string> Threats);

/// <summary>
/// Reads Microsoft Defender's state through its WMI provider and starts scans with MpCmdRun, Defender's own command-line
/// tool. Windows Ultimate never changes Defender settings.
/// </summary>
public static class DefenderClient
{
    private const string Ns = @"root\Microsoft\Windows\Defender";

    public static DefenderStatus Status()
    {
        var others = SystemChecks.Security().Antivirus.Where(a => !a.Name.Contains("Defender", StringComparison.OrdinalIgnoreCase)).ToList();
        var row = Wmi.TryQuery("SELECT * FROM MSFT_MpComputerStatus", Ns).FirstOrDefault();
        if (row is null) return new DefenderStatus { Available = false, OtherAntivirus = others };
        DateTime? Date(string key) => row.TryGetValue(key, out var v) && v is not null ? row.Date(key) : null;
        return new DefenderStatus
        {
            Available = row.Bool("AMServiceEnabled") ?? false,
            RunningMode = row.Str("AMRunningMode"),
            AntivirusEnabled = row.Bool("AntivirusEnabled") ?? false,
            RealTimeProtection = row.Bool("RealTimeProtectionEnabled") ?? false,
            BehaviorMonitor = row.Bool("BehaviorMonitorEnabled") ?? false,
            TamperProtected = row.Bool("IsTamperProtected"),
            SignaturesOutOfDate = row.Bool("DefenderSignaturesOutOfDate") ?? false,
            SignatureVersion = row.Str("AntivirusSignatureVersion"),
            SignaturesUpdated = Date("AntivirusSignatureLastUpdated"),
            LastQuickScan = Date("QuickScanEndTime"),
            LastFullScan = Date("FullScanEndTime"),
            OtherAntivirus = others,
        };
    }

    /// <summary>Detections recorded by Defender (protection history), most recent first.</summary>
    public static List<DefenderThreat> Threats()
    {
        var catalog = Wmi.TryQuery("SELECT ThreatID, ThreatName, SeverityID, CategoryID FROM MSFT_MpThreat", Ns)
            .GroupBy(r => r.Long("ThreatID")).ToDictionary(g => g.Key, g => g.First());
        return Wmi.TryQuery("SELECT ThreatID, InitialDetectionTime, ThreatStatusID, CurrentThreatExecutionStatusID, Resources FROM MSFT_MpThreatDetection", Ns)
            .Select(d =>
            {
                catalog.TryGetValue(d.Long("ThreatID"), out var t);
                var sev = t?.Long("SeverityID") ?? 0;
                var status = d.Long("ThreatStatusID");
                return new DefenderThreat
                {
                    Name = t?.Str("ThreatName") ?? $"Threat {d.Long("ThreatID")}",
                    Severity = sev switch { 1 => L.T("Low"), 2 => L.T("Moderate"), 4 => L.T("High"), 5 => L.T("Severe"), _ => L.T("Unknown") },
                    Level = sev switch { 1 => ThreatSeverity.Low, 2 => ThreatSeverity.Medium, 4 => ThreatSeverity.High, 5 => ThreatSeverity.Critical, _ => ThreatSeverity.Medium },
                    Category = CategoryName(t?.Long("CategoryID") ?? -1),
                    Status = status switch
                    {
                        1 => L.T("Detected — action needed"), 2 => L.T("Cleaned"), 3 => L.T("Quarantined"), 4 => L.T("Removed"), 5 => L.T("Allowed by you"),
                        6 => L.T("Blocked"), 102 => L.T("Quarantine failed"), 103 => L.T("Removal failed"), 107 => L.T("Blocking failed"), _ => L.T("Unknown"),
                    },
                    IsActive = status is 1 or 102 or 103 or 105 or 107,
                    DidExecute = d.Long("CurrentThreatExecutionStatusID") == 3,
                    Detected = d.Date("InitialDetectionTime"),
                    Resources = d.TryGetValue("Resources", out var r) && r is string[] res ? res.ToList() : [],
                };
            })
            .OrderByDescending(t => t.Detected)
            .ToList();
    }

    private static string CategoryName(long id) => id switch
    {
        1 => L.T("Adware"), 2 => L.T("Spyware"), 3 => L.T("Password stealer"), 4 => L.T("Trojan downloader"), 5 => L.T("Worm"), 6 => L.T("Backdoor"),
        7 => L.T("Remote access trojan"), 8 => L.T("Trojan"), 10 => L.T("Keylogger"), 12 => L.T("Monitoring software"), 13 => L.T("Browser modifier"),
        18 => L.T("Security disabler"), 21 => L.T("Software bundler"), 23 => L.T("Settings modifier"), 25 or 33 => L.T("Remote control software"),
        27 => L.T("Potentially unwanted application"), 30 => L.T("Exploit"), 34 => L.T("Hacking tool"), 37 => L.T("Trojan dropper"), 40 => L.T("Trojan proxy"),
        42 => L.T("Virus"), 46 => L.T("Suspicious behaviour"), 50 => L.T("Ransomware"), _ => L.T("Other"),
    };

    /// <summary>Defender's current command-line tool (the platform folder is updated separately from Windows).</summary>
    public static string? MpCmdRun()
    {
        try
        {
            var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender\Platform");
            if (Directory.Exists(platform))
            {
                var latest = Directory.GetDirectories(platform)
                    .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
                    .Where(x => x.Version is not null && File.Exists(Path.Combine(x.Dir, "MpCmdRun.exe")))
                    .MaxBy(x => x.Version);
                if (latest.Dir is not null) return Path.Combine(latest.Dir, "MpCmdRun.exe");
            }
        }
        catch { /* fall back */ }
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
        return File.Exists(legacy) ? legacy : null;
    }

    /// <summary>
    /// Runs a Defender scan. Quick and full scans use Defender's normal remediation (what Defender does on its own schedule).
    /// A single-file scan uses -DisableRemediation so the verdict is shown and the user decides what happens to the file.
    /// </summary>
    public static async Task<DefenderScanResult> ScanAsync(string type, string? file = null, CancellationToken ct = default)
    {
        var exe = MpCmdRun() ?? throw new InvalidOperationException(L.T("Microsoft Defender's scanner (MpCmdRun.exe) was not found."));
        var args = type switch
        {
            "quick" => "-Scan -ScanType 1",
            "full" => "-Scan -ScanType 2",
            "file" when file is not null && File.Exists(file) => $"-Scan -ScanType 3 -File \"{file.Replace("\"", "")}\" -DisableRemediation",
            _ => throw new ArgumentException("Unknown scan type"),
        };
        Log.Audit($"Defender scan started: {args}");
        try
        {
            var r = await ProcessRunner.RunAsync(exe, args, timeout: type == "full" ? TimeSpan.FromHours(6) : TimeSpan.FromHours(1), ct: ct).ConfigureAwait(false);
            var threats = r.StdOut.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("Threat ", StringComparison.OrdinalIgnoreCase) && l.Contains(':'))
                .Select(l => l[(l.IndexOf(':') + 1)..].Trim()).Where(l => l.Length > 0).Distinct().ToList();
            Log.Audit($"Defender scan finished: exit {r.ExitCode}, {threats.Count} threat(s)");
            return new DefenderScanResult(r.ExitCode is 0 or 2, r.ExitCode == 2 || threats.Count > 0, (r.StdOut + r.StdErr).Trim(), threats);
        }
        catch (OperationCanceledException)
        {
            await ProcessRunner.RunAsync(exe, "-Cancel").ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<(bool Ok, string Output)> UpdateSignaturesAsync()
    {
        var exe = MpCmdRun() ?? throw new InvalidOperationException(L.T("Microsoft Defender's scanner (MpCmdRun.exe) was not found."));
        var r = await ProcessRunner.RunAsync(exe, "-SignatureUpdate", timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        Log.Audit($"Defender signature update: exit {r.ExitCode}");
        return (r.Success, (r.StdOut + r.StdErr).Trim());
    }

    /// <summary>Asks Defender to remediate every active threat (Remove-MpThreat). Requires administrator rights.</summary>
    public static async Task<(bool Ok, string Output)> RemoveActiveThreatsAsync()
    {
        if (!Elevation.IsElevated) throw new AdminRequiredException("Remove-MpThreat");
        var r = await ProcessRunner.RunAsync(ProcessRunner.System32(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -NonInteractive -Command Remove-MpThreat",
            timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        Log.Audit($"Defender Remove-MpThreat: exit {r.ExitCode}");
        return (r.Success, (r.StdOut + r.StdErr).Trim());
    }

    /// <summary>Schedules a Microsoft Defender Offline scan. Windows restarts within a minute and scans outside Windows (~15 min).</summary>
    public static async Task<(bool Ok, string Output)> StartOfflineScanAsync()
    {
        if (!Elevation.IsElevated) throw new AdminRequiredException("Start-MpWDOScan");
        Log.Audit("Defender Offline scan requested by the user");
        var r = await ProcessRunner.RunAsync(ProcessRunner.System32(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -NonInteractive -Command Start-MpWDOScan",
            timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        return (r.Success, (r.StdOut + r.StdErr).Trim());
    }
}
