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
    ISocEventPublisher soc,
    ReconcileStateLog reconcileState,
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

        // From here to the commit runs in one transaction with the balance row
        // locked.
        //
        // WHY: the balance used to be read, changed in memory and written back.
        // Two settlements for the same merchant arriving together both read the
        // same figure, both added their own amount to it, and the second write
        // overwrote the first. The ledger kept both sets of entries, because
        // they belong to different invoices and nothing objected, so the books
        // silently disagreed with the balance by exactly one settlement.
        //
        // That is a lost update, and it happened: on 6 September two invoices
        // settled a second apart and R247.50 vanished from the stored balance
        // while staying in the ledger. The reconciliation check caught it and
        // raised RECONCILE_MISMATCH, which is the reason that check exists.
        //
        // The idempotency guard at the top of this method does not help. It
        // stops the SAME invoice settling twice; this was two DIFFERENT invoices
        // racing each other.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // The row has to exist before it can be locked. SELECT FOR UPDATE locks
        // rows that are present and has nothing to lock for a merchant who has
        // never been credited, which would leave two concurrent first
        // settlements racing to insert instead.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO merchant_balances " +
            "(merchant_id, available_zar, lifetime_credited_zar, lifetime_fees_zar, updated_at) " +
            "VALUES ({0}, 0, 0, 0, {1}) ON CONFLICT (merchant_id) DO NOTHING",
            [request.MerchantId, now], ct);

        // FOR UPDATE holds the row until this transaction commits. A concurrent
        // settlement for the same merchant waits here rather than reading a
        // figure that is about to be stale. The lock is per row, so merchants
        // never block one another.
        var balance = await db.MerchantBalances
            .FromSqlRaw("SELECT * FROM merchant_balances WHERE merchant_id = {0} FOR UPDATE", request.MerchantId)
            .FirstAsync(ct);

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
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackAsync(ct);

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
    public async Task<(decimal Stored, decimal Recomputed, bool Matches)> ReconcileAsync(
        Guid merchantId, string correlationId, CancellationToken ct)
    {
        var stored = (await GetBalanceAsync(merchantId, ct)).AvailableZar;

        var recomputed = await db.LedgerEntries
            .Where(l => l.MerchantId == merchantId)
            .SumAsync(l => (decimal?)l.AmountZar, ct) ?? 0m;

        var matches = stored == recomputed;

        if (matches)
        {
            // Nothing to publish, but the merchant stops being tracked so that
            // a gap appearing later is reported as new rather than suppressed
            // as a repeat of an older one.
            reconcileState.Clear(merchantId);
            return (stored, recomputed, matches);
        }

        // Published on a change, not on an observation.
        //
        // This check runs on every read of the balance and the dashboard polls
        // that read, so a merchant with a standing gap produced one CRITICAL
        // event every few seconds for as long as the page was open. The finding
        // was correct and the volume made it useless: several hundred copies of
        // one fact, burying everything else in the evidence store.
        if (reconcileState.ShouldReport(merchantId, stored - recomputed))
        {
            // The event type was a bare "LEDGER_MISMATCH" string here, which is
            // the precise drift the catalogue exists to stop: a type no rule
            // filters on and no graph node ever matches. It is a catalogue
            // constant now.
            //
            // CRITICAL, and the only event in the system graded that way. Every
            // other event describes something a system under load does anyway.
            // This one says the books do not add up, which is either a defect or
            // tampering, and both mean stop paying out until somebody looks.
            soc.Publish(new SocEvent
            {
                EventType = SocEventType.ReconcileMismatch,
                Severity = SocSeverity.Critical,
                CorrelationId = correlationId,
                UserId = merchantId,
                AffectedEntity = SocEntity.Ref(SocEntity.Ledger, merchantId),
                Message = $"Stored balance R{stored} disagrees with the ledger sum R{recomputed}",
                Metadata = new Dictionary<string, object?>
                {
                    ["stored"] = stored,
                    ["recomputed"] = recomputed,
                    ["differenceZar"] = stored - recomputed
                }
            });
        }

        return (stored, recomputed, matches);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
