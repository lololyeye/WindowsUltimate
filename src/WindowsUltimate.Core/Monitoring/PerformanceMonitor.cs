using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Monitoring;

public sealed record PerfSample(
    DateTime Time,
    double CpuPercent,
    double RamPercent,
    long RamUsedBytes,
    long RamTotalBytes,
    double? GpuPercent,
    long? VramUsedBytes,
    long? VramTotalBytes,
    double? CpuTempC,
    double? GpuTempC,
    double? DiskActivePercent,
    double DiskReadBps,
    double DiskWriteBps,
    double NetRxBps,
    double NetTxBps);

/// <summary>
/// Lightweight system sampler. Runs only while at least one subscriber is attached and uses cheap sources:
/// GetSystemTimes for CPU, GlobalMemoryStatusEx for RAM, PDH (English counter paths) for disk/GPU/thermal,
/// NVML for NVIDIA temperature when the driver provides it.
/// </summary>
public sealed partial class PerformanceMonitor : IDisposable
{
    private const string GpuEngine = @"\GPU Engine(*)\Utilization Percentage";
    private const string GpuDedicated = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string DiskIdle = @"\PhysicalDisk(_Total)\% Idle Time";
    private const string DiskRead = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
    private const string DiskWrite = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
    private const string Thermal = @"\Thermal Zone Information(*)\High Precision Temperature";

    private readonly object _gate = new();
    private readonly List<PerfSample> _history = [];
    private PdhQuery? _pdh;
    private Timer? _timer;
    private int _subscribers;
    private Native.FILETIME _idle, _kernel, _user;
    private (long Rx, long Tx, DateTime Time)? _net;
    private Nvml? _nvml;

    public long VramTotalBytes { get; set; }
    public TimeSpan Interval { get; private set; } = TimeSpan.FromSeconds(1);
    public int HistoryCapacity { get; set; } = 300;
    public event EventHandler<PerfSample>? Sampled;

    public IReadOnlyList<PerfSample> History
    {
        get { lock (_gate) return _history.ToList(); }
    }

    public PerfSample? Latest
    {
        get { lock (_gate) return _history.Count == 0 ? null : _history[^1]; }
    }

    /// <summary>Starts sampling if this is the first subscriber. Dispose the returned token to unsubscribe.</summary>
    public IDisposable Subscribe()
    {
        lock (_gate)
        {
            if (_subscribers++ == 0) Start();
        }
        return new Subscription(this);
    }

    public void SetInterval(TimeSpan interval)
    {
        Interval = interval < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : interval;
        _timer?.Change(Interval, Interval);
    }

    private void Start()
    {
        _pdh ??= CreateQuery();
        _nvml ??= Nvml.TryCreate();
        Native.GetSystemTimes(out _idle, out _kernel, out _user);
        _pdh.Collect();
        _timer = new Timer(_ => Tick(), null, Interval, Interval);
    }

    private void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private static PdhQuery CreateQuery()
    {
        var q = new PdhQuery();
        foreach (var path in new[] { GpuEngine, GpuDedicated, DiskIdle, DiskRead, DiskWrite, Thermal }) q.Add(path);
        return q;
    }

