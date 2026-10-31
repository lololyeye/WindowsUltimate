using System.Diagnostics.Eventing.Reader;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Diagnostics;

public sealed record EventItem(DateTime Time, string Provider, int EventId, string Summary);

public sealed class EventCategory
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Explanation { get; init; }
    public List<EventItem> Items { get; } = [];
    public int Count => Items.Count;
    public DateTime? Last => Items.Count == 0 ? null : Items.Max(i => i.Time);
    public bool Readable { get; set; } = true;
}

/// <summary>Reads the System and Application event logs for stability problems. Read-only; works without admin.</summary>
public static class EventLogScanner
{
    private sealed record Query(string Key, string Name, string Explanation, string Log, string Filter, Func<EventRecord, string>? Summarize = null);

    private static readonly Query[] Queries =
    [
        new("shutdown", "Unexpected shutdowns",
            "The PC turned off or rebooted without shutting down cleanly (power loss, hard freeze, PSU or overheating).",
            "System", "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or (Provider[@Name='EventLog'] and EventID=6008)"),
        new("bsod", "Blue screens (bug checks)",
            "Windows stopped with a STOP error. Usually caused by a driver, unstable overclock/XMP, or faulty RAM.",
            "System", "Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001",
            e => e.Properties.Count > 0 ? $"Bug check: {e.Properties[0].Value}" : "Bug check"),
        new("whea", "Hardware errors (WHEA)",
            "The CPU, memory or PCIe bus reported a hardware error. Repeated entries point to unstable overclocks, XMP/EXPO, or failing hardware.",
            "System", "Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2 or Level=3)"),
        new("tdr", "Graphics driver resets",
            "The display driver stopped responding and was recovered (screen goes black for a moment). Often an unstable GPU overclock, overheating, or a driver issue.",
            "System", "(Provider[@Name='Display'] and EventID=4101) or (Provider[@Name='nvlddmkm'] and (EventID=13 or EventID=14 or EventID=153))"),
        new("disk", "Disk and file system errors",
            "Storage reported bad blocks, timeouts or file system corruption. Back up your data and check disk health.",
            "System", "(Provider[@Name='disk'] and (EventID=7 or EventID=51 or EventID=153)) or (Provider[@Name='Ntfs'] and (EventID=55 or EventID=98)) or (Provider[@Name='stornvme'] and EventID=11)"),
        new("appcrash", "Application crashes",
            "Programs that closed because of an unhandled error.",
            "Application", "Provider[@Name='Application Error'] and EventID=1000",
            e => e.Properties.Count > 0 ? $"{e.Properties[0].Value} crashed" + (e.Properties.Count > 3 ? $" in {e.Properties[3].Value}" : "") : "Application crash"),
        new("apphang", "Application hangs",
            "Programs that stopped responding and were closed.",
            "Application", "Provider[@Name='Application Hang'] and EventID=1002",
            e => e.Properties.Count > 0 ? $"{e.Properties[0].Value} stopped responding" : "Application hang"),
    ];

    public static List<EventCategory> Scan(int days = 30, int maxPerCategory = 100)
    {
        var ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
        var result = new List<EventCategory>();
        foreach (var q in Queries)
        {
            var cat = new EventCategory { Key = q.Key, Name = q.Name, Explanation = q.Explanation };
            result.Add(cat);
            var xpath = $"*[System[({q.Filter}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(q.Log, PathType.LogName, xpath) { ReverseDirection = true });
                for (var e = reader.ReadEvent(); e is not null && cat.Items.Count < maxPerCategory; e = reader.ReadEvent())
                {
                    using (e)
                    {
                        string summary;
                        try { summary = q.Summarize?.Invoke(e) ?? e.ProviderName + " event " + e.Id; }
                        catch { summary = e.ProviderName + " event " + e.Id; }
                        cat.Items.Add(new EventItem(e.TimeCreated ?? DateTime.MinValue, e.ProviderName, e.Id, summary));
                    }
                }
            }
            catch (Exception ex)
            {
                cat.Readable = false;
                Log.Warn($"Event query {q.Key} failed: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Last boot duration from Diagnostics-Performance (event 100). Usually needs admin rights to read.</summary>
    public static TimeSpan? LastBootDuration()
    {
        try
        {
            var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[EventID=100]]") { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            using var e = reader.ReadEvent();
            if (e is null) return null;
            // Property 5 is BootTime in milliseconds for event 100.
            return e.Properties.Count > 5 && e.Properties[5].Value is { } v ? TimeSpan.FromMilliseconds(Convert.ToDouble(v)) : null;
        }
        catch
        {
            return null;
        }
    }
}
