using Microsoft.Win32;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;
using WindowsUltimate.Core.Startup;
using WindowsUltimate.Core.SystemServices;

namespace WindowsUltimate.Core.Security;

/// <summary>
/// Turns a finding's suggested remedy into an ordinary journaled tweak, so it goes through the same consent dialog,
/// restore point and History undo as every other change. Nothing here runs on its own.
/// </summary>
public static class SecurityRemedies
{
    /// <summary>True when the remedy is a reversible system change (as opposed to opening a page or asking Defender).</summary>
    public static bool IsChange(RemedyKind k) => k is RemedyKind.Quarantine or RemedyKind.DisableStartup or RemedyKind.DisableTask
        or RemedyKind.DisableService or RemedyKind.RemoveRegistryValue or RemedyKind.RestoreRegistryValue;

    public static Tweak? CreateTweak(SecurityFinding f)
    {
        var d = f.RemedyData;
        switch (f.Remedy)
        {
            case RemedyKind.Quarantine when f.FilePath is { } file && File.Exists(file):
                return QuarantineFileAction.CreateTweak(file, f.Title);

            case RemedyKind.DisableStartup:
            {
                var source = d.GetValueOrDefault("source");
                var name = d.GetValueOrDefault("name");
                var entry = StartupManager.List().FirstOrDefault(e => e.Source.ToString() == source && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                return entry is { Enabled: true } ? StartupManager.CreateToggle(entry, false) : null;
            }

            case RemedyKind.DisableTask:
            {
                var path = d.GetValueOrDefault("path");
                var name = d.GetValueOrDefault("name");
                var task = ScheduledTasks.List(includeMicrosoft: true)
                    .FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) && path is null);
                return task is { Enabled: true } ? ScheduledTasks.CreateToggle(task, false) : null;
            }

            case RemedyKind.DisableService when d.GetValueOrDefault("name") is { Length: > 0 } svc:
                return ServiceManager.CreateDisable(svc, f.Title);

            case RemedyKind.RemoveRegistryValue when Registry(d) is { } r:
                return Build(f, $"regdel:{r.Hive}\\{r.Path}\\{r.Name}", new RegistryDeleteValueAction(r.Hive, r.Path, r.Name),
                    L.F("Deletes the registry value {0} (the exact value is saved first so it can be restored).", $"{r.Hive}\\{r.Path}\\{r.Name}"));

            case RemedyKind.RestoreRegistryValue when Registry(d) is { } r && d.TryGetValue("value", out var value):
            {
                var dword = d.GetValueOrDefault("kind") == "DWord";
                object v = dword ? int.Parse(value) : value;
                var kind = dword ? RegistryValueKind.DWord : RegistryValueKind.String;
                return Build(f, $"regfix:{r.Hive}\\{r.Path}\\{r.Name}", new RegistryValueAction(r.Hive, r.Path, r.Name, v, kind),
                    L.F("Sets {0} back to the Windows default: {1}.", $"{r.Hive}\\{r.Path}\\{r.Name}", value));
            }
        }
        return null;
    }

    private static (RegistryHive Hive, string Path, string Name)? Registry(Dictionary<string, string> d) =>
        Enum.TryParse<RegistryHive>(d.GetValueOrDefault("hive"), out var hive) && d.GetValueOrDefault("path") is { Length: > 0 } path && d.GetValueOrDefault("name") is { } name
            ? (hive, path, name) : null;

    private static Tweak Build(SecurityFinding f, string id, IChangeAction action, string what) => new()
    {
        Id = id.ToLowerInvariant(),
        Name = f.Title,
        Category = TweakCategory.Security,
        Description = f.Recommendation,
        WhatItChanges = what,
        WhyItHelps = L.T("Removes a setting that makes Windows run something it would not run by default."),
        ExpectedImpact = L.T("The program that relied on this setting no longer starts through it."),
        Disadvantages = L.T("If you set this up yourself on purpose, it stops working. Undo it from History."),
        TechnicalDetails = action.Key,
        Source = L.T("Windows Ultimate Security Center (journaled, reversible)"),
        Risk = RiskLevel.Moderate,
        Benefit = BenefitLevel.Medium,
        Actions = [action],
    };
}
