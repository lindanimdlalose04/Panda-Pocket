using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using PandaPocket.Services.Settlement.Contracts;
using PandaPocket.Services.Settlement.Domain;
using PandaPocket.Services.Settlement.Persistence;
using PandaPocket.Shared.Contracts;
using PandaPocket.Shared.Contracts.Observability;

namespace PandaPocket.Services.Settlement.Endpoints;

public static class SettlementEndpoints
{
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapSettlementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settlements").WithTags("Settlements");

        // -------------------------------------------------------------------
        // Settle. Called by Invoice when a payment is confirmed.
        // -------------------------------------------------------------------
        group.MapPost("/", async (
            SettleInvoiceRequest request, SettlementService service, HttpContext ctx, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem) return problem;

            var (result, error, alreadySettled) = await service.SettleAsync(request, ctx.GetCorrelationId(), ct);

            if (result is null)
            {
                return Results.Problem(title: "Settlement failed", detail: error,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // 200 rather than 201 when it was already settled. The caller's
            // desired state holds either way, which is what makes this endpoint
            // safe for Invoice to retry, but the distinction is honest about
            // whether this particular call created anything.
            return alreadySettled
                ? Results.Ok(result)
                : Results.Created($"/api/settlements/{request.MerchantId}/ledger", result);
        })
        .WithName("SettleInvoice")
        .WithSummary("Credit a merchant for a paid invoice and queue the webhook. Idempotent per invoice.")
        .Produces<SettlementResponse>(StatusCodes.Status201Created)
        .Produces<SettlementResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // -------------------------------------------------------------------
        // Balance
        // -------------------------------------------------------------------
        group.MapGet("/{merchantId:guid}/balance", async (
            Guid merchantId, SettlementService service, HttpContext ctx, CancellationToken ct) =>
        {
            if (Forbidden(ctx, merchantId) is { } forbidden) return forbidden;

            var b = await service.GetBalanceAsync(merchantId, ct);
            return Results.Ok(new BalanceResponse(
                b.MerchantId, b.AvailableZar, b.LifetimeCreditedZar, b.LifetimeFeesZar, b.UpdatedAt));
        })
        .WithName("GetBalance")
        .WithSummary("Current available ZAR for a merchant")
        .Produces<BalanceResponse>()
        .ProducesProblem(StatusCodes.Status403Forbidden);

        // -------------------------------------------------------------------
        // Ledger
        // -------------------------------------------------------------------
        group.MapGet("/{merchantId:guid}/ledger", async (
            Guid merchantId, int? page, int? pageSize,
            SettlementDbContext db, HttpContext ctx, CancellationToken ct) =>
        {
            if (Forbidden(ctx, merchantId) is { } forbidden) return forbidden;

            var pageNumber = page is null or < 1 ? 1 : page.Value;
            var size = pageSize is null or < 1 or > MaxPageSize ? 50 : pageSize.Value;

            var query = db.LedgerEntries.AsNoTracking().Where(l => l.MerchantId == merchantId);
            var total = await query.CountAsync(ct);

            var entries = await query
                .OrderByDescending(l => l.CreatedAt)
                .Skip((pageNumber - 1) * size)
                .Take(size)
                .Select(l => new LedgerEntryResponse(
                    l.Id, l.InvoiceId, l.EntryType.ToString(), l.AmountZar, l.BalanceAfter,
                    l.Description, l.CorrelationId, l.CreatedAt))
                .ToListAsync(ct);

            return Results.Ok(new LedgerResponse(merchantId, pageNumber, size, total, entries));
        })
        .WithName("GetLedger")
        .WithSummary("The merchant's ZAR statement, newest first")
        .Produces<LedgerResponse>()
        .ProducesProblem(StatusCodes.Status403Forbidden);

        // -------------------------------------------------------------------
        // Reconciliation
        // -------------------------------------------------------------------
        group.MapGet("/{merchantId:guid}/reconcile", async (
            Guid merchantId, SettlementService service, HttpContext ctx, CancellationToken ct) =>
        {
            if (Forbidden(ctx, merchantId) is { } forbidden) return forbidden;

            var (stored, recomputed, matches) = await service.ReconcileAsync(merchantId, ct);
            return Results.Ok(new ReconciliationResponse(merchantId, stored, recomputed, matches));
        })
        .WithName("Reconcile")
        .WithSummary("Prove the stored balance equals the sum of the ledger")
        .Produces<ReconciliationResponse>();

        // -------------------------------------------------------------------
        // Webhook deliveries
        // -------------------------------------------------------------------
        group.MapGet("/webhooks", async (
            Guid? merchantId, string? status, SettlementDbContext db, HttpContext ctx, CancellationToken ct) =>
        {
            var caller = MerchantIdOf(ctx);
            var target = caller ?? merchantId;

            if (caller is not null && merchantId is not null && caller != merchantId)
            {
                return Results.Problem(title: "Forbidden", detail: "You may only view your own deliveries.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var query = db.WebhookDeliveries.AsNoTracking().AsQueryable();
            if (target is { } m) query = query.Where(w => w.MerchantId == m);

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<WebhookStatus>(status, ignoreCase: true, out var parsed))
                {
                    return Results.Problem(title: "Unknown status",
                        detail: $"Valid values: {string.Join(", ", Enum.GetNames<WebhookStatus>())}.",
                        statusCode: StatusCodes.Status400BadRequest);
                }
                query = query.Where(w => w.Status == parsed);
            }

            var items = await query
                .OrderByDescending(w => w.CreatedAt)
                .Take(100)
                .Select(w => new WebhookDeliveryResponse(
                    w.Id, w.MerchantId, w.InvoiceId, w.Url, w.EventType, w.Status.ToString(),
                    w.AttemptCount, w.LastStatusCode, w.LastError, w.NextAttemptAt, w.CreatedAt, w.DeliveredAt))
                .ToListAsync(ct);

            return Results.Ok(items);
        })
        .WithName("ListWebhookDeliveries")
        .WithSummary("Delivery log with attempt counts, errors and next scheduled attempt")
        .Produces<List<WebhookDeliveryResponse>>();

        // Manual retry, for a delivery that dead-lettered after a merchant fixed
        // whatever was broken. Resets the schedule rather than the attempt count,
        // so the delivery history stays intact.
        group.MapPost("/webhooks/{id:guid}/retry", async (
            Guid id, SettlementDbContext db, HttpContext ctx, CancellationToken ct) =>
        {
            var delivery = await db.WebhookDeliveries.FirstOrDefaultAsync(w => w.Id == id, ct);
            if (delivery is null) return Results.Problem(title: "Delivery not found", statusCode: StatusCodes.Status404NotFound);

            if (Forbidden(ctx, delivery.MerchantId) is { } forbidden) return forbidden;

            if (delivery.Status == WebhookStatus.Delivered)
            {
                return Results.Problem(title: "Already delivered",
                    detail: "This webhook was delivered successfully and does not need retrying.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            delivery.Status = WebhookStatus.Pending;
            delivery.AttemptCount = 0;
            delivery.NextAttemptAt = DateTime.UtcNow;
            delivery.LastError = null;
            await db.SaveChangesAsync(ct);

            return Results.Accepted($"/api/settlements/webhooks", new { delivery.Id, status = "Pending", queued = true });
        })
        .WithName("RetryWebhook")
        .WithSummary("Requeue a failed delivery")
        .Produces(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static Guid? MerchantIdOf(HttpContext ctx) =>
        Guid.TryParse(ctx.Request.Headers[CorrelationHeaders.MerchantId].FirstOrDefault(), out var id) ? id : null;

    /// <summary>
    /// When the request arrived through the gateway it carries an authenticated
    /// merchant, and that merchant may only see its own money. Calls without the
    /// header come from inside the network, such as Invoice settling an invoice,
    /// and are not scoped.
    /// </summary>
    private static IResult? Forbidden(HttpContext ctx, Guid merchantId)
    {
        var caller = MerchantIdOf(ctx);
        if (caller is null || caller == merchantId) return null;

        return Results.Problem(
            title: "Forbidden",
            detail: "This account may only access its own settlement data.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static IResult? Validate<T>(T model) where T : class
    {
        var context = new ValidationContext(model);
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(model, context, results, validateAllProperties: true)) return null;

        var errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty), (r, n) => (n, r.ErrorMessage))
            .GroupBy(x => x.n, x => x.ErrorMessage ?? "Invalid")
            .ToDictionary(g => g.Key, g => g.ToArray());

        return Results.ValidationProblem(errors);
    }
}
