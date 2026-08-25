using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using PandaPocket.Services.Invoice.Contracts;
using PandaPocket.Services.Invoice.Domain;
using PandaPocket.Services.Invoice.Persistence;
using PandaPocket.Shared.Contracts;
using PandaPocket.Shared.Contracts.Invoicing;
using PandaPocket.Shared.Contracts.Observability;

namespace PandaPocket.Services.Invoice.Endpoints;

public static class InvoiceEndpoints
{
    private const int MaxPageSize = 100;

    /// <summary>
    /// Stands in for the merchant the gateway will resolve from an API key on
    /// day 4. Until then a request may name its own merchant, and this is the
    /// fallback when it does not.
    /// </summary>
    private static readonly Guid DemoMerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/invoices").WithTags("Invoices");

        // -------------------------------------------------------------------
        // Create
        // -------------------------------------------------------------------
        group.MapPost("/", async (
            CreateInvoiceRequest request,
            InvoiceService service,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (TryValidate(request) is { } validationProblem) return validationProblem;

            var correlationId = ctx.GetCorrelationId();

            // Day 4 replaces this with the merchant id the gateway puts on the
            // request after validating the API key.
            var merchantId = ResolveMerchantId(ctx, request.MerchantId);

            var result = await service.CreateAsync(
                merchantId, request.Reference, request.AmountZar, request.Asset, correlationId, ct);

            return result.Outcome switch
            {
                InvoiceOutcome.Success =>
                    Results.Created($"/api/invoices/{result.Invoice!.Id}", InvoiceResponse.From(result.Invoice)),

                // 503 rather than 500: the Invoice service is working perfectly,
                // its dependency is not, and a merchant integration should retry
                // rather than treat this as a permanent failure.
                InvoiceOutcome.RateUnavailable =>
                    Problem("Rate service unavailable", result.Detail, StatusCodes.Status503ServiceUnavailable),

                InvoiceOutcome.UnknownAsset =>
                    Problem("Unknown asset", result.Detail, StatusCodes.Status400BadRequest),

                InvoiceOutcome.DuplicateReference =>
                    Problem("Duplicate reference", result.Detail, StatusCodes.Status409Conflict),

                _ => Problem("Could not create invoice", result.Detail, StatusCodes.Status400BadRequest)
            };
        })
        .WithName("CreateInvoice")
        .WithSummary("Create an invoice, locking a conversion rate for the payment window")
        .Produces<InvoiceResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // -------------------------------------------------------------------
        // Read one
        // -------------------------------------------------------------------
        group.MapGet("/{id:guid}", async (Guid id, InvoiceDbContext db, CancellationToken ct) =>
        {
            var invoice = await db.Invoices
                .AsNoTracking()
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == id, ct);

            return invoice is null
                ? Problem("Invoice not found", $"No invoice with id {id}.", StatusCodes.Status404NotFound)
                : Results.Ok(InvoiceResponse.From(invoice));
        })
        .WithName("GetInvoice")
        .WithSummary("Fetch a single invoice, including how much has been received")
        .Produces<InvoiceResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        // -------------------------------------------------------------------
        // List
        // -------------------------------------------------------------------
        group.MapGet("/", async (
            Guid? merchantId,
            string? status,
            int? page,
            int? pageSize,
            InvoiceDbContext db,
            CancellationToken ct) =>
        {
            // Nullable on purpose. A non-nullable int is a *required* query
            // parameter in minimal APIs, so GET /api/invoices with no query
            // string would return 400 telling the caller that "page" was not
            // provided. Paging should have sensible defaults, not be mandatory.
            var pageNumber = page is null or < 1 ? 1 : page.Value;
            var size = pageSize is null or < 1 or > MaxPageSize ? 20 : pageSize.Value;

            var query = db.Invoices.AsNoTracking().Include(i => i.Payments).AsQueryable();

            if (merchantId is { } m) query = query.Where(i => i.MerchantId == m);

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<InvoiceStatus>(status, ignoreCase: true, out var parsed))
                {
                    return Problem("Unknown status",
                        $"'{status}' is not a valid invoice status. Valid values: {string.Join(", ", Enum.GetNames<InvoiceStatus>())}.",
                        StatusCodes.Status400BadRequest);
                }

                query = query.Where(i => i.Status == parsed);
            }

            var total = await query.CountAsync(ct);

            var items = await query
                .OrderByDescending(i => i.CreatedAt)
                .Skip((pageNumber - 1) * size)
                .Take(size)
                .ToListAsync(ct);

            return Results.Ok(new InvoiceListResponse(pageNumber, size, total,
                items.Select(InvoiceResponse.From).ToList()));
        })
        .WithName("ListInvoices")
        .WithSummary("List invoices, filtered by merchant and status, newest first")
        .Produces<InvoiceListResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        // -------------------------------------------------------------------
        // Payment
        // -------------------------------------------------------------------
        group.MapPost("/{id:guid}/payments", async (
            Guid id,
            RecordPaymentRequest request,
            InvoiceService service,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (TryValidate(request) is { } validationProblem) return validationProblem;

            var result = await service.RecordPaymentAsync(
                id, request.TxHash, request.AmountCrypto, ctx.GetCorrelationId(), ct);

            return result.Outcome switch
            {
                InvoiceOutcome.Success => Results.Ok(InvoiceResponse.From(result.Invoice!)),

                InvoiceOutcome.NotFound =>
                    Problem("Invoice not found", $"No invoice with id {id}.", StatusCodes.Status404NotFound),

                // 410 Gone, not 404 or 400. The invoice existed and the caller
                // may well have had a valid quote; what has changed is that the
                // window closed. That is precisely what Gone means, and it tells
                // the integration to request a fresh invoice rather than retry.
                InvoiceOutcome.Expired =>
                    Problem("Invoice expired", result.Detail, StatusCodes.Status410Gone),

                // 409 Conflict. The request is well formed, but this transaction
                // hash is already recorded, so accepting it would double-credit.
                InvoiceOutcome.DuplicateTransaction =>
                    Problem("Duplicate transaction", result.Detail, StatusCodes.Status409Conflict),

                // 422 Unprocessable. The payment was understood and stored, but
                // it does not satisfy the invoice. Distinct from 400, which
                // would imply the request itself was malformed.
                InvoiceOutcome.Underpaid =>
                    Results.Json(new
                    {
                        type = "https://tools.ietf.org/html/rfc9110#section-15.5.21",
                        title = "Underpayment",
                        status = StatusCodes.Status422UnprocessableEntity,
                        detail = result.Detail,
                        invoice = InvoiceResponse.From(result.Invoice!)
                    }, statusCode: StatusCodes.Status422UnprocessableEntity),

                InvoiceOutcome.InvalidTransition =>
                    Problem("Invalid state transition", result.Detail, StatusCodes.Status409Conflict),

                _ => Problem("Payment could not be recorded", result.Detail, StatusCodes.Status400BadRequest)
            };
        })
        .WithName("RecordPayment")
        .WithSummary("Confirm a payment against an invoice, standing in for a chain confirmation")
        .Produces<InvoiceResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status410Gone)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // -------------------------------------------------------------------
        // Cancel
        //
        // An action endpoint, not a PATCH on the status field. A state
        // transition is not a field edit, and exposing status as writable would
        // let a client set any value it liked, including Settled. Stripe uses
        // the same shape with POST /v1/invoices/{id}/void.
        // -------------------------------------------------------------------
        group.MapPost("/{id:guid}/cancel", async (
            Guid id,
            CancelInvoiceRequest? request,
            InvoiceService service,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var result = await service.CancelAsync(
                id, request?.Reason ?? "Cancelled by merchant", ctx.GetCorrelationId(), ct);

            return result.Outcome switch
            {
                InvoiceOutcome.Success => Results.Ok(InvoiceResponse.From(result.Invoice!)),
                InvoiceOutcome.NotFound => Problem("Invoice not found", $"No invoice with id {id}.", StatusCodes.Status404NotFound),
                InvoiceOutcome.InvalidTransition => Problem("Invalid state transition", result.Detail, StatusCodes.Status409Conflict),
                _ => Problem("Could not cancel invoice", result.Detail, StatusCodes.Status400BadRequest)
            };
        })
        .WithName("CancelInvoice")
        .WithSummary("Cancel a pending invoice")
        .Produces<InvoiceResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // -------------------------------------------------------------------
        // History
        // -------------------------------------------------------------------
        group.MapGet("/{id:guid}/history", async (Guid id, InvoiceDbContext db, CancellationToken ct) =>
        {
            var exists = await db.Invoices.AnyAsync(i => i.Id == id, ct);
            if (!exists) return Problem("Invoice not found", $"No invoice with id {id}.", StatusCodes.Status404NotFound);

            var history = await db.StatusHistory
                .AsNoTracking()
                .Where(h => h.InvoiceId == id)
                .OrderBy(h => h.CreatedAt)
                .Select(h => new StatusHistoryEntry(
                    h.FromStatus == null ? null : h.FromStatus.ToString(),
                    h.ToStatus.ToString(),
                    h.Reason,
                    h.CorrelationId,
                    h.CreatedAt))
                .ToListAsync(ct);

            return Results.Ok(new StatusHistoryResponse(id, history));
        })
        .WithName("GetInvoiceHistory")
        .WithSummary("The full audit trail of state transitions for one invoice")
        .Produces<StatusHistoryResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static Guid ResolveMerchantId(HttpContext ctx, Guid? fromBody)
    {
        // The gateway will set this header once API keys exist. Reading it now
        // means day 4 is a change at the gateway rather than a change here.
        var header = ctx.Request.Headers[CorrelationHeaders.MerchantId].FirstOrDefault();
        if (Guid.TryParse(header, out var fromHeader)) return fromHeader;

        return fromBody ?? DemoMerchantId;
    }

    private static IResult Problem(string title, string? detail, int statusCode) =>
        Results.Problem(title: title, detail: detail, statusCode: statusCode);

    /// <summary>
    /// Minimal APIs do not run data annotation validation automatically, so it
    /// is invoked explicitly. Returning 400 with the specific field errors is
    /// considerably more useful to an integrator than a generic failure.
    /// </summary>
    private static IResult? TryValidate<T>(T model) where T : class
    {
        var context = new ValidationContext(model);
        var results = new List<ValidationResult>();

        if (Validator.TryValidateObject(model, context, results, validateAllProperties: true))
        {
            return null;
        }

        var errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty), (r, name) => (name, r.ErrorMessage))
            .GroupBy(x => x.name, x => x.ErrorMessage ?? "Invalid")
            .ToDictionary(g => g.Key, g => g.ToArray());

        return Results.ValidationProblem(errors);
    }
}
