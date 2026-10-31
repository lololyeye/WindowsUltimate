using System.ServiceProcess;
using System.Text;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Repair;

public sealed class RepairTool
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string WhatHappens { get; init; }
    public required string FileName { get; init; }
    public required string Arguments { get; init; }
    public string Duration { get; init; } = "";
    public bool RequiresAdmin { get; init; } = true;
    public bool ModifiesSystem { get; init; }
    public bool NeedsInternet { get; init; }
    public Encoding? OutputEncoding { get; init; }
    public string CommandLine => $"{Path.GetFileName(FileName)} {Arguments}";
}

/// <summary>
/// Microsoft's own repair tools, run with explicit consent and streamed output.
/// No destructive disk operation (chkdsk /f, /r, format) is ever offered.
/// </summary>
public static class RepairTools
{
    public static IReadOnlyList<RepairTool> All { get; } =
    [
        new()
        {
            Id = "dism.check", Name = "Check component store (quick)",
            Description = "Reports whether the Windows component store has already been flagged as corrupted.",
            WhatHappens = "Read-only check. Takes a few seconds and changes nothing.",
            FileName = ProcessRunner.System32("dism.exe"), Arguments = "/Online /Cleanup-Image /CheckHealth", Duration = "~10 s",
        },
        new()
        {
            Id = "dism.scan", Name = "Scan component store",
            Description = "Scans the component store (the source Windows uses to repair itself) for corruption.",
            WhatHappens = "Read-only scan. Changes nothing.",
            FileName = ProcessRunner.System32("dism.exe"), Arguments = "/Online /Cleanup-Image /ScanHealth", Duration = "2–10 min",
        },
        new()
        {
            Id = "dism.restore", Name = "Repair component store",
            Description = "Repairs corruption in the component store by downloading clean files from Windows Update. Run this before SFC if SFC cannot fix files.",
            WhatHappens = "Replaces corrupted Windows component files with official versions from Windows Update. Your apps, files and settings are not touched. Cannot be undone, but only restores Microsoft's original files.",
            FileName = ProcessRunner.System32("dism.exe"), Arguments = "/Online /Cleanup-Image /RestoreHealth", Duration = "5–30 min",
            ModifiesSystem = true, NeedsInternet = true,
        },
        new()
        {
            Id = "sfc.verify", Name = "Verify system files",
            Description = "Checks the integrity of protected Windows system files without repairing them.",
            WhatHappens = "Read-only verification. Changes nothing.",
            FileName = ProcessRunner.System32("sfc.exe"), Arguments = "/verifyonly", Duration = "5–15 min", OutputEncoding = Encoding.Unicode,
        },
        new()
        {
            Id = "sfc.scan", Name = "Repair system files (SFC)",
            Description = "Scans protected system files and replaces corrupted ones with the correct Microsoft versions.",
            WhatHappens = "Corrupted system files are replaced from the component store. Your apps, files and settings are not touched. Cannot be undone, but only restores Microsoft's original files. A restart may be requested.",
            FileName = ProcessRunner.System32("sfc.exe"), Arguments = "/scannow", Duration = "5–20 min", OutputEncoding = Encoding.Unicode,
            ModifiesSystem = true,
        },
        new()
        {
            Id = "chkdsk.scan", Name = "Scan system drive for file system errors",
            Description = "Online, read-only scan of the NTFS file system on the system drive while Windows keeps running.",
            WhatHappens = "Read-only. If problems are found, chkdsk reports them and Windows Ultimate tells you how to schedule a repair yourself — it never runs a repair automatically.",
            FileName = ProcessRunner.System32("chkdsk.exe"), Arguments = $"{Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\')} /scan", Duration = "1–10 min",
        },
        new()
        {
            Id = "dism.cleanup", Name = "Clean up superseded update components",
            Description = "Removes older versions of components that have been replaced by updates (WinSxS cleanup).",
            WhatHappens = "Frees disk space in C:\\Windows\\WinSxS. Superseded component versions are deleted permanently: you will not be able to uninstall updates that were installed before this cleanup. /ResetBase is never used.",
            FileName = ProcessRunner.System32("dism.exe"), Arguments = "/Online /Cleanup-Image /StartComponentCleanup", Duration = "5–30 min",
            ModifiesSystem = true,
        },
    ];

    public static async Task<ProcessResult> RunAsync(RepairTool tool, Action<string> onLine, CancellationToken ct)
    {
        if (tool.RequiresAdmin && !Elevation.IsElevated) throw new AdminRequiredException(tool.Name);
        Log.Audit($"Repair tool started: {tool.CommandLine}");
        var r = await ProcessRunner.RunAsync(tool.FileName, tool.Arguments, ct, onLine, tool.OutputEncoding).ConfigureAwait(false);
        Log.Audit($"Repair tool finished: {tool.CommandLine} exit {r.ExitCode}");
        return r;
    }
}

public sealed record ServiceState(string Name, string DisplayName, string StartType, string Status, string ExpectedStartType, bool IsHealthy);

public sealed record DiskReliability(string DeviceId, int? TemperatureC, int? WearPercent, long? ReadErrors, long? WriteErrors, long? PowerOnHours);

public static class UpdateHealth
{
    private static readonly (string Name, string Expected)[] Services =
    [
        ("wuauserv", "Manual"), ("bits", "Manual"), ("cryptsvc", "Automatic"), ("TrustedInstaller", "Manual"), ("UsoSvc", "Automatic"),
    ];

    public static List<ServiceState> ServiceStates()
    {
        var list = new List<ServiceState>();
        foreach (var (name, expected) in Services)
        {
            try
            {
                using var sc = new ServiceController(name);
                var start = sc.StartType.ToString();
                var healthy = sc.StartType != ServiceStartMode.Disabled;
                list.Add(new ServiceState(name, sc.DisplayName, start, sc.Status.ToString(), expected, healthy));
            }
            catch (Exception ex)
            {
                list.Add(new ServiceState(name, name, "Missing", ex.Message, expected, false));
            }
        }
        return list;
    }

    /// <summary>Asks the Windows Update agent for applicable updates (COM API). May take a while and needs internet.</summary>
    public static Task<(List<string> Titles, string? Error)> PendingUpdatesAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var t = Type.GetTypeFromProgID("Microsoft.Update.Session")!;
            dynamic session = Activator.CreateInstance(t)!;
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
            var titles = new List<string>();
            foreach (dynamic u in result.Updates) titles.Add((string)u.Title);
            return (titles, (string?)null);
        }
        catch (Exception ex)
        {
            return ([], ex.Message);
        }
    }, ct);

    /// <summary>SMART-like counters exposed by Windows Storage. Requires administrator rights.</summary>
    public static List<DiskReliability> Reliability()
    {
        static long? N(Dictionary<string, object?> r, string k) => r.TryGetValue(k, out var v) && v is not null ? Convert.ToInt64(v) : null;
        return Wmi.TryQuery("SELECT DeviceId, Temperature, Wear, ReadErrorsTotal, WriteErrorsTotal, PowerOnHours FROM MSFT_StorageReliabilityCounter", @"root\Microsoft\Windows\Storage")
            .Select(r => new DiskReliability(r.Str("DeviceId"), (int?)N(r, "Temperature") is > 0 and var t ? t : null, (int?)N(r, "Wear"), N(r, "ReadErrorsTotal"), N(r, "WriteErrorsTotal"), N(r, "PowerOnHours")))
            .ToList();
    }
}
