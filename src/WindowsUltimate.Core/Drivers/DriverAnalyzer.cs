using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Drivers;

public sealed class DriverInfo
{
    public string DeviceName { get; init; } = "";
    public string DeviceClass { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Date { get; init; }
    public string Provider { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public bool IsSigned { get; init; }
    public string InfName { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string Flag { get; set; } = "";
    public string Note { get; set; } = "";

    public int AgeDays => Date is null ? 0 : (int)(DateTime.Now - Date.Value).TotalDays;
    public bool IsKeyClass => DeviceClass is "DISPLAY" or "NET" or "MEDIA" or "SCSIADAPTER" or "HDC" or "BLUETOOTH" or "SYSTEM" or "USB";
}

public sealed class ProblemDevice
{
    public string Name { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string Class { get; init; } = "";
    public int Code { get; init; }
    public string Meaning { get; init; } = "";
    public bool IsMissingDriver => Code is 28 or 1;
}

/// <summary>
/// Installed driver inventory and problem-device detection. Windows does not expose "latest available version"
/// information, so age is only a hint and the app links to official sources rather than installing anything itself.
/// </summary>
public static class DriverAnalyzer
{
    public static List<DriverInfo> Drivers()
    {
        var list = Wmi.TryQuery("SELECT DeviceName, DeviceClass, DriverVersion, DriverDate, DriverProviderName, Manufacturer, IsSigned, InfName, DeviceID FROM Win32_PnPSignedDriver", timeout: TimeSpan.FromSeconds(60))
            .Where(r => r.Str("DeviceName").Length > 0 && r.Str("DriverVersion").Length > 0)
            .Select(r => new DriverInfo
            {
                DeviceName = r.Str("DeviceName"),
                DeviceClass = r.Str("DeviceClass").ToUpperInvariant(),
                Version = r.Str("DriverVersion"),
                Date = r.Date("DriverDate"),
                Provider = r.Str("DriverProviderName"),
                Manufacturer = r.Str("Manufacturer"),
                IsSigned = r.Bool("IsSigned") ?? true,
                InfName = r.Str("InfName"),
                DeviceId = r.Str("DeviceID"),
            })
            .ToList();

        foreach (var d in list)
        {
            if (!d.IsSigned) { d.Flag = "Unsigned"; d.Note = "Driver is not digitally signed. Prefer the manufacturer's signed release."; }
            else if (d.DeviceClass == "DISPLAY" && d.AgeDays > 365 && !d.Provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            { d.Flag = "Old"; d.Note = "Graphics driver is more than a year old. New games often need recent drivers."; }
            else if (d.IsKeyClass && d.AgeDays > 365 * 4 && !d.Provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            { d.Flag = "Old"; d.Note = "Driver is several years old. Check the manufacturer's site if you have problems with this device."; }
        }

        // Most important classes first, then newest problems.
        return list.OrderByDescending(d => d.Flag.Length > 0).ThenByDescending(d => d.IsKeyClass).ThenBy(d => d.DeviceClass).ThenBy(d => d.DeviceName).ToList();
    }

    public static List<ProblemDevice> ProblemDevices() =>
        Wmi.TryQuery("SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0")
            .Select(r => (row: r, code: (int)r.Long("ConfigManagerErrorCode")))
            .Where(x => x.code is not 22 and not 45) // 22 = disabled by the user, 45 = not currently connected
            .Select(x => new ProblemDevice
            {
                Name = x.row.Str("Name").Length > 0 ? x.row.Str("Name") : x.row.Str("DeviceID"),
                DeviceId = x.row.Str("DeviceID"),
                Class = x.row.Str("PNPClass"),
                Code = x.code,
                Meaning = Meaning(x.code),
            })
            .ToList();

    public static string Meaning(int code) => code switch
    {
        1 => "Not configured correctly (driver missing or incomplete).",
        3 => "The driver may be corrupted, or the system is low on memory.",
        10 => "The device cannot start. Often a driver/firmware incompatibility.",
        12 => "Resource conflict: the device cannot find enough free resources.",
        14 => "The device needs a restart to work properly.",
        18 => "The drivers must be reinstalled.",
        19 => "Configuration information in the registry is incomplete or damaged.",
        21 => "Windows is removing this device.",
        24 => "The device is not present, not working, or missing drivers.",
        28 => "No driver is installed for this device.",
        31 => "Windows cannot load the driver for this device.",
        37 => "The driver returned a failure during initialization.",
        39 => "The driver is corrupted or missing.",
        43 => "Windows stopped the device because it reported problems (common with GPUs/USB).",
        48 => "The driver was blocked because it has known compatibility issues.",
        52 => "Windows cannot verify the driver's digital signature.",
        _ => $"Device problem code {code}.",
    };

    public static string OfficialDriverSource(HardwareVendor vendor) => vendor switch
    {
        HardwareVendor.Nvidia => "https://www.nvidia.com/Download/index.aspx",
        HardwareVendor.Amd => "https://www.amd.com/en/support/download/drivers.html",
        HardwareVendor.Intel => "https://www.intel.com/content/www/us/en/support/detect.html",
        _ => "ms-settings:windowsupdate-optionalupdates",
    };
}
