using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Network;

public sealed class AdapterInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Type { get; init; } = "";
    public bool IsWireless { get; init; }
    public long SpeedBps { get; init; }
    public string Mac { get; init; } = "";
    public List<string> IPv4 { get; init; } = [];
    public List<string> Gateways { get; init; } = [];
    public List<string> DnsServers { get; init; } = [];
    public bool DhcpEnabled { get; init; }
    public long ErrorsIn { get; init; }
    public long ErrorsOut { get; init; }
    public long DiscardsIn { get; init; }
    public string DriverVersion { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public string DriverProvider { get; set; } = "";

    public string SpeedText => SpeedBps >= 1_000_000_000 ? $"{SpeedBps / 1e9:0.#} Gbps" : $"{SpeedBps / 1e6:0} Mbps";
}

public sealed class PingStats
{
    public string Target { get; init; } = "";
    public string Label { get; init; } = "";
    public List<double?> Samples { get; } = [];
    public int Sent => Samples.Count;
    public int Lost => Samples.Count(s => s is null);
    public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;
    private IEnumerable<double> Ok => Samples.OfType<double>();
    public double? Min => Ok.Any() ? Ok.Min() : null;
    public double? Max => Ok.Any() ? Ok.Max() : null;
    public double? Average => Ok.Any() ? Ok.Average() : null;

    /// <summary>Mean absolute difference between consecutive successful replies (RFC 3550-style jitter).</summary>
    public double? Jitter
    {
        get
        {
            var ok = Ok.ToList();
            return ok.Count < 2 ? null : ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        }
    }
}

public sealed record DnsResult(string Name, string Server, double? MedianMs, int Failures, bool IsCurrent);

