using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;

namespace WindowsUltimate.Core.Startup;

public sealed class ScheduledTaskInfo
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Enabled { get; set; }
    public string State { get; init; } = "";
    public string Author { get; init; } = "";
    public string Actions { get; init; } = "";
    public string Triggers { get; init; } = "";
    public bool RunsAtStartup { get; init; }
    public bool IsMicrosoft { get; init; }
    public DateTime? LastRun { get; init; }
    public DateTime? NextRun { get; init; }
}

/// <summary>Enumerates Task Scheduler through its COM API (no output parsing, language-independent).</summary>
public static class ScheduledTasks
{
    public static List<ScheduledTaskInfo> List(bool includeMicrosoft)
    {
        var result = new List<ScheduledTaskInfo>();
        try
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t is null) return result;
            dynamic svc = Activator.CreateInstance(t)!;
            svc.Connect();
            Walk(svc.GetFolder("\\"), result, includeMicrosoft);
        }
        catch (Exception ex)
        {
            Log.Warn($"Task Scheduler enumeration failed: {ex.Message}");
        }
        return result.OrderBy(r => r.IsMicrosoft).ThenByDescending(r => r.RunsAtStartup).ThenBy(r => r.Path).ToList();
    }

    private static void Walk(dynamic folder, List<ScheduledTaskInfo> result, bool includeMicrosoft)
    {
        string folderPath = folder.Path;
        var isMsFolder = folderPath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase);
        if (isMsFolder && !includeMicrosoft) return;

        try
        {
            foreach (dynamic task in folder.GetTasks(1)) // TASK_ENUM_HIDDEN
            {
                try { result.Add(Read(task, isMsFolder)); }
                catch { /* task with unreadable definition */ }
            }
            foreach (dynamic sub in folder.GetFolders(0)) Walk(sub, result, includeMicrosoft);
        }
        catch (Exception ex)
        {
            Log.Warn($"Folder {folderPath}: {ex.Message}");
        }
    }

    private static ScheduledTaskInfo Read(dynamic task, bool isMsFolder)
    {
        dynamic def = task.Definition;
        var triggers = new List<string>();
        var atStartup = false;
        foreach (dynamic trig in def.Triggers)
        {
            int type = trig.Type;
            atStartup |= type is 8 or 9;
            triggers.Add(type switch
            {
                0 => L.T("On event"), 1 => L.T("One time"), 2 => L.T("Daily"), 3 => L.T("Weekly"), 4 or 5 => L.T("Monthly"),
                6 => L.T("On idle"), 7 => L.T("On registration"), 8 => L.T("At startup"), 9 => L.T("At log on"), 11 => L.T("On session change"), _ => L.T("Other"),
            });
        }
        var actions = new List<string>();
        foreach (dynamic a in def.Actions)
        {
            if ((int)a.Type == 0) actions.Add($"{a.Path} {a.Arguments}".Trim());
            else actions.Add("COM handler");
        }
        string author = def.RegistrationInfo.Author ?? "";
        DateTime last = task.LastRunTime, next = task.NextRunTime;
        return new ScheduledTaskInfo
        {
            Path = task.Path,
            Name = task.Name,
            Enabled = task.Enabled,
            State = (int)task.State switch { 1 => L.T("Disabled"), 2 => L.T("Queued"), 3 => L.T("Ready"), 4 => L.T("Running"), _ => L.T("Unknown") },
            Author = author,
            Actions = string.Join(" | ", actions),
            Triggers = string.Join(", ", triggers.Distinct()),
            RunsAtStartup = atStartup,
            IsMicrosoft = isMsFolder || author.Contains("Microsoft", StringComparison.OrdinalIgnoreCase),
            LastRun = last.Year < 2000 ? null : last,
            NextRun = next.Year < 2000 ? null : next,
        };
    }

    public static Tweak CreateToggle(ScheduledTaskInfo task, bool enable) => new()
    {
        Id = $"task:{task.Path}",
        Name = $"{(enable ? "Enable" : "Disable")} scheduled task \"{task.Name}\"",
        Category = TweakCategory.Startup,
        Description = $"{(enable ? "Allows" : "Prevents")} the task {task.Path} from running.",
        WhatItChanges = L.T("Sets the task's Enabled flag in Task Scheduler. The task is not deleted."),
        WhyItHelps = L.T("Updaters and helpers that run at log on add work to every startup."),
        ExpectedImpact = L.T("Depends on the task."),
        Disadvantages = L.T("Updaters that are disabled stop keeping their program up to date."),
        TechnicalDetails = $"Task Scheduler › {task.Path} › Enabled = {enable}",
        Source = L.T("Task Scheduler COM API (ITaskService / IRegisteredTask.Enabled)"),
        Risk = task.IsMicrosoft ? RiskLevel.Advanced : RiskLevel.Safe,
        Benefit = BenefitLevel.Low,
        Actions = [new ScheduledTaskEnabledAction(task.Path, enable)],
    };
}
