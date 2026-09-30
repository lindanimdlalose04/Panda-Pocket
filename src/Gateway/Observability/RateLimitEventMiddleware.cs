using PandaPocket.Shared.Contracts;
using PandaPocket.Shared.Contracts.Observability;
using PandaPocket.Shared.Contracts.Soc;

namespace PandaPocket.Gateway.Observability;

/// <summary>
/// Raises RATE_LIMIT_EXCEEDED when Ocelot throttles a request.
///
/// Ocelot enforces its own limits and writes the 429 itself, without any hook to
/// observe it, so this sits above Ocelot in the pipeline and inspects the status
/// code on the way back out. That works because although Ocelot is terminal and
/// never calls the next middleware, the middleware that called it still resumes
/// once Ocelot has finished writing.
///
/// The alternative would be re-implementing rate limiting here purely to be able
/// to log it, which would mean two limiters disagreeing about the same request.
///
/// A merchant hitting a limit is not necessarily an attack. It is often a badly
/// written retry loop, and telling those apart is exactly the SOC layer's job,
/// which is why this records the event rather than acting on it.
/// </summary>
public sealed class RateLimitEventMiddleware(RequestDelegate next, ISocEventPublisher soc)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (context.Response.StatusCode != StatusCodes.Status429TooManyRequests) return;

        var merchantId = Guid.TryParse(
            context.Request.Headers[CorrelationHeaders.MerchantId].FirstOrDefault(), out var id)
            ? id
            : (Guid?)null;

        soc.Publish(new SocEvent
        {
            EventType = SocEventType.RateLimitExceeded,

            // MEDIUM on its own. One merchant briefly over quota is usually a
            // retry loop, not an attack. Rule R2 is what decides whether the
            // volume and spread make it something else.
            Severity = SocSeverity.Medium,
            CorrelationId = context.GetCorrelationId(),
            UserId = merchantId,
            SourceIp = context.Connection.RemoteIpAddress?.ToString(),
            Endpoint = context.Request.Path.Value,
            HttpMethod = context.Request.Method,
            StatusCode = StatusCodes.Status429TooManyRequests,
            Message = "Request throttled at the gateway after the merchant passed its quota",
            AffectedEntity = merchantId is { } m ? SocEntity.Ref(SocEntity.Merchant, m) : null,
            Metadata = new Dictionary<string, object?>
            {
                // Ocelot publishes the remaining quota and reset time in these
                // headers, so capturing them means the event records how far over
                // the limit the caller was rather than merely that they were.
                ["retryAfter"] = context.Response.Headers["Retry-After"].FirstOrDefault(),
                ["limit"] = context.Response.Headers["X-Rate-Limit-Limit"].FirstOrDefault()
            }
        });
    }
}

public static class RateLimitEventMiddlewareExtensions
{
    public static IApplicationBuilder UseRateLimitEvents(this IApplicationBuilder app) =>
        app.UseMiddleware<RateLimitEventMiddleware>();
}
