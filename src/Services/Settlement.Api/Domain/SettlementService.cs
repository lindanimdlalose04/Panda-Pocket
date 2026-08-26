using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PandaPocket.Services.Settlement.Clients;
using PandaPocket.Services.Settlement.Contracts;
using PandaPocket.Services.Settlement.Persistence;
using PandaPocket.Shared.Contracts.Soc;

namespace PandaPocket.Services.Settlement.Domain;

public sealed class SettlementService(
    SettlementDbContext db,
    IMerchantClient merchantClient,
    ILogger<SettlementService> logger)
{
    public async Task<(SettlementResponse? Result, string? Error, bool AlreadySettled)> SettleAsync(
        SettleInvoiceRequest request, string correlationId, CancellationToken ct)
    {
        // Idempotency check before doing any work. Invoice retries this call by
        // design, so a second arrival for the same invoice is expected traffic
        // rather than an error, and must not credit the merchant twice.
        var existing = await db.LedgerEntries
            .AsNoTracking()
            .Where(l => l.InvoiceId == request.InvoiceId && l.EntryType == LedgerEntryType.Credit)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            logger.LogInformation(
                "Invoice {InvoiceId} is already settled; returning the existing result rather than crediting again",
                request.InvoiceId);

            var balanceNow = await GetBalanceAsync(request.MerchantId, ct);
            return (new SettlementResponse(
                request.InvoiceId, request.MerchantId, request.AmountZar,
                0m, 0m, balanceNow.AvailableZar, existing.CreatedAt), null, true);
        }

        // The fee percentage comes from the Merchant service, which owns it. A
        // fee passed in by the caller would let whoever calls this endpoint
        // decide what commission the platform earns.
        var merchant = await merchantClient.GetAsync(request.MerchantId, correlationId, ct);
        if (merchant is null)
        {
            return (null, $"Merchant {request.MerchantId} could not be resolved.", false);
        }

        var gross = request.AmountZar;

        // Rounded to the cent, away from zero, so the platform is never left
        // holding a fraction it cannot represent. At 1 percent of R250 this is
        // R2.50 exactly, but at odd amounts the rounding has to go somewhere and
        // being explicit about the direction is what makes the ledger balance.
        var fee = Math.Round(gross * merchant.FeePercent / 100m, 2, MidpointRounding.AwayFromZero);
        var net = gross - fee;

        var now = DateTime.UtcNow;
        var balance = await db.MerchantBalances.FirstOrDefaultAsync(b => b.MerchantId == request.MerchantId, ct);

        if (balance is null)
        {
            balance = new MerchantBalance { MerchantId = request.MerchantId, UpdatedAt = now };
            db.MerchantBalances.Add(balance);
        }

        // Two entries, not one net entry. The merchant is credited the full
        // amount the customer paid and charged the commission separately,
        // because "you were paid R250 and we took R2.50" is a statement anybody
        // can check, whereas a single R247.50 line hides where the difference
        // went. It is also what makes platform fee income a summable column.
        var creditBalance = balance.AvailableZar + gross;
        var credit = new LedgerEntry
        {
            Id = Guid.NewGuid(),
            MerchantId = request.MerchantId,
            InvoiceId = request.InvoiceId,
            EntryType = LedgerEntryType.Credit,
            AmountZar = gross,
            BalanceAfter = creditBalance,
            Description = $"Invoice {request.Reference} settled",
            CorrelationId = correlationId,
            CreatedAt = now
        };

        var feeBalance = creditBalance - fee;
        var feeEntry = new LedgerEntry
        {
            Id = Guid.NewGuid(),
            MerchantId = request.MerchantId,
            InvoiceId = request.InvoiceId,
            EntryType = LedgerEntryType.Fee,

            // Negative, so the balance is a plain sum of the column.
            AmountZar = -fee,
            BalanceAfter = feeBalance,
            Description = $"Platform fee at {merchant.FeePercent}%",
            CorrelationId = correlationId,

            // A millisecond, not a tick. Postgres timestamps have microsecond
            // resolution and a .NET tick is 100 nanoseconds, so AddTicks(1) is
            // rounded away on write: both rows land on the identical timestamp
            // and "order by created_at" then returns the credit and the fee in
            // whichever order the database feels like. On a statement that reads
            // as the fee being charged before the money arrived.
            CreatedAt = now.AddMilliseconds(1)
        };

        db.LedgerEntries.AddRange(credit, feeEntry);

        balance.AvailableZar = feeBalance;
        balance.LifetimeCreditedZar += gross;
        balance.LifetimeFeesZar += fee;
        balance.UpdatedAt = now;

        // The webhook row is created inside the same transaction as the ledger.
        // If the notification were queued only after a successful commit, a crash
        // in between would leave a merchant credited and never told. Committing
        // them together means the intent to notify is as durable as the money.
        var delivery = BuildDelivery(request, merchant, net, fee, now, correlationId);
        if (delivery is not null) db.WebhookDeliveries.Add(delivery);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two settlement calls raced and both passed the check above. The
            // unique index on (invoice_id, entry_type) is the real guard; this
            // is the losing thread discovering it lost, which is a correct
            // outcome rather than a failure.
            logger.LogWarning("Concurrent settlement for invoice {InvoiceId}; the other attempt won", request.InvoiceId);

            var current = await GetBalanceAsync(request.MerchantId, ct);
            return (new SettlementResponse(
                request.InvoiceId, request.MerchantId, gross, 0m, 0m, current.AvailableZar, now), null, true);
        }

        logger.LogInformation(
            "Settled invoice {InvoiceId}: credited {Gross} ZAR, fee {Fee} ZAR, merchant {MerchantId} balance now {Balance}",
            request.InvoiceId, gross, fee, request.MerchantId, feeBalance);

        return (new SettlementResponse(
            request.InvoiceId, request.MerchantId, gross, fee, net, feeBalance, now), null, false);
    }

    private WebhookDelivery? BuildDelivery(
        SettleInvoiceRequest request, MerchantDetails merchant,
        decimal net, decimal fee, DateTime now, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(merchant.WebhookUrl))
        {
            logger.LogInformation("Merchant {MerchantId} has no webhook URL configured; nothing to deliver", request.MerchantId);
            return null;
        }

        var payload = JsonSerializer.Serialize(new
        {
            eventType = "invoice.settled",
            invoiceId = request.InvoiceId,
            merchantId = request.MerchantId,
            reference = request.Reference,
            amountZar = request.AmountZar,
            feeZar = fee,
            netZar = net,
            asset = request.Asset,
            settledAt = now,
            correlationId
        });

        return new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            MerchantId = request.MerchantId,
            InvoiceId = request.InvoiceId,
            Url = merchant.WebhookUrl,
            Payload = payload,
            EventType = "invoice.settled",
            AttemptCount = 0,
            Status = WebhookStatus.Pending,

            // Due immediately; the dispatcher picks it up on its next sweep.
            NextAttemptAt = now,
            CreatedAt = now
        };
    }

    public async Task<MerchantBalance> GetBalanceAsync(Guid merchantId, CancellationToken ct) =>
        await db.MerchantBalances.AsNoTracking().FirstOrDefaultAsync(b => b.MerchantId == merchantId, ct)
        ?? new MerchantBalance { MerchantId = merchantId, UpdatedAt = DateTime.UtcNow };

    /// <summary>
    /// Recomputes the balance from the ledger and compares it with the stored
    /// figure. The stored balance is a cache; this is what proves the cache is
    /// honest, and it is the kind of check an auditor asks for.
    /// </summary>
    public async Task<(decimal Stored, decimal Recomputed, bool Matches)> ReconcileAsync(Guid merchantId, CancellationToken ct)
    {
        var stored = (await GetBalanceAsync(merchantId, ct)).AvailableZar;

        var recomputed = await db.LedgerEntries
            .Where(l => l.MerchantId == merchantId)
            .SumAsync(l => (decimal?)l.AmountZar, ct) ?? 0m;

        var matches = stored == recomputed;

        if (!matches)
        {
            var soc = SocEvent.Create("LEDGER_MISMATCH", SocSeverity.Critical, "reconciliation", merchantId,
                metadata: new Dictionary<string, object?> { ["stored"] = stored, ["recomputed"] = recomputed });
            logger.LogError("SOC LEDGER_MISMATCH {@SocEvent}", soc);
        }

        return (stored, recomputed, matches);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
