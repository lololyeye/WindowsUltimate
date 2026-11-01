using WindowsUltimate.Core.Infrastructure;
namespace WindowsUltimate.Core.Diagnostics;

public enum Severity { Good, Info, Suggestion, Warning, Critical }

/// <summary>A single observation from a scan, with the evidence that produced it and what to do about it.</summary>
public sealed class Finding
{
    public required string Id { get; init; }
    public required Severity Severity { get; init; }
    public required string Area { get => L.T(field); init; }
    public required string Title { get => L.T(field); init; }
    public required string Detail { get => L.T(field); init; }
    public string? Evidence { get; init; }

    /// <summary>Points removed from the health score. Every point is shown to the user with its reason.</summary>
    public int ScorePenalty { get; init; }

    /// <summary>Catalog tweak that fixes this finding, if any.</summary>
    public string? TweakId { get; init; }

    /// <summary>Application page that helps with this finding (e.g. "storage", "startup").</summary>
    public string? NavigateTo { get; init; }

    /// <summary>Windows settings page or official URL to resolve this finding manually.</summary>
    public string? OpenUri { get; init; }
    public string? ActionLabel { get => L.TOrNull(field); init; }
}

public sealed class ScanReport
{
    public DateTime Time { get; init; } = DateTime.Now;
    public bool IsFull { get; init; }
    public TimeSpan Duration { get; set; }
    public List<Finding> Findings { get; init; } = [];

    public int Score => Math.Clamp(100 - Findings.Sum(f => f.ScorePenalty), 0, 100);

    public IEnumerable<Finding> Penalties => Findings.Where(f => f.ScorePenalty > 0).OrderByDescending(f => f.ScorePenalty);

    public IEnumerable<Finding> Actionable => Findings
        .Where(f => f.Severity >= Severity.Suggestion)
        .OrderByDescending(f => f.Severity).ThenByDescending(f => f.ScorePenalty);

    public string ScoreLabel => Score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 55 => "Needs attention",
        _ => "Poor",
    };

    public static string Methodology => L.T(MethodologyText);

    private const string MethodologyText =
        "The score starts at 100. Each problem found removes a fixed number of points, listed next to it: " +
        "critical issues (antivirus off, failing disk, Windows Update disabled) remove 15–25 points, " +
        "warnings (low disk space, crashes, hardware errors, memory pressure) 5–15 points, and suggestions 2–5 points. " +
        "Informational items never change the score. A Quick Scan checks configuration; a Full Analysis also reads the last 30 days of event logs, drivers and disk health, so its score can be lower.";
}
