using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PandaPocket.Services.Invoice.Configuration;
using PandaPocket.Services.Invoice.Persistence;
using PandaPocket.Shared.Contracts.Invoicing;

namespace PandaPocket.Services.Invoice.Domain;

/// <summary>
/// Moves invoices to Expired once their window has closed.
///
/// Expiry could be evaluated lazily whenever an invoice is read, and the payment
/// path does exactly that as a safety net. But lazy evaluation alone means an
/// invoice nobody looks at stays Pending for ever, so a merchant listing filtered
/// by status would report stale figures and the audit trail would have no record
/// of the moment the window actually closed. A sweeper gives every expiry a real
/// timestamp and a history row.
///
/// The query is driven by the ix_invoices_status_expires index, so it stays cheap
/// as the table grows.
/// </summary>
public sealed class ExpirySweeper(
    IServiceProvider services,
    IOptions<InvoiceOptions> options,
    ILogger<ExpirySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.ExpirySweepSeconds);
        using var timer = new PeriodicTimer(interval);

        logger.LogInformation("Expiry sweeper started, running every {Seconds}s", options.Value.ExpirySweepSeconds);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Same reasoning as the Rate generator: a failed sweep must not
                // take the service down. The database being briefly unavailable
                // should delay expiries, not stop invoices being created.
                logger.LogError(ex, "Expiry sweep failed; will retry on the next interval");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();

        var now = DateTime.UtcNow;

        var due = await db.Invoices
            .Where(i => (i.Status == InvoiceStatus.Pending || i.Status == InvoiceStatus.Underpaid)
                        && i.ExpiresAt <= now)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        foreach (var invoice in due)
        {
            var from = invoice.Status;
            invoice.Status = InvoiceStatus.Expired;

            db.StatusHistory.Add(new InvoiceStatusHistory
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                FromStatus = from,
                ToStatus = InvoiceStatus.Expired,
                Reason = "Expiry window elapsed",

                // No correlation id: nobody's request caused this, time did. A
                // null here is honest, and distinguishes system-driven
                // transitions from ones a caller triggered.
                CorrelationId = null,
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Expired {Count} invoice(s) whose payment window had closed", due.Count);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
