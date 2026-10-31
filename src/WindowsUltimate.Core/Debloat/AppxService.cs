using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Debloat;

public enum DebloatCategory { SafeToRemove, Optional, Advanced, DoNotRemove, Unclassified }

public sealed class AppxPackageInfo
{
    public string Name { get; init; } = "";
    public string FullName { get; init; } = "";
    public string FamilyName { get; init; } = "";
    public string Version { get; init; } = "";
    public string Publisher { get; init; } = "";
    public bool IsFramework { get; init; }
    public bool NonRemovable { get; init; }
    public int SignatureKind { get; init; } // 0 None, 1 Developer, 2 Enterprise, 3 Store, 4 System

    public CatalogEntry? Catalog { get; set; }
    public DebloatCategory Category => Catalog?.Category ?? (Protected ? DebloatCategory.DoNotRemove : DebloatCategory.Unclassified);
    public string FriendlyName => Catalog?.FriendlyName ?? Name;

    /// <summary>Frameworks, system-signed and non-removable packages can never be selected.</summary>
    public bool Protected => IsFramework || NonRemovable || SignatureKind == 4 || Catalog?.Category == DebloatCategory.DoNotRemove;

    public string StoreLink => $"ms-windows-store://pdp/?PFN={FamilyName}";
}

/// <summary>
/// Lists and removes Microsoft Store (AppX/MSIX) packages for the current user through the built-in
/// Appx PowerShell module (Windows PowerShell 5.1 from System32 — no downloaded scripts).
/// </summary>
public static partial class AppxService
{
    [GeneratedRegex(@"^[A-Za-z0-9.\-_~]+$")]
    private static partial Regex SafePackageName();

    private static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    public static async Task<List<AppxPackageInfo>> ListAsync(CancellationToken ct = default)
    {
        const string script = "Get-AppxPackage | Select-Object Name,PackageFullName,PackageFamilyName,@{n='Version';e={$_.Version.ToString()}},Publisher,IsFramework,NonRemovable,@{n='SignatureKind';e={[int]$_.SignatureKind}} | ConvertTo-Json -Compress -Depth 2";
        var r = await ProcessRunner.RunAsync(PowerShell, $"-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; {script}\"",
            ct, encoding: System.Text.Encoding.UTF8, timeout: TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        if (!r.Success || string.IsNullOrWhiteSpace(r.StdOut))
            throw new InvalidOperationException("Could not list installed apps: " + r.StdErr.Trim());

        using var doc = JsonDocument.Parse(r.StdOut.Trim());
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        var list = items.Select(e => new AppxPackageInfo
        {
            Name = Str(e, "Name"),
            FullName = Str(e, "PackageFullName"),
            FamilyName = Str(e, "PackageFamilyName"),
            Version = Str(e, "Version"),
            Publisher = Str(e, "Publisher"),
            IsFramework = e.TryGetProperty("IsFramework", out var f) && f.ValueKind == JsonValueKind.True,
            NonRemovable = e.TryGetProperty("NonRemovable", out var n) && n.ValueKind == JsonValueKind.True,
            SignatureKind = e.TryGetProperty("SignatureKind", out var s) && s.TryGetInt32(out var sk) ? sk : 0,
        }).ToList();

        foreach (var p in list) p.Catalog = DebloatCatalog.Match(p.Name);
        return list.GroupBy(p => p.Name).Select(g => g.OrderByDescending(p => p.Version).First()).OrderBy(p => p.Category).ThenBy(p => p.FriendlyName).ToList();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Removes one package for the current user. Protected packages are refused regardless of caller.</summary>
    public static async Task<(bool Success, string Message)> RemoveAsync(AppxPackageInfo package, CancellationToken ct = default)
    {
        if (package.Protected) return (false, "This package is protected and cannot be removed by Windows Ultimate.");
        if (!SafePackageName().IsMatch(package.FullName)) return (false, "Unexpected package name.");

        var r = await ProcessRunner.RunAsync(PowerShell,
            $"-NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '{package.FullName}' -ErrorAction Stop\"",
            ct, encoding: System.Text.Encoding.UTF8, timeout: TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        Log.Audit($"Remove-AppxPackage {package.FullName}: exit {r.ExitCode} {r.StdErr.Trim()}");
        return r.Success
            ? (true, "Removed")
            : (false, string.IsNullOrWhiteSpace(r.StdErr) ? $"Exit code {r.ExitCode}" : r.StdErr.Trim().Split('\n')[0]);
    }
}
