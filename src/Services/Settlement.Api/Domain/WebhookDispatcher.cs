using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PandaPocket.Services.Settlement.Clients;
using PandaPocket.Services.Settlement.Configuration;
using PandaPocket.Services.Settlement.Persistence;
using PandaPocket.Shared.Contracts.Soc;

namespace PandaPocket.Services.Settlement.Domain;

/// <summary>
/// Delivers webhooks, retrying with exponential backoff and dead-lettering what
/// cannot be delivered.
///
/// This is the retry pattern, and it earns its place rather than being a box to
/// tick. A merchant's endpoint is the one dependency in this system that is
/// genuinely outside our control: it can be down for deployment, slow, briefly
/// misconfigured, or behind a firewall that drops packets silently. Giving up
/// after one attempt would mean a merchant who redeployed at the wrong moment
/// simply never learns that a customer paid them.
///
/// Three properties matter, and each is a deliberate choice:
///
/// Durable, not in memory. The queue is a database table, so a restart resumes
/// rather than losing everything pending.
///
/// Backoff, not a tight loop. Retrying immediately and repeatedly against a
/// struggling endpoint is indistinguishable from attacking it, and would keep it
/// down. Each failure roughly doubles the wait.
///
/// Bounded, with a dead letter. Retrying for ever ties up resources on an
/// endpoint that may never return. After the limit the row is marked Failed and
/// kept, so somebody can see what was never delivered and retry it by hand.
/// </summary>
public sealed class WebhookDispatcher(
    IServiceProvider services,
    IHttpClientFactory httpClientFactory,
    IOptions<WebhookOptions> options,
    ILogger<WebhookDispatcher> logger) : BackgroundService
{
    private readonly WebhookOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollSeconds));

        logger.LogInformation(
            "Webhook dispatcher started: polling every {Poll}s, up to {Max} attempts, backoff base {Base}s",
            _options.PollSeconds, _options.MaxAttempts, _options.BackoffBaseSeconds);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await DispatchDueAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Same reasoning as the other background workers: a failed sweep
                // delays deliveries, it does not take the service down.
                logger.LogError(ex, "Webhook sweep failed; will retry on the next poll");
            }
        }
    }

    private async Task DispatchDueAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SettlementDbContext>();
        var merchantClient = scope.ServiceProvider.GetRequiredService<IMerchantClient>();

        var now = DateTime.UtcNow;

        // Driven by ix_webhook_status_next_attempt. The batch limit stops one
        // large backlog monopolising a sweep.
        var due = await db.WebhookDeliveries
            .Where(w => w.Status == Domain.WebhookStatus.Pending && w.NextAttemptAt <= now)
            .OrderBy(w => w.NextAttemptAt)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        foreach (var delivery in due)
        {
            await AttemptAsync(db, merchantClient, delivery, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task AttemptAsync(
        SettlementDbContext db, IMerchantClient merchantClient, Domain.WebhookDelivery delivery, CancellationToken ct)
    {
        delivery.AttemptCount++;

        var merchant = await merchantClient.GetAsync(delivery.MerchantId, delivery.Id.ToString(), ct);
        var secret = merchant?.WebhookSecret;

        try
        {
            var client = httpClientFactory.CreateClient("webhook");

            using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url)
            {
                Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json")
            };

            Sign(request, delivery, secret);

            using var response = await client.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                delivery.Status = Domain.WebhookStatus.Delivered;
                delivery.DeliveredAt = DateTime.UtcNow;
                delivery.LastStatusCode = (int)response.StatusCode;
                delivery.LastError = null;

                logger.LogInformation("Webhook {DeliveryId} delivered on attempt {Attempt} with {StatusCode}",
                    delivery.Id, delivery.AttemptCount, (int)response.StatusCode);
                return;
            }

            RecordFailure(delivery, (int)response.StatusCode,
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Connection refused, DNS failure, timeout. Indistinguishable from
            // the merchant's point of view and all retryable.
            RecordFailure(delivery, null, Truncate(ex.Message));
        }
    }

    private void RecordFailure(Domain.WebhookDelivery delivery, int? statusCode, string error)
    {
        delivery.LastStatusCode = statusCode;
        delivery.LastError = error;

        if (delivery.AttemptCount >= _options.MaxAttempts)
        {
            delivery.Status = Domain.WebhookStatus.Failed;

            var soc = SocEvent.Create(
                SocEventType.WebhookFailed, SocSeverity.Critical, delivery.Id.ToString(),
                delivery.MerchantId, delivery.InvoiceId,
                new Dictionary<string, object?>
                {
                    ["url"] = delivery.Url,
                    ["attempts"] = delivery.AttemptCount,
                    ["lastError"] = error,
                    ["deadLettered"] = true
                });

            logger.LogError("SOC {EventType} {@SocEvent}", SocEventType.WebhookFailed, soc);
            return;
        }

        var delay = BackoffFor(delivery.AttemptCount);
        delivery.NextAttemptAt = DateTime.UtcNow.Add(delay);

        logger.LogWarning(
            "Webhook {DeliveryId} attempt {Attempt}/{Max} failed ({Error}); next attempt in {Delay}s",
            delivery.Id, delivery.AttemptCount, _options.MaxAttempts, error, (int)delay.TotalSeconds);
    }

    /// <summary>
    /// Exponential backoff with jitter, capped.
    ///
    /// The doubling is what stops a struggling endpoint being hammered. The
    /// jitter matters more than it looks: without it, a hundred deliveries that
    /// failed together retry together for ever, arriving as synchronised bursts
    /// that are themselves a small denial of service. Spreading them randomly
    /// breaks that lockstep. The cap keeps the last attempts from drifting hours
    /// into the future.
    /// </summary>
    private TimeSpan BackoffFor(int attempt)
    {
        var seconds = _options.BackoffBaseSeconds * Math.Pow(2, attempt - 1);
        seconds = Math.Min(seconds, _options.MaxBackoffSeconds);

        var jitter = Random.Shared.NextDouble() * _options.JitterSeconds;
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    /// <summary>
    /// Signs the payload so the merchant can verify the callback came from us.
    ///
    /// Without a signature, anyone who learns a merchant's webhook URL can POST
    /// a fake "you have been paid" notification, and a shop that ships on that
    /// signal ships goods for nothing.
    ///
    /// The timestamp is inside the signed material on purpose. Signing only the
    /// body would let an attacker who ever captured one valid callback replay it
    /// verbatim for ever, since the signature would stay valid. With the
    /// timestamp signed, the merchant rejects anything older than a few minutes
    /// and a captured callback stops being useful.
    /// </summary>
    private static void Sign(HttpRequestMessage request, Domain.WebhookDelivery delivery, string? secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        request.Headers.Add("X-PandaPocket-Timestamp", timestamp);
        request.Headers.Add("X-PandaPocket-Event", delivery.EventType);
        request.Headers.Add("X-PandaPocket-Delivery", delivery.Id.ToString());
        request.Headers.Add("X-PandaPocket-Attempt", delivery.AttemptCount.ToString());

        if (string.IsNullOrEmpty(secret)) return;

        var signedPayload = $"{timestamp}.{delivery.Payload}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signedPayload));

        // The scheme prefix leaves room to rotate to a stronger algorithm later
        // without merchants having to guess which one was used.
        request.Headers.Add("X-PandaPocket-Signature", "sha256=" + Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static string Truncate(string value) => value.Length > 400 ? value[..400] : value;

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
