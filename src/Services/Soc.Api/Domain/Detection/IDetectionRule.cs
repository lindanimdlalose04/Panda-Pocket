using Npgsql;
using PandaPocket.Services.Soc.Persistence;

namespace PandaPocket.Services.Soc.Domain.Detection;

/// <summary>
/// One occurrence of a rule's condition being true.
///
/// A rule returns hits, not alerts. Whether a hit becomes a new alert or extends
/// one that is already open is the engine's decision, and keeping that out of the
/// rules means every rule gets suppression for free and none of them can get it
/// subtly wrong.
/// </summary>
/// <param name="SuppressionKey">
/// What the finding is about, within this rule. Two hits with the same key are
/// the same ongoing finding: one address still probing, not a second attack.
/// </param>
/// <param name="EventIds">
/// Every event that made the condition true. These become the alert's
/// relatedEvents, and in the graph they are the edges that answer "why".
/// </param>
public sealed record RuleHit(
    string SuppressionKey,
    string Description,
    IReadOnlyList<Guid> EventIds,
    string? SourceIp = null,
    Guid? UserId = null,
    string? AffectedService = null,
    string? AffectedEntity = null);

/// <summary>
/// A detection rule.
///
/// Every rule is a SQL query, deliberately. A detection condition is an
/// aggregate over a time window ("five failures from one address in five
/// minutes"), which is what SQL is for, and writing them in LINQ would put a
/// translation layer between the rule and the thing the report has to document.
/// Today's summary endpoint already failed at run time because a grouping could
/// not be translated; a rule failing that way would fail silently instead, and a
/// detection rule that quietly stops detecting is worse than no rule.
///
/// Thresholds and windows come from the database row rather than the code, so
/// the documented condition and the executed one cannot drift.
/// </summary>
public interface IDetectionRule
{
    string RuleId { get; }

    Task<IReadOnlyList<RuleHit>> EvaluateAsync(
        NpgsqlConnection connection,
        DetectionRuleRecord config,
        DateTime now,
        CancellationToken ct);
}
