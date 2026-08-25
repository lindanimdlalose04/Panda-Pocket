using PandaPocket.Shared.Contracts.Invoicing;

namespace PandaPocket.Services.Invoice.Domain;

/// <summary>
/// One row per state transition, written inside the same transaction as the
/// transition itself.
///
/// This table is three things at once, which is why it exists from day one
/// rather than being retrofitted. It is the audit trail, answering "what
/// happened to this invoice and when". It is the SOC event source, because a
/// rejected transition is a security signal. And it is the edge source for the
/// knowledge graph in the next phase, where each row becomes a timestamped edge
/// between states carrying the correlation id that links it to everything else
/// that happened in the same request.
/// </summary>
public sealed class InvoiceStatusHistory
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }

    /// <summary>Null for the initial transition into Pending at creation.</summary>
    public InvoiceStatus? FromStatus { get; set; }

    public InvoiceStatus ToStatus { get; set; }

    /// <summary>Why the transition happened, in words, for the audit trail.</summary>
    public required string Reason { get; set; }

    public string? CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Invoice? Invoice { get; set; }
}
