using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using PandaPocket.Services.Invoice.Clients;
using PandaPocket.Services.Invoice.Domain;
using PandaPocket.Services.Invoice.Persistence;
using PandaPocket.Shared.Contracts.Invoicing;
using PandaPocket.Shared.Contracts.Observability;

namespace PandaPocket.Services.Invoice.Endpoints;

/// <summary>
/// The customer-facing view of a single invoice.
///
/// Deliberately separate from /api/invoices, and deliberately unauthenticated,
/// because the person paying is not the merchant and has no API key. Requiring
/// one would mean either putting the merchant's credential in a page the
/// customer can read, or building a second credential system for shoppers.
///
/// The invoice id is the bearer token. That is why it is a version 4 GUID rather
/// than a sequential number: 122 bits of randomness make it unguessable, so
/// holding the link is the authorisation. BitPay and Coinbase Commerce both work
/// this way. It also means the response must contain only what a customer needs;
/// the merchant id and the rest of the record stay behind the authenticated
/// endpoint.
/// </summary>
public static class CheckoutEndpoints
{
    public static IEndpointRouteBuilder MapCheckoutEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/checkout").WithTags("Checkout");

        group.MapGet("/{id:guid}", async (Guid id, InvoiceDbContext db, CancellationToken ct) =>
        {
            var invoice = await db.Invoices
                .AsNoTracking()
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == id, ct);

            if (invoice is null)
            {
                return Results.Problem(title: "Invoice not found",
                    detail: "This payment link is not valid.", statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(CheckoutResponse.From(invoice));
        })
        .WithName("GetCheckout")
        .WithSummary("Public view of one invoice, for the customer paying it")
        .Produces<CheckoutResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        // A stand-in for a wallet sending funds and a chain watcher confirming
        // them. In a real deployment nothing customer-facing may record a
        // payment: confirmations arrive from a service watching the blockchain,
        // authenticated in its own right. This exists so the demo can be driven
        // from the checkout page, and is named so nobody mistakes it for the
        // real path.
        group.MapPost("/{id:guid}/simulate-payment", async (
            Guid id,
            SimulatePaymentRequest? request,
            InvoiceService service,
            ISettlementClient settlement,
            InvoiceDbContext db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
            if (invoice is null)
            {
                return Results.Problem(title: "Invoice not found",
                    detail: "This payment link is not valid.", statusCode: StatusCodes.Status404NotFound);
            }

            // Default to paying exactly what is owed. An explicit amount lets the
            // demo show an underpayment.
            var amount = request?.AmountCrypto ?? invoice.CryptoAmount;
            var txHash = request?.TxHash ?? $"tx-sim-{Guid.NewGuid().ToString("N")[..16]}";

            var correlationId = ctx.GetCorrelationId();
            var result = await service.RecordPaymentAsync(id, txHash, amount, correlationId, ct);

            if (result.Outcome == InvoiceOutcome.Success && result.Invoice is { } paid)
            {
                var settled = await settlement.SettleAsync(
                    paid.Id, paid.MerchantId, paid.AmountZar, paid.Reference, paid.Asset, correlationId, ct);

                if (settled is not null)
                {
                    var final = await service.MarkSettledAsync(paid.Id, correlationId, ct);
                    if (final.Invoice is { } done) return Results.Ok(CheckoutResponse.From(done));
                }

                return Results.Ok(CheckoutResponse.From(paid));
            }

            return result.Outcome switch
            {
                InvoiceOutcome.Expired =>
                    Results.Problem(title: "Invoice expired", detail: result.Detail, statusCode: StatusCodes.Status410Gone),

                InvoiceOutcome.DuplicateTransaction =>
                    Results.Problem(title: "Duplicate transaction", detail: result.Detail, statusCode: StatusCodes.Status409Conflict),

                InvoiceOutcome.Underpaid =>
                    Results.Json(new
                    {
                        title = "Underpayment",
                        status = StatusCodes.Status422UnprocessableEntity,
                        detail = result.Detail,
                        invoice = CheckoutResponse.From(result.Invoice!)
                    }, statusCode: StatusCodes.Status422UnprocessableEntity),

                InvoiceOutcome.InvalidTransition =>
                    Results.Problem(title: "Cannot pay this invoice", detail: result.Detail, statusCode: StatusCodes.Status409Conflict),

                _ => Results.Problem(title: "Payment failed", detail: result.Detail, statusCode: StatusCodes.Status400BadRequest)
            };
        })
        .WithName("SimulateCheckoutPayment")
        .WithSummary("DEMO ONLY: stands in for a wallet paying and a chain watcher confirming")
        .Produces<CheckoutResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status410Gone)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }
}

public sealed class SimulatePaymentRequest
{
    [Range(typeof(decimal), "0.00000001", "1000000")]
    public decimal? AmountCrypto { get; set; }

    [StringLength(128)]
    public string? TxHash { get; set; }
}

/// <summary>
/// What a customer is allowed to see. Note what is absent: no merchant id, no
/// internal identifiers beyond the invoice's own, nothing about the merchant's
/// account. A payment link handed to a stranger should reveal the payment and
/// nothing else.
/// </summary>
public sealed record CheckoutResponse(
    Guid Id,
    string Reference,
    decimal AmountZar,
    string Asset,
    decimal CryptoAmount,
    decimal LockedRate,
    string PayToAddress,
    string Status,
    decimal TotalReceived,
    decimal Outstanding,
    int SecondsRemaining,
    DateTime ExpiresAt)
{
    public static CheckoutResponse From(Domain.Invoice i)
    {
        var received = i.Payments.Sum(p => p.AmountCrypto);
        var live = i.Status is InvoiceStatus.Pending or InvoiceStatus.Underpaid;

        return new CheckoutResponse(
            i.Id, i.Reference, i.AmountZar, i.Asset, i.CryptoAmount, i.LockedRate,
            i.PayToAddress, i.Status.ToString(), received,
            Math.Max(0, i.CryptoAmount - received),

            // Computed server side, because the customer's clock cannot be
            // trusted and the countdown must agree with the server that will
            // actually reject a late payment.
            live ? (int)Math.Max(0, (i.ExpiresAt - DateTime.UtcNow).TotalSeconds) : 0,
            i.ExpiresAt);
    }
}
