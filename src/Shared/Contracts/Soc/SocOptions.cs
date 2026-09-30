namespace PandaPocket.Shared.Contracts.Soc;

/// <summary>
/// How this service ships its security events to the SOC service.
/// </summary>
public sealed class SocOptions
{
    public const string SectionName = "Soc";

    /// <summary>
    /// Off by default, so every service still runs outside Compose where there
    /// is no SOC service to ship to. Turned on by environment variable.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The SOC service, addressed directly rather than through the gateway.
    ///
    /// Through the gateway would need an API key that belongs to a merchant,
    /// which no service has, and it would be circular: the gateway is itself one
    /// of the sources of events. Internal telemetry is not merchant traffic.
    /// </summary>
    public string IngestBaseUrl { get; init; } = "http://localhost:5005";

    /// <summary>
    /// Stamped onto every event this service publishes, so the recorded origin
    /// cannot drift from the service that actually observed it.
    /// </summary>
    public string ServiceName { get; init; } = "unknown";

    /// <summary>Events per POST. Shipping one at a time would be a request per event.</summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// How long a partial batch waits before being sent anyway. Low, because an
    /// SOC feed that lags by a minute is not much of an SOC feed.
    /// </summary>
    public int FlushSeconds { get; init; } = 2;

    /// <summary>
    /// How many events may queue before the oldest are dropped.
    ///
    /// Bounded on purpose. If the SOC service is down, the alternative to
    /// dropping is growing the queue until the publishing service runs out of
    /// memory, which would mean a telemetry outage taking down payments. Seq
    /// still holds every event, so what is lost here is the analysis copy, not
    /// the record.
    /// </summary>
    public int QueueCapacity { get; init; } = 2000;

    public int RequestTimeoutSeconds { get; init; } = 5;
}