    private int _ticking;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return; // never overlap samples
        try
        {
            var sample = Take();
            lock (_gate)
            {
                _history.Add(sample);
                if (_history.Count > HistoryCapacity) _history.RemoveRange(0, _history.Count - HistoryCapacity);
            }
            Sampled?.Invoke(this, sample);
        }
        catch (Exception ex)
        {
            Log.Warn($"Monitor sample failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    [GeneratedRegex(@"luid_(0x[0-9a-fA-F]+_0x[0-9a-fA-F]+).*engtype_(\w+)")]
    private static partial Regex EngineInstance();

    private PerfSample Take()
    {
        // CPU from GetSystemTimes deltas.
        Native.GetSystemTimes(out var idle, out var kernel, out var user);
        var idleDelta = idle.Value - _idle.Value;
        var total = (kernel.Value - _kernel.Value) + (user.Value - _user.Value);
        var cpu = total == 0 ? 0 : Math.Clamp(100.0 * (total - idleDelta) / total, 0, 100);
        (_idle, _kernel, _user) = (idle, kernel, user);

        var mem = Native.MemoryStatus();
        var used = (long)(mem.ullTotalPhys - mem.ullAvailPhys);

        _pdh!.Collect();

        // GPU: like Task Manager, utilisation = busiest engine type (3D, Copy, VideoDecode…) of the busiest adapter.
        double? gpu = null;
        var engines = _pdh.Array(GpuEngine);
        if (engines.Count > 0)
        {
            gpu = engines
                .Select(e => (m: EngineInstance().Match(e.Instance), e.Value))
                .Where(x => x.m.Success)
                .GroupBy(x => (Adapter: x.m.Groups[1].Value, Engine: x.m.Groups[2].Value))
                .Select(g => g.Sum(x => x.Value))
                .DefaultIfEmpty(0)
                .Max();
            gpu = Math.Clamp(gpu.Value, 0, 100);
        }
        long? vram = null;
        var dedicated = _pdh.Array(GpuDedicated);
        if (dedicated.Count > 0) vram = (long)dedicated.Max(d => d.Value);

        double? gpuTemp = null;
        if (_nvml is not null && _nvml.Read() is { } nv)
        {
            gpuTemp = nv.TemperatureC;
            gpu ??= nv.UtilizationPercent;
            vram ??= nv.MemoryUsedBytes;
            if (VramTotalBytes == 0) VramTotalBytes = nv.MemoryTotalBytes;
        }

        // ACPI thermal zones are what Windows exposes without a kernel driver. Values are reported in tenths of Kelvin.
        double? cpuTemp = null;
        var zones = _pdh.Array(Thermal).Where(z => z.Value > 2000).ToList();
        if (zones.Count > 0) cpuTemp = Math.Round(zones.Max(z => z.Value) / 10.0 - 273.15, 1);

        var idlePct = _pdh.Value(DiskIdle);
        double? diskActive = idlePct is null ? null : Math.Clamp(100 - idlePct.Value, 0, 100);

        // Network throughput from interface byte counters.
        double rx = 0, tx = 0;
        long rxTotal = 0, txTotal = 0;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var s = n.GetIPStatistics();
                rxTotal += s.BytesReceived;
                txTotal += s.BytesSent;
            }
            catch { /* adapter vanished */ }
        }
        var now = DateTime.Now;
        if (_net is { } prev)
        {
            var secs = (now - prev.Time).TotalSeconds;
            if (secs > 0)
            {
                rx = Math.Max(0, (rxTotal - prev.Rx) / secs);
                tx = Math.Max(0, (txTotal - prev.Tx) / secs);
            }
        }
        _net = (rxTotal, txTotal, now);

        return new PerfSample(now, cpu, mem.dwMemoryLoad, used, (long)mem.ullTotalPhys, gpu, vram, VramTotalBytes > 0 ? VramTotalBytes : null,
            cpuTemp, gpuTemp, diskActive, _pdh.Value(DiskRead) ?? 0, _pdh.Value(DiskWrite) ?? 0, rx, tx);
    }

    public void Dispose()
    {
        Stop();
        _pdh?.Dispose();
        _nvml?.Dispose();
    }

    private sealed class Subscription(PerformanceMonitor owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            lock (owner._gate)
            {
                if (--owner._subscribers == 0) owner.Stop();
            }
        }
    }
}

/// <summary>NVIDIA Management Library, shipped with the NVIDIA driver (nvml.dll). Used read-only for temperature and usage.</summary>
internal sealed class Nvml : IDisposable
{
    private readonly nint _device;

    private Nvml(nint device) => _device = device;

    public static Nvml? TryCreate()
    {
        try
        {
            if (nvmlInit_v2() != 0) return null;
            if (nvmlDeviceGetHandleByIndex_v2(0, out var d) != 0) { nvmlShutdown(); return null; }
            return new Nvml(d);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    public (double TemperatureC, double UtilizationPercent, long MemoryUsedBytes, long MemoryTotalBytes)? Read()
    {
        if (nvmlDeviceGetTemperature(_device, 0, out var t) != 0) return null;
        nvmlDeviceGetUtilizationRates(_device, out var u);
        nvmlDeviceGetMemoryInfo(_device, out var m);
        return (t, u.gpu, (long)m.used, (long)m.total);
    }

    public void Dispose() => nvmlShutdown();

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint gpu, memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong total, free, used; }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(nint device, int sensor, out uint temp);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(nint device, out Utilization util);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(nint device, out Memory mem);
}
