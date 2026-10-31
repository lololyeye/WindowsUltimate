using WindowsUltimate.Core.Backup;
using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Optimization;

public enum ApplyOutcome { Applied, AlreadyApplied, SkippedNeedsAdmin, SkippedIncompatible, Failed }

public sealed record TweakResult(Tweak Tweak, ApplyOutcome Outcome, string Message);

public sealed record ApplySummary(Guid? SessionId, IReadOnlyList<TweakResult> Results)
{
    public int AppliedCount => Results.Count(r => r.Outcome == ApplyOutcome.Applied);
    public int FailedCount => Results.Count(r => r.Outcome == ApplyOutcome.Failed);
    public RestartRequirement Restart => Results.Where(r => r.Outcome == ApplyOutcome.Applied).Select(r => r.Tweak.Restart).DefaultIfEmpty(RestartRequirement.None).Max();
}

public sealed record RollbackSummary(int Restored, int Failed, IReadOnlyList<string> Messages);

public sealed record TweakEvaluation(Tweak Tweak, TweakState State, Compatibility Compatibility, Recommendation Recommendation)
{
    /// <summary>Ranking score: real benefit first, then lower risk. Dangerous tweaks never rank.</summary>
    public int Score => Tweak.Risk == RiskLevel.Dangerous ? -1 : (int)Tweak.Benefit * 10 - (int)Tweak.Risk * 4 + (Recommendation.IsRecommended ? 20 : 0);

    /// <summary>Eligible for "apply safe recommendations": recommended, compatible, safe and not yet applied.</summary>
    public bool IsSafeRecommendation =>
        Recommendation.IsRecommended && Compatibility.IsCompatible && Tweak.Risk == RiskLevel.Safe && State != TweakState.Applied && Tweak.IsReversible;
}

