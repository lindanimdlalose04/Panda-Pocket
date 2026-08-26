using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PandaPocket.Shared.Contracts;

namespace PandaPocket.Services.Settlement.Clients;

public sealed record MerchantDetails(
    Guid Id, string BusinessName, decimal FeePercent, string? WebhookUrl, string? WebhookSecret);

public interface IMerchantClient
{
    Task<MerchantDetails?> GetAsync(Guid merchantId, string correlationId, CancellationToken ct);
}

/// <summary>
/// Reads merchant configuration from the service that owns it.
///
/// Settlement needs three things it does not own: the fee percentage, the
/// webhook URL and the signing secret. Copying them into settlement_db would be
/// faster and would immediately go stale the moment a merchant changed their
/// webhook, so they are fetched instead. Database-per-service means asking the
/// owner, not reaching into their tables.
///
/// Results are cached briefly, because a settlement plus every webhook attempt
/// for it would otherwise each be a separate call for data that changes rarely.
/// </summary>
public sealed class MerchantClient(
    HttpClient http,
    IMemoryCacheAdapter cache,
    ILogger<MerchantClient> logger) : IMerchantClient
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    public async Task<MerchantDetails?> GetAsync(Guid merchantId, string correlationId, CancellationToken ct)
    {
        if (cache.TryGet(merchantId, out var cached)) return cached;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/internal/merchants/{merchantId}");
        request.Headers.Add(CorrelationHeaders.CorrelationId, correlationId);

        try
        {
            using var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Merchant {MerchantId} lookup returned {StatusCode}", merchantId, (int)response.StatusCode);
                return null;
            }

            var details = await response.Content.ReadFromJsonAsync<MerchantDetails>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);

            if (details is not null) cache.Set(merchantId, details, CacheDuration);
            return details;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Merchant service unreachable while resolving {MerchantId}", merchantId);
            return null;
        }
    }
}

/// <summary>
/// A thin wrapper so the cache can be swapped or stubbed without the client
/// taking a hard dependency on IMemoryCache.
/// </summary>
public interface IMemoryCacheAdapter
{
    bool TryGet(Guid merchantId, out MerchantDetails? details);
    void Set(Guid merchantId, MerchantDetails details, TimeSpan duration);
}

public sealed class MemoryCacheAdapter(IMemoryCache cache) : IMemoryCacheAdapter
{
    public bool TryGet(Guid merchantId, out MerchantDetails? details)
    {
        details = cache.Get<MerchantDetails>("merchant:" + merchantId);
        return details is not null;
    }

    public void Set(Guid merchantId, MerchantDetails details, TimeSpan duration) =>
        cache.Set("merchant:" + merchantId, details, duration);
}
