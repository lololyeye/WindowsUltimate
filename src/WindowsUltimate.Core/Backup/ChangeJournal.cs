using System.IO.Compression;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.Core.Backup;

public enum EntryStatus
{
    /// <summary>Original state saved, change not yet confirmed (if seen after a crash, rollback is still safe).</summary>
    Pending,
    Applied,
    Failed,
    RolledBack,
    RollbackFailed,
    /// <summary>Recorded for transparency only: the operation cannot be undone by the application.</summary>
    Irreversible,
}

public sealed class JournalEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public string TweakId { get; set; } = "";
    public string TweakName { get; set; } = "";
    public string ActionKey { get; set; } = "";
    public string Description { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public ActionSnapshot? Original { get; set; }
    public EntryStatus Status { get; set; }
    public string? Error { get; set; }
    public string? RestoreHint { get; set; }
    public DateTime? RolledBackUtc { get; set; }

    public bool CanRollBack => Original is not null && Status is EntryStatus.Applied or EntryStatus.Pending or EntryStatus.RollbackFailed;
}

public sealed class JournalSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public string Title { get; set; } = "";
    public string Origin { get; set; } = "";
    public bool Elevated { get; set; } = Elevation.IsElevated;
    public string? RestorePoint { get; set; }
    public List<JournalEntry> Entries { get; set; } = [];

    public bool HasRollbackableEntries => Entries.Any(e => e.CanRollBack);
}

/// <summary>
/// Write-ahead transaction log. The original value of every setting is persisted to disk
/// before the setting is modified, so a crash or power loss never loses the ability to undo.
/// </summary>
public sealed class ChangeJournal
{
    private readonly object _gate = new();
    private readonly List<JournalSession> _sessions = [];

    public event EventHandler? Changed;

    public ChangeJournal()
    {
        foreach (var file in Directory.EnumerateFiles(AppPaths.Journal, "*.json"))
        {
            if (JsonStore.Load<JournalSession>(file) is { } s) _sessions.Add(s);
        }
        _sessions.Sort((a, b) => a.StartedUtc.CompareTo(b.StartedUtc));
    }

    public IReadOnlyList<JournalSession> Sessions
    {
        get { lock (_gate) return _sessions.ToList(); }
    }

    public JournalSession Begin(string title, string origin)
    {
        var s = new JournalSession { Title = title, Origin = origin };
        lock (_gate) _sessions.Add(s);
        Save(s);
        Log.Audit($"Session started: {title} ({origin}) {s.Id}");
        return s;
    }

    public void Save(JournalSession session)
    {
        lock (_gate)
        {
            if (session.Entries.Count == 0 && session.RestorePoint is null)
            {
                // Empty sessions are not worth keeping on disk.
                File.Delete(PathFor(session));
            }
            else JsonStore.Save(PathFor(session), session);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops a session from memory and disk if nothing was recorded in it.</summary>
    public void DiscardIfEmpty(JournalSession session)
    {
        if (session.Entries.Count > 0 || session.RestorePoint is not null) return;
        lock (_gate) _sessions.Remove(session);
        File.Delete(PathFor(session));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records an operation that cannot be undone (app removal, file cleanup, network reset).</summary>
    public void RecordIrreversible(string title, string origin, IEnumerable<(string Name, string Description, string? RestoreHint)> items)
    {
        var s = new JournalSession { Title = title, Origin = origin };
        foreach (var (name, description, hint) in items)
        {
            s.Entries.Add(new JournalEntry { TweakId = origin, TweakName = name, Description = description, Status = EntryStatus.Irreversible, RestoreHint = hint });
        }
        if (s.Entries.Count == 0) return;
        lock (_gate) _sessions.Add(s);
        Save(s);
        Log.Audit($"{title}: {string.Join("; ", s.Entries.Select(e => e.TweakName))}");
    }

    /// <summary>Entries currently in effect for a tweak, newest first.</summary>
    public List<(JournalSession Session, JournalEntry Entry)> ActiveEntries(string? tweakId = null)
    {
        lock (_gate)
        {
            return _sessions
                .SelectMany(s => s.Entries.Select(e => (s, e)))
                .Where(x => x.e.CanRollBack && (tweakId is null || x.e.TweakId == tweakId))
                .OrderByDescending(x => x.e.TimeUtc)
                .ToList();
        }
    }

    public bool HasActiveChanges(string tweakId) => ActiveEntries(tweakId).Count > 0;

    private static string PathFor(JournalSession s) =>
        Path.Combine(AppPaths.Journal, $"{s.StartedUtc:yyyyMMdd-HHmmss}-{s.Id:N}.json");

    /// <summary>Exports every journal file, profile and setting into a single zip in the exports folder.</summary>
    public string Export(string? destination = null)
    {
        var target = destination ?? Path.Combine(AppPaths.Exports, $"WindowsUltimate-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var f in Directory.EnumerateFiles(AppPaths.Journal, "*.json"))
            zip.CreateEntryFromFile(f, "journal/" + Path.GetFileName(f));
        foreach (var f in new[] { AppPaths.ProfilesFile, AppPaths.ProfileStateFile, AppPaths.SettingsFile, AppPaths.BenchmarksFile, AppPaths.CleanupHistoryFile }.Where(File.Exists))
            zip.CreateEntryFromFile(f, Path.GetFileName(f));
        Log.Audit($"Backup exported to {target}");
        return target;
    }
}
