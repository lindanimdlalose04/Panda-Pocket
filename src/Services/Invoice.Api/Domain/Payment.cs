namespace PandaPocket.Services.Invoice.Domain;

/// <summary>
/// One inbound transfer against an invoice. In this system a payment arrives
/// through an endpoint standing in for a blockchain confirmation.
/// </summary>
public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }

    /// <summary>
    /// The transaction hash, unique across the whole table.
    ///
    /// That single unique constraint is doing two jobs. It makes payment
    /// submission idempotent, so a retried confirmation cannot credit a merchant
    /// twice. And it is the replay detector: a second attempt to use a hash
    /// already seen is rejected by the database rather than by application code
    /// that might be bypassed or race with itself.
    /// </summary>
    public required string TxHash { get; set; }

    public decimal AmountCrypto { get; set; }
    public DateTime ReceivedAt { get; set; }

    /// <summary>Kept so a payment can be traced back through the logs.</summary>
    public string? CorrelationId { get; set; }

    public Invoice? Invoice { get; set; }
}
