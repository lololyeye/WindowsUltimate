namespace WindowsUltimate.Core.Hardware;

public enum HardwareVendor { Unknown, Intel, Amd, Nvidia, Microsoft, Qualcomm, Other }

public sealed class OsInfo
{
    public string Caption { get; set; } = "Windows";
    public string DisplayVersion { get; set; } = "";
    public int Build { get; set; }
    public int Ubr { get; set; }
    public string Edition { get; set; } = "";
    public string Architecture { get; set; } = "";
    public DateTime? InstallDate { get; set; }
    public DateTime? LastBoot { get; set; }
    public bool IsInsider { get; set; }
    public string? InsiderChannel { get; set; }
    public bool IsHomeEdition => Edition.Contains("Core", StringComparison.OrdinalIgnoreCase) || Caption.Contains("Home", StringComparison.OrdinalIgnoreCase) || Caption.Contains("Famille", StringComparison.OrdinalIgnoreCase);
    public bool IsWindows11 => Build >= 22000;
    public string BuildString => Ubr > 0 ? $"{Build}.{Ubr}" : Build.ToString();
    public TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);
}

public sealed class CpuInfo
{
    public string Name { get; set; } = "Unknown CPU";
    public HardwareVendor Vendor { get; set; }
    public int Cores { get; set; }
    public int Threads { get; set; }
    public int MaxClockMhz { get; set; }
    public string Socket { get; set; } = "";
    public bool VirtualizationFirmwareEnabled { get; set; }
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public HardwareVendor Vendor { get; set; }
    public string DriverVersion { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public long VramBytes { get; set; }
    public bool IsIntegrated { get; set; }
    public string PnpDeviceId { get; set; } = "";

    /// <summary>NVIDIA driver version in the form users recognise (e.g. 32.0.15.6094 -> 560.94).</summary>
    public string FriendlyDriverVersion
    {
        get
        {
            if (Vendor != HardwareVendor.Nvidia) return DriverVersion;
            var digits = DriverVersion.Replace(".", "");
            return digits.Length >= 5 ? $"{digits[^5..^2]}.{digits[^2..]}" : DriverVersion;
        }
    }
}

public sealed class MemoryModule
{
    public long CapacityBytes { get; set; }
    public int RatedMhz { get; set; }
    public int ConfiguredMhz { get; set; }
    public string Manufacturer { get; set; } = "";
    public string PartNumber { get; set; } = "";
    public string Slot { get; set; } = "";
    public string Type { get; set; } = "";
}

public sealed class MemoryInfo
{
    public long TotalBytes { get; set; }
    public List<MemoryModule> Modules { get; set; } = [];
    public int SlotsTotal { get; set; }
    public string Type => Modules.Select(m => m.Type).FirstOrDefault(t => t.Length > 0) ?? "";
    public int ConfiguredMhz => Modules.Count == 0 ? 0 : Modules.Min(m => m.ConfiguredMhz);
}

public sealed class BoardInfo
{
    public string Manufacturer { get; set; } = "";
    public string Product { get; set; } = "";
    public string BiosVendor { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public DateTime? BiosDate { get; set; }
    public string SystemManufacturer { get; set; } = "";
    public string SystemModel { get; set; } = "";
}

public sealed class DiskInfo
{
    public string FriendlyName { get; set; } = "";
    public string MediaType { get; set; } = "Unknown"; // SSD / HDD / SCM / Unknown
    public string BusType { get; set; } = "";          // NVMe / SATA / USB / ...
    public long SizeBytes { get; set; }
    public string Health { get; set; } = "Unknown";    // Healthy / Warning / Unhealthy
    public string OperationalStatus { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string FirmwareVersion { get; set; } = "";
}

public sealed class DisplayInfo
{
    public string DeviceName { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string MonitorName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int CurrentHz { get; set; }
    public int MaxHzAtCurrentResolution { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>A common, real problem: a 144 Hz monitor left at 60 Hz. 1 Hz of slack covers 143.98 vs 144 rounding.</summary>
    public bool BelowMaxRefresh => MaxHzAtCurrentResolution > CurrentHz + 1;
}

public sealed class SystemProfile
{
    public DateTime DetectedAt { get; set; } = DateTime.Now;
    public string ComputerName { get; set; } = Environment.MachineName;
    public OsInfo Os { get; set; } = new();
    public CpuInfo Cpu { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = [];
    public MemoryInfo Memory { get; set; } = new();
    public BoardInfo Board { get; set; } = new();
    public List<DiskInfo> Disks { get; set; } = [];
    public List<DisplayInfo> Displays { get; set; } = [];
    public List<string> AudioDevices { get; set; } = [];
    public bool HasBattery { get; set; }
    public bool IsLaptop { get; set; }
    public bool IsVirtualMachine { get; set; }
    public List<string> DetectionNotes { get; set; } = [];

    public GpuInfo? PrimaryGpu => Gpus.OrderBy(g => g.IsIntegrated).ThenByDescending(g => g.VramBytes).FirstOrDefault();
    public bool HasDiscreteGpu => Gpus.Any(g => !g.IsIntegrated);
    public bool IsLowEnd => Memory.TotalBytes < 8L * 1024 * 1024 * 1024 || (Cpu.Threads > 0 && Cpu.Threads <= 4) || !HasDiscreteGpu;
}
