using Microsoft.Win32;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.Core.Diagnostics;

public sealed record AntivirusStatus(string Name, bool Enabled, bool UpToDate);

public sealed record SecurityStatus(List<AntivirusStatus> Antivirus, bool? VbsRunning, bool? MemoryIntegrityRunning, bool? FirewallEnabled)
{
    public bool AnyAntivirusEnabled => Antivirus.Any(a => a.Enabled);
}

public sealed record PendingRebootStatus(bool ComponentServicing, bool WindowsUpdate, bool FileRenames)
{
    public bool Required => ComponentServicing || WindowsUpdate;
}

/// <summary>Point-in-time checks of Windows configuration. All read-only.</summary>
public static class SystemChecks
{
    public static SecurityStatus Security()
    {
        var av = Wmi.TryQuery("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2")
            .Select(r =>
            {
                var state = r.Long("productState");
                return new AntivirusStatus(r.Str("displayName"), (state & 0xF000) == 0x1000, (state & 0xF0) == 0);
            })
            .ToList();

        bool? vbs = null, hvci = null;
        if (Wmi.TryQuery("SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard").FirstOrDefault() is { } dg)
        {
            vbs = dg.Long("VirtualizationBasedSecurityStatus") == 2;
            hvci = dg.TryGetValue("SecurityServicesRunning", out var running) && running is uint[] services && services.Contains(2u);
        }

        bool? firewall = null;
        var profiles = Wmi.TryQuery("SELECT Name, Enabled FROM MSFT_NetFirewallProfile", @"root\StandardCimv2");
        if (profiles.Count > 0) firewall = profiles.All(p => p.Long("Enabled") == 1);

        return new SecurityStatus(av, vbs, hvci, firewall);
    }

    public static PendingRebootStatus PendingReboot()
    {
        using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        var renames = sm?.GetValue("PendingFileRenameOperations") is string[] ops && ops.Any(o => o.Length > 0);
        return new PendingRebootStatus(cbs is not null, wu is not null, renames);
    }

    public static (double CommitPercent, double MemoryLoadPercent, long AvailableBytes, int Processes, int Threads, int Handles) Memory()
    {
        var m = Native.MemoryStatus();
        var p = Native.PerformanceInfo();
        var commit = p.CommitLimit == 0 ? 0 : 100.0 * p.CommitTotal / p.CommitLimit;
        return (commit, m.dwMemoryLoad, (long)m.ullAvailPhys, (int)p.ProcessCount, (int)p.ThreadCount, (int)p.HandleCount);
    }

    /// <summary>Samples total CPU usage over a short window with GetSystemTimes (cheap, no counters needed).</summary>
    public static async Task<double> SampleCpuAsync(TimeSpan window, CancellationToken ct = default)
    {
        Native.GetSystemTimes(out var i1, out var k1, out var u1);
        await Task.Delay(window, ct).ConfigureAwait(false);
        Native.GetSystemTimes(out var i2, out var k2, out var u2);
        var idle = i2.Value - i1.Value;
        var total = (k2.Value - k1.Value) + (u2.Value - u1.Value); // kernel time includes idle
        return total == 0 ? 0 : Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }

    public static (string Name, Guid Id)? PowerPlan()
    {
        try { return PowerPlanAction.ActivePlan() is { } a ? (a.Name, a.Id) : null; }
        catch { return null; }
    }

    public static (bool Disabled, string Detail) FullscreenOptimizations()
    {
        var mode = Reg.ReadInt(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode");
        return mode == 2 ? (true, "Fullscreen optimizations are disabled globally (GameDVR_FSEBehaviorMode = 2).") : (false, "Fullscreen optimizations use the Windows default.");
    }

    public static bool? GameModeEnabled() => Reg.ReadInt(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") switch
    {
        0 => false,
        _ => true, // missing value means the Windows default (on)
    };

    public static bool? HagsEnabled() => Reg.ReadInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") switch
    {
        2 => true,
        1 => false,
        _ => null,
    };

    public static bool BackgroundRecording() =>
        Reg.ReadInt(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled") == 1;

    public static Dictionary<string, string> DirectXGlobalSettings() =>
        RegistryTokenAction.Tokens(Reg.ReadString(RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings"));

    public static int? ServiceStart(string service) =>
        Reg.ReadInt(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{service}", "Start");
}
