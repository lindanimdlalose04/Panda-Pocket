using PandaPocket.Shared.Contracts;

namespace PandaPocket.Services.Invoice.Clients;

public sealed record SettlementResult(Guid InvoiceId, decimal GrossZar, decimal FeeZar, decimal NetZar, decimal BalanceAfterZar);

public interface ISettlementClient
{
    Task<SettlementResult?> SettleAsync(
        Guid invoiceId, Guid merchantId, decimal amountZar, string reference, string asset,
        string correlationId, CancellationToken ct);
}

/// <summary>
/// Tells Settlement to credit the merchant for a paid invoice.
///
/// This is the second of the three call types in this system, and it gets a
/// different resilience strategy from the other two on purpose.
///
/// Invoice to Rate is on the critical path of creating an invoice and must fail
/// fast, so it gets a circuit breaker and a cached fallback: better a slightly
/// stale rate than a checkout that hangs.
///
/// Invoice to Settlement is different. It is money. Losing it means a merchant
/// was paid and never credited, which is the worst failure this system has. So
/// it retries rather than failing fast, and the endpoint is idempotent per
/// invoice specifically so that retrying is safe. A duplicate call returns the
/// existing settlement instead of crediting twice.
///
/// Settlement to the merchant's webhook is the third: external, untrusted, and
/// handled with durable backoff and dead-lettering in the Settlement service.
/// </summary>
public sealed class SettlementClient(HttpClient http, ILogger<SettlementClient> logger) : ISettlementClient
{
    private const int MaxAttempts = 3;

    public async Task<SettlementResult?> SettleAsync(
        Guid invoiceId, Guid merchantId, decimal amountZar, string reference, string asset,
        string correlationId, CancellationToken ct)
    {
        var payload = new { invoiceId, merchantId, amountZar, reference, asset };
        var delay = TimeSpan.FromMilliseconds(500);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/settlements")
                {
                    Content = JsonContent.Create(payload)
                };
                request.Headers.Add(CorrelationHeaders.CorrelationId, correlationId);

                using var response = await http.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<SettlementResult>(cancellationToken: ct);
                }

                // A 4xx means the request itself is wrong, and sending it again
                // will produce the same answer. Only server-side and transport
                // failures are worth retrying.
                if ((int)response.StatusCode is >= 400 and < 500)
                {
                    logger.LogError("Settlement rejected invoice {InvoiceId} with {StatusCode}; not retrying",
                        invoiceId, (int)response.StatusCode);
                    return null;
                }

                logger.LogWarning("Settlement attempt {Attempt}/{Max} for invoice {InvoiceId} returned {StatusCode}",
                    attempt, MaxAttempts, invoiceId, (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Settlement attempt {Attempt}/{Max} for invoice {InvoiceId} failed",
                    attempt, MaxAttempts, invoiceId);
            }

            if (attempt < MaxAttempts)
            {
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }

        // Exhausted here, but not lost. The invoice stays Paid rather than
        // Settled, and the settlement sweeper picks it up later. That is the
        // difference between a retry that gives up and one that defers.
        logger.LogError("Settlement could not be reached for invoice {InvoiceId} after {Max} attempts; " +
                        "the invoice remains Paid and will be retried by the sweeper", invoiceId, MaxAttempts);
        return null;
    }
}
