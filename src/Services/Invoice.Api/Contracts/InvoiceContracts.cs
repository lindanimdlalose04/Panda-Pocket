using System.ComponentModel.DataAnnotations;
using PandaPocket.Shared.Contracts.Invoicing;

namespace PandaPocket.Services.Invoice.Contracts;

/// <summary>
/// What a merchant's system POSTs to create an invoice.
///
/// MerchantId is supplied in the body only until day 4, when the gateway
/// resolves it from a validated API key and a client can no longer assert who it
/// is. It is marked accordingly so the change is not forgotten.
/// </summary>
public sealed class CreateInvoiceRequest
{
    [Required]
    [Range(1, 1_000_000, ErrorMessage = "amountZar must be between 1 and 1000000.")]
    public decimal AmountZar { get; set; }

    [Required(ErrorMessage = "reference is required.")]
    [StringLength(100, MinimumLength = 1)]
    public string Reference { get; set; } = string.Empty;

    [Required(ErrorMessage = "asset is required.")]
    [StringLength(20, MinimumLength = 2)]
    public string Asset { get; set; } = "BTC";

    /// <summary>
    /// TEMPORARY. Replaced on day 4 by the merchant identity the gateway derives
    /// from the API key. A production gateway must never let a caller choose
    /// which merchant it is acting as.
    /// </summary>
    public Guid? MerchantId { get; set; }
}

public sealed class RecordPaymentRequest
{
    [Required(ErrorMessage = "txHash is required.")]
    [StringLength(128, MinimumLength = 4)]
    public string TxHash { get; set; } = string.Empty;

    [Required]
    [Range(typeof(decimal), "0.00000001", "1000000", ErrorMessage = "amountCrypto must be positive.")]
    public decimal AmountCrypto { get; set; }
}

public sealed class CancelInvoiceRequest
{
    [StringLength(300)]
    public string? Reason { get; set; }
}

public sealed record InvoiceResponse(
    Guid Id,
    Guid MerchantId,
    string Reference,
    decimal AmountZar,
    string Asset,
    decimal LockedRate,
    decimal CryptoAmount,
    string PayToAddress,
    string Status,
    DateTime ExpiresAt,
    DateTime CreatedAt,
    DateTime? SettledAt,
    decimal TotalReceived,
    int SecondsRemaining)
{
    public static InvoiceResponse From(Domain.Invoice i)
    {
        var remaining = (int)Math.Max(0, (i.ExpiresAt - DateTime.UtcNow).TotalSeconds);

        return new InvoiceResponse(
            i.Id, i.MerchantId, i.Reference, i.AmountZar, i.Asset,
            i.LockedRate, i.CryptoAmount, i.PayToAddress,
            i.Status.ToString(), i.ExpiresAt, i.CreatedAt, i.SettledAt,
            i.Payments.Sum(p => p.AmountCrypto),

            // Computed server side rather than left to the client to work out
            // from ExpiresAt, because the client's clock cannot be trusted and
            // the checkout countdown must agree with the server that will
            // actually reject a late payment.
            i.Status is InvoiceStatus.Pending or InvoiceStatus.Underpaid ? remaining : 0);
    }
}

public sealed record InvoiceListResponse(int Page, int PageSize, int TotalCount, IReadOnlyList<InvoiceResponse> Items);

public sealed record StatusHistoryResponse(
    Guid InvoiceId,
    IReadOnlyList<StatusHistoryEntry> History);

public sealed record StatusHistoryEntry(
    string? FromStatus,
    string ToStatus,
    string Reason,
    string? CorrelationId,
    DateTime CreatedAt);
