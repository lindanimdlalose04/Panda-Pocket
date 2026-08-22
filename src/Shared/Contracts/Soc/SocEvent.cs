namespace PandaPocket.Shared.Contracts.Soc;

/// <summary>
/// One security event, emitted as a structured Serilog property so that Seq can
/// index it and the future graph loader can read it without parsing message
/// text. The shape is deliberately flat: entity ids become graph nodes and
/// <see cref="Metadata"/> carries whatever the specific event type needs.
/// </summary>
/// <param name="EventType">One of <see cref="SocEventType"/>.</param>
/// <param name="Severity">One of <see cref="SocSeverity"/>.</param>
public sealed record SocEvent(
    string EventType,
    string Severity,
    string CorrelationId,
    DateTimeOffset Timestamp,
    Guid? MerchantId = null,
    Guid? InvoiceId = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public static SocEvent Create(
        string eventType,
        string severity,
        string correlationId,
        Guid? merchantId = null,
        Guid? invoiceId = null,
        IReadOnlyDictionary<string, object?>? metadata = null) =>
        new(eventType, severity, correlationId, DateTimeOffset.UtcNow, merchantId, invoiceId, metadata);
}
