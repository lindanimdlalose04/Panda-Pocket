namespace PandaPocket.Services.Invoice.Configuration;

public sealed class InvoiceOptions
{
    public const string SectionName = "Invoice";

    /// <summary>
    /// How long a customer has to pay before the locked rate is abandoned.
    /// Fifteen minutes is the industry norm: long enough for a human to move
    /// funds, short enough that the platform's exposure to a price move is
    /// small and quantifiable.
    /// </summary>
    public int ExpiryMinutes { get; init; } = 15;

    /// <summary>
    /// How close a payment must be to the invoiced amount to count as settled.
    /// Some tolerance is necessary because network fees and rounding at the
    /// sender mean an exact match is rare. Anything short of this is underpaid
    /// rather than paid, and can still be topped up.
    /// </summary>
    public decimal UnderpaymentTolerancePercent { get; init; } = 0.5m;

    /// <summary>How often expired invoices are swept.</summary>
    public int ExpirySweepSeconds { get; init; } = 30;

    public string RateServiceBaseUrl { get; init; } = "http://localhost:5003";

    /// <summary>Seconds before a Rate call is abandoned.</summary>
    public int RateTimeoutSeconds { get; init; } = 5;
}
