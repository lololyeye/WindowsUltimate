using Microsoft.Win32;
using WindowsUltimate.Core.Backup;
using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Infrastructure;
using WindowsUltimate.Core.Optimization;
using WindowsUltimate.Core.Startup;

namespace WindowsUltimate.Tests;

/// <summary>
/// Exercises the apply → verify → journal → rollback pipeline against a private HKCU sandbox key.
/// Nothing outside HKCU\Software\WindowsUltimateTests is touched.
/// </summary>
[Collection("registry")]
public sealed class EngineTests : IDisposable
{
    private readonly string _key = $@"Software\WindowsUltimateTests\{Guid.NewGuid():N}";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "wu-tests-" + Guid.NewGuid().ToString("N"));

    public EngineTests()
    {
        AppPaths.OverrideRoot(_dataDir);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_key, false);
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private Tweak MakeTweak(string id, params IChangeAction[] actions) => new()
    {
        Id = id, Name = id, Category = TweakCategory.Gaming, Description = "t", WhatItChanges = "t", WhyItHelps = "t",
        ExpectedImpact = "t", Disadvantages = "t", TechnicalDetails = "t", Source = "test", Actions = actions,
    };

    private int? ReadInt(string name) => Registry.CurrentUser.OpenSubKey(_key)?.GetValue(name) as int?;

    [Fact]
    public void LogPurge_DeletesOnlyDailyLogsOlderThanTheRetention()
    {
        var old = Path.Combine(AppPaths.Logs, "app-20200101.log");
        var recent = Path.Combine(AppPaths.Logs, "app-recent.log");
        var other = Path.Combine(AppPaths.Logs, "notes.txt");
        foreach (var f in new[] { old, recent, other }) File.WriteAllText(f, "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-Log.RetentionDays - 2));
        File.SetLastWriteTime(other, DateTime.Now.AddDays(-Log.RetentionDays - 2));

        Log.PurgeOld();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void RegistryAction_RestoresMissingValueByDeletingItAndTheKeyItCreated()
    {
        var a = new RegistryValueAction(RegistryHive.CurrentUser, _key + @"\Sub", "Value", 7);
        var snap = a.Capture();
        a.Apply();
        Assert.True(a.IsInDesiredState());
        a.Restore(snap);
        Assert.False(a.IsInDesiredState());
        Assert.Null(Registry.CurrentUser.OpenSubKey(_key + @"\Sub"));
    }

    [Fact]
    public void RegistryAction_RestoresOriginalValueAndKind()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(_key)) k.SetValue("Mixed", "original", RegistryValueKind.ExpandString);
        var a = new RegistryValueAction(RegistryHive.CurrentUser, _key, "Mixed", 1);
        var snap = a.Capture();
        a.Apply();
        a.Restore(snap);
        using var check = Registry.CurrentUser.OpenSubKey(_key)!;
        Assert.Equal(RegistryValueKind.ExpandString, check.GetValueKind("Mixed"));
        Assert.Equal("original", check.GetValue("Mixed", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    [Fact]
    public void TokenAction_PreservesOtherTokens()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(_key)) k.SetValue("S", "VRROptimizeEnable=0;AutoHDREnable=1;");
        var a = new RegistryTokenAction(RegistryHive.CurrentUser, _key, "S", "SwapEffectUpgradeEnable", "1");
        var snap = a.Capture();
        a.Apply();
        var applied = Registry.CurrentUser.OpenSubKey(_key)!.GetValue("S") as string;
        Assert.Contains("AutoHDREnable=1;", applied);
        Assert.Contains("SwapEffectUpgradeEnable=1;", applied);
        a.Restore(snap);
        var restored = RegistryTokenAction.Tokens(Registry.CurrentUser.OpenSubKey(_key)!.GetValue("S") as string);
        Assert.False(restored.ContainsKey("SwapEffectUpgradeEnable"));
        Assert.Equal("1", restored["AutoHDREnable"]);
        Assert.Equal("0", restored["VRROptimizeEnable"]);
    }

    [Fact]
    public async Task Engine_AppliesJournalsAndRollsBack()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(_key)) k.SetValue("A", 5);
        var tweak = MakeTweak("test.a", Reg.User(_key, "A", 1), Reg.User(_key, "B", 2));
        var engine = new OptimizationEngine(new ChangeJournal(), [tweak]);

        var summary = await engine.ApplyAsync([tweak], "test", "unit");
        Assert.Equal(ApplyOutcome.Applied, summary.Results[0].Outcome);
        Assert.Equal(1, ReadInt("A"));
        Assert.Equal(2, ReadInt("B"));
        Assert.Equal(TweakState.Applied, tweak.Evaluate());

        // The journal on disk must contain the original values before we roll back.
        var reloaded = new ChangeJournal();
        Assert.Equal(2, reloaded.ActiveEntries("test.a").Count);

        var rb = await engine.RollbackTweakAsync("test.a");
        Assert.Equal(2, rb.Restored);
        Assert.Equal(5, ReadInt("A"));
        Assert.Null(ReadInt("B"));
        Assert.Equal(TweakState.NotApplied, tweak.Evaluate());
    }

    [Fact]
    public async Task Engine_DoesNotRecordAlreadyAppliedSettings()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(_key)) k.SetValue("A", 1);
        var tweak = MakeTweak("test.same", Reg.User(_key, "A", 1));
        var engine = new OptimizationEngine(new ChangeJournal(), [tweak]);
        var summary = await engine.ApplyAsync([tweak], "test", "unit");
        Assert.Equal(ApplyOutcome.AlreadyApplied, summary.Results[0].Outcome);
        Assert.Null(summary.SessionId);
        Assert.Empty(engine.Journal.ActiveEntries("test.same"));
    }

    [Fact]
    public async Task Engine_RollsBackPartialChangesWhenAnActionFails()
    {
        var tweak = MakeTweak("test.fail", Reg.User(_key, "Good", 1), new FailingAction());
        var engine = new OptimizationEngine(new ChangeJournal(), [tweak]);
        var summary = await engine.ApplyAsync([tweak], "test", "unit");
        Assert.Equal(ApplyOutcome.Failed, summary.Results[0].Outcome);
        Assert.Null(ReadInt("Good"));
        Assert.Empty(engine.Journal.ActiveEntries("test.fail"));
    }

    [Fact]
    public async Task Engine_RollsBackConflictingTweakBeforeApplyingNewOne()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(_key)) k.SetValue("Plan", 0);
        var high = new Tweak
        {
            Id = "plan.high", Name = "h", Category = TweakCategory.Power, Description = "", WhatItChanges = "", WhyItHelps = "", ExpectedImpact = "",
            Disadvantages = "", TechnicalDetails = "", Source = "", ConflictGroup = "plan", Actions = [Reg.User(_key, "Plan", 1)],
        };
        var balanced = new Tweak
        {
            Id = "plan.bal", Name = "b", Category = TweakCategory.Power, Description = "", WhatItChanges = "", WhyItHelps = "", ExpectedImpact = "",
            Disadvantages = "", TechnicalDetails = "", Source = "", ConflictGroup = "plan", Actions = [Reg.User(_key, "Plan", 2)],
        };
        var engine = new OptimizationEngine(new ChangeJournal(), [high, balanced]);
        await engine.ApplyAsync([high], "1", "unit");
        await engine.ApplyAsync([balanced], "2", "unit");
        Assert.Empty(engine.Journal.ActiveEntries("plan.high"));
        await engine.RollbackAllAsync();
        Assert.Equal(0, ReadInt("Plan")); // the user's original value, not the other tweak's
    }

    [Fact]
    public async Task Journal_EntriesStayRestorableAfterRestartForDynamicTweaks()
    {
        var dyn = MakeTweak("startup:test", Reg.User(_key, "Dyn", 3));
        var engine = new OptimizationEngine(new ChangeJournal(), []);
        engine.Register(dyn);
        await engine.ApplyAsync([dyn], "dyn", "unit");
        Assert.Equal(3, ReadInt("Dyn"));

        // Simulate an application restart: new journal + engine with no knowledge of the dynamic tweak.
        var restarted = new OptimizationEngine(new ChangeJournal(), []);
        var rb = await restarted.RollbackAllAsync();
        Assert.Equal(1, rb.Restored);
        Assert.Null(ReadInt("Dyn"));
    }

    [Fact]
    public void StartupApprovedAction_TogglesAndRestores()
    {
        var a = new StartupApprovedAction(RegistryHive.CurrentUser, _key, "App.exe", enable: false);
        Assert.False(a.IsInDesiredState()); // missing value = enabled
        var snap = a.Capture();
        a.Apply();
        Assert.True(a.IsInDesiredState());
        var bytes = (byte[])Registry.CurrentUser.OpenSubKey(_key)!.GetValue("App.exe")!;
        Assert.Equal(12, bytes.Length);
        Assert.Equal(3, bytes[0]);
        a.Restore(snap);
        Assert.Null(Registry.CurrentUser.OpenSubKey(_key)?.GetValue("App.exe"));
    }

    private sealed class FailingAction : IChangeAction
    {
        public string Key => "test:fail";
        public string Describe() => "fail";
        public string DescribeDesired() => "x";
        public string DescribeCurrent() => "y";
        public bool RequiresAdmin => false;
        public bool IsReversible => true;
        public bool IsInDesiredState() => false;
        public ActionSnapshot Capture() => new() { ActionKey = Key };
        public void Apply() => throw new InvalidOperationException("boom");
        public void Restore(ActionSnapshot snapshot) { }
    }
}
