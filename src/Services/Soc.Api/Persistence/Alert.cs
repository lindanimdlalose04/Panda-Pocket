namespace PandaPocket.Services.Soc.Persistence;

/// <summary>
/// A detection rule, as stored.
///
/// The catalogue lives in the database rather than only in code for two
/// reasons. The report has to document each rule's purpose, inputs, condition,
/// severity and recommended response, and a table that already holds those is
/// one source of truth rather than a second copy that drifts. And the graph
/// loader turns each row into a (:Rule) node, so an alert can be traced to the
/// rule that raised it without the loader knowing any C#.
/// </summary>
public sealed class DetectionRuleRecord
{
    /// <summary>Short stable id, "R1" through "R7".</summary>
    public string RuleId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Comma separated event types this rule reads.</summary>
    public string InputEventTypes { get; set; } = string.Empty;

    /// <summary>The condition in words, for the report and the dashboard.</summary>
    public string Condition { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;
    public string AlertType { get; set; } = string.Empty;
    public string RecommendedAction { get; set; } = string.Empty;

    /// <summary>MITRE, CWE or OWASP reference, where one genuinely fits.</summary>
    public string? ThreatReference { get; set; }

    public int WindowMinutes { get; set; }
    public int Threshold { get; set; }

    /// <summary>
    /// How long an open alert absorbs further hits before a second one is
    /// raised. Without this a sustained attack produces one alert per evaluation
    /// cycle, which buries the finding in its own repetitions.
    /// </summary>
    public int SuppressionMinutes { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// One finding. The fields are the ones the specification names for an ALERT,
/// plus the bookkeeping that makes suppression work.
/// </summary>
public sealed class Alert
{
    public Guid AlertId { get; set; }

    /// <summary>When the pattern was first seen, not when it was last seen.</summary>
    public DateTime FirstSeenAt { get; set; }

    /// <summary>
    /// Moves forward every time the same pattern is seen again while this alert
    /// is open. An analyst needs both: when it started and whether it is still
    /// happening.
    /// </summary>
    public DateTime LastSeenAt { get; set; }

    public string RuleId { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;

    /// <summary>OPEN, ACKNOWLEDGED or CLOSED.</summary>
    public string Status { get; set; } = AlertStatus.Open;

    public string Description { get; set; } = string.Empty;
    public string RecommendedAction { get; set; } = string.Empty;

    public string? AffectedService { get; set; }
    public Guid? AffectedUserId { get; set; }
    public string? AffectedEntity { get; set; }
    public string? SourceIp { get; set; }

    /// <summary>How many events this alert now covers.</summary>
    public int EventCount { get; set; }

    /// <summary>
    /// Rule id plus the entity the finding is about, for example "R1:41.12.8.9".
    /// Two hits sharing this are the same ongoing finding rather than two.
    /// </summary>
    public string SuppressionKey { get; set; } = string.Empty;

    public Guid? IncidentId { get; set; }

    public List<AlertEventLink> Events { get; set; } = [];
}

/// <summary>
/// Which events caused an alert. This is the specification's relatedEvents, and
/// it is the edge the knowledge graph needs to answer "why was this raised".
/// </summary>
public sealed class AlertEventLink
{
    public Guid AlertId { get; set; }
    public Guid EventId { get; set; }
}

/// <summary>
/// Several alerts that are one story.
///
/// Rule R5 is the reason this exists: key probing, then a payout address change,
/// then a new key. Each alert is true on its own and none of them is the finding.
/// The incident is.
/// </summary>
public sealed class Incident
{
    public Guid IncidentId { get; set; }
    public DateTime OpenedAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Status { get; set; } = AlertStatus.Open;
    public Guid? MerchantId { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public static class AlertStatus
{
    public const string Open         = "OPEN";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Closed       = "CLOSED";
}
