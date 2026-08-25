using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace PandaPocket.Shared.Contracts.Observability;

/// <summary>
/// Reads X-Correlation-Id from the incoming request, or mints one if the caller
/// did not supply it, then pushes it into the Serilog LogContext so every log
/// line written while handling this request carries it automatically.
///
/// The id is echoed on the response so a caller can quote it, and stored on
/// HttpContext.Items so handlers can read it without re-parsing headers.
///
/// In the finished system the gateway mints the id and every service downstream
/// finds one already present. Minting here as a fallback means a service called
/// directly, during development or from a .http file, still produces traceable
/// logs instead of a gap.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string ItemsKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationHeaders.CorrelationId].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N")[..16];
        }

        context.Items[ItemsKey] = correlationId;

        // Echo it before the response starts, since headers cannot be added once
        // the body has begun to be written.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationHeaders.CorrelationId] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}

public static class CorrelationIdMiddlewareExtensions
{
    /// <summary>
    /// Register immediately after Serilog request logging and before routing, so
    /// that the id exists for every log line the request produces.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();

    /// <summary>The correlation id for the current request, for handler code.</summary>
    public static string GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.ItemsKey, out var v) && v is string s
            ? s
            : "unknown";
}
