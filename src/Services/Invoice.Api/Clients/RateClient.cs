using Microsoft.Extensions.Options;
using PandaPocket.Services.Invoice.Configuration;
using PandaPocket.Shared.Contracts;
using PandaPocket.Shared.Contracts.Soc;
using Polly;

namespace PandaPocket.Services.Invoice.Clients;

public sealed record RateQuote(string Pair, decimal Rate, DateTime AsOf);

/// <summary>
/// A quote, and whether it came from Rate or from the cache.
///
/// The caller needs the distinction, not just the number: a fallback rate is
/// recorded on the invoice's audit trail, so months later it is still possible
/// to answer "why was this invoice priced at that". Hiding the difference would
/// make a degraded invoice indistinguishable from a healthy one.
/// </summary>
public sealed record RateResult(RateQuote Quote, bool IsFallback, TimeSpan Staleness);

public interface IRateClient
{
    Task<RateResult?> GetQuoteAsync(string pair, string correlationId, CancellationToken ct);
}

/// <summary>
/// Talks to the Rate service, behind a circuit breaker.
///
/// This is the first of the three call types in this system, and it gets a
/// circuit breaker with a cached fallback because it sits on the critical path
/// of creating an invoice. A merchant's checkout must not hang waiting for a
/// price lookup.
///
/// A breaker on its own would only convert a slow failure into a fast one, which
/// helps the system and not the merchant. The cached rate is what turns it into
/// a degradation: while Rate is down, invoices are still created, priced from
/// the last rate we saw, and marked as such.
/// </summary>
public sealed class RateClient(
    HttpClient http,
    RateCache cache,
    IOptions<InvoiceOptions> options,
    ILogger<RateClient> logger) : IRateClient
{
    private readonly InvoiceOptions _options = options.Value;

    public async Task<RateResult?> GetQuoteAsync(string pair, string correlationId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/rates/{pair}");
            request.Headers.Add(CorrelationHeaders.CorrelationId, correlationId);

            using var response = await http.SendAsync(request, ct);

            // A 404 is Rate working correctly and telling us the pair does not
            // exist. That is not a failure to fall back from: serving a cached
            // rate for a pair Rate has stopped publishing would be worse than
            // refusing, so the cache is deliberately not consulted here.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning("Rate service does not publish pair {Pair}", pair);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var quote = await response.Content.ReadFromJsonAsync<RateQuote>(cancellationToken: ct);
            if (quote is null) return FallBack(pair, correlationId, "empty response");

            cache.Store(quote);
            return new RateResult(quote, IsFallback: false, Staleness: TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                     or TaskCanceledException
                                     or InvalidOperationException
                                     or ExecutionRejectedException)
        {
            // ExecutionRejectedException is the one that is easy to miss, and
            // missing it defeats the whole design.
            //
            // While the breaker is CLOSED, a failing call surfaces as
            // HttpRequestException and the fallback runs. Once the breaker
            // OPENS, it stops calling Rate at all and throws
            // BrokenCircuitException instead, which derives from
            // ExecutionRejectedException and not from HttpRequestException.
            //
            // A catch written only for the underlying transport failure
            // therefore works right up until the breaker trips, and then stops
            // working at precisely the moment the breaker starts doing its job:
            // the first couple of requests degrade gracefully and every one
            // after that returns 500. Catching the base rejection type also
            // covers timeout and rate-limiter strategies if they are added to
            // this pipeline later.
            return FallBack(pair, correlationId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Serve the last known good rate, if there is one and it is recent enough.
    /// </summary>
    private RateResult? FallBack(string pair, string correlationId, string reason)
    {
        var cached = cache.Get(pair);

        if (cached is null)
        {
            // A cold start with Rate already down. There is no honest number to
            // fall back to, so the caller returns 503 rather than inventing one.
            logger.LogError("Rate unavailable for {Pair} ({Reason}) and no cached rate exists", pair, reason);
            return null;
        }

        var staleness = DateTime.UtcNow - cached.AsOf;

        // A ceiling, because a stale rate stops being a degradation and becomes
        // a liability. Locking a merchant to an hour-old crypto price could mean
        // settling an invoice for meaningfully less than the customer paid, and
        // the platform absorbs that difference. Past the ceiling, declining is
        // the cheaper mistake.
        if (staleness > TimeSpan.FromMinutes(_options.MaxFallbackRateAgeMinutes))
        {
            logger.LogError(
                "Cached rate for {Pair} is {Minutes:F1} minutes old, beyond the {Max} minute ceiling; refusing to quote",
                pair, staleness.TotalMinutes, _options.MaxFallbackRateAgeMinutes);
            return null;
        }

        logger.LogWarning(
            "Serving a cached rate for {Pair} ({Reason}); the quote is {Seconds:F0}s old",
            pair, reason, staleness.TotalSeconds);

        var soc = SocEvent.Create(
            SocEventType.CircuitOpened, SocSeverity.Warning, correlationId,
            metadata: new Dictionary<string, object?>
            {
                ["dependency"] = "rate-service",
                ["pair"] = pair,
                ["reason"] = reason,
                ["fallbackRate"] = cached.Rate,
                ["stalenessSeconds"] = Math.Round(staleness.TotalSeconds, 1)
            });

        logger.LogWarning("SOC {EventType} {@SocEvent}", SocEventType.CircuitOpened, soc);

        return new RateResult(cached, IsFallback: true, Staleness: staleness);
    }
}
