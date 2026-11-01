using System.Diagnostics;
using System.Text.RegularExpressions;
using WindowsUltimate.Core.Drivers;
using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Monitoring;
using WindowsUltimate.Core.Optimization;
using WindowsUltimate.Core.Startup;

namespace WindowsUltimate.Core.Diagnostics;

/// <summary>
/// Turns raw measurements into findings. Each finding states the evidence it is based on;
/// nothing here changes the system.
/// </summary>
public static class SystemAnalyzer
{
    public sealed class Context
    {
        public List<EventCategory>? Events { get; set; }
        public List<ProblemDevice>? ProblemDevices { get; set; }
        public List<DriverInfo>? Drivers { get; set; }
        public int EnabledStartup { get; set; }
        public double IdleCpu { get; set; }
        public List<ProcessSample> TopCpu { get; set; } = [];
    }

    public static async Task<(ScanReport Report, Context Context)> ScanAsync(SystemProfile profile, bool full, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var ctx = new Context();
        var findings = new List<Finding>();

        progress?.Report(L.T("Measuring background activity…"));
        var monitor = new ProcessMonitor();
        monitor.Sample();
        ctx.IdleCpu = await SystemChecks.SampleCpuAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        ctx.TopCpu = monitor.Sample().Where(p => p.Pid > 4).OrderByDescending(p => p.CpuPercent).Take(5).ToList();

        await Task.Run(() =>
        {
            progress?.Report(L.T("Checking Windows configuration…"));
            Guard(findings, () => CheckSecurity(findings));
            Guard(findings, () => CheckStorage(findings));
            Guard(findings, () => CheckMemory(profile, findings));
            Guard(findings, () => CheckDisplays(profile, findings));
            Guard(findings, () => CheckPower(profile, findings));
            Guard(findings, () => CheckGaming(profile, findings));
            Guard(findings, () => CheckRepairs(findings));
            Guard(findings, () =>
            {
                ctx.EnabledStartup = StartupManager.List().Count(e => e.Enabled);
                CheckStartup(ctx.EnabledStartup, findings);
            });
            CheckBackground(ctx, findings);
            CheckUptime(profile, findings);

            if (profile.Os.IsInsider)
                findings.Add(new Finding
                {
                    Id = "os.insider", Severity = Severity.Info, Area = "Windows",
                    Title = "Windows Insider build",
                    Detail = "Pre-release builds can contain bugs that affect performance or stability. Some privacy settings are restricted by the Insider programme.",
                    Evidence = L.F("Build {0}{1}", profile.Os.BuildString, (profile.Os.InsiderChannel is { } c ? L.F(", channel {0}", c) : "")),
                });

            if (full)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(L.T("Reading event logs (last 30 days)…"));
                Guard(findings, () => { ctx.Events = EventLogScanner.Scan(); CheckEvents(ctx.Events, findings); });
                ct.ThrowIfCancellationRequested();
                progress?.Report(L.T("Checking devices and drivers…"));
                Guard(findings, () => { ctx.ProblemDevices = DriverAnalyzer.ProblemDevices(); CheckDevices(ctx.ProblemDevices, findings); });
                Guard(findings, () => CheckGpuDriver(profile, findings));
                progress?.Report(L.T("Checking disk health…"));
                Guard(findings, () => { profile.Disks = HardwareDetector.DetectDisks(); CheckDisks(profile, findings); });
            }
        }, ct).ConfigureAwait(false);

        if (findings.All(f => f.Severity < Severity.Suggestion))
            findings.Add(new Finding { Id = "all.good", Severity = Severity.Good, Area = "System", Title = "No problems found", Detail = "Nothing in this scan needs your attention." });

