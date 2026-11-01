using Microsoft.Win32;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Hardware;

/// <summary>
/// Detects hardware and Windows configuration through WMI, the registry and Win32 display APIs.
/// Every probe is isolated: a failing probe adds a note instead of failing detection.
/// </summary>
public static class HardwareDetector
{
    private static readonly int[] PortableChassis = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];

    public static Task<SystemProfile> DetectAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var p = new SystemProfile();
        Probe(p, "Windows", () => p.Os = DetectOs());
        ct.ThrowIfCancellationRequested();
        Probe(p, "CPU", () => p.Cpu = DetectCpu());
        Probe(p, "GPU", () => p.Gpus = DetectGpus());
        ct.ThrowIfCancellationRequested();
        Probe(p, "Memory", () => p.Memory = DetectMemory());
        Probe(p, "Motherboard", () => p.Board = DetectBoard());
        ct.ThrowIfCancellationRequested();
        Probe(p, "Storage", () => p.Disks = DetectDisks());
        Probe(p, "Displays", () => p.Displays = DetectDisplays());
        Probe(p, "Audio", () => p.AudioDevices = Wmi.TryQuery("SELECT Name FROM Win32_SoundDevice").Select(r => r.Str("Name")).Where(n => n.Length > 0).Distinct().ToList());
        Probe(p, "Chassis", () =>
        {
            p.HasBattery = Wmi.TryQuery("SELECT DeviceID FROM Win32_Battery").Count > 0;
            var chassis = Wmi.TryQuery("SELECT ChassisTypes FROM Win32_SystemEnclosure")
                .SelectMany(r => r.TryGetValue("ChassisTypes", out var v) && v is ushort[] a ? a.Select(x => (int)x) : []);
            p.IsLaptop = p.HasBattery || chassis.Any(PortableChassis.Contains);
            var model = $"{p.Board.SystemManufacturer} {p.Board.SystemModel}";
            p.IsVirtualMachine = model.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || model.Contains("VMware", StringComparison.OrdinalIgnoreCase) || model.Contains("QEMU", StringComparison.OrdinalIgnoreCase);
        });
        Log.Info($"Hardware detected: {p.Cpu.Name} / {string.Join(", ", p.Gpus.Select(g => g.Name))} / {Format.Bytes(p.Memory.TotalBytes)} / build {p.Os.BuildString}");
        return p;
    }, ct);

    private static void Probe(SystemProfile p, string what, Action probe)
    {
        try { probe(); }
        catch (Exception ex)
        {
            p.DetectionNotes.Add($"{what}: {ex.Message}");
            Log.Warn($"{what} detection failed: {ex.Message}");
        }
    }

    public static HardwareVendor VendorFrom(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("nvidia") || t.Contains("geforce") || t.Contains("quadro")) return HardwareVendor.Nvidia;
        if (t.Contains("amd") || t.Contains("radeon") || t.Contains("advanced micro")) return HardwareVendor.Amd;
        if (t.Contains("intel")) return HardwareVendor.Intel;
        if (t.Contains("qualcomm") || t.Contains("snapdragon")) return HardwareVendor.Qualcomm;
        if (t.Contains("microsoft")) return HardwareVendor.Microsoft;
        return t.Length == 0 ? HardwareVendor.Unknown : HardwareVendor.Other;
    }

    private static OsInfo DetectOs()
    {
        var os = new OsInfo();
        var row = Wmi.Query("SELECT Caption, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem").FirstOrDefault();
        if (row is not null)
        {
            os.Caption = row.Str("Caption").Replace("Microsoft ", "");
            os.Build = int.TryParse(row.Str("BuildNumber"), out var b) ? b : Environment.OSVersion.Version.Build;
            os.Architecture = row.Str("OSArchitecture");
            os.InstallDate = row.Date("InstallDate");
            os.LastBoot = row.Date("LastBootUpTime");
        }
        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        os.DisplayVersion = cv?.GetValue("DisplayVersion") as string ?? "";
        os.Ubr = cv?.GetValue("UBR") is int ubr ? ubr : 0;
        os.Edition = cv?.GetValue("EditionID") as string ?? "";

        // Windows Insider enrolment: these keys exist only when the device is flighting.
        using var selfHost = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsSelfHost\Applicability");
        var branch = selfHost?.GetValue("BranchName") as string;
        using var ui = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsSelfHost\UI\Selection");
        var channel = ui?.GetValue("UIBranch") as string ?? ui?.GetValue("UIContentType") as string;
        os.IsInsider = !string.IsNullOrEmpty(branch) || !string.IsNullOrEmpty(channel) || (selfHost?.GetValue("EnablePreviewBuilds") is int e && e != 0);
        os.InsiderChannel = channel ?? branch;
        return os;
    }

    private static CpuInfo DetectCpu()
    {
        var rows = Wmi.Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation, VirtualizationFirmwareEnabled FROM Win32_Processor");
        var cpu = new CpuInfo();
        foreach (var r in rows)
        {
            cpu.Name = System.Text.RegularExpressions.Regex.Replace(r.Str("Name"), @"\s+", " ");
            cpu.Vendor = VendorFrom(r.Str("Manufacturer") + " " + cpu.Name);
            cpu.Cores += (int)r.Long("NumberOfCores");
            cpu.Threads += (int)r.Long("NumberOfLogicalProcessors");
            cpu.MaxClockMhz = (int)r.Long("MaxClockSpeed");
            cpu.Socket = r.Str("SocketDesignation");
            cpu.VirtualizationFirmwareEnabled = r.Bool("VirtualizationFirmwareEnabled") ?? false;
        }
        if (cpu.Threads == 0) cpu.Threads = Environment.ProcessorCount;
        return cpu;
    }

    private static List<GpuInfo> DetectGpus()
    {
        var vramByName = ReadVramFromDisplayClass();
        var list = new List<GpuInfo>();
        foreach (var r in Wmi.Query("SELECT Name, AdapterCompatibility, DriverVersion, DriverDate, AdapterRAM, PNPDeviceID FROM Win32_VideoController"))
        {
            var name = r.Str("Name");
            if (name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
            var g = new GpuInfo
            {
                Name = name,
                Vendor = VendorFrom(r.Str("AdapterCompatibility") + " " + name),
                DriverVersion = r.Str("DriverVersion"),
                DriverDate = r.Date("DriverDate"),
                PnpDeviceId = r.Str("PNPDeviceID"),
            };
            // Win32_VideoController.AdapterRAM is a 32-bit field capped at 4 GB; the display class key has the real size.
            g.VramBytes = vramByName.TryGetValue(name, out var v) ? v : (long)(uint)r.Long("AdapterRAM");
            g.IsIntegrated = IsIntegrated(g);
            list.Add(g);
        }
        return list;
    }

    private static bool IsIntegrated(GpuInfo g)
    {
        var n = g.Name.ToLowerInvariant();
        // Discrete Arc cards carry a model number (A770, B580); "Intel Arc Graphics" alone is the Meteor Lake iGPU.
        if (g.Vendor == HardwareVendor.Intel) return !System.Text.RegularExpressions.Regex.IsMatch(n, @"arc(\(tm\))?\s+[ab]\d{3}");
        if (g.Vendor == HardwareVendor.Amd)
            return !(n.Contains(" rx ") || n.Contains("rx ") || n.Contains("pro w") || n.Contains("vega 56") || n.Contains("vega 64") || n.Contains("radeon vii"));
        if (g.Vendor == HardwareVendor.Qualcomm) return true;
        return g.Vendor != HardwareVendor.Nvidia && g.VramBytes < 1L << 30;
    }

    private static Dictionary<string, long> ReadVramFromDisplayClass()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return result;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            try
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string desc) continue;
                var size = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0),
                    byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
                    int i => (uint)i,
                    _ => 0L,
                };
                if (size > 0) result[desc] = size;
            }
            catch { /* access denied on some subkeys */ }
        }
        return result;
    }

    private static MemoryInfo DetectMemory()
    {
        var mem = new MemoryInfo { TotalBytes = (long)Native.MemoryStatus().ullTotalPhys };
        foreach (var r in Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            mem.Modules.Add(new MemoryModule
            {
                CapacityBytes = r.Long("Capacity"),
                RatedMhz = (int)r.Long("Speed"),
                ConfiguredMhz = (int)r.Long("ConfiguredClockSpeed"),
                Manufacturer = r.Str("Manufacturer"),
                PartNumber = r.Str("PartNumber"),
                Slot = r.Str("DeviceLocator"),
                Type = r.Long("SMBIOSMemoryType") switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", 30 => "LPDDR4", 35 => "LPDDR5", _ => "" },
            });
        }
        mem.SlotsTotal = (int)Wmi.TryQuery("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(r => r.Long("MemoryDevices"));
        return mem;
    }

    private static BoardInfo DetectBoard()
    {
        var b = new BoardInfo();
        if (Wmi.TryQuery("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault() is { } bb)
        {
            b.Manufacturer = bb.Str("Manufacturer");
            b.Product = bb.Str("Product");
        }
        if (Wmi.TryQuery("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault() is { } bios)
        {
            b.BiosVendor = bios.Str("Manufacturer");
            b.BiosVersion = bios.Str("SMBIOSBIOSVersion");
            b.BiosDate = bios.Date("ReleaseDate");
        }
        if (Wmi.TryQuery("SELECT Manufacturer, Model FROM Win32_ComputerSystem").FirstOrDefault() is { } cs)
        {
            b.SystemManufacturer = cs.Str("Manufacturer");
            b.SystemModel = cs.Str("Model");
        }
        return b;
    }

    public static List<DiskInfo> DetectDisks()
    {
        var disks = new List<DiskInfo>();
        foreach (var r in Wmi.Query("SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus, OperationalStatus, SerialNumber, FirmwareVersion FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
        {
            disks.Add(new DiskInfo
            {
                DeviceId = r.Str("DeviceId"),
                FriendlyName = r.Str("FriendlyName"),
                MediaType = r.Long("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" },
                BusType = r.Long("BusType") switch { 7 => "USB", 11 => "SATA", 17 => "NVMe", 8 => "RAID", 10 => "SAS", 3 => "ATA", 12 => "SD", 15 => "Virtual", _ => "Other" },
                SizeBytes = r.Long("Size"),
                Health = r.Long("HealthStatus") switch { 0 => L.T("Healthy"), 1 => L.T("Warning"), 2 => L.T("Unhealthy"), _ => L.T("Unknown") },
                OperationalStatus = r.TryGetValue("OperationalStatus", out var os) && os is ushort[] codes
                    ? string.Join(", ", codes.Select(c => c switch { 2 => L.T("OK"), 3 => L.T("Degraded"), 5 => L.T("Predictive failure"), 6 => L.T("Error"), 10 => L.T("Stopped"), 11 => L.T("In service"), 13 => L.T("Lost communication"), _ => c.ToString() }))
                    : "",
                SerialNumber = r.Str("SerialNumber"),
                FirmwareVersion = r.Str("FirmwareVersion"),
            });
        }
        return disks;
    }

    public static List<DisplayInfo> DetectDisplays()
    {
        var list = new List<DisplayInfo>();
        var adapter = new Native.DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
        for (uint i = 0; Native.EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            if ((adapter.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                var current = NewDevMode();
                if (Native.EnumDisplaySettings(adapter.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref current))
                {
                    var max = current.dmDisplayFrequency;
                    var mode = NewDevMode();
                    for (var m = 0; Native.EnumDisplaySettings(adapter.DeviceName, m, ref mode); m++)
                    {
                        if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight && mode.dmBitsPerPel == current.dmBitsPerPel)
                            max = Math.Max(max, mode.dmDisplayFrequency);
                    }

                    var monitor = new Native.DISPLAY_DEVICE { cb = adapter.cb };
                    var monitorName = Native.EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0) ? monitor.DeviceString : "";
                    list.Add(new DisplayInfo
                    {
                        DeviceName = adapter.DeviceName,
                        AdapterName = adapter.DeviceString,
                        MonitorName = monitorName,
                        Width = current.dmPelsWidth,
                        Height = current.dmPelsHeight,
                        CurrentHz = current.dmDisplayFrequency,
                        MaxHzAtCurrentResolution = max,
                        IsPrimary = (adapter.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                    });
                }
            }
            adapter = new Native.DISPLAY_DEVICE { cb = adapter.cb };
        }
        return list;
    }

    private static Native.DEVMODE NewDevMode() => new() { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<Native.DEVMODE>() };
}
