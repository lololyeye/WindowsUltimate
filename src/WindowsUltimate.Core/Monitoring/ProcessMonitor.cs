using System.Diagnostics;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Monitoring;

public enum ProcessKind { Windows, ThirdParty, Protected }

public sealed class ProcessSample
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public string Company { get; init; } = "";
    public ProcessKind Kind { get; init; }
    public double CpuPercent { get; init; }
    public long WorkingSet { get; init; }
    public long PrivateBytes { get; init; }
    public double IoBytesPerSecond { get; init; }

    public string KindLabel => Kind switch
    {
        ProcessKind.Windows => "Windows",
        ProcessKind.Protected => "Windows (protected)",
        _ => "Third-party",
    };
}

/// <summary>
/// Per-process CPU, memory and I/O sampling. CPU and I/O are deltas between calls, so the first sample reports zero.
/// Never terminates processes on its own.
/// </summary>
public sealed class ProcessMonitor
{
    private static readonly HashSet<string> CoreWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "services", "lsass", "winlogon", "svchost", "fontdrvhost",
        "dwm", "Memory Compression", "Secure System", "LsaIso", "MsMpEng", "SecurityHealthService", "spoolsv", "audiodg", "conhost",
        "explorer", "sihost", "taskhostw", "RuntimeBroker", "SearchHost", "StartMenuExperienceHost", "ctfmon", "dllhost",
    };

    private readonly Dictionary<int, (ulong Cpu, ulong Io, long Ticks)> _previous = new();
    private readonly Dictionary<string, string> _companyCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public List<ProcessSample> Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var cpus = Environment.ProcessorCount;
        var result = new List<ProcessSample>();
        var seen = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var pid = p.Id;
                seen.Add(pid);
                string? path = null;
                ulong cpu = 0, io = 0;
                var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (handle != 0)
                {
                    try
                    {
                        path = Native.ProcessImagePath(handle);
                        if (Native.GetProcessTimes(handle, out _, out _, out var k, out var u)) cpu = k.Value + u.Value;
                        if (Native.GetProcessIoCounters(handle, out var c)) io = c.ReadTransferCount + c.WriteTransferCount + c.OtherTransferCount;
                    }
                    finally
                    {
                        Native.CloseHandle(handle);
                    }
                }

                double cpuPct = 0, ioRate = 0;
                if (_previous.TryGetValue(pid, out var prev) && cpu >= prev.Cpu)
                {
                    var seconds = (now - prev.Ticks) / (double)Stopwatch.Frequency;
                    if (seconds > 0)
                    {
                        cpuPct = Math.Clamp((cpu - prev.Cpu) / 1e7 / seconds / cpus * 100, 0, 100);
                        ioRate = io >= prev.Io ? (io - prev.Io) / seconds : 0;
                    }
                }
                _previous[pid] = (cpu, io, now);

                long ws = 0, priv = 0;
                try { ws = p.WorkingSet64; priv = p.PrivateMemorySize64; } catch { /* exited */ }

                var name = p.ProcessName;
                result.Add(new ProcessSample
                {
                    Pid = pid,
                    Name = name,
                    Path = path,
                    Company = path is null ? "" : Company(path),
                    Kind = Classify(pid, name, path),
                    CpuPercent = cpuPct,
                    WorkingSet = ws,
                    PrivateBytes = priv,
                    IoBytesPerSecond = ioRate,
                });
            }
        }

        foreach (var stale in _previous.Keys.Where(k => !seen.Contains(k)).ToList()) _previous.Remove(stale);
        return result;
    }

    private static ProcessKind Classify(int pid, string name, string? path)
    {
        // A process we cannot even query is treated as protected: it is never offered for termination.
        if (pid <= 4 || path is null) return ProcessKind.Protected;
        if (path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase) || CoreWindows.Contains(name) && path.Contains(@"\Microsoft\Windows Defender\", StringComparison.OrdinalIgnoreCase))
            return ProcessKind.Windows;
        return ProcessKind.ThirdParty;
    }

    private string Company(string path)
    {
        if (_companyCache.TryGetValue(path, out var c)) return c;
        try { c = FileVersionInfo.GetVersionInfo(path).CompanyName ?? ""; }
        catch { c = ""; }
        _companyCache[path] = c;
        return c;
    }

    /// <summary>Ends a third-party process after the user confirmed it in the UI. Windows processes are refused.</summary>
    public static void Terminate(ProcessSample sample)
    {
        if (sample.Kind != ProcessKind.ThirdParty)
            throw new InvalidOperationException("Windows system processes cannot be ended from Windows Ultimate.");
        using var p = Process.GetProcessById(sample.Pid);
        Log.Audit($"User ended process {sample.Name} (pid {sample.Pid}, {sample.Path})");
        p.Kill();
    }

    /// <summary>Known overlays/recorders that hook into games. Detected by running process name.</summary>
    public static readonly (string Process, string Name, string Note)[] KnownOverlays =
    [
        ("GameBar", "Xbox Game Bar", "Only active when opened (Win+G)."),
        ("gameoverlayui", "Steam overlay", "Can be disabled per game in Steam › Properties › General."),
        ("gameoverlayui64", "Steam overlay", "Can be disabled per game in Steam › Properties › General."),
        ("Discord", "Discord", "Its in-game overlay can be turned off in Discord › Settings › Game Overlay."),
        ("NVIDIA Overlay", "NVIDIA app / GeForce Experience overlay", "Turn off 'In-game overlay' if you don't use instant replay or filters."),
        ("RTSS", "RivaTuner Statistics Server", "Frame limiter/OSD. Known to conflict with some anti-cheats and with HAGS on older versions."),
        ("MSIAfterburner", "MSI Afterburner", "Fine for monitoring; GPU overclocks applied by it can cause driver resets."),
        ("obs64", "OBS Studio", "Game capture hooks add a small overhead while recording/streaming."),
        ("Medal", "Medal.tv", "Continuous clip recording uses the GPU encoder."),
        ("Overwolf", "Overwolf", "Overlay platform injected into supported games."),
        ("EpicWebHelper", "Epic Games overlay helper", "Usually light."),
        ("RadeonSoftware", "AMD Software overlay", "Disable the in-game overlay if you don't use it."),
    ];
}
