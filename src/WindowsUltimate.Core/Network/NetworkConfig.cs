using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.Core.Network;

/// <summary>A public DNS resolver the user can switch an adapter to. Only well-known, audited providers are listed.</summary>
public sealed class DnsProvider
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string[] V4 { get; init; }
    public required string[] V6 { get; init; }
    public required string Strengths { get => L.T(field); init; }
    public required string Drawbacks { get => L.T(field); init; }
    public required string PolicyUrl { get; init; }
    public string AddressesText => string.Join(", ", V4);
}

public static class DnsProviders
{
    public static IReadOnlyList<DnsProvider> All { get; } =
    [
        new()
        {
            Id = "cloudflare", Name = "Cloudflare",
            V4 = ["1.1.1.1", "1.0.0.1"], V6 = ["2606:4700:4700::1111", "2606:4700:4700::1001"],
            Strengths = "Usually among the fastest resolvers worldwide. Does not sell query data; logs are deleted within 25 hours and audited.",
            Drawbacks = "No filtering of malicious domains. Some ISP-specific services (TV boxes, captive portals) may expect the ISP resolver.",
            PolicyUrl = "https://developers.cloudflare.com/1.1.1.1/privacy/public-dns-resolver/",
        },
        new()
        {
            Id = "google", Name = "Google Public DNS",
            V4 = ["8.8.8.8", "8.8.4.4"], V6 = ["2001:4860:4860::8888", "2001:4860:4860::8844"],
            Strengths = "Very reliable, large anycast network, strict DNSSEC validation.",
            Drawbacks = "Google keeps temporary logs (24–48 h) and some anonymised data longer. No malware filtering.",
            PolicyUrl = "https://developers.google.com/speed/public-dns/privacy",
        },
        new()
        {
            Id = "quad9", Name = "Quad9",
            V4 = ["9.9.9.9", "149.112.112.112"], V6 = ["2620:fe::fe", "2620:fe::9"],
            Strengths = "Blocks domains known to host malware and phishing, using threat intelligence from security companies. Non-profit, no personal data logged.",
            Drawbacks = "Often a few milliseconds slower than Cloudflare or Google. A legitimate site can very occasionally be blocked by mistake.",
            PolicyUrl = "https://quad9.net/privacy/policy/",
        },
    ];

    public static DnsProvider? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>The provider the given servers belong to, or null for ISP/router/custom resolvers.</summary>
    public static DnsProvider? Match(IEnumerable<string> servers)
    {
        var set = servers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.FirstOrDefault(p => p.V4.Any(set.Contains));
    }
}

/// <summary>
/// Sets the DNS servers of one adapter (IPv4 and, when IPv6 is bound, IPv6). The original configuration —
/// automatic (DHCP) or a static list — is captured from the registry and restored exactly on undo.
/// Changes go through netsh with validated IP literals only.
/// </summary>
public sealed class DnsServersAction(string interfaceGuid, string alias, string[] v4, string[] v6) : IChangeAction
{
    public string Key => $"dns:{interfaceGuid}".ToLowerInvariant();
    public bool RequiresAdmin => true;
    public bool IsReversible => true;
    public string Describe() => L.F("DNS servers of {0}", alias.Length > 0 ? alias : interfaceGuid);
    public string DescribeDesired() => Format(v4, v6);
    public string DescribeCurrent() => Format(Read("Tcpip"), Ipv6Bound ? Read("Tcpip6") : []);

