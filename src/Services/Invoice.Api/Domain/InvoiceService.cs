using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PandaPocket.Services.Invoice.Clients;
using PandaPocket.Services.Invoice.Configuration;
using PandaPocket.Services.Invoice.Persistence;
using PandaPocket.Shared.Contracts.Invoicing;
using PandaPocket.Shared.Contracts.Soc;

namespace PandaPocket.Services.Invoice.Domain;

public sealed class InvoiceService(
    InvoiceDbContext db,
    IRateClient rateClient,
    IOptions<InvoiceOptions> options,
    ILogger<InvoiceService> logger)
{
    private readonly InvoiceOptions _options = options.Value;

    // -----------------------------------------------------------------------
    // Creation
    // -----------------------------------------------------------------------
    public async Task<InvoiceResult> CreateAsync(
        Guid merchantId, string reference, decimal amountZar, string asset, string correlationId, CancellationToken ct)
    {
        var pair = $"{asset.ToUpperInvariant()}ZAR";

        var quote = await rateClient.GetQuoteAsync(pair, correlationId, ct);
        if (quote is null)
        {
            // Day 6 puts a circuit breaker with a cached rate here, at which
            // point this becomes the fallback path rather than a hard failure.
            return InvoiceResult.Fail(InvoiceOutcome.RateUnavailable, $"No rate available for {pair}.");
        }

        if (quote.Rate <= 0)
        {
            return InvoiceResult.Fail(InvoiceOutcome.UnknownAsset, $"Rate service returned a non-positive rate for {pair}.");
        }

        var now = DateTime.UtcNow;

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            MerchantId = merchantId,
            Reference = reference,
            AmountZar = amountZar,
            Asset = asset.ToUpperInvariant(),

            // The rate is fixed here and never recalculated. This single
            // assignment is the product: the merchant is quoted a rand amount
            // and receives that rand amount, and the platform absorbs whatever
            // the price does over the next fifteen minutes.
            LockedRate = quote.Rate,

            // Rounded up, not to nearest. Rounding down would ask the customer
            // for very slightly less than the invoice is worth, which over many
            // invoices is a systematic loss to the platform.
            CryptoAmount = Math.Round(amountZar / quote.Rate, 8, MidpointRounding.ToPositiveInfinity),

            PayToAddress = GeneratePayToAddress(asset),
            Status = InvoiceStatus.Pending,
            ExpiresAt = now.AddMinutes(_options.ExpiryMinutes),
            CreatedAt = now
        };

        db.Invoices.Add(invoice);

        // The initial transition has no "from", because the invoice did not
        // exist before this moment.
        AddHistory(invoice, null, InvoiceStatus.Pending, "Invoice created", correlationId, now);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "ux_invoices_merchant_reference"))
        {
            // A retried create with the same reference. Rejecting it is what
            // stops a merchant retry producing two invoices for one order.
            return InvoiceResult.Fail(InvoiceOutcome.DuplicateReference,
                $"Reference '{reference}' already exists for this merchant.");
        }

        LogSoc(SocEventType.InvoiceCreated, SocSeverity.Info, correlationId, merchantId, invoice.Id, new()
        {
            ["amountZar"] = amountZar,
            ["asset"] = invoice.Asset,
            ["lockedRate"] = invoice.LockedRate,
            ["cryptoAmount"] = invoice.CryptoAmount
        });

        return InvoiceResult.Ok(invoice);
    }

    // -----------------------------------------------------------------------
    // Payment
    // -----------------------------------------------------------------------
    public async Task<InvoiceResult> RecordPaymentAsync(
        Guid invoiceId, string txHash, decimal amountCrypto, string correlationId, CancellationToken ct)
    {
        var invoice = await db.Invoices
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct);

        if (invoice is null) return InvoiceResult.Fail(InvoiceOutcome.NotFound);

        var now = DateTime.UtcNow;

        // Expiry is checked before anything else. A payment arriving after the
        // window has closed was quoted at a rate the platform is no longer
        // willing to honour, so it is refused rather than silently accepted.
        if (invoice.Status is InvoiceStatus.Pending or InvoiceStatus.Underpaid && invoice.IsExpired(now))
        {
            await TransitionAsync(invoice, InvoiceStatus.Expired, "Payment attempted after expiry", correlationId, now, ct);

            LogSoc(SocEventType.PaymentOnExpired, SocSeverity.Warning, correlationId, invoice.MerchantId, invoice.Id, new()
            {
                ["txHash"] = txHash,
                ["expiredAt"] = invoice.ExpiresAt
            });

            return InvoiceResult.Fail(InvoiceOutcome.Expired, "This invoice expired before the payment arrived.", invoice);
        }

        if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Settled or InvoiceStatus.Cancelled or InvoiceStatus.Expired)
        {
            return InvoiceResult.Fail(InvoiceOutcome.InvalidTransition,
                $"Cannot accept a payment against an invoice in state '{invoice.Status}'.", invoice);
        }

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            TxHash = txHash,
            AmountCrypto = amountCrypto,
            ReceivedAt = now,
            CorrelationId = correlationId
        };

        // The running total is computed from what was already on the invoice
        // plus this payment, BEFORE the new payment is attached.
        //
        // Adding the payment first and then summing the navigation collection
        // looks equivalent and is not. EF Core performs relationship fixup: as
        // soon as db.Payments.Add sees a tracked parent, it appends the payment
        // to invoice.Payments itself. Adding it manually as well puts it in the
        // collection twice, the sum doubles, and an underpayment is accepted as
        // fully paid. That failure is silent, produces a valid-looking invoice,
        // and would credit a merchant for money never received.
        var totalReceived = invoice.Payments.Sum(p => p.AmountCrypto) + amountCrypto;
        var minimumAcceptable = invoice.CryptoAmount * (1 - _options.UnderpaymentTolerancePercent / 100m);

        db.Payments.Add(payment);

        var fullyPaid = totalReceived >= minimumAcceptable;
        var previousStatus = invoice.Status;

        if (fullyPaid)
        {
            invoice.Status = InvoiceStatus.Paid;
            AddHistory(invoice, previousStatus, InvoiceStatus.Paid,
                $"Payment received: {totalReceived} {invoice.Asset}", correlationId, now);
        }
        else
        {
            invoice.Status = InvoiceStatus.Underpaid;
            AddHistory(invoice, previousStatus, InvoiceStatus.Underpaid,
                $"Underpaid: received {totalReceived} of {invoice.CryptoAmount} {invoice.Asset}", correlationId, now);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "ux_payments_tx_hash"))
        {
            // The unique constraint caught a hash already used. This is either a
            // duplicated confirmation, which is harmless and idempotent, or a
            // deliberate replay, which is not. Both are worth recording; the SOC
            // layer decides which by looking at the surrounding traffic.
            LogSoc(SocEventType.PaymentReplay, SocSeverity.Critical, correlationId, invoice.MerchantId, invoice.Id, new()
            {
                ["txHash"] = txHash,
                ["amountCrypto"] = amountCrypto
            });

            return InvoiceResult.Fail(InvoiceOutcome.DuplicateTransaction,
                $"Transaction '{txHash}' has already been recorded.", invoice);
        }

        if (fullyPaid)
        {
            LogSoc(SocEventType.PaymentConfirmed, SocSeverity.Info, correlationId, invoice.MerchantId, invoice.Id, new()
            {
                ["txHash"] = txHash,
                ["totalReceived"] = totalReceived
            });

            return InvoiceResult.Ok(invoice);
        }

        LogSoc(SocEventType.PaymentUnderpaid, SocSeverity.Warning, correlationId, invoice.MerchantId, invoice.Id, new()
        {
            ["txHash"] = txHash,
            ["expected"] = invoice.CryptoAmount,
            ["received"] = totalReceived,
            ["shortfall"] = invoice.CryptoAmount - totalReceived
        });

        return InvoiceResult.Fail(InvoiceOutcome.Underpaid,
            $"Received {totalReceived} of {invoice.CryptoAmount} {invoice.Asset}. The invoice can still be topped up until it expires.",
            invoice);
    }

    // -----------------------------------------------------------------------
    // Cancellation and settlement
    // -----------------------------------------------------------------------
    public async Task<InvoiceResult> CancelAsync(Guid invoiceId, string reason, string correlationId, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
        if (invoice is null) return InvoiceResult.Fail(InvoiceOutcome.NotFound);

        if (!InvoiceStatusRules.CanTransition(invoice.Status, InvoiceStatus.Cancelled))
        {
            return InvoiceResult.Fail(InvoiceOutcome.InvalidTransition,
                $"An invoice in state '{invoice.Status}' cannot be cancelled.", invoice);
        }

        await TransitionAsync(invoice, InvoiceStatus.Cancelled, reason, correlationId, DateTime.UtcNow, ct);
        return InvoiceResult.Ok(invoice);
    }

    /// <summary>
    /// Marks the invoice settled once Settlement has written the ledger. Called
    /// by the settlement flow on day 5; exposed now so the state machine is
    /// complete rather than having a hole in it.
    /// </summary>
    public async Task<InvoiceResult> MarkSettledAsync(Guid invoiceId, string correlationId, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
        if (invoice is null) return InvoiceResult.Fail(InvoiceOutcome.NotFound);

        if (!InvoiceStatusRules.CanTransition(invoice.Status, InvoiceStatus.Settled))
        {
            return InvoiceResult.Fail(InvoiceOutcome.InvalidTransition,
                $"An invoice in state '{invoice.Status}' cannot be settled.", invoice);
        }

        var now = DateTime.UtcNow;
        invoice.SettledAt = now;
        await TransitionAsync(invoice, InvoiceStatus.Settled, "Ledger written by Settlement", correlationId, now, ct);
        return InvoiceResult.Ok(invoice);
    }

    // -----------------------------------------------------------------------
    // Shared transition path
    // -----------------------------------------------------------------------

    /// <summary>
    /// The only place a status is written. Every transition is validated against
    /// the shared state machine and recorded in history within the same
    /// SaveChanges, so the audit trail cannot drift from the invoice it
    /// describes.
    /// </summary>
    private async Task TransitionAsync(
        Invoice invoice, InvoiceStatus to, string reason, string correlationId, DateTime now, CancellationToken ct)
    {
        var from = invoice.Status;

        if (!InvoiceStatusRules.CanTransition(from, to))
        {
            throw new InvalidOperationException($"Illegal transition {from} to {to} for invoice {invoice.Id}.");
        }

        invoice.Status = to;
        AddHistory(invoice, from, to, reason, correlationId, now);
        await db.SaveChangesAsync(ct);
    }

    private void AddHistory(Invoice invoice, InvoiceStatus? from, InvoiceStatus to, string reason, string correlationId, DateTime now)
    {
        var row = new InvoiceStatusHistory
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            FromStatus = from,
            ToStatus = to,
            Reason = reason,
            CorrelationId = correlationId,
            CreatedAt = now
        };

        db.StatusHistory.Add(row);
        invoice.History.Add(row);
    }

    /// <summary>
    /// A stand-in for deriving a fresh receiving address per invoice from an
    /// extended public key. Fresh per invoice, not per merchant, because reusing
    /// one address makes every payment to that merchant publicly linkable.
    /// </summary>
    private static string GeneratePayToAddress(string asset)
    {
        var suffix = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..32].ToLowerInvariant();
        return asset.ToUpperInvariant() switch
        {
            "BTC" => $"bc1q{suffix}",
            "ETH" => $"0x{suffix}",
            _ => $"{asset.ToLowerInvariant()}1{suffix}"
        };
    }

    /// <summary>
    /// Npgsql surfaces a unique violation as SQLSTATE 23505. Matching on the
    /// constraint name distinguishes a duplicate reference from a duplicate
    /// transaction hash, which are different failures needing different answers.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex, string constraintName) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg &&
        (pg.ConstraintName?.Equals(constraintName, StringComparison.OrdinalIgnoreCase) ?? false);

    private void LogSoc(string eventType, string severity, string correlationId,
        Guid? merchantId, Guid? invoiceId, Dictionary<string, object?> metadata)
    {
        var soc = SocEvent.Create(eventType, severity, correlationId, merchantId, invoiceId, metadata);

        // Logged as a structured property rather than interpolated into the
        // message, so Seq indexes it and the graph loader in the next phase can
        // read the fields without parsing text.
        logger.LogInformation("SOC {EventType} {@SocEvent}", eventType, soc);
    }
}
