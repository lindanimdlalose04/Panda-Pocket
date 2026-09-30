namespace PandaPocket.Services.Soc.Persistence;

/// <summary>
/// One collected event, as stored.
///
/// The columns are the fields the specification names, kept as real columns
/// rather than a JSON blob, because every detection rule is a filter and an
/// aggregate over them. "Count distinct merchants per source address in five
/// minutes" is an index scan against this shape and a full table scan plus JSON
/// parsing against the other one.
///
/// Whatever is genuinely specific to a single event type stays in
/// <see cref="MetadataJson"/>, which is jsonb so it is still queryable when a
/// rule needs it, without every event type adding a column that is null for the
/// other fifteen.
/// </summary>
public sealed class SocEventRecord
{
    /// <summary>Assigned by the publishing service, so a retried batch cannot duplicate.</summary>
    public Guid EventId { get; set; }

    /// <summary>When the observing service saw it.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// When this service stored it. Different from <see cref="Timestamp"/>
    /// whenever a batch was delayed, and the gap is worth keeping: a rule that
    /// fired late because telemetry lagged looks exactly like a rule that fired
    /// late because of a defect, unless both times are recorded.
    /// </summary>
    public DateTime ReceivedAt { get; set; }

    public string ServiceName { get; set; } = "unknown";
    public string EventType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;

    public Guid? UserId { get; set; }
    public string? SourceIp { get; set; }
    public string? Endpoint { get; set; }
    public string? HttpMethod { get; set; }
    public int? StatusCode { get; set; }
    public string? Message { get; set; }

    /// <summary>"kind:id", for example "invoice:cdca5d11-...".</summary>
    public string? AffectedEntity { get; set; }

    public string? MetadataJson { get; set; }
}
