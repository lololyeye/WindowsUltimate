using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;
using WindowsUltimate.Core.Startup;

namespace WindowsUltimate.Core.Security;

/// <summary>
/// Defensive indicator scan: autostart locations, services and drivers, running programs, high-risk folders, browser
/// extensions/policies/shortcuts, hosts file, proxy and Defender configuration. Everything is read-only. Browser
/// passwords, cookies and tokens are never opened — only the presence and location of their files is checked.
/// </summary>
public static class SecurityScanner
{
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string Temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Public = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";

    private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
        { "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe", "cmd.exe" };

    private sealed class Context
    {
        public required SecurityScanResult Result { get; init; }
        public required HashSet<string> Trusted { get; init; }
        public Dictionary<string, FileReport> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Reported { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FileReport? Inspect(string? path)
        {
            if (path is null) return null;
            try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
            catch { return null; }
            if (Files.TryGetValue(path, out var r)) return r;
            if (!File.Exists(path)) return null;
            Result.ItemsReviewed++;
            try { r = FileInspector.Inspect(path, hash: false); }
            catch (Exception ex) { Log.Warn($"Inspect {path}: {ex.Message}"); return null; }
            return Files[path] = r;
        }

        public void Add(SecurityFinding f)
        {
            if (Trusted.Contains(f.TrustKey) || (f.FilePath is { } p && f.Sha256 is { Length: > 0 } h && Trusted.Contains("sha:" + h))) { Result.Ignored++; return; }
            if (!Reported.Add(f.Id)) return;
            if (f.FilePath is { } path && f.Sha256 is null) f.Sha256 = FileInspector.Sha256(path);
            Result.Findings.Add(f);
        }
    }

    public static async Task<SecurityScanResult> ScanAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.Now;
        var result = new SecurityScanResult();
        var ctx = new Context { Result = result, Trusted = TrustList.Load() };
        await Task.Run(() =>
        {
            void Step(string label, Action<Context> work)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(label);
                try { work(ctx); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error($"Security check '{label}' failed", ex);
                    result.Limitations.Add(L.F("{0}: could not be completed ({1}).", label, ex.Message));
                }
            }

            Step(L.T("Microsoft Defender detections"), DefenderDetections);
            Step(L.T("Startup entries"), RunEntries);
            Step(L.T("Scheduled tasks"), Tasks);
            Step(L.T("Services and drivers"), ServicesAndDrivers);
            Step(L.T("Sign-in and hijack points"), HijackPoints);
            Step(L.T("WMI event subscriptions"), WmiSubscriptions);
            Step(L.T("Running programs"), Processes);
            Step(L.T("High-risk folders"), c => Folders(c, ct));
            Step(L.T("Browser extensions"), BrowserExtensions);
            Step(L.T("Browser policies and shortcuts"), BrowserPoliciesAndShortcuts);
            Step(L.T("Hosts file, proxy and Defender exclusions"), Configuration);
        }, ct).ConfigureAwait(false);

