using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.Core.SystemServices;

public enum ServiceClass
{
    /// <summary>Windows needs it to boot, sign in, network or update. Never changed.</summary>
    Essential,
    /// <summary>Protects the PC (Defender, Firewall, updates of security software). Never changed.</summary>
    Security,
    /// <summary>A Windows feature that is better left as it is: changing it gains nothing measurable.</summary>
    KeepDefault,
    /// <summary>A feature you may not use; Windows Ultimate offers one documented, reversible change.</summary>
    Optional,
    /// <summary>Installed by another program.</summary>
    ThirdParty,
    /// <summary>A Windows service Windows Ultimate has no verified information about. Never changed.</summary>
    Unclassified,
}

/// <summary>What Windows Ultimate knows about one service, and the only change it will offer (if any).</summary>
public sealed class ServiceKnowledge
{
    public required string Name { get; init; }
    public required ServiceClass Class { get; init; }
    public required string Why { get => L.T(field); init; }

    /// <summary>Start value to offer (3 = Manual, 4 = Disabled); null = no change offered.</summary>
    public int? SuggestedStart { get; init; }
    public string Benefit { get => L.T(field); init; } = "";
    public string Drawback { get => L.T(field); init; } = "";
    public RiskLevel Risk { get; init; } = RiskLevel.LowRisk;
    public Func<SystemProfile, Recommendation>? Recommend { get; init; }
    public Func<SystemProfile, Compatibility>? Compatible { get; init; }
}

public static class ServiceCatalog
{
    private static readonly string[] EssentialNames =
    [
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "Power", "PlugPlay", "ProfSvc", "UserManager", "Schedule", "EventLog", "EventSystem",
        "Winmgmt", "CryptSvc", "Dhcp", "Dnscache", "nsi", "NlaSvc", "netprofm", "LanmanWorkstation", "Netman", "WlanSvc", "Wcmsvc", "WinHttpAutoProxySvc",
        "AudioSrv", "AudioEndpointBuilder", "Themes", "FontCache", "CoreMessagingRegistrar", "SystemEventsBroker", "TimeBrokerSvc", "BrokerInfrastructure",
        "StateRepository", "AppXSvc", "ClipSVC", "AppReadiness", "DeviceInstall", "DsmSvc", "TrustedInstaller", "msiserver", "gpsvc", "camsvc",
        "TokenBroker", "KeyIso", "VaultSvc", "Appinfo", "ShellHWDetection", "StorSvc", "TextInputManagementService", "TabletInputService",
        "DispBrokerDesktopSvc", "DisplayEnhancementService", "WinRM", "W32Time", "Wcncsvc", "iphlpsvc", "BFE", "IKEEXT", "PolicyAgent", "LicenseManager",
        "InstallService", "DoSvc", "wlidsvc", "CDPSvc", "CDPUserSvc", "OneSyncSvc", "UnistoreSvc", "UserDataSvc", "PimIndexMaintenanceSvc",
        "Audiosrv", "bthserv", "BthAvctpSvc", "hidserv", "SENS", "seclogon", "UsoSvc", "WaaSMedicSvc", "wuauserv", "BITS", "VSS", "swprv", "sppsvc",
        "SysMain", "WdiServiceHost", "WdiSystemHost", "DPS", "pla", "Spooler", "LanmanServer", "fdPHost", "FDResPub", "SSDPSrv", "upnphost",
        "WbioSrvc", "NgcSvc", "NgcCtnrSvc", "WpnService", "WpnUserService", "webthreatdefsvc", "webthreatdefusersvc", "EFS", "PcaSvc", "lmhosts",
    ];

    private static readonly string[] SecurityNames =
    [
        "WinDefend", "WdNisSvc", "Sense", "SecurityHealthService", "wscsvc", "mpssvc", "SgrmBroker", "MDCoreSvc", "edgeupdate", "edgeupdatem",
        "MicrosoftEdgeElevationService", "gupdate", "gupdatem", "GoogleChromeElevationService", "MozillaMaintenance",
    ];

    private static readonly Dictionary<string, ServiceKnowledge> Known = Build();

    private static Dictionary<string, ServiceKnowledge> Build()
    {
        var d = new Dictionary<string, ServiceKnowledge>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in EssentialNames)
            d[n] = new ServiceKnowledge { Name = n, Class = ServiceClass.Essential, Why = "Part of Windows' core: startup, sign-in, networking, audio, apps or updates depend on it." };
        foreach (var n in SecurityNames)
            d[n] = new ServiceKnowledge { Name = n, Class = ServiceClass.Security, Why = "Protects this PC or keeps security software up to date. Windows Ultimate never changes it." };