/// <summary>
/// The heart of the application: evaluates tweaks against the detected system, applies them
/// transactionally with a write-ahead journal, verifies the result and rolls back on failure or on request.
/// </summary>
public sealed class OptimizationEngine(ChangeJournal journal, IReadOnlyList<Tweak> catalog)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ChangeJournal Journal => journal;
    public IReadOnlyList<Tweak> Catalog => catalog;

    public Tweak? Find(string id) => catalog.FirstOrDefault(t => t.Id == id);

    public TweakEvaluation Evaluate(Tweak t, SystemProfile profile)
    {
        var compat = t.CheckCompatibility(profile);
        var state = t.Evaluate();
        var rec = compat.IsCompatible ? t.Recommend(profile) : Recommendation.No(compat.Reason ?? "Not compatible with this PC.");
        return new TweakEvaluation(t, state, compat, rec);
    }

    public List<TweakEvaluation> EvaluateAll(SystemProfile profile, Func<Tweak, bool>? filter = null) =>
        catalog.Where(filter ?? (_ => true)).Select(t => Evaluate(t, profile)).ToList();

    /// <summary>Recommendations ordered by usefulness and risk. Excludes applied, incompatible and dangerous tweaks.</summary>
    public List<TweakEvaluation> Rank(SystemProfile profile) =>
        EvaluateAll(profile)
            .Where(e => e.Compatibility.IsCompatible && e.State != TweakState.Applied && e.Recommendation.IsRecommended && e.Tweak.Risk < RiskLevel.Dangerous)
            .OrderByDescending(e => e.Score)
            .ToList();

    public async Task<ApplySummary> ApplyAsync(IEnumerable<Tweak> tweaks, string title, string origin, SystemProfile? profile = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ApplyCore(tweaks.ToList(), title, origin, profile, progress, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private ApplySummary ApplyCore(List<Tweak> tweaks, string title, string origin, SystemProfile? profile, IProgress<string>? progress, CancellationToken ct)
    {
        var session = journal.Begin(title, origin);
        var results = new List<TweakResult>();

        foreach (var tweak in tweaks)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Applying: {tweak.Name}");

            if (profile is not null && tweak.CheckCompatibility(profile) is { IsCompatible: false } c)
            {
                results.Add(new(tweak, ApplyOutcome.SkippedIncompatible, c.Reason ?? "Not compatible"));
                continue;
            }
            if (tweak.RequiresAdmin && !Elevation.IsElevated)
            {
                results.Add(new(tweak, ApplyOutcome.SkippedNeedsAdmin, "Requires administrator privileges"));
                continue;
            }
            if (tweak.Risk == RiskLevel.Dangerous)
            {
                results.Add(new(tweak, ApplyOutcome.Failed, "Dangerous operations are never applied by the engine."));
                continue;
            }

            // Mutually exclusive tweaks: undo the other members of the group first so their originals are restored cleanly.
            if (tweak.ConflictGroup is { } group)
            {
                foreach (var other in catalog.Where(o => o.ConflictGroup == group && o.Id != tweak.Id && journal.HasActiveChanges(o.Id)))
                    RollbackCore(journal.ActiveEntries(other.Id));
            }

            results.Add(ApplyOne(tweak, session));
        }

        journal.Save(session);
        journal.DiscardIfEmpty(session);
        var summary = new ApplySummary(session.Entries.Count > 0 ? session.Id : null, results);
        Log.Info($"Apply '{title}': {summary.AppliedCount} applied, {summary.FailedCount} failed, {results.Count} total");
        return summary;
    }

    private TweakResult ApplyOne(Tweak tweak, JournalSession session)
    {
        var done = new List<(IChangeAction Action, JournalEntry Entry)>();
        try
        {
            foreach (var action in tweak.Actions)
            {
                if (action.IsInDesiredState()) continue; // nothing to do, nothing to record

                var entry = new JournalEntry
                {
                    TweakId = tweak.Id,
                    TweakName = tweak.Name,
                    ActionKey = action.Key,
                    Description = action.Describe(),
                    OldValue = action.DescribeCurrent(),
                    NewValue = action.DescribeDesired(),
                    Original = action.Capture(),
                    Status = EntryStatus.Pending,
                };
                session.Entries.Add(entry);
                journal.Save(session); // write-ahead: original state is on disk before we touch anything

                action.Apply();
                if (!action.IsInDesiredState())
                    throw new InvalidOperationException($"Verification failed for {action.Describe()} (Windows may be enforcing this setting through policy).");

                entry.Status = EntryStatus.Applied;
                journal.Save(session);
                done.Add((action, entry));
                Log.Audit($"[{tweak.Id}] {entry.Description}: {entry.OldValue} -> {entry.NewValue}");
            }

            return done.Count == 0
                ? new(tweak, ApplyOutcome.AlreadyApplied, "Already configured — nothing changed.")
                : new(tweak, ApplyOutcome.Applied, $"{done.Count} setting(s) changed and verified.");
        }
        catch (Exception ex)
        {
            Log.Error($"Applying {tweak.Id} failed; rolling back its partial changes", ex);
            var pending = session.Entries.LastOrDefault(e => e.TweakId == tweak.Id && e.Status == EntryStatus.Pending);
            if (pending is not null)
            {
                // The failing action may have partially applied: restore it as well.
                var failedAction = tweak.Actions.FirstOrDefault(a => a.Key == pending.ActionKey);
                if (failedAction is not null) done.Add((failedAction, pending));
            }
            foreach (var (action, entry) in Enumerable.Reverse(done))
            {
                try
                {
                    action.Restore(entry.Original!);
                    entry.Status = EntryStatus.RolledBack;
                    entry.RolledBackUtc = DateTime.UtcNow;
                }
                catch (Exception rex)
                {
                    entry.Status = EntryStatus.RollbackFailed;
                    entry.Error = rex.Message;
                }
            }
            if (pending is not null && pending.Status == EntryStatus.RolledBack) pending.Error = ex.Message;
            journal.Save(session);
            return new(tweak, ApplyOutcome.Failed, Friendly(ex));
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException or System.Security.SecurityException => "Access denied — this setting needs administrator rights or is locked by policy.",
        _ => ex.Message,
    };

    public Task<RollbackSummary> RollbackTweakAsync(string tweakId) => Locked(() => RollbackCore(journal.ActiveEntries(tweakId)));

    public Task<RollbackSummary> RollbackSessionAsync(Guid sessionId) => Locked(() =>
        RollbackCore(journal.ActiveEntries().Where(x => x.Session.Id == sessionId).ToList()));

    public Task<RollbackSummary> RollbackEntryAsync(Guid entryId) => Locked(() =>
        RollbackCore(journal.ActiveEntries().Where(x => x.Entry.Id == entryId).ToList()));

    /// <summary>"Undo all": reverts every change the application has made, newest first.</summary>
    public Task<RollbackSummary> RollbackAllAsync() => Locked(() => RollbackCore(journal.ActiveEntries()));

    private async Task<RollbackSummary> Locked(Func<RollbackSummary> work)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(work).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    private RollbackSummary RollbackCore(List<(JournalSession Session, JournalEntry Entry)> entries)
    {
        int ok = 0, failed = 0;
        var messages = new List<string>();
        var touched = new HashSet<JournalSession>();

        foreach (var (session, entry) in entries.OrderByDescending(x => x.Entry.TimeUtc))
        {
            var action = ResolveAction(entry);
            if (action is null)
            {
                failed++;
                messages.Add($"{entry.TweakName}: the action is no longer known to this version.");
                continue;
            }
            if (action.RequiresAdmin && !Elevation.IsElevated)
            {
                failed++;
                messages.Add($"{entry.TweakName}: requires administrator privileges to restore.");
                continue;
            }
            try
            {
                action.Restore(entry.Original!);
                entry.Status = EntryStatus.RolledBack;
                entry.RolledBackUtc = DateTime.UtcNow;
                entry.Error = null;
                ok++;
                Log.Audit($"[{entry.TweakId}] restored {entry.Description} to {entry.OldValue}");
            }
            catch (Exception ex)
            {
                entry.Status = EntryStatus.RollbackFailed;
                entry.Error = ex.Message;
                failed++;
                messages.Add($"{entry.TweakName}: {Friendly(ex)}");
                Log.Error($"Rollback failed for {entry.TweakId}/{entry.Description}", ex);
            }
            touched.Add(session);
        }

        foreach (var s in touched) journal.Save(s);
        return new RollbackSummary(ok, failed, messages);
    }

    /// <summary>Ad-hoc tweaks (startup entries, scheduled tasks) are registered so their journal entries stay restorable.</summary>
    private readonly Dictionary<string, Tweak> _dynamic = new();

    public void Register(Tweak t)
    {
        lock (_dynamic) _dynamic[t.Id] = t;
    }

    private IChangeAction? ResolveAction(JournalEntry entry)
    {
        Tweak? t;
        lock (_dynamic) _dynamic.TryGetValue(entry.TweakId, out t);
        t ??= Find(entry.TweakId);
        var action = t?.Actions.FirstOrDefault(a => a.Key == entry.ActionKey);
        return action ?? DynamicActions.Rebuild(entry);
    }
}

/// <summary>
/// Recreates actions for journal entries whose tweak is not in the static catalog (startup toggles,
/// scheduled tasks, service resets) from the data stored in the snapshot, so they remain restorable after a restart.
/// </summary>
public static class DynamicActions
{
    public static IChangeAction? Rebuild(JournalEntry e)
    {
        var key = e.ActionKey;
        if (key.StartsWith("task:", StringComparison.Ordinal) && e.Original?.Data.GetValueOrDefault("path") is { } taskPath)
            return new ScheduledTaskEnabledAction(taskPath, false);
        if (key.StartsWith("reg:", StringComparison.Ordinal) && e.Original?.Data.GetValueOrDefault("hive") is { } hive
            && e.Original.Data.GetValueOrDefault("path") is { } path && e.Original.Data.GetValueOrDefault("name") is { } name)
        {
            return new RegistryValueAction(Enum.Parse<Microsoft.Win32.RegistryHive>(hive), path, name, 0);
        }
        return null;
    }
}
