using System.Text.RegularExpressions;
using WindowsUltimate.Core.Benchmark;
using WindowsUltimate.Core.Debloat;
using WindowsUltimate.Core.Diagnostics;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;
using WindowsUltimate.Core.Repair;
using WindowsUltimate.Core.Storage;

namespace WindowsUltimate.Tests;

/// <summary>Every user-facing text of the engine's catalogs must have a French translation.</summary>
public sealed class LocalizationTests
{
    private static readonly IReadOnlyDictionary<string, string> French = L.Table("fr");

    // Properties that hold identifiers, URLs or raw technical data rather than prose.
    private static readonly HashSet<string> NotText = ["Id", "Pattern", "TechnicalDetails", "Source", "Unit", "NavigateTo", "ConflictGroup", "FileName", "Arguments", "CommandLine", "Duration"];

    private static IEnumerable<(string Where, string Text)> Texts(object item, string where) =>
        item.GetType().GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0 && !NotText.Contains(p.Name))
            .Select(p => ($"{where}.{p.Name}", p.GetValue(item) as string ?? ""))
            .Where(x => Regex.IsMatch(x.Item2, "[a-z]{2}"));

    private static bool Translated(string english) => French.ContainsKey(english) || L.BySentence(english, French).Complete;

    [Fact]
    public void FrenchTable_CoversCatalogs()
    {
        var texts = new List<(string, string)>();
        foreach (var t in TweakCatalog.Create()) texts.AddRange(Texts(t, t.Id));
        foreach (var m in Myths.All) texts.AddRange(Texts(m, "myth"));
        foreach (var e in DebloatCatalog.Entries) texts.AddRange(Texts(e, e.Pattern));
        foreach (var r in RepairTools.All) texts.AddRange(Texts(r, r.Id));
        foreach (var b in BenchmarkRunner.Metrics) texts.AddRange(Texts(b, "metric"));
        foreach (var c in CleanupService.Categories().Where(c => !c.Id.StartsWith("browser.") || c.Id == "browser.firefox")) texts.AddRange(Texts(c, c.Id));
        texts.Add(("methodology", ScanReport.Methodology));

        var missing = texts.Where(x => !Translated(x.Item2)).Select(x => $"{x.Item1}: {x.Item2}").Distinct().ToList();
        Assert.True(missing.Count == 0, $"{missing.Count} untranslated:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void FrenchTable_KeepsPlaceholders()
    {
        var placeholder = new Regex(@"\{\d+(:[^}]*)?\}");
        foreach (var (en, fr) in French)
            Assert.Equal(placeholder.Matches(en).Select(m => m.Value).Order(), placeholder.Matches(fr).Select(m => m.Value).Order());
    }
}
