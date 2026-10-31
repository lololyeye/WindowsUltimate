using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Debloat;

public sealed record OptionalFeature(string Name, string Caption, bool Enabled, string? Advice, bool IsNotable);

/// <summary>
/// Read-only inventory of Windows optional features. Changes are made by the user in the built-in
/// "Turn Windows features on or off" dialog, which handles dependencies correctly.
/// </summary>
public static class OptionalFeatures
{
    private static readonly Dictionary<string, string> Advice = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SMB1Protocol"] = "SMB 1.0 is obsolete and insecure (exploited by WannaCry). Disable it unless you need a very old NAS.",
        ["SMB1Protocol-Client"] = "SMB 1.0 client — obsolete and insecure.",
        ["SMB1Protocol-Server"] = "SMB 1.0 server — obsolete and insecure.",
        ["MicrosoftWindowsPowerShellV2Root"] = "PowerShell 2.0 is deprecated and can be abused to bypass modern logging. Disable it if no legacy script needs it.",
        ["MicrosoftWindowsPowerShellV2"] = "PowerShell 2.0 engine — deprecated.",
        ["Microsoft-Hyper-V-All"] = "Hyper-V runs Windows on top of a hypervisor, which can cost a little performance in some games. Keep it if you use VMs, WSL 2 or Docker.",
        ["VirtualMachinePlatform"] = "Required by WSL 2 and Android apps. It enables the hypervisor; slight overhead in some games.",
        ["Microsoft-Windows-Subsystem-Linux"] = "Windows Subsystem for Linux.",
        ["Containers-DisposableClientVM"] = "Windows Sandbox (enables the hypervisor).",
        ["WorkFolders-Client"] = "Work Folders — only used in some corporate environments.",
        ["Printing-XPSServices-Features"] = "XPS document printing — rarely used today.",
        ["TelnetClient"] = "Unencrypted remote terminal client. Disable it if you don't need it.",
        ["TFTP"] = "Unauthenticated file transfer client. Disable it if you don't need it.",
        ["Internet-Explorer-Optional-amd64"] = "Internet Explorer 11 components (retired).",
        ["MediaPlayback"] = "Windows Media Player legacy and media features. Some apps rely on them.",
        ["DirectPlay"] = "Needed by some very old games (pre-2008).",
        ["NetFx3"] = ".NET Framework 3.5 — needed by older games and tools.",
    };

    public static Task<(List<OptionalFeature> Features, string? Error)> ListAsync() => Task.Run(() =>
    {
        try
        {
            var list = Wmi.Query("SELECT Name, Caption, InstallState FROM Win32_OptionalFeature", timeout: TimeSpan.FromSeconds(60))
                .Select(r => new OptionalFeature(
                    r.Str("Name"),
                    r.Str("Caption"),
                    r.Long("InstallState") == 1,
                    Advice.GetValueOrDefault(r.Str("Name")),
                    Advice.ContainsKey(r.Str("Name"))))
                .OrderByDescending(f => f.IsNotable && f.Enabled)
                .ThenByDescending(f => f.Enabled)
                .ThenBy(f => f.Caption)
                .ToList();
            return (list, (string?)null);
        }
        catch (Exception ex)
        {
            return ([], Elevation.IsElevated ? ex.Message : "Reading optional features may require administrator privileges: " + ex.Message);
        }
    });

    public static void OpenWindowsFeaturesDialog() => ProcessRunner.ShellOpen(ProcessRunner.System32("OptionalFeatures.exe"));
}
