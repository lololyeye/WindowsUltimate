using System.Diagnostics;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.Core.SystemServices;

public sealed class ServiceInfo
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string State { get; init; } = "";
    public string StartMode { get; init; } = "";
    public bool DelayedStart { get; init; }
    public string PathName { get; init; } = "";
    public int ProcessId { get; init; }
    public string Company { get; init; } = "";
    public bool IsWindows { get; init; }
    public long MemoryBytes { get; init; }
    public int SharedWith { get; init; }
    public ServiceClass Class { get; init; }
    public ServiceKnowledge? Knowledge { get; init; }
    public bool IsRunning => State == "Running";

    /// <summary>Registry Start value: 2 automatic, 3 manual, 4 disabled.</summary>
    public int StartValue => StartMode switch { "Auto" => 2, "Manual" => 3, "Disabled" => 4, "Boot" => 0, "System" => 1, _ => -1 };

    /// <summary>The change Windows Ultimate offers for this service, or null (unknown, essential, or already in that state).</summary>
    public int? OfferedStart => Knowledge?.SuggestedStart is { } s && Knowledge.Class == ServiceClass.Optional && StartValue != s && StartValue > 1
        && !(s == 3 && StartValue == 4) ? s : null;
}

public static class ServiceManager
{
    public static List<ServiceInfo> List()
    {
        var rows = Wmi.TryQuery("SELECT Name, DisplayName, Description, State, StartMode, DelayedAutoStart, PathName, ProcessId, ServiceType FROM Win32_Service");
        var sharing = rows.Where(r => r.Long("ProcessId") > 0).GroupBy(r => r.Long("ProcessId")).ToDictionary(g => g.Key, g => g.Count());
        var memory = new Dictionary<long, long>();
        foreach (var pid in sharing.Keys)
        {
            try { using var p = Process.GetProcessById((int)pid); memory[pid] = p.WorkingSet64; }
            catch { /* exited or protected */ }
        }
        var companies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        return rows.Select(r =>
            {
                var name = r.Str("Name");
                var path = r.Str("PathName");
                var exe = Startup.StartupManager.ExtractExecutable(path) ?? "";
                if (!companies.TryGetValue(exe, out var company))
                {
                    try { company = File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).CompanyName ?? "" : ""; } catch { company = ""; }
                    companies[exe] = company;
                }
                var isWindows = exe.StartsWith(windows, StringComparison.OrdinalIgnoreCase) && company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
                var knowledge = ServiceCatalog.Find(name);
                var pid = r.Long("ProcessId");
                return new ServiceInfo
                {
                    Name = name,
                    DisplayName = r.Str("DisplayName"),
                    Description = r.Str("Description"),
                    State = r.Str("State"),
                    StartMode = r.Str("StartMode"),
                    DelayedStart = r.Bool("DelayedAutoStart") == true,
                    PathName = path,
                    ProcessId = (int)pid,
                    Company = company.Trim(),
                    IsWindows = isWindows,
                    MemoryBytes = memory.GetValueOrDefault(pid),
                    SharedWith = pid > 0 ? sharing.GetValueOrDefault(pid) : 0,
                    Knowledge = knowledge,
                    Class = knowledge?.Class ?? (isWindows || company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? ServiceClass.Unclassified : ServiceClass.ThirdParty),
                };
            })
            .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string StartLabel(int value) => value switch
    {
        0 => L.T("Boot"), 1 => L.T("System"), 2 => L.T("Automatic"), 3 => L.T("Manual"), 4 => L.T("Disabled"), _ => L.T("Unknown"),
    };

    /// <summary>The reversible change offered for a cataloged optional service (registry Start value; applies after restart).</summary>
    public static Tweak? CreateChange(ServiceInfo s)
    {
        if (s.OfferedStart is not { } target || s.Knowledge is not { } k) return null;
        return new Tweak
        {
            Id = $"service:{s.Name}".ToLowerInvariant(),
            Name = L.F("Set {0} to {1}", s.DisplayName, StartLabel(target)),
            Category = TweakCategory.Services,
            Description = k.Why,
            WhatItChanges = L.F("Changes the startup type of the service {0} from {1} to {2}. The service is not deleted.", s.Name, StartLabel(s.StartValue), StartLabel(target)),
            WhyItHelps = k.Benefit,
            ExpectedImpact = k.Benefit,
            Disadvantages = k.Drawback,
            TechnicalDetails = $@"HKLM\SYSTEM\CurrentControlSet\Services\{s.Name} › Start = {target} (was {s.StartValue})",
            Source = "https://learn.microsoft.com/windows/application-management/per-user-services-in-windows",
            Risk = k.Risk,
            Benefit = BenefitLevel.Low,
            Restart = RestartRequirement.Restart,
            Actions = [Reg.ServiceStart(s.Name, target)],
            CompatibilityCheck = k.Compatible,
            RecommendationCheck = k.Recommend,
        };
    }

    /// <summary>Reversible "disable this service" used by the Security Center for a service flagged as suspicious.</summary>
    public static Tweak CreateDisable(string serviceName, string reason) => new()
    {
        Id = $"service:{serviceName}".ToLowerInvariant(),
        Name = L.F("Disable the service {0}", serviceName),
        Category = TweakCategory.Security,
        Description = reason,
        WhatItChanges = L.F("Sets the startup type of {0} to Disabled. The service and its files are kept.", serviceName),
        WhyItHelps = L.T("Prevents a suspicious service from starting with Windows while you investigate."),
        ExpectedImpact = L.T("Takes effect at the next restart."),
        Disadvantages = L.T("If the service belongs to software you use, that software stops working until you undo this."),
        TechnicalDetails = $@"HKLM\SYSTEM\CurrentControlSet\Services\{serviceName} › Start = 4",
        Source = L.T("Windows service configuration (registry Start value)"),
        Risk = RiskLevel.Moderate,
        Benefit = BenefitLevel.Medium,
        Restart = RestartRequirement.Restart,
        Actions = [Reg.ServiceStart(serviceName, 4)],
    };
}
