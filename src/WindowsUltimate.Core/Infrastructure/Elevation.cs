using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace WindowsUltimate.Core.Infrastructure;

public static class Elevation
{
    public static bool IsElevated { get; } = Check();

    private static bool Check()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Relaunches the current executable through UAC. Returns false if the user declined the prompt.</summary>
    public static bool RestartElevated(string? arguments = null)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments ?? string.Empty) { UseShellExecute = true, Verb = "runas" });
            Log.Info("Relaunched elevated at user request.");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // ERROR_CANCELLED: the user declined UAC.
        }
    }
}

public sealed class AdminRequiredException(string operation)
    : InvalidOperationException($"'{operation}' requires administrator privileges. Restart Windows Ultimate as administrator to use it.");
