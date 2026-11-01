using WindowsUltimate.Core.Hardware;
using WindowsUltimate.Core.Infrastructure;

namespace WindowsUltimate.Core.Optimization;

public enum RiskLevel { Safe, LowRisk, Moderate, Advanced, Dangerous }

public enum BenefitLevel { None, Low, Medium, High }

public enum RestartRequirement { None, SignOut, Restart }

public enum TweakCategory { Gaming, Power, Responsiveness, Visual, Privacy, ConsumerFeatures, Network, Security, Input, Startup, Services }

public enum TweakState { Applied, NotApplied, Partial, Unknown }

/// <summary>Whether a tweak can run on this PC at all.</summary>
public sealed record Compatibility(bool IsCompatible, string? Reason = null)
{
    public static readonly Compatibility Yes = new(true);
    public static Compatibility No(string reason) => new(false, L.T(reason));
}

/// <summary>Whether the engine recommends the tweak for this specific hardware/configuration, and why.</summary>
public sealed record Recommendation(bool IsRecommended, string Reason)
{
    public static Recommendation Yes(string reason) => new(true, L.T(reason));
    public static Recommendation No(string reason) => new(false, L.T(reason));
}

/// <summary>
/// One documented optimization. Every tweak is a list of reversible actions plus the metadata
/// the user needs to make an informed decision. Nothing is applied without going through
/// <see cref="OptimizationEngine"/>, which journals the original state first.
/// </summary>
public sealed class Tweak
{
    public required string Id { get; init; }
    public required string Name { get => L.T(field); init; }
    public required TweakCategory Category { get; init; }

    /// <summary>One-line plain-language summary.</summary>
    public required string Description { get => L.T(field); init; }

    /// <summary>What exactly is changed on the system.</summary>
    public required string WhatItChanges { get => L.T(field); init; }

    public required string WhyItHelps { get => L.T(field); init; }
    public required string ExpectedImpact { get => L.T(field); init; }
    public required string Disadvantages { get => L.T(field); init; }
    public required string TechnicalDetails { get; init; }

    /// <summary>Documentation the tweak is based on (Microsoft docs wherever possible).</summary>
    public required string Source { get => L.T(field); init; }

    public RiskLevel Risk { get; init; } = RiskLevel.Safe;
    public BenefitLevel Benefit { get; init; } = BenefitLevel.Low;
    public RestartRequirement Restart { get; init; } = RestartRequirement.None;

    /// <summary>Tweaks in the same group are mutually exclusive (e.g. power plans).</summary>
    public string? ConflictGroup { get; init; }

    public required IReadOnlyList<IChangeAction> Actions { get; init; }

    public Func<SystemProfile, Compatibility>? CompatibilityCheck { get; init; }
    public Func<SystemProfile, Recommendation>? RecommendationCheck { get; init; }

    public bool RequiresAdmin => Actions.Any(a => a.RequiresAdmin);
    public bool IsReversible => Actions.All(a => a.IsReversible);

    public string UndoDescription => IsReversible
        ? L.T("Windows Ultimate saves the original values before applying and restores them exactly from History & Rollback.")
        : L.T("This change cannot be automatically reverted.");

    public Compatibility CheckCompatibility(SystemProfile p) => CompatibilityCheck?.Invoke(p) ?? Compatibility.Yes;

    public Recommendation Recommend(SystemProfile p) =>
        RecommendationCheck?.Invoke(p) ?? Recommendation.No("Optional — useful depending on your preferences.");

    public TweakState Evaluate()
    {
        try
        {
            var verified = Actions.Count(a => a.IsInDesiredState());
            return verified == Actions.Count ? TweakState.Applied : verified == 0 ? TweakState.NotApplied : TweakState.Partial;
        }
        catch
        {
            return TweakState.Unknown;
        }
    }
}