        d["SysMain"] = new() { Name = "SysMain", Class = ServiceClass.KeepDefault, Why = "SysMain (Superfetch) preloads frequently used programs into free memory. Disabling it is a popular myth: free RAM is not faster, and launches get slower — especially on hard drives." };
        d["WSearch"] = new()
        {
            Name = "WSearch", Class = ServiceClass.Optional,
            Why = "Indexes files, e-mail and settings for instant search in Start, File Explorer and Outlook.",
            SuggestedStart = 3, Risk = RiskLevel.Advanced,
            Benefit = "Less background disk activity after large file changes — noticeable mainly on hard drives.",
            Drawback = "Search in Start and File Explorer becomes slow and incomplete; Outlook search stops working well.",
            Recommend = p => p.Disks.Any(x => x.MediaType == "HDD") && p.Memory.TotalBytes <= 8L << 30
                ? Recommendation.No("Possible on your hard drive with little RAM, but only if you rarely use search.")
                : Recommendation.No("Indexing is cheap on an SSD; keep it unless you never search."),
        };
        d["DiagTrack"] = new()
        {
            Name = "DiagTrack", Class = ServiceClass.Optional,
            Why = "Connected User Experiences and Telemetry: collects and sends Windows diagnostic data.",
            SuggestedStart = 4, Risk = RiskLevel.LowRisk,
            Benefit = "Stops the diagnostic data upload service and its periodic background work.",
            Drawback = "Feedback Hub and some troubleshooters stop working. Not suitable for Windows Insider builds, which require diagnostic data.",
            Recommend = _ => Recommendation.No("For privacy, the 'Send only required diagnostic data' setting is the supported option; disabling the service is a further, optional step."),
            Compatible = p => p.Os.IsInsider ? Compatibility.No("Windows Insider builds require diagnostic data.") : Compatibility.Yes,
        };
        d["MapsBroker"] = new()
        {
            Name = "MapsBroker", Class = ServiceClass.Optional,
            Why = "Downloaded Maps Manager: updates offline maps for the Maps app.",
            SuggestedStart = 3, Risk = RiskLevel.Safe,
            Benefit = "One fewer service started automatically at boot. It still starts on demand if you open Maps.",
            Drawback = "None for most users: Windows starts it when the Maps app needs it.",
            Recommend = _ => Recommendation.No("Tiny gain; fine to leave as is."),
        };
        d["RemoteRegistry"] = new()
        {
            Name = "RemoteRegistry", Class = ServiceClass.Optional,
            Why = "Lets other computers on the network edit this PC's registry. Disabled by default on Windows 11.",
            SuggestedStart = 4, Risk = RiskLevel.Safe,
            Benefit = "Removes a remote administration entry point (security hardening). This is the Windows default.",
            Drawback = "Remote administration tools used in companies may need it.",
            Recommend = _ => Recommendation.Yes("It is enabled although Windows ships it disabled."),
        };
        d["RemoteAccess"] = new()
        {
            Name = "RemoteAccess", Class = ServiceClass.Optional,
            Why = "Routing and Remote Access: turns the PC into a router or VPN server. Disabled by default.",
            SuggestedStart = 4, Risk = RiskLevel.Safe,
            Benefit = "Restores the Windows default and closes an unused network service.",
            Drawback = "Only needed if this PC deliberately acts as a VPN server or router.",
            Recommend = _ => Recommendation.Yes("It is enabled although Windows ships it disabled."),
        };
        d["Fax"] = new() { Name = "Fax", Class = ServiceClass.KeepDefault, Why = "Fax service. It is set to start on demand, so it uses no resources unless you send a fax: changing it gains nothing." };
        d["dmwappushservice"] = new() { Name = "dmwappushservice", Class = ServiceClass.KeepDefault, Why = "Device management message routing. Starts only on demand; disabling it gains nothing and can break device enrolment." };
        foreach (var x in new[] { "XblAuthManager", "XblGameSave", "XboxNetApiSvc", "XboxGipSvc", "GamingServices", "GamingServicesNet" })
            d[x] = new() { Name = x, Class = ServiceClass.KeepDefault, Why = "Xbox / Game Pass component. Starts only when an Xbox app, game or controller needs it, so it costs nothing otherwise. Disabling it breaks Game Pass games, cloud saves and Xbox controllers." };
        d["Spooler"] = new()
        {
            Name = "Spooler", Class = ServiceClass.Optional,
            Why = "Print Spooler: manages printing, including 'Microsoft Print to PDF'.",
            SuggestedStart = 4, Risk = RiskLevel.Moderate,
            Benefit = "Reduces attack surface (the spooler has had serious vulnerabilities).",
            Drawback = "No printing and no 'Print to PDF' until it is re-enabled.",
            Recommend = _ => Recommendation.No("Only if you never print."),
            Compatible = _ => TweakCatalog.HasPhysicalPrinter() ? Compatibility.No("A physical or network printer is installed.") : Compatibility.Yes,
        };
        d["WerSvc"] = new() { Name = "WerSvc", Class = ServiceClass.KeepDefault, Why = "Windows Error Reporting. Starts only when a program crashes; disabling it hides crash information you may need." };
        d["lfsvc"] = new() { Name = "lfsvc", Class = ServiceClass.KeepDefault, Why = "Geolocation. Starts on demand; control location access in Privacy settings instead." };
        d["RetailDemo"] = new() { Name = "RetailDemo", Class = ServiceClass.KeepDefault, Why = "Store demo mode. Starts only in demo mode; it uses nothing otherwise." };
        d["TermService"] = new() { Name = "TermService", Class = ServiceClass.KeepDefault, Why = "Remote Desktop. It only accepts connections when Remote Desktop is turned on in Settings — that switch is the right control." };
        return d;
    }

    public static ServiceKnowledge? Find(string name)
    {
        if (Known.TryGetValue(name, out var k)) return k;
        // Per-user services are named Base_xxxxx (e.g. CDPUserSvc_1a2b3).
        var i = name.LastIndexOf('_');
        return i > 0 && Known.TryGetValue(name[..i], out k) ? k : null;
    }

    public static IEnumerable<ServiceKnowledge> All => Known.Values;
}
