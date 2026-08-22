namespace PandaPocket.Shared.Contracts.Invoicing;

/// <summary>
/// The invoice lifecycle. Cancelled, Expired and Settled are terminal.
/// Any transition not permitted by <see cref="InvoiceStatusRules"/> is both an
/// HTTP error and a logged security event.
/// </summary>
public enum InvoiceStatus
{
    Pending,
    Underpaid,
    Paid,
    Settled,
    Expired,
    Cancelled
}

/// <summary>
/// The state machine expressed as data rather than as scattered if-statements.
/// Keeping it here means the rule is stated once and can be unit tested without
/// a database.
/// </summary>
public static class InvoiceStatusRules
{
    private static readonly Dictionary<InvoiceStatus, InvoiceStatus[]> Allowed = new()
    {
        [InvoiceStatus.Pending]   = [InvoiceStatus.Paid, InvoiceStatus.Underpaid, InvoiceStatus.Expired, InvoiceStatus.Cancelled],
        [InvoiceStatus.Underpaid] = [InvoiceStatus.Paid, InvoiceStatus.Expired],
        [InvoiceStatus.Paid]      = [InvoiceStatus.Settled],
        [InvoiceStatus.Settled]   = [],
        [InvoiceStatus.Expired]   = [],
        [InvoiceStatus.Cancelled] = []
    };

    public static bool CanTransition(InvoiceStatus from, InvoiceStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsTerminal(InvoiceStatus status) =>
        Allowed.TryGetValue(status, out var targets) && targets.Length == 0;
}
