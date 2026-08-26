using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PandaPocket.Services.Invoice.Clients;
using PandaPocket.Services.Invoice.Configuration;
using PandaPocket.Services.Invoice.Persistence;
using PandaPocket.Shared.Contracts.Invoicing;

namespace PandaPocket.Services.Invoice.Domain;

/// <summary>
/// Finds invoices that were paid but never settled, and settles them.
///
/// This is what makes "Invoice to Settlement must not be lost" true rather than
/// aspirational. The inline call made when a payment arrives already retries
/// three times, but three attempts over a couple of seconds does not survive
/// Settlement being redeployed, or its database being briefly unavailable. When
/// that happens the invoice is left in Paid, which is the honest state: the
/// customer's money arrived, the merchant has not yet been credited.
///
/// This sweeper closes that gap. Paid is a transient state that should always
/// become Settled, so anything sitting in Paid for more than a short grace
/// period is by definition a settlement that did not complete.
///
/// It is safe to run repeatedly because the Settlement endpoint is idempotent
/// per invoice: a second call for an invoice already credited returns the
/// existing result rather than crediting again.
/// </summary>
public sealed class SettlementSweeper(
    IServiceProvider services,
    IOptions<InvoiceOptions> options,
    ILogger<SettlementSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.SettlementSweepSeconds);
        using var timer = new PeriodicTimer(interval);

        logger.LogInformation("Settlement sweeper started, running every {Seconds}s",
            options.Value.SettlementSweepSeconds);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Settlement sweep failed; will retry on the next interval");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var settlement = scope.ServiceProvider.GetRequiredService<ISettlementClient>();
        var service = scope.ServiceProvider.GetRequiredService<InvoiceService>();

        // A grace period, so the sweeper does not race the inline call that is
        // probably still in flight for a payment received a second ago.
        var cutoff = DateTime.UtcNow.AddSeconds(-options.Value.SettlementGraceSeconds);

        var stranded = await db.Invoices
            .Where(i => i.Status == InvoiceStatus.Paid && i.CreatedAt <= cutoff)
            .OrderBy(i => i.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        if (stranded.Count == 0) return;

        logger.LogInformation("Found {Count} paid invoice(s) awaiting settlement", stranded.Count);

        foreach (var invoice in stranded)
        {
            var correlationId = $"sweeper-{invoice.Id.ToString()[..8]}";

            var result = await settlement.SettleAsync(
                invoice.Id, invoice.MerchantId, invoice.AmountZar, invoice.Reference, invoice.Asset,
                correlationId, ct);

            if (result is null) continue;

            await service.MarkSettledAsync(invoice.Id, correlationId, ct);
            logger.LogInformation("Invoice {InvoiceId} settled by the sweeper", invoice.Id);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
