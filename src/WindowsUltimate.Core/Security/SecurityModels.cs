using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Security;

public enum ThreatSeverity { Info, Low, Medium, High, Critical }

/// <summary>How sure the finding is. Only a Microsoft Defender detection is reported as High confidence.</summary>
public enum Confidence { Low, Medium, High }

public enum ThreatCategory
{
    DefenderDetection, InfoStealer, Keylogger, Backdoor, RemoteAccess, Persistence, SuspiciousScript,
    SuspiciousExecutable, Masquerading, BrowserHijack, BrowserExtension, Configuration, Unwanted,
}

/// <summary>What the user can do about a finding. Every option asks for confirmation; none runs automatically.</summary>
public enum RemedyKind { None, Quarantine, DisableStartup, DisableTask, DisableService, RemoveRegistryValue, RestoreRegistryValue, DefenderRemove, OpenSettings }

/// <summary>One security observation, worded as an indicator unless Defender confirmed it.</summary>
public sealed class SecurityFinding
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required ThreatCategory Category { get; init; }
    public required ThreatSeverity Severity { get; init; }
    public required Confidence Confidence { get; init; }

    /// <summary>File, registry location or setting concerned.</summary>
    public string Location { get; init; } = "";

    /// <summary>How it was found (the check and the evidence), in plain language.</summary>
    public required string Method { get; init; }

    public string Evidence { get; init; } = "";
    public required string Recommendation { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>File the finding points to, when there is one (used for hashing, Defender scan, quarantine).</summary>
    public string? FilePath { get; init; }
    public string? Sha256 { get; set; }

    public RemedyKind Remedy { get; init; }

    /// <summary>Data the remedy needs (startup entry name, task path, registry location, settings URI…).</summary>
    public Dictionary<string, string> RemedyData { get; init; } = [];

    /// <summary>Stable key used for "trust this item" so a dismissed finding does not come back.</summary>
    public string TrustKey => FilePath is { } p ? "file:" + p.ToLowerInvariant() : "id:" + Id;
}

public sealed class SecurityScanResult
{
    public DateTime Time { get; init; } = DateTime.Now;
    public TimeSpan Duration { get; set; }
    public List<SecurityFinding> Findings { get; } = [];
    public int ItemsReviewed { get; set; }
    public int Ignored { get; set; }
    public List<string> Limitations { get; } = [];
    public List<BrowserExtensionInfo> Extensions { get; } = [];
    public List<PersistenceItem> Persistence { get; } = [];
}

/// <summary>An autostart location entry, listed so advanced users can review everything that starts automatically.</summary>
public sealed record PersistenceItem(string Kind, string Name, string Location, string Command, string Signer, bool Flagged);

public sealed record BrowserExtensionInfo(string Browser, string Profile, string Id, string Name, string Version, string Source, IReadOnlyList<string> PowerfulPermissions)
{
    public string PermissionsText => PowerfulPermissions.Count == 0 ? "—" : string.Join(", ", PowerfulPermissions);
}

/// <summary>Paths and hashes the user marked as trusted. Stored locally; nothing is uploaded.</summary>
public static class TrustList
{
    private static string File => Path.Combine(AppPaths.Root, "security-trusted.json");

    public static HashSet<string> Load() => (JsonStore.Load<List<string>>(File) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void Add(string key)
    {
        var all = Load();
        all.Add(key);
        JsonStore.Save(File, all.Order().ToList());
    }

    public static void Clear() => JsonStore.Save(File, new List<string>());
}
