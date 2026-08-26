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

    /// <summary>
    /// How old a cached rate may be before the fallback refuses to use it.
    ///
    /// Past this point a stale rate stops being a degradation and becomes a
    /// liability: the platform would be locking a merchant to a price that no
    /// longer reflects the market, and absorbing the difference at settlement.
    /// </summary>
    public int MaxFallbackRateAgeMinutes { get; init; } = 30;

    /// <summary>Consecutive-failure ratio that opens the breaker.</summary>
    public double CircuitFailureRatio { get; init; } = 0.5;

    /// <summary>How long the breaker stays open before probing again.</summary>
    public int CircuitBreakSeconds { get; init; } = 15;

    /// <summary>Minimum calls in the sampling window before the ratio is judged.</summary>
    public int CircuitMinimumThroughput { get; init; } = 3;

    public string SettlementServiceBaseUrl { get; init; } = "http://localhost:5004";
    public int SettlementTimeoutSeconds { get; init; } = 10;

    /// <summary>How often paid-but-unsettled invoices are swept up.</summary>
    public int SettlementSweepSeconds { get; init; } = 20;

    /// <summary>
    /// How long an invoice may sit in Paid before the sweeper treats it as
    /// stranded. Long enough not to race the inline settlement call that is
    /// probably still in flight.
    /// </summary>
    public int SettlementGraceSeconds { get; init; } = 15;
}
