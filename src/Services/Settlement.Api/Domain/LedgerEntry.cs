namespace PandaPocket.Services.Settlement.Domain;

/// <summary>
/// One immutable line in a merchant's ZAR ledger.
///
/// Rows are only ever inserted, never updated or deleted. That is what makes
/// this a ledger rather than a balance table with history bolted on: the current
/// balance is a consequence of the entries, so it can always be recomputed and
/// checked against the stored figure. A ledger you can edit is one nobody can
/// audit.
/// </summary>
public sealed class LedgerEntry
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }

    /// <summary>Null for entries not tied to an invoice, such as a payout.</summary>
    public Guid? InvoiceId { get; set; }

    public LedgerEntryType EntryType { get; set; }

    /// <summary>
    /// Signed. Credits are positive, fees and payouts negative, so the balance
    /// is a plain sum rather than a conditional that has to know which types
    /// subtract. Getting a sign wrong is then a visible arithmetic error rather
    /// than a silently wrong total.
    /// </summary>
    public decimal AmountZar { get; set; }

    /// <summary>
    /// The running balance immediately after this entry.
    ///
    /// Strictly redundant, since it is the sum of everything up to here, and
    /// stored anyway for two reasons. A statement can be rendered without
    /// recomputing a running total across the whole history, and any divergence
    /// between this column and the recomputed sum is proof that something wrote
    /// the ledger incorrectly. Redundancy that can be checked is a feature.
    /// </summary>
    public decimal BalanceAfter { get; set; }

    public string? Description { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum LedgerEntryType
{
    /// <summary>Gross amount of a settled invoice, credited to the merchant.</summary>
    Credit,

    /// <summary>The platform's commission, taken off the credit. Negative.</summary>
    Fee,

    /// <summary>Money paid out to the merchant's bank account. Negative.</summary>
    Payout
}
