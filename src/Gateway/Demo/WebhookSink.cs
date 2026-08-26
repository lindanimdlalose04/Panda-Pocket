using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PandaPocket.Gateway.Demo;

/// <summary>
/// A stand-in for a merchant's own server receiving webhooks.
///
/// This is a test harness, not part of the product, and it is namespaced and
/// routed under /demo accordingly. It exists because "the retry backs off
/// correctly" is only half the story: without a receiver, nothing ever shows a
/// delivery succeeding, and nothing shows the HMAC signature being verified by
/// the party it is meant to protect.
///
/// It does what a real integration should do, so it doubles as documentation of
/// the expected merchant side:
///
///   1. Recompute the HMAC over timestamp + "." + the raw body.
///   2. Compare in fixed time.
///   3. Reject anything older than a few minutes, so a captured callback cannot
///      be replayed indefinitely.
///
/// One difference from a real merchant: it looks the signing secret up from the
/// Merchant service, because it stands in for any merchant rather than one. A
/// real integration already knows its own secret and would never fetch it.
/// </summary>
public static class WebhookSink
{
    private const int MaxStored = 50;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentQueue<ReceivedWebhook> Received = new();

    public static void MapWebhookSink(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/demo").WithTags("Demo webhook sink");

        group.MapPost("/webhook-sink", async (HttpContext ctx, IHttpClientFactory factory) =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            var signature = ctx.Request.Headers["X-PandaPocket-Signature"].FirstOrDefault();
            var timestamp = ctx.Request.Headers["X-PandaPocket-Timestamp"].FirstOrDefault();
            var attempt = ctx.Request.Headers["X-PandaPocket-Attempt"].FirstOrDefault();
            var deliveryId = ctx.Request.Headers["X-PandaPocket-Delivery"].FirstOrDefault();

            var secret = await ResolveSecretAsync(body, factory, ctx.RequestAborted);
            var verdict = Verify(body, signature, timestamp, secret);

            Store(new ReceivedWebhook(
                deliveryId ?? "unknown", attempt ?? "?", verdict,
                body.Length > 400 ? body[..400] : body, DateTime.UtcNow));

            // A merchant that cannot verify a signature must refuse the callback.
            // Returning 200 to an unverified payload would defeat the point of
            // signing it, and would let anyone who learned the URL post a fake
            // "you have been paid" to a shop that ships on that signal.
            return verdict == "verified"
                ? Results.Ok(new { received = true, verdict })
                : Results.Json(new { received = false, verdict }, statusCode: StatusCodes.Status401Unauthorized);
        })
        .WithSummary("Test receiver standing in for a merchant's server. Verifies the HMAC signature.");

        group.MapGet("/webhook-sink", () => Results.Ok(Received.Reverse().ToList()))
            .WithSummary("What this sink has received, newest first");

        group.MapDelete("/webhook-sink", () =>
        {
            Received.Clear();
            return Results.NoContent();
        })
        .WithSummary("Clear the sink between demo runs");
    }

    private static async Task<string?> ResolveSecretAsync(string body, IHttpClientFactory factory, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("merchantId", out var idElement)) return null;
            if (!Guid.TryParse(idElement.GetString(), out var merchantId)) return null;

            var client = factory.CreateClient("merchant");
            var details = await client.GetFromJsonAsync<MerchantDetails>(
                $"/api/internal/merchants/{merchantId}", ct);

            return details?.WebhookSecret;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static string Verify(string body, string? signature, string? timestamp, string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "secret-unavailable";
        if (string.IsNullOrEmpty(signature)) return "missing-signature";
        if (string.IsNullOrEmpty(timestamp)) return "missing-timestamp";

        // The timestamp is inside the signed material, so it cannot be adjusted
        // without invalidating the signature. Checking its age is what stops a
        // captured callback being replayed for ever.
        if (!long.TryParse(timestamp, out var unix)) return "bad-timestamp";

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix);
        if (age > MaxAge) return $"stale ({(int)age.TotalSeconds}s old)";

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{body}"));

        var provided = signature.StartsWith("sha256=", StringComparison.Ordinal)
            ? signature["sha256=".Length..]
            : signature;

        byte[] providedBytes;
        try { providedBytes = Convert.FromHexString(provided); }
        catch (FormatException) { return "malformed-signature"; }

        // Fixed time, so the comparison does not leak how many leading bytes
        // were correct.
        return CryptographicOperations.FixedTimeEquals(expected, providedBytes)
            ? "verified"
            : "signature-mismatch";
    }

    private static void Store(ReceivedWebhook item)
    {
        Received.Enqueue(item);
        while (Received.Count > MaxStored) Received.TryDequeue(out _);
    }

    private sealed record ReceivedWebhook(
        string DeliveryId, string Attempt, string Verdict, string Body, DateTime ReceivedAt);

    private sealed record MerchantDetails(
        Guid Id, string BusinessName, decimal FeePercent, string? WebhookUrl, string? WebhookSecret);
}
