using PandaPocket.Shared.Contracts;

namespace PandaPocket.Services.Invoice.Clients;

public sealed record RateQuote(string Pair, decimal Rate, DateTime AsOf);

public interface IRateClient
{
    /// <summary>Returns null when Rate cannot be reached or does not know the pair.</summary>
    Task<RateQuote?> GetQuoteAsync(string pair, string correlationId, CancellationToken ct);
}

/// <summary>
/// Talks to the Rate service.
///
/// This is the call that gets a circuit breaker with a cached fallback on day 6.
/// It is on the critical path of creating an invoice, so it must fail quickly
/// rather than holding a request open: a merchant's checkout should not hang
/// because a rate lookup is slow. The timeout is set on the registered
/// HttpClient in Program.cs for that reason.
///
/// The correlation id is forwarded on every call. That is what makes one
/// payment traceable across Invoice and Rate when filtering Seq by a single id.
/// </summary>
public sealed class RateClient(HttpClient http, ILogger<RateClient> logger) : IRateClient
{
    public async Task<RateQuote?> GetQuoteAsync(string pair, string correlationId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/rates/{pair}");
        request.Headers.Add(CorrelationHeaders.CorrelationId, correlationId);

        try
        {
            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning("Rate service does not publish pair {Pair}", pair);
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<RateQuote>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Swallowed and reported as null rather than thrown, because the
            // caller's decision is a 503 to the merchant, not a stack trace. Day
            // 6 replaces this with a breaker plus a cached rate so that a Rate
            // outage degrades the service instead of stopping it.
            logger.LogError(ex, "Rate service unreachable when quoting {Pair}", pair);
            return null;
        }
    }
}
