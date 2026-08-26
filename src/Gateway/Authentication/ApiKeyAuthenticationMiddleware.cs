using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PandaPocket.Shared.Contracts;
using PandaPocket.Shared.Contracts.Observability;
using PandaPocket.Shared.Contracts.Soc;

namespace PandaPocket.Gateway.Authentication;

public sealed record MerchantIdentity(Guid MerchantId, string BusinessName, decimal FeePercent);

/// <summary>
/// Authenticates every request that reaches a protected route, and stamps the
/// resulting merchant identity onto the request for services downstream.
///
/// This middleware is the reason the Invoice service can trust X-Merchant-Id.
/// Two things make that trustworthy, and both matter:
///
/// First, the header is STRIPPED from every inbound request before anything
/// else happens. Without that, a caller could simply send their own
/// X-Merchant-Id and act as any merchant they liked, and the fact that a valid
/// API key was also required would not help, because the key would be theirs and
/// the header would name somebody else. Stripping first is what makes the header
/// an assertion by the gateway rather than by the client.
///
/// Second, the identity is resolved from the key by the Merchant service, which
/// owns that data. The gateway never reads the merchant database itself.
/// </summary>
public sealed class ApiKeyAuthenticationMiddleware(
    RequestDelegate next,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ILogger<ApiKeyAuthenticationMiddleware> logger)
{
    /// <summary>
    /// Paths this middleware does not API-key check. Three different reasons,
    /// worth separating because "not key checked" is not the same as "public".
    ///
    /// Genuinely public: /health, /api/rates, and the client's own assets. Rates
    /// are market data. A checkout page must display a price before anyone has
    /// authenticated, nothing about a rate is specific to a merchant, and BitPay
    /// and Coinbase Commerce both publish theirs openly. Requiring a credential
    /// would protect nothing and would force the browser to hold a key just to
    /// draw a number.
    ///
    /// Necessarily anonymous: /api/auth/login and merchant sign-up. There is no
    /// credential to present yet; that is the point of the request.
    ///
    /// Protected, but by a different mechanism: /api/merchants and
    /// /api/api-keys are dashboard operations, authenticated with a JWT that the
    /// Merchant service validates itself. The two credential types are not
    /// interchangeable by design. A server holds a long-lived API key because it
    /// cannot retype a password; a person holds a short-lived token because a
    /// browser session should not outlive them. Letting an API key manage API
    /// keys would mean a leaked key could mint replacements for itself and
    /// revoke the real ones.
    /// </summary>
    private static readonly string[] AnonymousPaths =
    [
        "/health",
        "/api/rates",
        "/swagger",
        "/favicon.ico",
        "/api/auth",
        "/api/merchants",
        "/api/api-keys"
    ];

    /// <summary>
    /// How long a successful validation is reused.
    ///
    /// Without this, every single API call becomes two network hops and a
    /// database read. With it, a burst of traffic from one merchant costs one
    /// validation. The cost is that a revoked key keeps working for up to this
    /// long, so the window is deliberately short: thirty seconds is long enough
    /// to absorb a burst and short enough that revocation still means something.
    /// Failures are never cached, so a brute force attempt gets no cheaper.
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async Task InvokeAsync(HttpContext context)
    {
        // Strip first, unconditionally, before any routing decision. See above.
        context.Request.Headers.Remove(CorrelationHeaders.MerchantId);

        if (IsAnonymous(context.Request.Path))
        {
            await next(context);
            return;
        }

        var correlationId = context.GetCorrelationId();
        var apiKey = context.Request.Headers[CorrelationHeaders.ApiKey].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await RejectAsync(context, correlationId, "missing_key",
                "An API key is required. Send it in the X-API-Key header.");
            return;
        }

        var identity = await ResolveAsync(apiKey, correlationId, context.RequestAborted);

        if (identity is null)
        {
            await RejectAsync(context, correlationId, "invalid_key",
                "The API key is not valid.");
            return;
        }

        // Now the header can be trusted downstream, because only this line sets it.
        context.Request.Headers[CorrelationHeaders.MerchantId] = identity.MerchantId.ToString();
        context.Items["MerchantIdentity"] = identity;

        await next(context);
    }

    private static bool IsAnonymous(PathString path) =>
        AnonymousPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))
        || path == "/" || !path.HasValue;

    private async Task<MerchantIdentity?> ResolveAsync(string apiKey, string correlationId, CancellationToken ct)
    {
        // Keyed by the key itself, which never leaves this process. Caching by
        // hash would be no safer and would cost a hash per request.
        var cacheKey = "apikey:" + apiKey;

        if (cache.TryGetValue<MerchantIdentity>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var client = httpClientFactory.CreateClient("merchant");

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/internal/keys/validate")
            {
                Content = JsonContent.Create(new { apiKey })
            };
            request.Headers.Add(CorrelationHeaders.CorrelationId, correlationId);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var result = await response.Content.ReadFromJsonAsync<ValidateKeyResult>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);

            if (result is null || !result.Valid || result.MerchantId is null) return null;

            var identity = new MerchantIdentity(
                result.MerchantId.Value, result.BusinessName ?? "unknown", result.FeePercent ?? 0m);

            // Only successes are cached. Caching a rejection would let an
            // attacker probe cheaply and would make a newly issued key appear
            // broken for the length of the window.
            cache.Set(cacheKey, identity, CacheDuration);
            return identity;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Merchant is unreachable. The request is refused rather than let
            // through: failing open on an authentication check would mean an
            // outage in one service silently removed authentication from the
            // entire system.
            logger.LogError(ex, "Merchant service unreachable during key validation");
            return null;
        }
    }

    private async Task RejectAsync(HttpContext context, string correlationId, string reason, string detail)
    {
        var soc = SocEvent.Create(
            reason == "missing_key" ? SocEventType.AuthFailed : SocEventType.ApiKeyInvalid,
            SocSeverity.Warning,
            correlationId,
            metadata: new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["path"] = context.Request.Path.Value,
                ["method"] = context.Request.Method,

                // The caller's address, so the SOC layer can spot one source
                // trying many keys. This is the raw socket address; behind a real
                // load balancer it would come from a forwarded header.
                ["remoteIp"] = context.Connection.RemoteIpAddress?.ToString()
            });

        logger.LogWarning("SOC {EventType} {@SocEvent}", soc.EventType, soc);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
            title = "Unauthorized",
            status = 401,
            detail,
            correlationId
        });
    }

    private sealed record ValidateKeyResult(
        bool Valid, Guid? MerchantId, string? BusinessName, decimal? FeePercent, string? Reason);
}

public static class ApiKeyAuthenticationExtensions
{
    public static IApplicationBuilder UseApiKeyAuthentication(this IApplicationBuilder app) =>
        app.UseMiddleware<ApiKeyAuthenticationMiddleware>();
}
