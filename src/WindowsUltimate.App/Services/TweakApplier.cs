using WindowsUltimate.App.Dialogs;
using WindowsUltimate.App.Localization;
using WindowsUltimate.Core.Backup;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.App.Services;

/// <summary>
/// The single entry point the UI uses to change the system: consent dialog → optional restore point →
/// engine (journal + verify) → result. No page calls the engine's apply method directly.
/// </summary>
public static class TweakApplier
{
    public static async Task<ApplySummary?> ApplyAsync(IReadOnlyList<Tweak> requested, string title, string origin)
    {
        var profile = await AppServices.ProfileTask;
        var blocked = new List<(Tweak, string)>();
        var tweaks = new List<Tweak>();
        foreach (var t in requested)
        {
            var compat = t.CheckCompatibility(profile);
            if (!compat.IsCompatible) blocked.Add((t, compat.Reason ?? "Not compatible"));
            else if (t.RequiresAdmin && !Elevation.IsElevated) blocked.Add((t, Loc.Get("NeedsAdmin")));
            else if (t.Evaluate() == TweakState.Applied) continue;
            else tweaks.Add(t);
        }

        if (tweaks.Count == 0)
        {
            if (blocked.Count > 0) Notifier.Warning(string.Join(" ", blocked.Select(b => $"{b.Item1.Name}: {b.Item2}.")));
            else Notifier.Info(Loc.Get("NothingToApply"));
            return null;
        }

        var consent = ConsentDialog.Ask(title, tweaks, blocked);
        if (consent is null) return null;

        if (consent.CreateRestorePoint)
        {
            var (ok, message) = await RestorePoint.CreateAsync("Windows Ultimate: " + title);
            if (!ok) Notifier.Warning(message);
        }

        var summary = await AppServices.Engine.ApplyAsync(tweaks, title, origin, profile);
        Report(summary);
        AppServices.NotifyConfigurationChanged();
        return summary;
    }

    public static void Report(ApplySummary summary)
    {
        var failed = summary.Results.Where(r => r.Outcome == ApplyOutcome.Failed).ToList();
        var restart = summary.Restart switch
        {
            RestartRequirement.Restart => " " + Loc.Get("RestartToFinish"),
            RestartRequirement.SignOut => " " + Loc.Get("SignOutToFinish"),
            _ => "",
        };
        if (failed.Count == 0)
            Notifier.Success(string.Format(Loc.Get("AppliedVerified"), summary.AppliedCount) + restart);
        else
            Notifier.Error(string.Format(Loc.Get("AppliedWithFailures"), summary.AppliedCount, failed.Count) + " " +
                           string.Join(" ", failed.Select(f => $"{f.Tweak.Name}: {f.Message}")));
    }

    public static async Task UndoAsync(Tweak tweak)
    {
        if (!ConfirmDialog.Show(Loc.Get("UndoTitle"), string.Format(Loc.Get("UndoTweakMessage"), tweak.Name), confirmText: Loc.Get("Undo")))
            return;
        var r = await AppServices.Engine.RollbackTweakAsync(tweak.Id);
        ReportRollback(r);
        AppServices.NotifyConfigurationChanged();
    }

    public static void ReportRollback(RollbackSummary r)
    {
        if (r.Failed == 0) Notifier.Success(string.Format(Loc.Get("RestoredCount"), r.Restored));
        else Notifier.Error(string.Format(Loc.Get("RestoredWithFailures"), r.Restored, r.Failed) + " " + string.Join(" ", r.Messages));
    }
}