/// <summary>Network adapter inventory and active tests. Nothing here changes the network configuration.</summary>
public static class NetworkDiagnostics
{
    public static List<AdapterInfo> Adapters()
    {
        var drivers = Wmi.TryQuery("SELECT DeviceName, DriverVersion, DriverDate, DriverProviderName FROM Win32_PnPSignedDriver WHERE DeviceClass = 'NET'");
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n =>
            {
                var ip = n.GetIPProperties();
                IPv4InterfaceStatistics? stats = null;
                try { stats = n.GetIPv4Statistics(); } catch { /* not supported */ }
                var info = new AdapterInfo
                {
                    Name = n.Name,
                    Description = n.Description,
                    Type = n.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                        _ => n.NetworkInterfaceType.ToString(),
                    },
                    IsWireless = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    SpeedBps = n.Speed,
                    Mac = string.Join(":", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))),
                    IPv4 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList(),
                    Gateways = ip.GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList(),
                    DnsServers = ip.DnsAddresses.Select(d => d.ToString()).ToList(),
                    DhcpEnabled = TryGet(() => ip.GetIPv4Properties()?.IsDhcpEnabled ?? false),
                    ErrorsIn = stats?.IncomingPacketsWithErrors ?? 0,
                    ErrorsOut = stats?.OutgoingPacketsWithErrors ?? 0,
                    DiscardsIn = stats?.IncomingPacketsDiscarded ?? 0,
                };
                var drv = drivers.FirstOrDefault(d => d.Str("DeviceName").Equals(n.Description, StringComparison.OrdinalIgnoreCase));
                if (drv is not null)
                {
                    info.DriverVersion = drv.Str("DriverVersion");
                    info.DriverDate = drv.Date("DriverDate");
                    info.DriverProvider = drv.Str("DriverProviderName");
                }
                return info;
            })
            // NDIS filter layers (WFP, QoS, Npcap...) appear as extra "Up" interfaces without an address.
            .Where(a => a.IPv4.Count > 0)
            .OrderByDescending(a => a.Gateways.Count > 0)
            .ToList();
    }

    private static T TryGet<T>(Func<T> f)
    {
        try { return f(); } catch { return default!; }
    }

    public static async Task<PingStats> PingAsync(string target, string label, int count, TimeSpan interval, IProgress<PingStats>? progress = null, CancellationToken ct = default)
    {
        var stats = new PingStats { Target = target, Label = label };
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(target, TimeSpan.FromSeconds(1), cancellationToken: ct).ConfigureAwait(false);
                stats.Samples.Add(reply.Status == IPStatus.Success ? reply.RoundtripTime : null);
            }
            catch (PingException)
            {
                stats.Samples.Add(null);
            }
            progress?.Report(stats);
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
        return stats;
    }

    /// <summary>Interprets ping results into a plain-language diagnosis.</summary>
    public static string Diagnose(PingStats? gateway, PingStats? internet, bool wireless)
    {
        if (gateway is null || internet is null) return "";
        if (gateway.LossPercent > 0)
            return wireless
                ? L.F("Packets are lost between this PC and your router ({0:0.#}%). The problem is local: weak Wi-Fi signal, interference or distance. Try the 5 GHz band, move closer, or use Ethernet.", gateway.LossPercent)
                : L.F("Packets are lost between this PC and your router ({0:0.#}%). Check the Ethernet cable, the router port and the adapter driver.", gateway.LossPercent);
        if (gateway.Jitter > 10 && wireless)
            return L.F("Latency to your router is unstable (jitter {0:0.0} ms). This is typical of Wi-Fi congestion and causes in-game rubber-banding even when average ping is low.", gateway.Jitter);
        if (internet.LossPercent > 0)
            return L.F("Your local network is clean, but {0:0.#}% of packets to the internet are lost. The issue is beyond your router (ISP or modem). Contact your provider if it persists.", internet.LossPercent);
        if (internet.Average > 80)
            return L.F("Your connection is stable but internet latency is high ({0:0} ms). This depends on your ISP and distance, not on Windows settings.", internet.Average);
        return L.T("Your connection looks healthy: no packet loss and stable latency.");
    }

    /// <summary>
    /// Measures resolver response time with raw DNS queries over UDP (no system cache involved). This compares
    /// resolvers only; it does not change your DNS and does not affect in-game ping.
    /// </summary>
    public static async Task<List<DnsResult>> CompareDnsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var current = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(n => n.GetIPProperties().DnsAddresses)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .Distinct()
            .ToList();

        var servers = current.Select(s => (Name: L.T("Your current DNS"), Ip: s, IsCurrent: true))
            .Concat(new (string Name, string Ip, bool IsCurrent)[] { ("Cloudflare", "1.1.1.1", false), ("Google", "8.8.8.8", false), ("Quad9", "9.9.9.9", false) })
            .GroupBy(s => s.Ip).Select(g => g.First()).ToList();

        string[] domains = ["microsoft.com", "steampowered.com", "wikipedia.org", "github.com", "epicgames.com", "riotgames.com", "cloudflare.com", "amazon.com"];
        var results = new List<DnsResult>();
        foreach (var (name, ip, isCurrent) in servers)
        {
            progress?.Report(L.F("Testing {0} ({1})…", name, ip));
            var times = new List<double>();
            var failures = 0;
            foreach (var d in domains)
            {
                ct.ThrowIfCancellationRequested();
                var t = await DnsQuery.MeasureAsync(IPAddress.Parse(ip), d, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                if (t is null) failures++; else times.Add(t.Value);
            }
            times.Sort();
            results.Add(new DnsResult(name, ip, times.Count == 0 ? null : times[times.Count / 2], failures, isCurrent));
        }
        return results.OrderBy(r => r.MedianMs ?? double.MaxValue).ToList();
    }

    /// <summary>Runs `ipconfig /flushdns` (no admin needed, harmless: the cache refills automatically).</summary>
    public static Task<ProcessResult> FlushDnsAsync() => ProcessRunner.RunAsync(ProcessRunner.System32("ipconfig.exe"), "/flushdns");

    /// <summary>Resets Winsock and the TCP/IP stack to defaults. Requires admin and a restart; cannot be undone.</summary>
    public static async Task<(bool Success, string Output)> ResetStackAsync()
    {
        if (!Elevation.IsElevated) throw new AdminRequiredException("Network reset");
        var a = await ProcessRunner.RunAsync(ProcessRunner.System32("netsh.exe"), "winsock reset").ConfigureAwait(false);
        var b = await ProcessRunner.RunAsync(ProcessRunner.System32("netsh.exe"), "int ip reset").ConfigureAwait(false);
        Log.Audit($"Network reset: winsock={a.ExitCode} ip={b.ExitCode}");
        return (a.Success && b.Success, (a.StdOut + b.StdOut).Trim());
    }
}

/// <summary>Minimal DNS A-record query over UDP (RFC 1035), used only for timing resolvers.</summary>
public static class DnsQuery
{
    public static byte[] Build(ushort id, string name)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var l = System.Text.Encoding.ASCII.GetBytes(label);
            bytes.Add((byte)l.Length);
            bytes.AddRange(l);
        }
        bytes.AddRange([0, 0, 1, 0, 1]); // root, QTYPE A, QCLASS IN
        return bytes.ToArray();
    }

    /// <summary>True if the response matches the query ID and reports NOERROR or NXDOMAIN.</summary>
    public static bool IsValidResponse(byte[] response, ushort id) =>
        response.Length >= 12 && response[0] == (byte)(id >> 8) && response[1] == (byte)id && (response[2] & 0x80) != 0 && (response[3] & 0x0F) is 0 or 3;

    public static async Task<double?> MeasureAsync(IPAddress server, string name, TimeSpan timeout, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(1, 65535);
        // Popular domains are usually cached by the resolver, so this measures round-trip time to it — what you feel when browsing.
        var query = Build(id, name);
        using var udp = new UdpClient(server.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(query, new IPEndPoint(server, 53), cts.Token).ConfigureAwait(false);
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (IsValidResponse(r.Buffer, id)) return sw.Elapsed.TotalMilliseconds;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
