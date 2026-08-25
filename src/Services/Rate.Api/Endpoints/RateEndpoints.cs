using MongoDB.Driver;
using PandaPocket.Services.Rate.Contracts;
using PandaPocket.Services.Rate.Domain;
using PandaPocket.Services.Rate.Persistence;
using PandaPocket.Shared.Contracts.Observability;

namespace PandaPocket.Services.Rate.Endpoints;

public static class RateEndpoints
{
    /// <summary>
    /// A history request cannot be allowed to pull the whole collection. This is
    /// the ceiling regardless of what window the caller asks for.
    /// </summary>
    private const int MaxHistoryPoints = 5000;

    private static readonly TimeSpan DefaultHistoryWindow = TimeSpan.FromHours(1);

    public static IEndpointRouteBuilder MapRateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rates").WithTags("Rates");

        group.MapGet("/", (RateBook book) =>
        {
            var quotes = book.GetAll()
                .Select(r => new RateQuoteResponse(r.Pair, r.Rate, r.AsOf))
                .ToList();

            return Results.Ok(quotes);
        })
        .WithName("GetAllRates")
        .WithSummary("Current rate for every configured pair")
        .Produces<List<RateQuoteResponse>>();

        group.MapGet("/{pair}", (string pair, RateBook book, ILogger<Program> logger, HttpContext ctx) =>
        {
            var entry = book.Get(pair);

            if (entry is null)
            {
                // A 404 rather than a zero rate. Returning zero would let Invoice
                // compute a crypto amount of infinity for a typo in the asset
                // name, which is the kind of failure that should be loud.
                logger.LogWarning("Quote requested for unknown pair {Pair} (correlation {CorrelationId})",
                    pair, ctx.GetCorrelationId());

                return Results.Problem(
                    title: "Unknown pair",
                    detail: $"No rate is published for pair '{pair}'.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(new RateQuoteResponse(entry.Pair, entry.Rate, entry.AsOf));
        })
        .WithName("GetRate")
        .WithSummary("Current rate for one pair, as used by the Invoice service")
        .Produces<RateQuoteResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{pair}/history", async (
            string pair,
            DateTime? from,
            DateTime? to,
            RateBook book,
            ITickRepository repository,
            ILogger<Program> logger,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (book.Get(pair) is null)
            {
                return Results.Problem(
                    title: "Unknown pair",
                    detail: $"No rate is published for pair '{pair}'.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var toUtc = ToUtc(to) ?? DateTime.UtcNow;
            var fromUtc = ToUtc(from) ?? toUtc - DefaultHistoryWindow;

            if (fromUtc > toUtc)
            {
                return Results.Problem(
                    title: "Invalid range",
                    detail: "'from' must not be later than 'to'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var ticks = await repository.GetHistoryAsync(pair, fromUtc, toUtc, MaxHistoryPoints, ct);

                var points = ticks
                    .Select(t => new RateHistoryPoint(t.Rate, t.Ts, t.Source))
                    .ToList();

                return Results.Ok(new RateHistoryResponse(pair, fromUtc, toUtc, points.Count, points));
            }
            // Server selection timeouts surface as System.TimeoutException, which
            // does not derive from MongoException, so catching only the latter
            // misses the most common failure: the database being unreachable.
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                // History needs the database; quotes do not. Saying so plainly
                // with a 503 is more useful to a caller than a 500, because it
                // distinguishes "this service is broken" from "this dependency
                // is down, the rest of the service still works". Retry-After
                // tells a client how long to wait rather than leaving it to
                // hammer the endpoint.
                logger.LogError(ex, "History query failed for {Pair} (correlation {CorrelationId})",
                    pair, ctx.GetCorrelationId());

                ctx.Response.Headers.RetryAfter = "10";

                return Results.Problem(
                    title: "Rate history unavailable",
                    detail: "The tick store is not reachable. Current quotes remain available at /api/rates.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .WithName("GetRateHistory")
        .WithSummary("Tick history for one pair, newest first, served from MongoDB")
        .Produces<RateHistoryResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>
    /// Query string values arrive as unspecified or local kind depending on
    /// whether the caller wrote a Z suffix. Ticks are stored in UTC, so
    /// normalise rather than comparing values of mixed kinds.
    /// </summary>
    private static DateTime? ToUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } => value,
        { Kind: DateTimeKind.Local } => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
    };
}
