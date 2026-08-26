using System.ComponentModel.DataAnnotations;

namespace PandaPocket.Services.Settlement.Contracts;

/// <summary>
/// What Invoice sends when a payment is confirmed.
///
/// The fee is deliberately not a field. Settlement fetches the merchant's rate
/// from the Merchant service, because a fee supplied by the caller would let
/// whoever calls this endpoint decide what commission the platform earns.
/// </summary>
public sealed class SettleInvoiceRequest
{
    [Required] public Guid InvoiceId { get; set; }
    [Required] public Guid MerchantId { get; set; }

    [Required, Range(0.01, 10_000_000)]
    public decimal AmountZar { get; set; }

    [Required, StringLength(100)]
    public string Reference { get; set; } = string.Empty;

    [StringLength(20)]
    public string Asset { get; set; } = "BTC";
}

public sealed record SettlementResponse(
    Guid InvoiceId,
    Guid MerchantId,
    decimal GrossZar,
    decimal FeeZar,
    decimal NetZar,
    decimal BalanceAfterZar,
    DateTime SettledAt);

public sealed record BalanceResponse(
    Guid MerchantId,
    decimal AvailableZar,
    decimal LifetimeCreditedZar,
    decimal LifetimeFeesZar,
    DateTime UpdatedAt);

public sealed record LedgerEntryResponse(
    Guid Id,
    Guid? InvoiceId,
    string EntryType,
    decimal AmountZar,
    decimal BalanceAfterZar,
    string? Description,
    string? CorrelationId,
    DateTime CreatedAt);

public sealed record LedgerResponse(
    Guid MerchantId, int Page, int PageSize, int TotalCount, IReadOnlyList<LedgerEntryResponse> Entries);

public sealed record WebhookDeliveryResponse(
    Guid Id,
    Guid MerchantId,
    Guid? InvoiceId,
    string Url,
    string EventType,
    string Status,
    int AttemptCount,
    int? LastStatusCode,
    string? LastError,
    DateTime NextAttemptAt,
    DateTime CreatedAt,
    DateTime? DeliveredAt);

/// <summary>
/// Proof that the stored balance agrees with the sum of the ledger. The stored
/// figure is a cache of the entries, and this is what demonstrates the cache has
/// not drifted.
/// </summary>
public sealed record ReconciliationResponse(
    Guid MerchantId, decimal StoredBalanceZar, decimal RecomputedFromLedgerZar, bool Matches);
