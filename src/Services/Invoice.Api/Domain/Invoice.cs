using PandaPocket.Shared.Contracts.Invoicing;

namespace PandaPocket.Services.Invoice.Domain;

/// <summary>
/// One payment request. The heart of the system.
///
/// The rate is locked at creation and never recalculated. That is the whole
/// product: the merchant is quoted R250 and receives R250 regardless of what
/// the crypto price does inside the payment window, because the platform, not
/// the merchant, carries the exchange rate risk for those fifteen minutes.
/// </summary>
public sealed class Invoice
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }

    /// <summary>The merchant's own order reference, echoed back on the webhook.</summary>
    public required string Reference { get; set; }

    public decimal AmountZar { get; set; }

    /// <summary>The crypto asset, for example BTC. Combined with ZAR to form the rate pair.</summary>
    public required string Asset { get; set; }

    /// <summary>Rate in ZAR per unit of asset, fixed at creation.</summary>
    public decimal LockedRate { get; set; }

    /// <summary>AmountZar divided by LockedRate, rounded to the asset's precision.</summary>
    public decimal CryptoAmount { get; set; }

    /// <summary>
    /// Where the customer sends funds. Simulated here. A real gateway derives a
    /// fresh address per invoice from an extended public key, which is why this
    /// is per-invoice rather than per-merchant.
    /// </summary>
    public required string PayToAddress { get; set; }

    public InvoiceStatus Status { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Set when the invoice reaches a terminal state or is settled. Kept so the
    /// ledger and the invoice can be reconciled without joining across services.
    /// </summary>
    public DateTime? SettledAt { get; set; }

    public List<Payment> Payments { get; set; } = [];
    public List<InvoiceStatusHistory> History { get; set; } = [];

    /// <summary>
    /// Total crypto received so far. An underpaid invoice can be topped up, so
    /// this is a sum rather than the value of a single payment.
    /// </summary>
    public decimal TotalReceived => Payments.Sum(p => p.AmountCrypto);

    public bool IsExpired(DateTime now) => now >= ExpiresAt;
}
