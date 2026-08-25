namespace PandaPocket.Services.Invoice.Domain;

/// <summary>
/// What happened, expressed so the endpoint layer can map it to a status code
/// without re-deriving the reasoning.
///
/// The status code table in the specification is not decoration: 409 for a
/// duplicate transaction hash, 410 for a payment against an expired invoice and
/// 422 for an underpayment say three genuinely different things to a merchant's
/// integration. Keeping the outcomes distinct here is what makes that possible.
/// </summary>
public enum InvoiceOutcome
{
    Success,

    /// <summary>No such invoice, or not this merchant's.</summary>
    NotFound,

    /// <summary>Rate could not be reached and there is no cached fallback. 503.</summary>
    RateUnavailable,

    /// <summary>Rate does not publish this pair. 400.</summary>
    UnknownAsset,

    /// <summary>This merchant already has an invoice with that reference. 409.</summary>
    DuplicateReference,

    /// <summary>This transaction hash has been seen before. 409, and a replay signal.</summary>
    DuplicateTransaction,

    /// <summary>The invoice is past its expiry. 410.</summary>
    Expired,

    /// <summary>Payment received but short of the invoiced amount. 422.</summary>
    Underpaid,

    /// <summary>The transition asked for is not permitted from the current state. 409.</summary>
    InvalidTransition
}

public sealed record InvoiceResult(InvoiceOutcome Outcome, Invoice? Invoice = null, string? Detail = null)
{
    public static InvoiceResult Ok(Invoice invoice) => new(InvoiceOutcome.Success, invoice);
    public static InvoiceResult Fail(InvoiceOutcome outcome, string? detail = null, Invoice? invoice = null) =>
        new(outcome, invoice, detail);
}