        var report = new ScanReport { IsFull = full, Findings = findings, Duration = sw.Elapsed };
        Log.Info($"{(full ? "Full" : "Quick")} scan: score {report.Score}, {findings.Count} findings, {sw.Elapsed.TotalSeconds:0.0}s");
        return (report, ctx);
    }

    private static void Guard(List<Finding> findings, Action check)
    {
        try { check(); }
        catch (Exception ex) { Log.Warn($"Check failed: {ex.Message}"); }
    }

    private static void CheckSecurity(List<Finding> f)
    {
        var s = SystemChecks.Security();
        if (s.Antivirus.Count > 0 && !s.AnyAntivirusEnabled)
            f.Add(new Finding
            {
                Id = "security.av", Severity = Severity.Critical, Area = "Security", ScorePenalty = 20,
                Title = "Real-time antivirus protection is off",
                Detail = "No antivirus is actively protecting this PC. Windows Ultimate never disables Defender; something else did.",
                Evidence = string.Join(", ", s.Antivirus.Select(a => $"{a.Name}: {(a.Enabled ? "on" : "off")}")),
                OpenUri = "windowsdefender://threat", ActionLabel = "Open Windows Security",
            });
        else if (s.Antivirus.Any(a => a.Enabled && !a.UpToDate))
            f.Add(new Finding
            {
                Id = "security.av.outdated", Severity = Severity.Warning, Area = "Security", ScorePenalty = 8,
                Title = "Antivirus definitions are out of date",
                Detail = "Your antivirus reports outdated signatures. Run Windows Update or update your antivirus.",
                Evidence = string.Join(", ", s.Antivirus.Where(a => !a.UpToDate).Select(a => a.Name)),
                OpenUri = "windowsdefender://threat", ActionLabel = "Open Windows Security",
            });

        if (s.FirewallEnabled == false)
            f.Add(new Finding
            {
                Id = "security.firewall", Severity = Severity.Warning, Area = "Security", ScorePenalty = 10,
                Title = "Windows Firewall is off for at least one network profile",
                Detail = "Unless another firewall product manages this PC, turn it back on in Windows Security.",
                OpenUri = "windowsdefender://network", ActionLabel = "Open firewall settings",
            });

        if (s.MemoryIntegrityRunning is { } hvci)
            f.Add(new Finding
            {
                Id = "security.hvci", Severity = Severity.Info, Area = "Security",
                Title = hvci ? "Memory Integrity (HVCI) is on" : "Memory Integrity (HVCI) is off",
                Detail = hvci
                    ? "This protects the kernel against malicious drivers. Microsoft notes it can cost some performance in certain games; Windows Ultimate reports it but never turns it off. You can decide in Windows Security › Device security."
                    : "Memory Integrity protects the kernel against malicious or vulnerable drivers. Consider enabling it in Windows Security › Device security if all your drivers are compatible.",
                OpenUri = "windowsdefender://coreisolation", ActionLabel = "Core isolation settings",
            });
    }

    private static void CheckStorage(List<Finding> f)
    {
        var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        var freePct = 100.0 * sys.AvailableFreeSpace / sys.TotalSize;
        var evidence = L.F("{0} {1} free of {2} ({3:0}%)", sys.Name, Format.Bytes(sys.AvailableFreeSpace), Format.Bytes(sys.TotalSize), freePct);
        if (freePct < 10 || sys.AvailableFreeSpace < 15L << 30)
            f.Add(new Finding
            {
                Id = "storage.low", Severity = Severity.Warning, Area = "Storage", ScorePenalty = 15,
                Title = "System drive is almost full",
                Detail = "Windows needs free space for updates, the paging file and shader caches. Below ~10% free, updates can fail and SSD write performance drops.",
                Evidence = evidence, NavigateTo = "storage", ActionLabel = "Free up space",
            });
        else if (freePct < 20)
            f.Add(new Finding
            {
                Id = "storage.medium", Severity = Severity.Suggestion, Area = "Storage", ScorePenalty = 5,
                Title = "System drive space is getting low",
                Detail = "Consider cleaning temporary files or moving large games to another drive.",
                Evidence = evidence, NavigateTo = "storage", ActionLabel = "Analyze storage",
            });
    }

    private static void CheckMemory(SystemProfile p, List<Finding> f)
    {
        var mem = SystemChecks.Memory();
        if (mem.CommitPercent > 90)
            f.Add(new Finding
            {
                Id = "memory.pressure", Severity = Severity.Warning, Area = "Memory", ScorePenalty = 10,
                Title = "Memory is under heavy pressure",
                Detail = "Committed memory is close to the limit. Programs may stutter while Windows pages to disk, or fail with out-of-memory errors. Close unused programs or review startup apps.",
                Evidence = L.F("Commit charge {0:0}% of limit, physical memory load {1:0}%", mem.CommitPercent, mem.MemoryLoadPercent),
                NavigateTo = "startup", ActionLabel = "Review programs",
            });

        if (TweakCatalog.PagingFileDisabled())
            f.Add(new Finding
            {
                Id = "memory.nopagefile", Severity = Severity.Critical, Area = "Memory", ScorePenalty = 10,
                Title = "The paging file is disabled",
                Detail = "Without a paging file, programs crash with out-of-memory errors long before RAM is full. This is a common harmful 'optimization'.",
                TweakId = "fix.pagefile", ActionLabel = "Restore paging file",
            });

        var desktop = !p.IsLaptop;
        if (desktop && p.Memory.Modules.Count == 1 && p.Memory.SlotsTotal >= 2)
            f.Add(new Finding
            {
                Id = "memory.singlechannel", Severity = Severity.Suggestion, Area = "Memory", ScorePenalty = 3,
                Title = "Memory is running in single-channel mode",
                Detail = "Only one memory module is installed. A second identical module enables dual-channel mode, which doubles memory bandwidth — a real FPS gain in CPU-bound games and a large one with integrated graphics.",
                Evidence = L.F("1 module of {0} slots: {1} {2}", p.Memory.SlotsTotal, Format.Bytes(p.Memory.Modules[0].CapacityBytes), p.Memory.Type),
            });

        var speed = p.Memory.ConfiguredMhz;
        var jedecDefault = p.Memory.Type == "DDR4" ? speed is > 0 and <= 2666 : p.Memory.Type == "DDR5" && speed is > 0 and <= 4800;
        if (desktop && jedecDefault)
            f.Add(new Finding
            {
                Id = "memory.xmp", Severity = Severity.Suggestion, Area = "Memory",
                Title = "Memory may be running below its rated speed",
                Detail = L.F("Your {0} runs at {1} MT/s, a JEDEC default. If your kit is rated faster (check its label or part number {2}), enabling XMP/EXPO in the BIOS can noticeably improve CPU-bound FPS. This is a BIOS setting: Windows Ultimate never changes firmware.", p.Memory.Type, speed, p.Memory.Modules.FirstOrDefault()?.PartNumber),
                Evidence = L.F("Configured {0} MT/s, module reports {1} MT/s", speed, p.Memory.Modules.FirstOrDefault()?.RatedMhz),
            });
    }

    private static void CheckDisplays(SystemProfile p, List<Finding> f)
    {
        foreach (var d in p.Displays.Where(d => d.BelowMaxRefresh))
            f.Add(new Finding
            {
                Id = "display.refresh." + d.DeviceName, Severity = Severity.Warning, Area = "Gaming", ScorePenalty = 5,
                Title = L.F("Display runs at {0} Hz but supports {1} Hz", d.CurrentHz, d.MaxHzAtCurrentResolution),
                Detail = "Your monitor can refresh faster than it currently does. This is one of the most common and most noticeable gaming misconfigurations. Choose the highest rate in Settings › Display › Advanced display.",
                Evidence = L.F("{0} on {1}: {2}×{3} @ {4} Hz (max {5} Hz)", d.MonitorName, d.AdapterName, d.Width, d.Height, d.CurrentHz, d.MaxHzAtCurrentResolution),
                OpenUri = "ms-settings:display-advanced", ActionLabel = "Open display settings",
            });
    }

    private static void CheckPower(SystemProfile p, List<Finding> f)
    {
        if (SystemChecks.PowerPlan() is { } plan && plan.Id == PowerPlanAction.PowerSaver && !p.HasBattery)
            f.Add(new Finding
            {
                Id = "power.saver", Severity = Severity.Warning, Area = "Power", ScorePenalty = 5,
                Title = "Power saver plan is active on a desktop",
                Detail = "Power saver limits processor performance. Switch back to Balanced.",
                Evidence = L.F("Active plan: {0}", plan.Name), TweakId = "power.balanced", ActionLabel = "Use Balanced",
            });
    }

    private static void CheckGaming(SystemProfile p, List<Finding> f)
    {
        if (SystemChecks.BackgroundRecording())
            f.Add(new Finding
            {
                Id = "gaming.bgrecording", Severity = Severity.Suggestion, Area = "Gaming", ScorePenalty = 3,
                Title = "Background game recording is on",
                Detail = "Xbox Game Bar is continuously recording gameplay, which keeps the GPU encoder and disk busy.",
                TweakId = "gaming.dvr.background", ActionLabel = "Turn off",
            });
        if (SystemChecks.GameModeEnabled() == false)
            f.Add(new Finding
            {
                Id = "gaming.gamemode", Severity = Severity.Suggestion, Area = "Gaming", ScorePenalty = 2,
                Title = "Game Mode is off",
                Detail = "Game Mode stops Windows Update from installing drivers and showing restart prompts while you play.",
                TweakId = "gaming.gamemode", ActionLabel = "Turn on",
            });
        if (SystemChecks.FullscreenOptimizations().Disabled)
            f.Add(new Finding
            {
                Id = "gaming.fso", Severity = Severity.Info, Area = "Gaming",
                Title = "Fullscreen optimizations are disabled globally",
                Detail = "This is a leftover from an older tweak. On current Windows 11 builds fullscreen optimizations enable flip-model presentation and Auto HDR; if a specific game has issues, disable them for that game only (Properties › Compatibility).",
                Evidence = SystemChecks.FullscreenOptimizations().Detail,
            });
        if (p.Os.IsWindows11 && SystemChecks.DirectXGlobalSettings().GetValueOrDefault("SwapEffectUpgradeEnable") is not "1")
            f.Add(new Finding
            {
                Id = "gaming.windowed", Severity = Severity.Suggestion, Area = "Gaming", ScorePenalty = 2,
                Title = "Optimizations for windowed games are not enabled",
                Detail = "Borderless and windowed DX10/11 games use the slower legacy presentation path, adding latency.",
                TweakId = "gaming.windowed", ActionLabel = "Enable",
            });
    }

    private static void CheckRepairs(List<Finding> f)
    {
        if (SystemChecks.ServiceStart("wuauserv") == 4)
            f.Add(new Finding
            {
                Id = "wu.disabled", Severity = Severity.Critical, Area = "Windows Update", ScorePenalty = 15,
                Title = "Windows Update is disabled",
                Detail = "The Windows Update service is disabled, so this PC no longer receives security fixes. The Store and Defender definitions are affected too.",
                TweakId = "fix.wuauserv", ActionLabel = "Repair",
            });

        var reboot = SystemChecks.PendingReboot();
        if (reboot.Required)
            f.Add(new Finding
            {
                Id = "reboot.pending", Severity = Severity.Suggestion, Area = "Windows Update", ScorePenalty = 3,
                Title = "A restart is pending",
                Detail = "Updates or component changes are waiting for a restart. Until then, servicing operations (and some repairs) may not work correctly.",
                Evidence = string.Join(", ", new[] { reboot.ComponentServicing ? "Component servicing" : null, reboot.WindowsUpdate ? "Windows Update" : null }.OfType<string>()),
            });
    }

    private static void CheckStartup(int enabled, List<Finding> f)
    {
        if (enabled > 15)
            f.Add(new Finding
            {
                Id = "startup.many", Severity = Severity.Suggestion, Area = "Startup", ScorePenalty = enabled > 25 ? 8 : 5,
                Title = L.F("{0} programs start with Windows", enabled),
                Detail = "Every startup program delays the moment your PC becomes usable and keeps running in the background. Disable the ones you don't need right away (they can still be started manually).",
                Evidence = L.F("{0} enabled startup entries", enabled), NavigateTo = "startup", ActionLabel = "Review startup",
            });
    }

    private static void CheckBackground(Context ctx, List<Finding> f)
    {
        if (ctx.IdleCpu > 20)
            f.Add(new Finding
            {
                Id = "cpu.background", Severity = Severity.Warning, Area = "Responsiveness", ScorePenalty = 8,
                Title = L.F("High background CPU usage ({0:0}%)", ctx.IdleCpu),
                Detail = "Something is using the processor while the PC should be mostly idle. This reduces the CPU time available to games and can cause stutter. (If you were running something heavy during the scan, this is expected.)",
                Evidence = string.Join(", ", ctx.TopCpu.Where(p => p.CpuPercent >= 1).Select(p => $"{p.Name} {p.CpuPercent:0}%")),
                NavigateTo = "startup", ActionLabel = "View processes",
            });
    }

    private static void CheckUptime(SystemProfile p, List<Finding> f)
    {
        var up = p.Os.Uptime;
        if (up.TotalDays > 14)
            f.Add(new Finding
            {
                Id = "uptime.long", Severity = Severity.Suggestion, Area = "Windows", ScorePenalty = 2,
                Title = L.F("PC has not restarted for {0} days", (int)up.TotalDays),
                Detail = "With Fast Startup, 'Shut down' does not restart the Windows kernel. A real restart applies updates and clears driver state; it often fixes gradually worsening performance.",
                Evidence = L.F("Uptime {0}", Format.Duration(up)),
            });
    }

    private static void CheckEvents(List<EventCategory> events, List<Finding> f)
    {
        EventCategory? Get(string key) => events.FirstOrDefault(e => e.Key == key);

        if (Get("bsod") is { Count: > 0 } bsod)
            f.Add(new Finding
            {
                Id = "events.bsod", Severity = Severity.Warning, Area = "Stability", ScorePenalty = Math.Min(15, 5 + bsod.Count * 2),
                Title = L.F("{0} blue screen(s) in the last 30 days", bsod.Count),
                Detail = bsod.Explanation, Evidence = string.Join("; ", bsod.Items.Take(3).Select(i => $"{i.Time:g}: {i.Summary}")), NavigateTo = "repair", ActionLabel = "Details",
            });
        if (Get("shutdown") is { Count: >= 2 } sd)
            f.Add(new Finding
            {
                Id = "events.shutdown", Severity = Severity.Warning, Area = "Stability", ScorePenalty = 5,
                Title = L.F("{0} unexpected shutdowns in the last 30 days", sd.Count),
                Detail = sd.Explanation, Evidence = L.F("Last: {0:g}", sd.Last), NavigateTo = "repair", ActionLabel = "Details",
            });
        if (Get("whea") is { Count: > 0 } whea)
            f.Add(new Finding
            {
                Id = "events.whea", Severity = Severity.Warning, Area = "Stability", ScorePenalty = 10,
                Title = L.F("{0} hardware error report(s)", whea.Count),
                Detail = whea.Explanation + " If you enabled XMP/EXPO or an overclock, test at stock settings.",
                Evidence = L.F("Last: {0:g}", whea.Last), NavigateTo = "repair", ActionLabel = "Details",
            });
        if (Get("tdr") is { Count: > 0 } tdr)
            f.Add(new Finding
            {
                Id = "events.tdr", Severity = Severity.Warning, Area = "Gaming", ScorePenalty = 5,
                Title = L.F("Graphics driver recovered from {0} crash(es)", tdr.Count),
                Detail = tdr.Explanation, Evidence = L.F("Last: {0:g}", tdr.Last), NavigateTo = "drivers", ActionLabel = "Check drivers",
            });
        if (Get("disk") is { Count: > 0 } disk)
            f.Add(new Finding
            {
                Id = "events.disk", Severity = Severity.Warning, Area = "Storage", ScorePenalty = 10,
                Title = L.F("{0} disk or file system error(s)", disk.Count),
                Detail = disk.Explanation, Evidence = string.Join("; ", disk.Items.Take(3).Select(i => $"{i.Provider} {i.EventId} at {i.Time:g}")), NavigateTo = "repair", ActionLabel = "Check disks",
            });
        if (Get("appcrash") is { Count: >= 10 } crash)
        {
            var top = crash.Items.GroupBy(i => Regex.Replace(i.Summary, " crashed.*", "")).OrderByDescending(g => g.Count()).Take(3);
            f.Add(new Finding
            {
                Id = "events.appcrash", Severity = Severity.Suggestion, Area = "Stability", ScorePenalty = 3,
                Title = L.F("{0} application crashes in the last 30 days", crash.Count),
                Detail = "Frequent crashes of the same program usually point to that program or one of its plug-ins; crashes across many programs point to system instability (RAM, overclock, drivers).",
                Evidence = string.Join(", ", top.Select(g => $"{g.Key} ×{g.Count()}")), NavigateTo = "repair", ActionLabel = "Details",
            });
        }
    }

    private static void CheckDevices(List<ProblemDevice> devices, List<Finding> f)
    {
        if (devices.Count == 0) return;
        f.Add(new Finding
        {
            Id = "devices.problem", Severity = Severity.Warning, Area = "Drivers", ScorePenalty = Math.Min(15, devices.Count * 5),
            Title = L.F("{0} device(s) with a driver problem", devices.Count),
            Detail = devices.Any(d => d.IsMissingDriver)
                ? "At least one device has no driver installed. Check Windows Update › Optional updates or the manufacturer's website."
                : "Windows reports problems with these devices.",
            Evidence = string.Join("; ", devices.Take(4).Select(d => $"{d.Name} (code {d.Code})")),
            NavigateTo = "drivers", ActionLabel = "View devices",
        });
    }

    private static void CheckGpuDriver(SystemProfile p, List<Finding> f)
    {
        var gpu = p.PrimaryGpu;
        if (gpu?.DriverDate is not { } date || gpu.Vendor is not (HardwareVendor.Nvidia or HardwareVendor.Amd or HardwareVendor.Intel)) return;
        var age = DateTime.Now - date;
        if (age.TotalDays > 365)
            f.Add(new Finding
            {
                Id = "driver.gpu.old", Severity = Severity.Suggestion, Area = "Drivers", ScorePenalty = 2,
                Title = L.F("Graphics driver is {0} months old", (int)(age.TotalDays / 30)),
                Detail = "Recent games frequently ship with fixes and optimizations in new GPU drivers. Download only from the official vendor site.",
                Evidence = L.F("{0}: {1} ({2:d})", gpu.Name, gpu.FriendlyDriverVersion, date),
                OpenUri = DriverAnalyzer.OfficialDriverSource(gpu.Vendor), ActionLabel = "Official drivers",
            });
    }

    private static void CheckDisks(SystemProfile p, List<Finding> f)
    {
        foreach (var d in p.Disks.Where(d => d.Health is "Warning" or "Unhealthy"))
            f.Add(new Finding
            {
                Id = "disk.health." + d.DeviceId, Severity = Severity.Critical, Area = "Storage", ScorePenalty = d.Health == "Unhealthy" ? 25 : 15,
                Title = L.F("{0} reports {1} health", d.FriendlyName, d.Health.ToLowerInvariant()),
                Detail = "The drive itself reports a problem. Back up important data now and plan a replacement.",
                Evidence = L.F("Health: {0}; status: {1}", d.Health, d.OperationalStatus), NavigateTo = "repair", ActionLabel = "Disk details",
            });
        foreach (var d in p.Disks.Where(d => d.MediaType == "HDD" && d.BusType != "USB"))
            f.Add(new Finding
            {
                Id = "disk.hdd." + d.DeviceId, Severity = Severity.Info, Area = "Storage",
                Title = L.F("{0} is a hard disk drive", d.FriendlyName),
                Detail = "If Windows or your games are installed on it, moving them to an SSD is the single biggest responsiveness upgrade for load times and stutter caused by asset streaming.",
            });
    }
}
