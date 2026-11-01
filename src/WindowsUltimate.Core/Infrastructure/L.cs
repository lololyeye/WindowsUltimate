using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsUltimate.Core.Infrastructure;

/// <summary>
/// Translation of the engine's user-facing text (tweak documentation, findings, catalogs).
/// English is the source language and the key: <c>L.T("Turn on Game Mode")</c> returns the French text when the
/// interface is in French, and the English text when no translation exists. Logs and the change journal stay in English.
/// </summary>
public static class L
{
    private static readonly Lazy<Dictionary<string, string>> French = new(() => Load("fr"));

    /// <summary>Two-letter interface language ("en" or "fr"). Set by the app at startup and when the user switches.</summary>
    public static string Language { get; set; } = "en";

    public static string T(string english)
    {
        if (Language != "fr" || english.Length == 0) return english;
        var table = French.Value;
        if (table.TryGetValue(english, out var fr)) return fr;
        return Joined.GetOrAdd(english, s => BySentence(s, table).Text);
    }

    // Text built by joining documented pieces ("None. " + restore hint) is translated piece by piece:
    // at each sentence, the longest run of sentences that has a translation wins.
    private static readonly ConcurrentDictionary<string, string> Joined = new();
    private static readonly Regex SentenceBreak = new(@"(?<=[.!?…:])\s+", RegexOptions.Compiled);

    internal static (string Text, bool Complete) BySentence(string s, IReadOnlyDictionary<string, string> table)
    {
        var parts = SentenceBreak.Split(s.Trim());
        var output = new List<string>();
        var complete = true;
        for (var start = 0; start < parts.Length;)
        {
            var end = parts.Length;
            string? fr = null;
            for (; end > start && !table.TryGetValue(string.Join(' ', parts[start..end]), out fr); end--) { }
            if (fr is null) { output.Add(parts[start++]); complete = false; }
            else { output.Add(fr); start = end; }
        }
        return (complete || output.Count > 1 ? string.Join(' ', output) : s, complete);
    }

    public static string? TOrNull(string? english) => english is null ? null : T(english);

    /// <summary>Translates a composite-format template, then formats it with the current culture.</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    /// <summary>Translates text recorded in English (journal entries, action descriptions) for display only.</summary>
    public static string Display(string? recorded) => recorded switch
    {
        null => "",
        _ when recorded.StartsWith(ScheduledTask, StringComparison.Ordinal) => F(ScheduledTask + "{0}", recorded[ScheduledTask.Length..]),
        _ => T(recorded),
    };

    private const string ScheduledTask = "Scheduled task ";

    internal static IReadOnlyDictionary<string, string> Table(string language) => language == "fr" ? French.Value : new Dictionary<string, string>();

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"WindowsUltimate.Core.Localization.{language}.json");
        if (stream is null) return new();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }
}