    private bool Ipv6Bound
    {
        get
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\{interfaceGuid}");
            return k is not null;
        }
    }

    private static string Format(string[] a, string[] b) =>
        a.Length == 0 && b.Length == 0 ? L.T("Automatic (DHCP)") : string.Join(", ", a.Concat(b));

    private string[] Read(string stack)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{stack}\Parameters\Interfaces\{interfaceGuid}");
        return (k?.GetValue("NameServer") as string ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public bool IsInDesiredState()
    {
        if (!Read("Tcpip").SequenceEqual(v4, StringComparer.OrdinalIgnoreCase)) return false;
        return v6.Length == 0 || !Ipv6Bound || Read("Tcpip6").SequenceEqual(v6, StringComparer.OrdinalIgnoreCase);
    }

    public ActionSnapshot Capture() => new()
    {
        ActionKey = Key,
        Kind = "dns",
        Data =
        {
            ["guid"] = interfaceGuid,
            ["alias"] = alias,
            ["v4"] = string.Join(",", Read("Tcpip")),
            ["v6"] = Ipv6Bound ? string.Join(",", Read("Tcpip6")) : "",
        },
    };

    public void Apply()
    {
        var index = ResolveIndex();
        Set("ipv4", index, v4);
        if (v6.Length > 0 && Ipv6Bound) Set("ipv6", index, v6);
        ProcessRunner.Run(ProcessRunner.System32("ipconfig.exe"), "/flushdns");
    }

    public void Restore(ActionSnapshot s)
    {
        var index = ResolveIndex();
        Set("ipv4", index, Split(s.Data.GetValueOrDefault("v4")));
        if (Ipv6Bound) Set("ipv6", index, Split(s.Data.GetValueOrDefault("v6")));
        ProcessRunner.Run(ProcessRunner.System32("ipconfig.exe"), "/flushdns");
    }

    private static string[] Split(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private int ResolveIndex()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id.Equals(interfaceGuid, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException(L.F("The network adapter {0} is no longer present.", alias));
        return nic.GetIPProperties().GetIPv4Properties()?.Index ?? nic.GetIPProperties().GetIPv6Properties()?.Index
               ?? throw new InvalidOperationException(L.F("The network adapter {0} has no IP configuration.", alias));
    }

    private static void Set(string family, int index, string[] servers)
    {
        var wanted = family == "ipv4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        foreach (var s in servers)
            if (!IPAddress.TryParse(s, out var ip) || ip.AddressFamily != wanted)
                throw new ArgumentException($"Invalid {family} DNS server address: {s}");

        var netsh = ProcessRunner.System32("netsh.exe");
        var steps = new List<string>();
        if (servers.Length == 0) steps.Add($"interface {family} set dnsservers name={index} source=dhcp");
        else
        {
            steps.Add($"interface {family} set dnsservers name={index} source=static address={servers[0]} register=primary validate=no");
            for (var i = 1; i < servers.Length; i++) steps.Add($"interface {family} add dnsservers name={index} address={servers[i]} index={i + 1} validate=no");
        }
        foreach (var args in steps)
        {
            var r = ProcessRunner.Run(netsh, args, TimeSpan.FromSeconds(20));
            if (!r.Success) throw new InvalidOperationException($"netsh {args}: {(r.StdOut + r.StdErr).Trim()}");
        }
    }
}

/// <summary>Restores TCP receive-window auto-tuning (netsh int tcp set global autotuninglevel). Captures the previous level.</summary>
public sealed class TcpAutoTuningAction(string level) : IChangeAction
{
    public const string ActionKey = "tcp:autotuning";
    public string Key => ActionKey;
    public bool RequiresAdmin => true;
    public bool IsReversible => true;
    public string Describe() => "TCP receive window auto-tuning";
    public string DescribeDesired() => level;
    public string DescribeCurrent() => NetworkConfig.Tcp()?.AutoTuning ?? "unknown";
    public bool IsInDesiredState() => string.Equals(NetworkConfig.Tcp()?.AutoTuning, level, StringComparison.OrdinalIgnoreCase);
    public ActionSnapshot Capture() => new() { ActionKey = Key, Kind = "tcp", Data = { ["level"] = NetworkConfig.Tcp()?.AutoTuning ?? "normal" } };
    public void Apply() => Set(level);
    public void Restore(ActionSnapshot s) => Set(s.Data.GetValueOrDefault("level") ?? "normal");

    private static void Set(string value)
    {
        if (!NetworkConfig.AutoTuningLevels.Contains(value)) throw new ArgumentException($"Invalid auto-tuning level {value}");
        var r = ProcessRunner.Run(ProcessRunner.System32("netsh.exe"), $"int tcp set global autotuninglevel={value}", TimeSpan.FromSeconds(20));
        if (!r.Success) throw new InvalidOperationException((r.StdOut + r.StdErr).Trim());
    }
}

public sealed record TcpGlobalInfo(string AutoTuning, string Congestion, bool? ReceiveSideScaling, bool? SegmentCoalescing);

public sealed record ProcessConnections(int Pid, string Process, int Established, int Listening, IReadOnlyList<string> Remotes)
{
    public string RemotesText => string.Join("  ·  ", Remotes);
}

/// <summary>Read-only network configuration checks plus the few reversible changes that have a documented benefit.</summary>
public static class NetworkConfig
{
    internal static readonly string[] AutoTuningLevels = ["disabled", "highlyrestricted", "restricted", "normal", "experimental"];

    public static TcpGlobalInfo? Tcp()
    {
        var row = Wmi.TryQuery("SELECT SettingName, AutoTuningLevelLocal, CongestionProvider FROM MSFT_NetTCPSetting WHERE SettingName='Internet'", @"root\StandardCimv2").FirstOrDefault();
        if (row is null) return null;
        var level = row.TryGetValue("AutoTuningLevelLocal", out var l) && l is not null ? Convert.ToInt32(l) : -1;
        var cc = row.Long("CongestionProvider") switch
        {
            0 => "Default", 1 => "NewReno", 2 => "CTCP", 3 => "DCTCP", 4 => "LEDBAT", 5 => "CUBIC", var n => $"#{n}",
        };
        var offload = Wmi.TryQuery("SELECT ReceiveSideScaling, ReceiveSegmentCoalescing FROM MSFT_NetOffloadGlobalSetting", @"root\StandardCimv2").FirstOrDefault();
        bool? Flag(string name) => offload is null || !offload.TryGetValue(name, out var v) || v is null ? null : Convert.ToInt32(v) == 1;
        return new TcpGlobalInfo(level is >= 0 and < 5 ? AutoTuningLevels[level] : "unknown", cc, Flag("ReceiveSideScaling"), Flag("ReceiveSegmentCoalescing"));
    }

    /// <summary>Adapter names whose IPv6 protocol binding has been unticked (Microsoft advises against disabling IPv6).</summary>
    public static HashSet<string> Ipv6Unbound() =>
        Wmi.TryQuery("SELECT Name, Enabled FROM MSFT_NetAdapterBindingSettingData WHERE ComponentID='ms_tcpip6'", @"root\StandardCimv2")
            .Where(r => r.Bool("Enabled") == false).Select(r => r.Str("Name")).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static Tweak DnsTweak(AdapterInfo a, DnsProvider p) => new()
    {
        Id = $"dns:{a.Id}:{p.Id}".ToLowerInvariant(),
        Name = L.F("Use {0} DNS on {1}", p.Name, a.Name),
        Category = TweakCategory.Network,
        Description = L.F("Name lookups on {0} go to {1} instead of the resolver given by your router or provider.", a.Name, p.Name),
        WhatItChanges = L.F("Sets the adapter's DNS servers to {0} (IPv4 and IPv6 when available).", string.Join(", ", p.V4.Concat(p.V6))),
        WhyItHelps = p.Strengths,
        ExpectedImpact = L.T("Can make the first connection to a website start sooner if your current resolver is slow or unreliable. It does not change in-game ping, download speed or latency to game servers."),
        Disadvantages = p.Drawbacks + " " + L.T("If you use a VPN or a corporate network, its DNS may be bypassed or conflict."),
        TechnicalDetails = $"netsh interface ipv4/ipv6 set dnsservers name={a.Index} source=static …; original read from HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip(6)\\Parameters\\Interfaces\\{a.Id} › NameServer",
        Source = p.PolicyUrl,
        Risk = RiskLevel.LowRisk,
        Benefit = BenefitLevel.Low,
        ConflictGroup = $"dns:{a.Id}".ToLowerInvariant(),
        Actions = [new DnsServersAction(a.Id, a.Name, p.V4, p.V6)],
    };

    private const string NetClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    // Energy-saving keywords exposed by common Ethernet drivers ("*EEE" is the NDIS standardised one).
    private static readonly string[] PowerKeywords = ["*EEE", "AdvancedEEE", "EEELinkAdvertisement", "EnableGreenEthernet", "GigaLite", "PowerSavingMode", "ReduceSpeedOnPowerDown"];

    /// <summary>Registry path of the adapter's driver settings (Class\{net}\NNNN), found by its interface GUID.</summary>
    public static string? DriverKey(string interfaceGuid)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(NetClass);
            if (cls is null) return null;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (string.Equals(k?.GetValue("NetCfgInstanceId") as string, interfaceGuid, StringComparison.OrdinalIgnoreCase))
                        return $@"{NetClass}\{sub}";
                }
                catch { /* key not readable */ }
            }
        }
        catch (Exception ex) { Log.Warn($"Adapter class enumeration failed: {ex.Message}"); }
        return null;
    }

    /// <summary>
    /// Turns off the Ethernet driver's energy-saving features (Energy-Efficient Ethernet, "green" modes) where the driver
    /// exposes them and they are on. Returns null when there is nothing to change.
    /// </summary>
    public static Tweak? AdapterPowerTweak(AdapterInfo a)
    {
        if (!a.IsEthernet || DriverKey(a.Id) is not { } path) return null;
        var actions = new List<IChangeAction>();
        var names = new List<string>();
        using (var k = Registry.LocalMachine.OpenSubKey(path))
        {
            if (k is null) return null;
            foreach (var kw in PowerKeywords)
            {
                if (k.GetValue(kw) is not string current || current == "0") continue;
                // Only keywords whose driver declares "0" as a valid choice.
                using var options = k.OpenSubKey($@"Ndi\params\{kw}\enum");
                if (options?.GetValue("0") is not string label) continue;
                actions.Add(new RegistryValueAction(RegistryHive.LocalMachine, path, kw, "0", RegistryValueKind.String));
                using var param = k.OpenSubKey($@"Ndi\params\{kw}");
                names.Add($"{param?.GetValue("ParamDesc") as string ?? kw} → {label}");
            }
        }
        if (actions.Count == 0) return null;
        return new Tweak
        {
            Id = $"netpower:{a.Id}".ToLowerInvariant(),
            Name = L.F("Turn off energy saving on {0}", a.Name),
            Category = TweakCategory.Network,
            Description = L.T("Keeps the Ethernet link at full power instead of letting it doze between packets."),
            WhatItChanges = string.Join("; ", names),
            WhyItHelps = L.T("Energy-Efficient Ethernet and 'green' modes put the link to sleep during idle moments. With some switches and routers this causes short link drops, renegotiation to a lower speed or a few extra microseconds of wake-up delay."),
            ExpectedImpact = L.T("Fixes link drops and speed renegotiation where these features misbehave. On a link that already works well there is no measurable change in ping."),
            Disadvantages = L.T("Slightly higher power use of the network card (well under 1 W). The adapter must be restarted (or Windows restarted) for the driver to apply it."),
            TechnicalDetails = $@"HKLM\{path} › {string.Join(", ", actions.Cast<RegistryValueAction>().Select(x => x.Name))} = 0",
            Source = "https://learn.microsoft.com/windows-hardware/drivers/network/standardized-inf-keywords-for-power-management",
            Risk = RiskLevel.LowRisk,
            Benefit = BenefitLevel.Low,
            Restart = RestartRequirement.Restart,
            Actions = actions,
        };
    }

    /// <summary>
    /// Largest packet that reaches the target without fragmentation (ICMP with the Don't Fragment flag, binary search).
    /// Returns null when ICMP is blocked.
    /// </summary>
    public static async Task<int?> ProbePathMtuAsync(string target = "1.1.1.1", CancellationToken ct = default)
    {
        using var ping = new Ping();
        var options = new PingOptions(64, dontFragment: true);
        var address = IPAddress.Parse(target);
        async Task<bool> Passes(int payload)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var r = await ping.SendPingAsync(address, 1000, new byte[payload], options).ConfigureAwait(false);
                    if (r.Status == IPStatus.Success) return true;
                    if (r.Status == IPStatus.PacketTooBig) return false;
                }
                catch (PingException) { return false; }
            }
            return false;
        }

        if (!await Passes(64).ConfigureAwait(false)) return null;
        if (await Passes(1472).ConfigureAwait(false)) return 1500;
        int lo = 64, hi = 1472;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (await Passes(mid).ConfigureAwait(false)) lo = mid; else hi = mid;
        }
        return lo + 28; // + IPv4 (20) and ICMP (8) headers
    }

    /// <summary>Clears the DNS client cache, and with administrator rights the ARP and NetBIOS name caches. All refill automatically.</summary>
    public static async Task<List<(string Step, bool Ok)>> ClearCachesAsync()
    {
        var steps = new List<(string, bool)>();
        var dns = await ProcessRunner.RunAsync(ProcessRunner.System32("ipconfig.exe"), "/flushdns").ConfigureAwait(false);
        steps.Add((L.T("DNS client cache"), dns.Success));
        if (Elevation.IsElevated)
        {
            var arp = await ProcessRunner.RunAsync(ProcessRunner.System32("netsh.exe"), "interface ip delete arpcache").ConfigureAwait(false);
            steps.Add((L.T("ARP cache (address of devices on your network)"), arp.Success));
            var nbt = await ProcessRunner.RunAsync(ProcessRunner.System32("nbtstat.exe"), "-R").ConfigureAwait(false);
            steps.Add((L.T("NetBIOS name cache"), nbt.Success));
        }
        Log.Audit($"Network caches cleared: {string.Join(", ", steps.Select(s => $"{s.Item1}={s.Item2}"))}");
        return steps;
    }

    // ── Open TCP connections per process (GetExtendedTcpTable) ──

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int af, int tableClass, int reserved);

    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int StateListen = 2, StateEstablished = 5;

    /// <summary>
    /// Which programs hold network connections right now. This is a count of connections, not bandwidth:
    /// Windows does not expose per-process traffic without tracing.
    /// </summary>
    public static List<ProcessConnections> Connections()
    {
        var rows = new List<(int Pid, int State, IPAddress Remote, int Port)>();
        Read(AddressFamily.InterNetwork, rows);
        Read(AddressFamily.InterNetworkV6, rows);

        var names = new Dictionary<int, string>();
        string NameOf(int pid)
        {
            if (names.TryGetValue(pid, out var n)) return n;
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); n = pid == 4 ? "System" : p.ProcessName; }
            catch { n = pid == 0 ? "System Idle" : $"PID {pid}"; }
            return names[pid] = n;
        }

        return rows.GroupBy(r => r.Pid)
            .Select(g =>
            {
                var est = g.Where(r => r.State == StateEstablished && !IPAddress.IsLoopback(r.Remote) && !r.Remote.Equals(IPAddress.Any) && !r.Remote.Equals(IPAddress.IPv6Any)).ToList();
                return new ProcessConnections(g.Key, NameOf(g.Key), est.Count, g.Count(r => r.State == StateListen),
                    est.Select(r => r.Remote.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{r.Remote}]:{r.Port}" : $"{r.Remote}:{r.Port}").Distinct().Take(4).ToList());
            })
            .Where(p => p.Established > 0)
            .OrderByDescending(p => p.Established)
            .ToList();
    }

    private static void Read(AddressFamily family, List<(int, int, IPAddress, int)> rows)
    {
        var af = family == AddressFamily.InterNetwork ? 2 : 23;
        var size = 0;
        GetExtendedTcpTable(0, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size == 0) return;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = af == 2 ? 24 : 56;
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * rowSize;
                if (af == 2)
                {
                    var state = Marshal.ReadInt32(row);
                    var remote = new IPAddress((uint)Marshal.ReadInt32(row, 12));
                    var port = Port(Marshal.ReadInt32(row, 16));
                    rows.Add((Marshal.ReadInt32(row, 20), state, remote, port));
                }
                else
                {
                    var bytes = new byte[16];
                    Marshal.Copy(row + 24, bytes, 0, 16);
                    rows.Add((Marshal.ReadInt32(row, 52), Marshal.ReadInt32(row, 48), new IPAddress(bytes), Port(Marshal.ReadInt32(row, 44))));
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
}
