namespace PandaPocket.Services.Settlement.Domain;

/// <summary>
/// One attempt-tracked webhook notification to a merchant.
///
/// This table is the retry pattern made durable. Holding the queue in memory
/// would mean a restart loses every pending notification, and a merchant whose
/// endpoint was briefly down would simply never hear about a payment. Because
/// the row survives, delivery is resumable, auditable and manually retryable.
///
/// It is also the dead letter store: a delivery that exhausts its attempts stays
/// here as a Failed row rather than disappearing, so somebody can see what was
/// never delivered and why.
/// </summary>
public sealed class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }
    public Guid? InvoiceId { get; set; }

    public required string Url { get; set; }

    /// <summary>
    /// The exact bytes that were signed and sent.
    ///
    /// Stored verbatim rather than regenerated per attempt, because the HMAC
    /// covers this payload. Rebuilding it would risk a different serialisation,
    /// a different signature, and a retry the merchant rejects as forged.
    /// </summary>
    public required string Payload { get; set; }

    public string EventType { get; set; } = "invoice.settled";

    public int AttemptCount { get; set; }
    public WebhookStatus Status { get; set; } = WebhookStatus.Pending;

    /// <summary>Truncated: a failing endpoint can return a whole HTML error page.</summary>
    public string? LastError { get; set; }

    public int? LastStatusCode { get; set; }

    /// <summary>
    /// When this delivery becomes eligible again. The dispatcher selects on this
    /// column, which is what implements the backoff: a failed attempt simply
    /// pushes the timestamp further out.
    /// </summary>
    public DateTime NextAttemptAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
}

public enum WebhookStatus
{
    /// <summary>Awaiting its first attempt, or waiting out a backoff.</summary>
    Pending,

    Delivered,

    /// <summary>Attempts exhausted. Dead lettered, kept for inspection and manual retry.</summary>
    Failed
}
