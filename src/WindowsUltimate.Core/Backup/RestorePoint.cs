using System.Management;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Backup;

/// <summary>
/// Creates a Windows System Restore point. This is an extra safety net only — the journal is the primary
/// rollback mechanism because restore points can be disabled, throttled (one per 24 h by default) or purged.
/// </summary>
public static class RestorePoint
{
    public static Task<(bool Success, string Message)> CreateAsync(string description) => Task.Run(() =>
    {
        if (!Elevation.IsElevated) return (false, L.T("Creating a restore point requires administrator privileges."));
        try
        {
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var input = cls.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description.Length > 60 ? description[..60] : description;
            input["RestorePointType"] = 12; // MODIFY_SETTINGS
            input["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
            using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
            var code = Convert.ToUInt32(output?["ReturnValue"] ?? 1u);
            Log.Audit($"CreateRestorePoint('{description}') returned {code}");
            return code switch
            {
                0 => (true, L.T("Restore point requested. Note: Windows silently skips it if another one was created in the last 24 hours.")),
                1058 => (false, L.T("System Protection is disabled for the system drive, so Windows cannot create restore points.")),
                _ => (false, L.F("Windows returned error {0} while creating the restore point.", code)),
            };
        }
        catch (Exception ex)
        {
            Log.Error("Restore point creation failed", ex);
            return (false, L.T("Restore point could not be created: ") + ex.Message);
        }
    });
}
