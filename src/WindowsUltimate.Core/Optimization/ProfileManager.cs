using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Optimization;

public sealed class GamingProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> TweakIds { get; set; } = [];
    public bool BuiltIn { get; set; }
    /// <summary>The "Recommended for this PC" profile is computed from the detected hardware.</summary>
    public bool IsSmart { get; set; }
}

public sealed class ProfileState
{
    public string? ActiveProfileId { get; set; }
    public Guid? ActiveSessionId { get; set; }
    public DateTime? ActivatedAt { get; set; }
}

/// <summary>
/// Gaming profiles are named sets of catalog tweaks. Switching profile first rolls back the previous
/// profile's session (restoring the user's original values) and then applies the new one.
/// </summary>
public sealed class ProfileManager(OptimizationEngine engine)
{
    private List<GamingProfile> _custom = JsonStore.Load<List<GamingProfile>>(AppPaths.ProfilesFile) ?? [];

    public ProfileState State { get; private set; } = JsonStore.Load<ProfileState>(AppPaths.ProfileStateFile) ?? new();

    public static readonly string[] SmartCategories = ["Gaming", "Power", "Input"];

    public IReadOnlyList<GamingProfile> Profiles(SystemProfile system) =>
    [
        Smart(system),
        new()
        {
            Id = "competitive", Name = "Competitive Gaming", BuiltIn = true,
            Description = "Lowest latency and fewest interruptions: Game Mode, flip-model presentation, no background recording or capture hooks, High performance power plan (desktops), raw mouse movement.",
            TweakIds = ["gaming.gamemode", "gaming.windowed", "gaming.dvr.background", "gaming.dvr.off", "power.high", "input.mouseaccel", "consumer.tips"],
        },
        new()
        {
            Id = "balanced", Name = "Balanced Gaming", BuiltIn = true,
            Description = "The safe essentials for every gamer: Game Mode, windowed-game optimizations and no background recording. Keeps your power plan.",
            TweakIds = ["gaming.gamemode", "gaming.windowed", "gaming.dvr.background"],
        },
        new()
        {
            Id = "stability", Name = "Maximum Stability", BuiltIn = true,
            Description = "Windows defaults with Game Mode on and the Balanced power plan. Use it to rule out configuration issues when troubleshooting crashes or stutter.",
            TweakIds = ["gaming.gamemode", "power.balanced"],
        },
        new()
        {
            Id = "desktop", Name = "Normal Desktop", BuiltIn = true,
            Description = "Reverts the previous gaming profile and returns to the Balanced power plan for everyday use.",
            TweakIds = ["power.balanced"],
        },
        .. _custom,
    ];

    /// <summary>Builds a profile from the engine's hardware-aware recommendations (safe and low-risk only).</summary>
    public GamingProfile Smart(SystemProfile system)
    {
        var ids = engine.EvaluateAll(system, t => SmartCategories.Contains(t.Category.ToString()))
            .Where(e => e.Compatibility.IsCompatible && e.Recommendation.IsRecommended && e.Tweak.Risk <= RiskLevel.LowRisk)
            .Select(e => e.Tweak.Id)
            .ToList();
        return new GamingProfile
        {
            Id = "smart", Name = "Recommended for this PC", BuiltIn = true, IsSmart = true,
            Description = $"Built from your hardware ({system.Cpu.Name}, {system.PrimaryGpu?.Name ?? "GPU"}{(system.IsLaptop ? ", laptop" : "")}): only safe and low-risk settings the engine recommends for this machine.",
            TweakIds = ids,
        };
    }

    public void SaveCustom(GamingProfile profile)
    {
        profile.BuiltIn = false;
        if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        _custom.RemoveAll(p => p.Id == profile.Id);
        _custom.Add(profile);
        JsonStore.Save(AppPaths.ProfilesFile, _custom);
    }

    public void DeleteCustom(string id)
    {
        _custom.RemoveAll(p => p.Id == id);
        JsonStore.Save(AppPaths.ProfilesFile, _custom);
    }

    public async Task<ApplySummary> ActivateAsync(GamingProfile profile, SystemProfile system, IProgress<string>? progress = null)
    {
        await RestorePreviousAsync().ConfigureAwait(false);
        var tweaks = profile.TweakIds.Select(engine.Find).OfType<Tweak>().ToList();
        var summary = await engine.ApplyAsync(tweaks, $"Profile: {profile.Name}", $"profile:{profile.Id}", system, progress).ConfigureAwait(false);
        State = new ProfileState { ActiveProfileId = profile.Id, ActiveSessionId = summary.SessionId, ActivatedAt = DateTime.Now };
        JsonStore.Save(AppPaths.ProfileStateFile, State);
        return summary;
    }

    /// <summary>Undoes the currently active profile, restoring the configuration that existed before it.</summary>
    public async Task<RollbackSummary?> RestorePreviousAsync()
    {
        RollbackSummary? result = null;
        if (State.ActiveSessionId is { } sid) result = await engine.RollbackSessionAsync(sid).ConfigureAwait(false);
        State = new ProfileState();
        JsonStore.Save(AppPaths.ProfileStateFile, State);
        return result;
    }
}
