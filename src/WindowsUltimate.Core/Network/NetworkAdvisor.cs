using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Network;

/// <summary>One network recommendation. <see cref="ActionId"/> names the change the UI can offer (null = advice only).</summary>
public sealed record NetworkAdvice(string Title, string Detail, string Severity, string? ActionId = null, string? ActionLabel = null);

/// <summary>Everything measured so far on the Network page. Missing measurements are simply null.</summary>
public sealed class NetworkSnapshot
{
    public IReadOnlyList<AdapterInfo> Adapters { get; init; } = [];
    public IReadOnlyList<WifiStatus> Wifi { get; init; } = [];
    public TcpGlobalInfo? Tcp { get; init; }
    public IReadOnlySet<string> Ipv6Unbound { get; init; } = new HashSet<string>();
    public PingStats? Gateway { get; init; }
    public PingStats? Internet { get; init; }
    public IReadOnlyList<DnsResult> Dns { get; init; } = [];
    public NetworkMeasurement? Speed { get; init; }
    public int? PathMtu { get; init; }
    public bool DeliveryOptimizationLanOnly { get; init; }
    public bool HasPowerSavingTweak { get; init; }
}

/// <summary>
/// Turns measurements into plain-language recommendations. Rules: never promise lower ping, recommend a change only when a
/// measurement or a documented misconfiguration supports it, and say when the cause is outside Windows.
/// </summary>
public static class NetworkAdvisor
{
    public static List<NetworkAdvice> Advise(NetworkSnapshot s)
    {
        var list = new List<NetworkAdvice>();
        var active = s.Adapters.FirstOrDefault(a => a.Gateways.Count > 0);
        if (active is null)
        {
            list.Add(new(L.T("No active internet connection"), L.T("No adapter has a default gateway. Check the cable or Wi-Fi connection, then refresh."), "bad"));
            return list;
        }

        // Connection type
        if (active.IsWireless)
        {
            var hasEthernet = s.Adapters.Any(a => a.IsEthernet) || NetworkDiagnostics.HasDisconnectedEthernet();
            list.Add(new(L.T("You are on Wi-Fi"),
                hasEthernet
                    ? L.T("This PC also has an Ethernet port. For gaming and calls a cable gives lower, steadier latency than any Wi-Fi setting can.")
                    : L.T("Wi-Fi adds a few milliseconds and, more importantly, variation (jitter). Signal quality and the band matter more than any Windows setting."),
                "info"));
            foreach (var w in s.Wifi)
                if (WifiInfo.Advice(w) is { Length: > 0 } advice && w.SignalQuality < 70)
                    list.Add(new(L.F("Weak Wi-Fi signal on {0}", w.Ssid), advice, w.SignalQuality < 45 ? "bad" : "warn"));
        }

        // Link problems
        if (active.ErrorsIn + active.ErrorsOut > 0)
            list.Add(new(L.T("The adapter reports packet errors"),
                L.F("{0} receive / {1} send errors since the adapter started. Usually a damaged cable, a bad router port or an outdated driver — not a Windows setting.", active.ErrorsIn, active.ErrorsOut),
                "warn", "drivers", L.T("Check drivers")));
        if (active.IsEthernet && active.SpeedBps is > 0 and <= 100_000_000)
            list.Add(new(L.T("Ethernet link negotiated at 100 Mbps"),
                L.T("Gigabit adapters drop to 100 Mbps with a damaged or old (Cat 5) cable, or a 100 Mbps router port. Try another cable or port."), "warn"));
        if (active.DriverDate is { } date && (DateTime.Now - date).TotalDays > 730)
            list.Add(new(L.T("Network driver is more than two years old"),
                L.F("Driver dated {0:d}. Get the current driver from your PC or motherboard manufacturer, or Windows Update — Windows Ultimate never installs drivers itself.", date),
                "info", "drivers", L.T("Check drivers")));

        // Measurements
        if (s.Gateway is { Sent: > 0 } g && s.Internet is { Sent: > 0 } i)
        {
            var diagnosis = NetworkDiagnostics.Diagnose(g, i, active.IsWireless);
            var healthy = g.LossPercent == 0 && i.LossPercent == 0 && !(g.Jitter > 10);
            list.Add(new(healthy ? L.T("Latency test: healthy") : L.T("Latency test: problem found"), diagnosis, healthy ? "good" : "warn"));
        }
        if (s.Speed?.Bufferbloat is { } bloat)
        {
            if (bloat > 60)
                list.Add(new(L.F("Heavy lag under load (+{0:0} ms)", bloat),
                    L.T("When the line is busy, latency rises sharply (bufferbloat). This happens in the router or modem, not in Windows. Enable SQM / Smart Queue / QoS on your router if it has it, and avoid big downloads while gaming."),
                    "bad"));
            else if (bloat > 25)
                list.Add(new(L.F("Moderate lag under load (+{0:0} ms)", bloat),
                    L.T("Latency rises noticeably while downloading. A router with SQM / Smart Queue limits this; Windows settings cannot."), "warn"));
            else
                list.Add(new(L.T("Latency stays stable under load"), L.F("Only +{0:0} ms while the line is saturated — no bufferbloat.", bloat), "good"));
        }

        // DNS: recommend a provider only if the benchmark shows a clear, repeatable gap.
        if (s.Dns.FirstOrDefault(d => d.IsCurrent) is { MedianMs: { } current } && s.Dns.Where(d => !d.IsCurrent && d.Failures == 0 && d.MedianMs is not null).MinBy(d => d.MedianMs) is { } best
            && best.MedianMs < current * 0.7 && current - best.MedianMs > 10 && DnsProviders.All.FirstOrDefault(p => p.V4.Contains(best.Server)) is { } provider)
        {
            list.Add(new(L.F("{0} answers faster than your current DNS", provider.Name),
                L.F("{0:0} ms instead of {1:0} ms per lookup. This only shortens the moment before a site or launcher starts connecting; it does not change game ping or download speed.", best.MedianMs, current),
                "info", "dns:" + provider.Id, L.F("Use {0}", provider.Name)));
        }
        else if (s.Dns.Any(d => d.IsCurrent && d.Failures > 1))
            list.Add(new(L.T("Your current DNS failed several lookups"), L.T("Unreliable name resolution causes 'site can't be reached' errors. A public resolver below is a reasonable alternative."), "warn"));

        // TCP auto-tuning: only the documented default is recommended.
        if (s.Tcp is { } tcp && tcp.AutoTuning is "disabled" or "highlyrestricted" or "restricted")
            list.Add(new(L.F("TCP auto-tuning is limited ({0})", tcp.AutoTuning),
                L.T("This is usually left behind by an old 'internet booster' guide. It caps the TCP receive window, which limits download speed on fast or distant connections and has no latency benefit. Windows' default is 'normal'."),
                "warn", "autotuning", L.T("Restore default")));

        if (s.Ipv6Unbound.Contains(active.Name))
            list.Add(new(L.T("IPv6 is disabled on the active adapter"),
                L.T("Microsoft does not recommend disabling IPv6: some Windows components and many ISPs (and Xbox networking) prefer it, and disabling it does not make IPv4 faster. Re-enable it in the adapter properties unless you disabled it for a specific reason."),
                "info"));
        else if (active.HasGlobalIPv6)
            list.Add(new(L.T("IPv6 is working"), L.T("Your connection has a public IPv6 address. Nothing to change."), "good"));

        if (s.PathMtu is { } mtu && mtu < 1500)
            list.Add(new(L.F("Path MTU is {0} bytes", mtu),
                L.T("Lower than 1500 is normal on PPPoE (fibre/DSL), VPNs and some mobile links, and Windows detects it automatically. Lowering the MTU by hand is only useful when specific sites fail to load."),
                "info"));

        if (!s.DeliveryOptimizationLanOnly)
            list.Add(new(L.T("Windows may upload updates to other PCs"),
                L.T("Delivery Optimization can use your upload bandwidth to share update pieces over the internet, which can cause lag spikes on slow-upload connections."),
                "info", "deliveryopt", L.T("Limit to my network")));

        if (s.HasPowerSavingTweak)
            list.Add(new(L.T("Ethernet energy saving is on"),
                L.T("Worth turning off only if you see link drops or speed renegotiations. It does not lower ping on a link that works well."),
                "info", "adapterpower", L.T("Review")));

        return list;
    }
}
