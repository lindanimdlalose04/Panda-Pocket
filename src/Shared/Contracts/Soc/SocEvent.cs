namespace PandaPocket.Shared.Contracts.Soc;

/// <summary>
/// One security or operational event.
///
/// The field list is the one the Phase 2 specification names, promoted to
/// first-class properties rather than left in a loose metadata bag. That matters
/// twice over: Seq indexes each property separately, so a query becomes a filter
/// over structured data rather than a text search; and the graph loader can map
/// a property to a node or an edge without guessing at a dictionary key that may
/// or may not be present.
///
/// <see cref="Metadata"/> survives for what is genuinely specific to one event
/// type, such as how stale a cached rate was. Anything an analyst would filter
/// on across event types belongs in a real field.
/// </summary>
public sealed record SocEvent
{
    /// <summary>Stable identity, and the node key in the graph.</summary>
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Which service observed this. Stamped by the publisher rather than typed
    /// at every call site, so it cannot disagree with the service it came from.
    /// </summary>
    public string ServiceName { get; init; } = "unknown";

    /// <summary>One of <see cref="SocEventType"/>.</summary>
    public required string EventType { get; init; }

    /// <summary>One of <see cref="SocSeverity"/>, judged in isolation.</summary>
    public required string Severity { get; init; }

    /// <summary>
    /// The request this belongs to, minted at the gateway and propagated
    /// downstream. It is what makes one button press followable across four
    /// services, and it is the join key between events from different services.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// The merchant account involved, where one is known. Named UserId to match
    /// the specification; in this domain the acting party is a merchant rather
    /// than a person, because authentication here is machine to machine.
    /// </summary>
    public Guid? UserId { get; init; }

    /// <summary>
    /// The calling address. For an attack spread across several accounts this is
    /// the only thing tying the attempts together, which is why it becomes a
    /// node in the graph rather than a property of one.
    /// </summary>
    public string? SourceIp { get; init; }

    public string? Endpoint { get; init; }
    public string? HttpMethod { get; init; }
    public int? StatusCode { get; init; }

    /// <summary>
    /// A short human sentence. Deliberately not the place for anything a rule
    /// needs to read: rules read fields, people read this.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// What was acted on, as "kind:id", for example "invoice:cdca5d11-...".
    /// Built with <see cref="SocEntity.Ref(string, Guid)"/> so the graph loader
    /// can split the prefix and attach the event to the right node type.
    /// </summary>
    public string? AffectedEntity { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// The vocabulary for <see cref="SocEvent.AffectedEntity"/>.
///
/// A bare GUID would tell the graph loader nothing about what it points at, and
/// a free-text label would drift the moment two call sites disagreed on
/// spelling.
/// </summary>
public static class SocEntity
{
    public const string Invoice  = "invoice";
    public const string Merchant = "merchant";
    public const string ApiKey   = "apikey";
    public const string Service  = "service";
    public const string Webhook  = "webhook";
    public const string Ledger   = "ledger";

    public static string Ref(string kind, Guid id) => $"{kind}:{id}";
    public static string Ref(string kind, string id) => $"{kind}:{id}";

    /// <summary>Splits "invoice:abc" back into its parts, or null if unusable.</summary>
    public static (string Kind, string Id)? Parse(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        var separator = reference.IndexOf(':');
        return separator <= 0 || separator == reference.Length - 1
            ? null
            : (reference[..separator], reference[(separator + 1)..]);
    }
}