        if (!Elevation.IsElevated)
            result.Limitations.Add(L.T("Not running as administrator: Defender exclusions, some services and programs of other accounts could not be fully reviewed."));
        result.Limitations.Add(L.T("Indicators are clues, not verdicts. Microsoft Defender gives the verdict: scan any flagged file with it before acting."));
        result.Limitations.Add(L.T("Rootkits that hide inside Windows cannot be reliably detected from inside Windows. Microsoft Defender Offline scans before Windows starts."));
        result.Duration = DateTime.Now - started;
        result.Findings.Sort((a, b) => b.Severity != a.Severity ? b.Severity.CompareTo(a.Severity) : b.Confidence.CompareTo(a.Confidence));
        Log.Info($"Security check: {result.Findings.Count} finding(s), {result.ItemsReviewed} items reviewed, {result.Duration.TotalSeconds:0.0}s");
        return result;
    }

    // ───────────── Defender ─────────────

    private static void DefenderDetections(Context c)
    {
        foreach (var t in DefenderClient.Threats().Where(t => t.IsActive))
        {
            var file = t.Resources.Select(r => r.StartsWith("file:_", StringComparison.OrdinalIgnoreCase) ? r[6..] : null).FirstOrDefault(p => p is not null);
            c.Add(new SecurityFinding
            {
                Id = "defender:" + t.Name + ":" + t.Detected?.Ticks,
                Title = L.F("Microsoft Defender detected {0}", t.Name),
                Category = ThreatCategory.DefenderDetection,
                Severity = t.Level,
                Confidence = Confidence.High,
                Location = t.ResourcesText,
                Method = L.F("Reported by Microsoft Defender ({0}, {1}). Status: {2}.", t.Category, t.Severity, t.Status),
                Evidence = t.DidExecute ? L.T("Defender reports that it was running.") : "",
                Recommendation = L.T("Let Microsoft Defender remove it. If it ran, change the passwords of important accounts from another device afterwards."),
                FilePath = file,
                Sha256 = "",
                Remedy = RemedyKind.DefenderRemove,
            });
        }
    }

    // ───────────── Autostart ─────────────

    private static List<Indicator> CommandIndicators(string command, out string? scriptFile)
    {
        scriptFile = null;
        var list = new List<Indicator>();
        var exe = StartupManager.ExtractExecutable(command);
        var host = exe is null ? "" : Path.GetFileName(exe);
        if (!ScriptHosts.Contains(host)) return list;
        var args = command.Length > (exe?.Length ?? 0) ? command : "";
        if (Regex.IsMatch(args, @"\s-e(nc(odedcommand)?)?\s+[A-Za-z0-9+/=]{40,}", RegexOptions.IgnoreCase))
            list.Add(new(L.T("Starts an encoded (hidden) PowerShell command"), 4, ThreatCategory.SuspiciousScript));
        if (Regex.IsMatch(args, @"-w(indowstyle)?\s+h(idden)?\b", RegexOptions.IgnoreCase))
            list.Add(new(L.T("Starts in a hidden window"), 1, ThreatCategory.SuspiciousScript));
        if (Regex.IsMatch(args, @"https?://|javascript:|vbscript:", RegexOptions.IgnoreCase))
            list.Add(new(L.F("{0} fetches and runs code from the internet at every start", host), 4, ThreatCategory.SuspiciousScript));
        var m = Regex.Match(args, @"[A-Za-z]:\\[^""<>|*?\r\n]+?\.(ps1|vbs|vbe|js|jse|wsf|hta|bat|cmd|dll)\b", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            scriptFile = m.Value;
            if (FileInspector.InUserWritableLocation(m.Value))
                list.Add(new(L.F("{0} runs a file stored in a user-writable folder", host), 1, ThreatCategory.Persistence));
        }
        return list;
    }

    /// <summary>Scores one autostart command. Returns the combined indicators and the main file involved.</summary>
    private static (List<Indicator> Indicators, FileReport? File) Assess(Context c, string command)
    {
        var indicators = CommandIndicators(command, out var script);
        var exe = StartupManager.ExtractExecutable(command);
        var target = script is not null ? c.Inspect(script) : null;
        var exeReport = exe is not null && !FileInspector.InWindowsFolder(exe) ? c.Inspect(exe) : null;
        var file = target ?? exeReport;
        if (file is not null)
        {
            indicators.AddRange(file.Indicators);
            if (!file.IsSigned && FileInspector.InUserWritableLocation(file.Path) && file.Indicators.Count > 0)
                indicators.Add(new(L.T("Starts automatically from a user-writable folder"), 1, ThreatCategory.Persistence));
        }
        return (indicators, file);
    }

    private static ThreatSeverity SeverityOf(int score) => score switch { >= 8 => ThreatSeverity.High, >= 5 => ThreatSeverity.Medium, >= 3 => ThreatSeverity.Low, _ => ThreatSeverity.Info };
    private static Confidence ConfidenceOf(int score) => score >= 6 ? Confidence.Medium : Confidence.Low;

    private static string Explain(IEnumerable<Indicator> indicators) => string.Join(" · ", indicators.Select(i => i.Text).Distinct());

    private static void RunEntries(Context c)
    {
        foreach (var e in StartupManager.List())
        {
            var (ind, file) = Assess(c, e.Command);
            var score = ind.Sum(i => i.Weight);
            c.Result.Persistence.Add(new PersistenceItem(L.T("Startup"), e.Name, e.SourceLabel, e.Command, e.Signer, score >= 3));
            if (score < 3 || !e.Enabled) continue;
            c.Add(new SecurityFinding
            {
                Id = $"run:{e.Source}:{e.Name}",
                Title = L.F("Suspicious startup entry: {0}", e.Name),
                Category = ind.MaxBy(i => i.Weight)!.Category,
                Severity = SeverityOf(score),
                Confidence = ConfidenceOf(score),
                Location = e.SourceLabel,
                Method = L.T("Startup locations review (Run keys and Startup folders)") + " — " + Explain(ind),
                Evidence = e.Command,
                Recommendation = L.T("If you don't recognise this program, disable the entry (reversible) and scan the file with Microsoft Defender."),
                FilePath = file?.Path,
                Remedy = RemedyKind.DisableStartup,
                RemedyData = { ["source"] = e.Source.ToString(), ["name"] = e.Name },
            });
        }
    }

    private static void Tasks(Context c)
    {
        foreach (var t in ScheduledTasks.List(includeMicrosoft: true))
        {
            if (!t.Enabled) continue;
            foreach (var action in t.Actions.Split(" | ", StringSplitOptions.RemoveEmptyEntries))
            {
                if (action == "COM handler") continue;
                var exe = StartupManager.ExtractExecutable(action);
                // Tasks in \Microsoft\ that run Windows binaries are normal; malware hides its own task there with a non-Windows program.
                if (t.IsMicrosoft && exe is not null && (FileInspector.InWindowsFolder(exe) || exe.Contains(@"\Program Files", StringComparison.OrdinalIgnoreCase))
                    && CommandIndicators(action, out _).Count == 0)
                    continue;
                var (ind, file) = Assess(c, action);
                if (t.IsMicrosoft && exe is not null && !FileInspector.InWindowsFolder(exe) && FileInspector.InUserWritableLocation(exe))
                    ind.Add(new(L.T("Task stored among Windows' own tasks but runs a program from a user folder"), 3, ThreatCategory.Persistence));
                var score = ind.Sum(i => i.Weight);
                if (!t.IsMicrosoft || score >= 3)
                    c.Result.Persistence.Add(new PersistenceItem(L.T("Scheduled task"), t.Name, t.Path, action, file?.Signer ?? "", score >= 3));
                if (score < 3) continue;
                c.Add(new SecurityFinding
                {
                    Id = "task:" + t.Path,
                    Title = L.F("Suspicious scheduled task: {0}", t.Name),
                    Category = ind.MaxBy(i => i.Weight)!.Category,
                    Severity = SeverityOf(score),
                    Confidence = ConfidenceOf(score),
                    Location = t.Path,
                    Method = L.T("Task Scheduler review") + " — " + Explain(ind),
                    Evidence = action + (t.Triggers.Length > 0 ? $"  [{t.Triggers}]" : ""),
                    Recommendation = L.T("Disable the task (reversible) if you don't recognise it, then scan the file with Microsoft Defender."),
                    FilePath = file?.Path,
                    Remedy = RemedyKind.DisableTask,
                    RemedyData = { ["path"] = t.Path, ["name"] = t.Name },
                });
                break;
            }
        }
    }

    private static string NormalizeImagePath(string p)
    {
        p = p.Trim();
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(WinDir, p[12..]);
        if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(WinDir, p);
        return p;
    }

    private static void ServicesAndDrivers(Context c)
    {
        foreach (var s in Wmi.TryQuery("SELECT Name, DisplayName, PathName, StartMode, State FROM Win32_Service"))
        {
            var command = s.Str("PathName");
            var exe = StartupManager.ExtractExecutable(NormalizeImagePath(command));
            if (exe is null || FileInspector.InWindowsFolder(exe) || s.Str("StartMode") == "Disabled") continue;
            var (ind, file) = Assess(c, command);
            if (FileInspector.InUserWritableLocation(exe) && !FileInspector.IsUnder(exe, ProgramData))
                ind.Add(new(L.T("Service program stored in a user folder (services normally live in Program Files or Windows)"), 3, ThreatCategory.Persistence));
            var score = ind.Sum(i => i.Weight);
            c.Result.Persistence.Add(new PersistenceItem(L.T("Service"), s.Str("DisplayName"), s.Str("Name"), command, file?.Signer ?? "", score >= 3));
            if (score < 3) continue;
            c.Add(new SecurityFinding
            {
                Id = "service:" + s.Str("Name"),
                Title = L.F("Suspicious service: {0}", s.Str("DisplayName")),
                Category = ind.MaxBy(i => i.Weight)!.Category,
                Severity = SeverityOf(score),
                Confidence = ConfidenceOf(score),
                Location = $@"HKLM\SYSTEM\CurrentControlSet\Services\{s.Str("Name")}",
                Method = L.T("Services review") + " — " + Explain(ind),
                Evidence = command,
                Recommendation = L.T("If you don't recognise it, disable the service (reversible, takes effect after restart) and scan the file with Microsoft Defender."),
                FilePath = file?.Path,
                Remedy = RemedyKind.DisableService,
                RemedyData = { ["name"] = s.Str("Name") },
            });
        }

        // Kernel drivers loaded from user folders are a rootkit technique (legitimate drivers live in System32\drivers).
        foreach (var d in Wmi.TryQuery("SELECT Name, DisplayName, PathName, State FROM Win32_SystemDriver WHERE State='Running'"))
        {
            var path = NormalizeImagePath(d.Str("PathName"));
            if (path.Length == 0 || FileInspector.InWindowsFolder(path) || !FileInspector.InUserWritableLocation(path)) continue;
            var file = c.Inspect(path);
            c.Add(new SecurityFinding
            {
                Id = "driver:" + d.Str("Name"),
                Title = L.F("Kernel driver running from a user folder: {0}", d.Str("DisplayName")),
                Category = ThreatCategory.Backdoor,
                Severity = file?.IsSigned == true ? ThreatSeverity.Medium : ThreatSeverity.High,
                Confidence = Confidence.Medium,
                Location = path,
                Method = L.T("Loaded drivers review: drivers normally load from Windows\\System32\\drivers.") + (file is null ? "" : " " + file.SignatureText),
                Recommendation = L.T("Run a Microsoft Defender Offline scan. Some anti-cheat and hardware tools do this legitimately — check the signer."),
                FilePath = file?.Path,
                Remedy = RemedyKind.None,
            });
        }
    }

    private static void HijackPoints(Context c)
    {
        const string winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        var shell = Reg.ReadString(RegistryHive.LocalMachine, winlogon, "Shell");
        if (shell is { Length: > 0 } && !shell.Trim().Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            c.Add(Hijack("winlogon.shell", L.T("The Windows shell has been replaced"), $@"HKLM\{winlogon} › Shell", shell,
                L.T("Windows starts something other than explorer.exe when you sign in — a known persistence technique."), ThreatSeverity.High,
                RemedyKind.RestoreRegistryValue, new() { ["hive"] = "LocalMachine", ["path"] = winlogon, ["name"] = "Shell", ["value"] = "explorer.exe" }));
        var userinit = Reg.ReadString(RegistryHive.LocalMachine, winlogon, "Userinit");
        var expected = Path.Combine(Environment.SystemDirectory, "userinit.exe");
        if (userinit is { Length: > 0 } && !userinit.Trim().TrimEnd(',').Equals(expected, StringComparison.OrdinalIgnoreCase))
            c.Add(Hijack("winlogon.userinit", L.T("Sign-in program list modified"), $@"HKLM\{winlogon} › Userinit", userinit,
                L.T("Userinit normally contains only userinit.exe. Extra programs here run at every sign-in."), ThreatSeverity.High,
                RemedyKind.RestoreRegistryValue, new() { ["hive"] = "LocalMachine", ["path"] = winlogon, ["name"] = "Userinit", ["value"] = expected + "," }));
        if (Reg.ReadString(RegistryHive.CurrentUser, winlogon, "Shell") is { Length: > 0 } userShell)
            c.Add(Hijack("winlogon.usershell", L.T("A per-user Windows shell is set"), $@"HKCU\{winlogon} › Shell", userShell,
                L.T("This value does not exist on a normal installation; it replaces Explorer for your account."), ThreatSeverity.High,
                RemedyKind.RemoveRegistryValue, new() { ["hive"] = "CurrentUser", ["path"] = winlogon, ["name"] = "Shell" }));

        const string ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        string[] accessibility = ["sethc.exe", "utilman.exe", "osk.exe", "magnify.exe", "narrator.exe", "displayswitch.exe", "atbroker.exe"];
        string[] security = ["msmpeng.exe", "mpcmdrun.exe", "securityhealthsystray.exe", "taskmgr.exe", "regedit.exe", "mmc.exe", "msconfig.exe", "autoruns.exe", "procexp.exe", "mbam.exe"];
        using (var root = Registry.LocalMachine.OpenSubKey(ifeo))
        {
            foreach (var name in root?.GetSubKeyNames() ?? [])
            {
                // Some IFEO subkeys are ACL-restricted for standard users: skip those instead of aborting the whole step.
                string? debugger;
                try
                {
                    using var k = root!.OpenSubKey(name);
                    debugger = k?.GetValue("Debugger") as string;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { continue; }
                if (debugger is null || debugger.Trim().Length == 0) continue;
                if (Regex.IsMatch(debugger, @"procexp|vsjitdebugger|devenv|windbg", RegexOptions.IgnoreCase)) continue; // developer tools
                var lower = name.ToLowerInvariant();
                var (sev, why) = accessibility.Contains(lower)
                    ? (ThreatSeverity.High, L.T("Accessibility tools can be launched from the sign-in screen: redirecting them gives anyone a SYSTEM command prompt without a password (sticky-keys backdoor)."))
                    : security.Contains(lower)
                        ? (ThreatSeverity.High, L.T("Stops a security or diagnostic tool from starting — a common malware self-protection trick."))
                        : (ThreatSeverity.Medium, L.T("Every time this program starts, Windows launches the 'debugger' instead."));
                c.Add(Hijack("ifeo:" + lower, L.F("Program launch hijacked: {0}", name), $@"HKLM\{ifeo}\{name} › Debugger", debugger, why, sev,
                    RemedyKind.RemoveRegistryValue, new() { ["hive"] = "LocalMachine", ["path"] = $@"{ifeo}\{name}", ["name"] = "Debugger" }));
            }
        }

        const string windows = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
        if (Reg.ReadString(RegistryHive.LocalMachine, windows, "AppInit_DLLs") is { Length: > 0 } dlls && Reg.ReadInt(RegistryHive.LocalMachine, windows, "LoadAppInit_DLLs") == 1)
            c.Add(Hijack("appinit", L.T("AppInit DLLs are loaded into every program"), $@"HKLM\{windows} › AppInit_DLLs", dlls,
                L.T("A legacy mechanism that injects a DLL into every application using user32. Rarely used by legitimate software today."), ThreatSeverity.Medium,
                RemedyKind.RestoreRegistryValue, new() { ["hive"] = "LocalMachine", ["path"] = windows, ["name"] = "LoadAppInit_DLLs", ["value"] = "0", ["kind"] = "DWord" }));
    }

    private static SecurityFinding Hijack(string id, string title, string location, string value, string why, ThreatSeverity sev, RemedyKind remedy, Dictionary<string, string> data) => new()
    {
        Id = id,
        Title = title,
        Category = ThreatCategory.Persistence,
        Severity = sev,
        Confidence = Confidence.Medium,
        Location = location,
        Method = L.T("Registry hijack points review") + " — " + why,
        Evidence = value,
        Recommendation = L.T("Unless you set this yourself, restore the Windows default (reversible) and run a full Microsoft Defender scan."),
        Remedy = remedy,
        RemedyData = data,
    };

    private static void WmiSubscriptions(Context c)
    {
        foreach (var r in Wmi.TryQuery("SELECT Name, CommandLineTemplate, ExecutablePath FROM CommandLineEventConsumer", @"root\subscription"))
            c.Add(WmiFinding("cmd:" + r.Str("Name"), r.Str("Name"), $"{r.Str("ExecutablePath")} {r.Str("CommandLineTemplate")}".Trim()));
        foreach (var r in Wmi.TryQuery("SELECT Name, ScriptFileName, ScriptText FROM ActiveScriptEventConsumer", @"root\subscription"))
            c.Add(WmiFinding("script:" + r.Str("Name"), r.Str("Name"), r.Str("ScriptFileName").Length > 0 ? r.Str("ScriptFileName") : r.Str("ScriptText")[..Math.Min(300, r.Str("ScriptText").Length)]));
    }

    private static SecurityFinding WmiFinding(string id, string name, string evidence) => new()
    {
        Id = "wmi:" + id,
        Title = L.F("WMI event subscription runs a command: {0}", name),
        Category = ThreatCategory.Persistence,
        Severity = ThreatSeverity.Medium,
        Confidence = Confidence.Medium,
        Location = @"root\subscription",
        Method = L.T("WMI permanent event consumers review. They run code on system events without any file in the startup folders — a fileless persistence technique, rarely used by consumer software."),
        Evidence = evidence,
        Recommendation = L.T("If no management software you know of explains it, scan with Microsoft Defender and remove it with Microsoft's Autoruns tool (WMI tab)."),
    };

    // ───────────── Running programs & remote access tools ─────────────

    private static readonly (string Process, string Name)[] RemoteTools =
    [
        ("anydesk", "AnyDesk"), ("teamviewer", "TeamViewer"), ("rustdesk", "RustDesk"), ("screenconnect.clientservice", "ScreenConnect"),
        ("screenconnect.windowsclient", "ScreenConnect"), ("client32", "NetSupport Manager"), ("ateraagent", "Atera"), ("srservice", "Splashtop"),
        ("aa_v3", "Ammyy Admin"), ("winvnc", "UltraVNC"), ("tvnserver", "TightVNC"), ("rserver3", "Radmin"), ("remcos", "Remcos"),
    ];

    private static void Processes(Context c)
    {
        var running = new Monitoring.ProcessMonitor().Sample();
        foreach (var p in running.Where(p => p.Path is not null).GroupBy(p => p.Path!, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            var path = p.Path!;
            var tool = RemoteTools.FirstOrDefault(t => p.Name.Equals(t.Process, StringComparison.OrdinalIgnoreCase));
            if (tool.Name is not null)
            {
                var r = c.Inspect(path);
                var risky = FileInspector.InUserWritableLocation(path) && tool.Name is "NetSupport Manager" or "ScreenConnect" or "Remcos" || tool.Name == "Remcos";
                c.Add(new SecurityFinding
                {
                    Id = "rat:" + path,
                    Title = L.F("Remote access software is running: {0}", tool.Name),
                    Category = ThreatCategory.RemoteAccess,
                    Severity = risky ? ThreatSeverity.High : ThreatSeverity.Low,
                    Confidence = risky ? Confidence.Medium : Confidence.Low,
                    Location = path,
                    Method = L.T("Running programs review: known remote-control software.") + (r is null ? "" : " " + r.SignatureText),
                    Recommendation = L.T("Fine if you installed it yourself. If someone asked you to install it (fake support call) or you don't know it, uninstall it, then change your passwords from another device."),
                    FilePath = path,
                    Remedy = RemedyKind.Quarantine,
                    RemedyData = { ["pid"] = p.Pid.ToString() },
                });
                continue;
            }
            if (FileInspector.InWindowsFolder(path) || !FileInspector.InUserWritableLocation(path)) continue;
            var report = c.Inspect(path);
            if (report is null || report.Score < 3) continue;
            c.Add(new SecurityFinding
            {
                Id = "proc:" + path,
                Title = L.F("Suspicious running program: {0}", Path.GetFileName(path)),
                Category = report.MainCategory,
                Severity = SeverityOf(report.Score + 1),
                Confidence = ConfidenceOf(report.Score),
                Location = path,
                Method = L.T("Running programs review") + " — " + Explain(report.Indicators),
                Recommendation = L.T("Scan it with Microsoft Defender. If you don't recognise it, quarantine it (the program is closed first; reversible)."),
                FilePath = path,
                Remedy = RemedyKind.Quarantine,
                RemedyData = { ["pid"] = p.Pid.ToString() },
            });
        }
    }

    // ───────────── High-risk folders ─────────────

    private static readonly HashSet<string> CredentialStoreNames = new(StringComparer.OrdinalIgnoreCase)
        { "Login Data", "Login Data For Account", "Cookies", "Web Data", "Local State", "logins.json", "key4.db", "cookies.sqlite", "wallet.dat" };

    private static void Folders(Context c, CancellationToken ct)
    {
        var downloads = Path.Combine(UserProfile, "Downloads");
        // (root, depth, kind): kind "all" checks programs and scripts, "lure" only double extensions / disguised programs / scripts.
        var roots = new List<(string Root, int Depth, string Kind)>
        {
            (Temp, 3, "all"), (Path.Combine(WinDir, "Temp"), 2, "all"), (AppData, 0, "all"), (LocalAppData, 0, "all"),
            (ProgramData, 0, "all"), (Public, 3, "all"), (downloads, 1, "lure"),
        };
        var budget = 6000;
        var credentialFolders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, depth, kind) in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Walk(root, depth))
            {
                ct.ThrowIfCancellationRequested();
                if (--budget < 0) { c.Result.Limitations.Add(L.T("Folder review stopped after 6,000 files.")); return; }
                var name = Path.GetFileName(file);
                var dir = Path.GetDirectoryName(file)!;

                // Copies of browser credential databases outside the browser (never opened — presence only).
                if (CredentialStoreNames.Contains(name) && (kind == "lure" || FileInspector.IsUnder(file, Temp) || FileInspector.IsUnder(file, Public)
                    || FileInspector.DirectlyIn(file, AppData) || FileInspector.DirectlyIn(file, LocalAppData) || FileInspector.DirectlyIn(file, ProgramData)))
                {
                    if (!credentialFolders.TryGetValue(dir, out var names)) credentialFolders[dir] = names = [];
                    names.Add(name);
                    continue;
                }

                var ext = Path.GetExtension(file);
                var interesting = FileInspector.ExecutableExtensions.Contains(ext) || FileInspector.ScriptExtensions.Contains(ext)
                                  || (ext is ".pdf" or ".jpg" or ".png" or ".docx" or ".txt" && kind == "lure");
                if (!interesting) continue;
                var report = c.Inspect(file);
                if (report is null) continue;
                var indicators = report.Indicators.ToList();
                if (kind == "lure") indicators.RemoveAll(i => i.Weight <= 2 && i.Category == ThreatCategory.SuspiciousExecutable);
                // Installers unpack unsigned helpers into Temp sub-folders: lower the weight of the location there.
                if (FileInspector.IsUnder(file, Temp) && !FileInspector.DirectlyIn(file, Temp))
                    indicators = indicators.Select(i => i.Text == L.T("Program stored in a temporary folder") ? i with { Weight = ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ? 0 : 1 } : i).ToList();
                var score = indicators.Sum(i => i.Weight);
                if (score < 3) continue;
                c.Add(new SecurityFinding
                {
                    Id = "file:" + file,
                    Title = L.F("Suspicious file: {0}", name),
                    Category = indicators.MaxBy(i => i.Weight)!.Category,
                    Severity = SeverityOf(score),
                    Confidence = ConfidenceOf(score),
                    Location = file,
                    Method = L.T("High-risk folders review") + " — " + Explain(indicators),
                    Recommendation = L.T("Scan it with Microsoft Defender or look up its hash. Quarantine it if you don't know where it came from (reversible)."),
                    FilePath = file,
                    Remedy = RemedyKind.Quarantine,
                });
            }
        }

        foreach (var (dir, names) in credentialFolders)
        {
            var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            c.Add(new SecurityFinding
            {
                Id = "credcopy:" + dir,
                Title = L.T("Copies of browser password or cookie databases outside the browser"),
                Category = ThreatCategory.InfoStealer,
                Severity = distinct.Count >= 2 ? ThreatSeverity.High : ThreatSeverity.Medium,
                Confidence = distinct.Count >= 2 ? Confidence.Medium : Confidence.Low,
                Location = dir,
                Method = L.T("Info-stealers copy the browser's password and cookie databases to a temporary folder before sending them. These files were found by name only; their content was not opened.") ,
                Evidence = string.Join(", ", distinct),
                Recommendation = L.T("Run a full Microsoft Defender scan now. If it finds a stealer, sign out of all sessions and change your passwords from another device, starting with email and banking."),
                Remedy = RemedyKind.None,
                RemedyData = { ["folder"] = dir },
            });
        }
    }

    private static IEnumerable<string> Walk(string root, int depth)
    {
        var stack = new Stack<(string Dir, int Level)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, level) = stack.Pop();
            string[] files = [], dirs = [];
            try
            {
                var info = new DirectoryInfo(dir);
                if (level > 0 && (info.Attributes & FileAttributes.ReparsePoint) != 0) continue; // never follow junctions
                files = Directory.GetFiles(dir);
                if (level < depth) dirs = Directory.GetDirectories(dir);
            }
            catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs) stack.Push((d, level + 1));
        }
    }

    // ───────────── Browsers ─────────────

    private static readonly string[] PowerfulPermissions =
        ["<all_urls>", "*://*/*", "http://*/*", "https://*/*", "cookies", "webRequest", "webRequestBlocking", "debugger", "nativeMessaging", "proxy", "management", "clipboardRead", "desktopCapture"];

    private static IEnumerable<(string Browser, string Root)> ChromiumBrowsers()
    {
        yield return ("Google Chrome", Path.Combine(LocalAppData, @"Google\Chrome\User Data"));
        yield return ("Microsoft Edge", Path.Combine(LocalAppData, @"Microsoft\Edge\User Data"));
        yield return ("Brave", Path.Combine(LocalAppData, @"BraveSoftware\Brave-Browser\User Data"));
        yield return ("Vivaldi", Path.Combine(LocalAppData, @"Vivaldi\User Data"));
        yield return ("Opera", Path.Combine(AppData, @"Opera Software\Opera Stable"));
        yield return ("Opera GX", Path.Combine(AppData, @"Opera Software\Opera GX Stable"));
    }

    private static void BrowserExtensions(Context c)
    {
        foreach (var (browser, root) in ChromiumBrowsers().Where(b => Directory.Exists(b.Root)))
        {
            var profiles = File.Exists(Path.Combine(root, "Preferences"))
                ? [root]
                : Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "Preferences"))).ToArray();
            foreach (var profile in profiles)
                foreach (var ext in ChromiumExtensions(profile))
                    Report(c, browser, Path.GetFileName(profile), ext);
        }

        var ff = Path.Combine(AppData, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ff))
            foreach (var profile in Directory.GetDirectories(ff))
                foreach (var ext in FirefoxExtensions(profile))
                    Report(c, "Firefox", Path.GetFileName(profile), ext);
    }

    private sealed record ExtensionData(string Id, string Name, string Version, string Source, int Location, bool FromStore, string? Folder, List<string> Powerful);

    private static void Report(Context c, string browser, string profile, ExtensionData e)
    {
        c.Result.ItemsReviewed++;
        c.Result.Extensions.Add(new BrowserExtensionInfo(browser, profile, e.Id, e.Name, e.Version, e.Source, e.Powerful));
        var sideloaded = e.Location is 4 or 8;
        var unsignedFirefox = browser == "Firefox" && e.Location == -2;
        if (!sideloaded && !unsignedFirefox && !(e.Powerful.Contains("debugger") && !e.FromStore)) return;
        c.Add(new SecurityFinding
        {
            Id = $"ext:{browser}:{profile}:{e.Id}",
            Title = L.F("{0} extension not installed from a store: {1}", browser, e.Name),
            Category = ThreatCategory.BrowserExtension,
            Severity = e.Powerful.Count > 0 ? ThreatSeverity.Medium : ThreatSeverity.Low,
            Confidence = Confidence.Low,
            Location = e.Folder ?? profile,
            Method = (unsignedFirefox ? L.T("Firefox reports this add-on as unsigned.") : L.T("Loaded from a folder on disk instead of the browser's store — the method used by cookie-stealing extensions, but also by developers."))
                     + (e.Powerful.Count > 0 ? " " + L.F("Powerful permissions: {0}.", string.Join(", ", e.Powerful)) : ""),
            Evidence = $"{e.Id} {e.Version}",
            Recommendation = L.T("If you didn't add it yourself, remove it from the browser's extensions page, then sign out of all sessions of your important accounts."),
            Remedy = RemedyKind.None,
            RemedyData = e.Folder is null ? [] : new() { ["folder"] = e.Folder },
        });
    }

    private static IEnumerable<ExtensionData> ChromiumExtensions(string profile)
    {
        var list = new List<ExtensionData>();
        var settings = new Dictionary<string, JsonElement>();
        foreach (var prefsFile in new[] { "Secure Preferences", "Preferences" })
        {
            try
            {
                using var fs = new FileStream(Path.Combine(profile, prefsFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                if (doc.RootElement.TryGetProperty("extensions", out var ex) && ex.TryGetProperty("settings", out var s))
                    foreach (var p in s.EnumerateObject()) settings.TryAdd(p.Name, p.Value.Clone());
            }
            catch { /* file missing or locked */ }
        }

        foreach (var (id, s) in settings)
        {
            var location = s.TryGetProperty("location", out var l) && l.TryGetInt32(out var li) ? li : 0;
            if (location is 5 or 10) continue; // components built into the browser
            var fromStore = s.TryGetProperty("from_webstore", out var fw) && fw.ValueKind == JsonValueKind.True;
            var rel = s.TryGetProperty("path", out var p) ? p.GetString() : null;
            string? folder = rel is null ? null : Path.IsPathRooted(rel) ? rel : Path.Combine(profile, "Extensions", rel);
            if (folder is null || !Directory.Exists(folder))
            {
                var dir = Path.Combine(profile, "Extensions", id);
                folder = Directory.Exists(dir) ? Directory.GetDirectories(dir).OrderByDescending(d => d).FirstOrDefault() : null;
            }
            var manifest = ReadManifest(folder);
            if (manifest is null && location == 0) continue;
            var source = location switch
            {
                1 => fromStore ? L.T("Store") : L.T("Installed"), 2 or 3 or 6 => L.T("Installed by another program"), 4 => L.T("Unpacked folder"),
                7 or 9 => L.T("Policy"), 8 => L.T("Command line"), _ => L.T("Other"),
            };
            list.Add(new ExtensionData(id, manifest?.Name ?? id, manifest?.Version ?? "", source, location, fromStore, folder, manifest?.Powerful ?? []));
        }
        return list;
    }

    private sealed record Manifest(string Name, string Version, List<string> Powerful);

    private static Manifest? ReadManifest(string? folder)
    {
        if (folder is null) return null;
        var file = Path.Combine(folder, "manifest.json");
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name.StartsWith("__MSG_", StringComparison.Ordinal))
                name = Message(folder, root.TryGetProperty("default_locale", out var dl) ? dl.GetString() : "en", name[6..].TrimEnd('_')) ?? name;
            var perms = new List<string>();
            foreach (var prop in new[] { "permissions", "host_permissions", "optional_permissions" })
                if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    perms.AddRange(arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            if (root.TryGetProperty("content_scripts", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var script in cs.EnumerateArray())
                    if (script.TryGetProperty("matches", out var m) && m.ValueKind == JsonValueKind.Array)
                        perms.AddRange(m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            var powerful = perms.Where(x => PowerfulPermissions.Contains(x)).Distinct().ToList();
            return new Manifest(name, root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "", powerful);
        }
        catch { return null; }
    }

    private static string? Message(string folder, string? locale, string key)
    {
        foreach (var loc in new[] { locale ?? "en", "en", "en_US" }.Distinct())
        {
            var file = Path.Combine(folder, "_locales", loc, "messages.json");
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetProperty("message", out var m)) return m.GetString();
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<ExtensionData> FirefoxExtensions(string profile)
    {
        var file = Path.Combine(profile, "extensions.json");
        if (!File.Exists(file)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
        catch { yield break; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("addons", out var addons)) yield break;
            foreach (var a in addons.EnumerateArray())
            {
                if (!a.TryGetProperty("type", out var t) || t.GetString() != "extension") continue;
                var location = a.TryGetProperty("location", out var l) ? l.GetString() ?? "" : "";
                if (location is "app-builtin" or "app-system-defaults" or "app-system-addons") continue;
                var signed = a.TryGetProperty("signedState", out var ss) && ss.TryGetInt32(out var sv) ? sv : 0;
                var name = a.TryGetProperty("defaultLocale", out var dl) && dl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var perms = new List<string>();
                if (a.TryGetProperty("userPermissions", out var up))
                    foreach (var prop in new[] { "permissions", "origins" })
                        if (up.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                            perms.AddRange(arr.EnumerateArray().Select(x => x.GetString() ?? ""));
                yield return new ExtensionData(a.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "", name.Length > 0 ? name : "?",
                    a.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "", location == "app-profile" ? L.T("Installed") : location,
                    signed <= 0 ? -2 : 1, signed > 0, a.TryGetProperty("path", out var p) ? p.GetString() : null,
                    perms.Where(x => PowerfulPermissions.Contains(x)).Distinct().ToList());
            }
        }
    }

    private static readonly (string Browser, string Key)[] BrowserPolicyKeys =
    [
        ("Google Chrome", @"SOFTWARE\Policies\Google\Chrome"), ("Microsoft Edge", @"SOFTWARE\Policies\Microsoft\Edge"),
        ("Brave", @"SOFTWARE\Policies\BraveSoftware\Brave"),
    ];

    private static readonly string[] StoreUpdateUrls = ["clients2.google.com/service/update2/crx", "edge.microsoft.com/extensionwebstorebase"];

    private static void BrowserPoliciesAndShortcuts(Context c)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            foreach (var (browser, key) in BrowserPolicyKeys)
            {
                using var forced = root.OpenSubKey(key + @"\ExtensionInstallForcelist");
                foreach (var name in forced?.GetValueNames() ?? [])
                {
                    if (forced!.GetValue(name) is not string value) continue;
                    var store = StoreUpdateUrls.Any(u => value.Contains(u, StringComparison.OrdinalIgnoreCase)) || !value.Contains(';');
                    c.Add(new SecurityFinding
                    {
                        Id = $"forcelist:{hive}:{key}:{name}",
                        Title = L.F("{0}: an extension is force-installed by policy", browser),
                        Category = ThreatCategory.BrowserHijack,
                        Severity = store ? ThreatSeverity.Low : ThreatSeverity.High,
                        Confidence = store ? Confidence.Low : Confidence.Medium,
                        Location = $@"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{key}\ExtensionInstallForcelist › {name}",
                        Method = store
                            ? L.T("Browser policies review. Force-installed extensions cannot be removed by the user. Normal on a work PC managed by an organisation; on a personal PC this is a typical adware technique.")
                            : L.T("Browser policies review. The extension is forced from a server that is not the official store — a technique used by malicious extensions."),
                        Evidence = value,
                        Recommendation = L.T("On a personal PC, remove the policy (reversible) and then remove the extension from the browser."),
                        Remedy = RemedyKind.RemoveRegistryValue,
                        RemedyData = { ["hive"] = hive.ToString(), ["path"] = key + @"\ExtensionInstallForcelist", ["name"] = name },
                    });
                }
                using var policy = root.OpenSubKey(key);
                foreach (var name in new[] { "ProxyServer", "ProxyPacUrl", "HomepageLocation", "DefaultSearchProviderSearchURL", "RestoreOnStartupURLs" })
                {
                    var v = policy?.GetValue(name) as string;
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    var proxy = name.StartsWith("Proxy", StringComparison.Ordinal);
                    c.Add(new SecurityFinding
                    {
                        Id = $"browserpolicy:{hive}:{key}:{name}",
                        Title = proxy ? L.F("{0}: proxy imposed by policy", browser) : L.F("{0}: start page or search engine imposed by policy", browser),
                        Category = ThreatCategory.BrowserHijack,
                        Severity = proxy ? ThreatSeverity.Medium : ThreatSeverity.Low,
                        Confidence = Confidence.Low,
                        Location = $@"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{key} › {name}",
                        Method = L.T("Browser policies review. Settings forced by policy cannot be changed in the browser; on a personal PC this is a typical browser-hijacker technique."),
                        Evidence = v,
                        Recommendation = L.T("If this is not a work PC, remove the policy (reversible)."),
                        Remedy = RemedyKind.RemoveRegistryValue,
                        RemedyData = { ["hive"] = hive.ToString(), ["path"] = key, ["name"] = name },
                    });
                }
            }
        }

        // Shortcuts that start a browser with debugging or an injected extension (used by cookie stealers).
        string[] browserExes = ["chrome.exe", "msedge.exe", "brave.exe", "vivaldi.exe", "opera.exe", "launcher.exe", "firefox.exe"];
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Path.Combine(AppData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
        };
        dynamic? shell = null;
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); } catch { }
        if (shell is not null)
        {
            foreach (var lnk in folders.Where(Directory.Exists).SelectMany(f => Walk(f, 2)).Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
            {
                string target, args;
                try { dynamic sc = shell.CreateShortcut(lnk); target = sc.TargetPath; args = sc.Arguments; }
                catch { continue; }
                if (!browserExes.Contains(Path.GetFileName(target ?? ""), StringComparer.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(args)) continue;
                c.Result.ItemsReviewed++;
                var dangerous = Regex.IsMatch(args, @"--(load-extension|remote-debugging-port|remote-debugging-pipe|disable-extensions-except)", RegexOptions.IgnoreCase);
                var url = Regex.IsMatch(args, @"https?://", RegexOptions.IgnoreCase);
                if (!dangerous && !url) continue;
                c.Add(new SecurityFinding
                {
                    Id = "lnk:" + lnk,
                    Title = dangerous ? L.F("Browser shortcut starts the browser in a remote-control mode: {0}", Path.GetFileName(lnk)) : L.F("Browser shortcut opens a fixed web page: {0}", Path.GetFileName(lnk)),
                    Category = dangerous ? ThreatCategory.InfoStealer : ThreatCategory.BrowserHijack,
                    Severity = dangerous ? ThreatSeverity.High : ThreatSeverity.Low,
                    Confidence = dangerous ? Confidence.Medium : Confidence.Low,
                    Location = lnk,
                    Method = dangerous
                        ? L.T("Shortcut review: --load-extension or --remote-debugging lets another program read everything in the browser, including session cookies. Stealers modify shortcuts this way.")
                        : L.T("Shortcut review: a web address in a browser shortcut is a common browser-hijacker trick."),
                    Evidence = $"{target} {args}",
                    Recommendation = L.T("Delete this shortcut and pin the browser again from the Start menu. If it was the remote-debugging type, also run a full Microsoft Defender scan."),
                    Remedy = RemedyKind.None,
                    RemedyData = { ["folder"] = Path.GetDirectoryName(lnk)! },
                });
            }
        }

        foreach (var p in Wmi.TryQuery("SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name='chrome.exe' OR Name='msedge.exe' OR Name='brave.exe' OR Name='vivaldi.exe' OR Name='opera.exe'"))
        {
            var cmd = p.Str("CommandLine");
            if (!Regex.IsMatch(cmd, @"--(remote-debugging-port|load-extension)=", RegexOptions.IgnoreCase) || cmd.Contains("--type=", StringComparison.Ordinal)) continue;
            c.Add(new SecurityFinding
            {
                Id = "browserdebug:" + p.Str("Name"),
                Title = L.F("{0} is running with remote debugging or an injected extension", p.Str("Name")),
                Category = ThreatCategory.InfoStealer,
                Severity = ThreatSeverity.High,
                Confidence = Confidence.Medium,
                Location = p.Str("Name"),
                Method = L.T("Running browser review: in this mode any local program can read pages and session cookies. Normal only for web developers using automation tools."),
                Evidence = cmd.Length > 400 ? cmd[..400] + "…" : cmd,
                Recommendation = L.T("Close the browser completely and reopen it normally. If the flag comes back, run a full Microsoft Defender scan."),
                Remedy = RemedyKind.None,
            });
        }
    }

    // ───────────── Configuration ─────────────

    private static void Configuration(Context c)
    {
        var hosts = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
        if (File.Exists(hosts))
        {
            var security = new Regex(@"(microsoft|windowsupdate|msftncsi|defender|virustotal|malwarebytes|kaspersky|eset|avast|avg\.com|bitdefender|norton|mcafee|sophos|trendmicro|f-secure|emsisoft)", RegexOptions.IgnoreCase);
            var redirects = new List<string>();
            var blocking = 0;
            foreach (var raw in File.ReadLines(hosts))
            {
                var line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var ip = parts[0];
                var sink = ip is "0.0.0.0" or "127.0.0.1" or "::1" or "::";
                foreach (var host in parts.Skip(1))
                {
                    if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) continue;
                    if (security.IsMatch(host))
                    {
                        c.Add(new SecurityFinding
                        {
                            Id = "hosts:" + host,
                            Title = L.F("The hosts file blocks or redirects a security or update site: {0}", host),
                            Category = ThreatCategory.Configuration,
                            Severity = ThreatSeverity.High,
                            Confidence = Confidence.Medium,
                            Location = hosts,
                            Method = L.T("Hosts file review: malware edits it to stop Windows Update and security products from reaching their servers. Some 'privacy' tools do it too and break updates."),
                            Evidence = line,
                            Recommendation = L.T("Remove this line from the hosts file (open it with Notepad as administrator), then update Microsoft Defender."),
                            Remedy = RemedyKind.None,
                            RemedyData = { ["folder"] = Path.GetDirectoryName(hosts)! },
                        });
                    }
                    else if (sink) blocking++;
                    else redirects.Add($"{host} → {ip}");
                }
            }
            if (redirects.Count > 0)
                c.Add(new SecurityFinding
                {
                    Id = "hosts:redirects",
                    Title = L.F("The hosts file redirects {0} site(s) to other addresses", redirects.Count),
                    Category = ThreatCategory.Configuration,
                    Severity = ThreatSeverity.Low,
                    Confidence = Confidence.Low,
                    Location = hosts,
                    Method = L.T("Hosts file review: redirections are used by developers and some games, and by phishing malware to send you to fake sites."),
                    Evidence = string.Join("\n", redirects.Take(10)),
                    Recommendation = L.T("Check that you recognise every entry."),
                    RemedyData = { ["folder"] = Path.GetDirectoryName(hosts)! },
                });
            if (blocking > 0) c.Result.ItemsReviewed += blocking;
        }

        const string inet = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        var proxy = Reg.ReadInt(RegistryHive.CurrentUser, inet, "ProxyEnable") == 1 ? Reg.ReadString(RegistryHive.CurrentUser, inet, "ProxyServer") : null;
        var pac = Reg.ReadString(RegistryHive.CurrentUser, inet, "AutoConfigURL");
        if (!string.IsNullOrWhiteSpace(proxy) || !string.IsNullOrWhiteSpace(pac))
            c.Add(new SecurityFinding
            {
                Id = "proxy:system",
                Title = L.T("All web traffic goes through a proxy"),
                Category = ThreatCategory.Configuration,
                Severity = ThreatSeverity.Low,
                Confidence = Confidence.Low,
                Location = $@"HKCU\{inet}",
                Method = L.T("Proxy settings review: normal with a company network, some VPNs or debugging tools; also used by malware to intercept traffic."),
                Evidence = string.Join("  ", new[] { proxy, pac }.Where(x => !string.IsNullOrWhiteSpace(x))),
                Recommendation = L.T("If you don't know why a proxy is set, turn it off in Settings › Network › Proxy."),
                Remedy = RemedyKind.OpenSettings,
                RemedyData = { ["uri"] = "ms-settings:network-proxy" },
            });

        var pref = Wmi.TryQuery("SELECT ExclusionPath, ExclusionProcess, ExclusionExtension FROM MSFT_MpPreference", @"root\Microsoft\Windows\Defender").FirstOrDefault();
        if (pref is not null)
        {
            static string[] Values(Dictionary<string, object?> r, string k) => r.TryGetValue(k, out var v) && v is string[] a ? a : [];
            var all = Values(pref, "ExclusionPath").Select(v => ("path", v)).Concat(Values(pref, "ExclusionProcess").Select(v => ("process", v)))
                .Concat(Values(pref, "ExclusionExtension").Select(v => ("extension", v))).ToList();
            if (all.Any(x => x.v.StartsWith("N/A", StringComparison.OrdinalIgnoreCase)))
                c.Result.Limitations.Add(L.T("Microsoft Defender exclusions can only be read as administrator."));
            else
                foreach (var (kind, value) in all)
                {
                    var risky = kind == "extension" && value.Trim('.').ToLowerInvariant() is "exe" or "dll" or "ps1" or "bat" or "cmd" or "vbs" or "js" or "scr"
                                || kind == "path" && (value.TrimEnd('\\').Length <= 2 || FileInspector.InUserWritableLocation(value.TrimEnd('\\') + "\\x") || value.TrimEnd('\\').Equals(UserProfile, StringComparison.OrdinalIgnoreCase));
                    c.Add(new SecurityFinding
                    {
                        Id = $"exclusion:{kind}:{value}",
                        Title = risky ? L.F("Risky Microsoft Defender exclusion: {0}", value) : L.F("Microsoft Defender exclusion: {0}", value),
                        Category = ThreatCategory.Configuration,
                        Severity = risky ? ThreatSeverity.High : ThreatSeverity.Info,
                        Confidence = risky ? Confidence.Medium : Confidence.Low,
                        Location = L.T("Windows Security › Virus & threat protection › Exclusions"),
                        Method = L.T("Defender configuration review: files matching an exclusion are never scanned. Malware adds broad exclusions to hide; game mods and developer tools add narrow ones."),
                        Evidence = $"{kind}: {value}",
                        Recommendation = risky ? L.T("Remove this exclusion unless you added it yourself for a precise reason.") : L.T("Keep it only if you know why it is there."),
                        Remedy = RemedyKind.OpenSettings,
                        RemedyData = { ["uri"] = "windowsdefender://threatsettings" },
                    });
                }
        }
    }

    /// <summary>A plain-text report suitable for sharing with a technician (no file contents, only paths and hashes).</summary>
    public static string ExportText(SecurityScanResult r, DefenderStatus? defender)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Windows Ultimate — Security Center report — {r.Time:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"{Environment.MachineName} · {r.ItemsReviewed} items reviewed · {r.Findings.Count} findings · {r.Duration.TotalSeconds:0.0}s");
        if (defender is not null)
            sb.AppendLine($"Defender: {(defender.IsActive ? "active" : "not active")} · real-time {(defender.RealTimeProtection ? "on" : "off")} · signatures {defender.SignatureVersion} ({defender.SignaturesUpdated:g})");
        sb.AppendLine();
        foreach (var f in r.Findings)
        {
            sb.AppendLine($"[{f.Severity}] [{f.Confidence} confidence] {f.Title}");
            sb.AppendLine($"  Category: {f.Category}   Time: {f.Time:yyyy-MM-dd HH:mm:ss}");
            if (f.Location.Length > 0) sb.AppendLine($"  Location: {f.Location}");
            if (f.FilePath is not null) sb.AppendLine($"  File: {f.FilePath}  SHA-256: {f.Sha256}");
            sb.AppendLine($"  Method: {f.Method}");
            if (f.Evidence.Length > 0) sb.AppendLine($"  Evidence: {f.Evidence.Replace("\n", " | ")}");
            sb.AppendLine($"  Recommended: {f.Recommendation}");
            sb.AppendLine();
        }
        sb.AppendLine("Limitations:");
        foreach (var l in r.Limitations) sb.AppendLine("  - " + l);
        return sb.ToString();
    }
}
