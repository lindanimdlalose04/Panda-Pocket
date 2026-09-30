using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PandaPocket.Services.Soc.Persistence;
using PandaPocket.Shared.Contracts.Soc;

namespace PandaPocket.Services.Soc.Domain;

/// <summary>
/// Turns registry health into events.
///
/// The specification requires container or service health as an event source,
/// and this is the honest way to get it: Consul is already polling every
/// instance's /health endpoint every ten seconds, so the information exists and
/// nobody is recording it. Asking Consul is also better than asking each service
/// whether it is well, because a service that has stopped answering cannot
/// report that it has stopped answering. The observation comes from outside.
///
/// Only transitions are recorded, not every poll. A service that is healthy for
/// an hour would otherwise produce three hundred and sixty identical events an
/// hour per instance, which is noise that buries the two that matter.
/// </summary>
public sealed class ServiceHealthPoller(
    IServiceProvider services,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<ServiceHealthPoller> logger) : BackgroundService
{
    private readonly int _pollSeconds = configuration.GetValue("Soc:HealthPollSeconds", 10);

    /// <summary>Last status seen per check, so only changes are written.</summary>
    private readonly Dictionary<string, string> _lastSeen = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Soc:HealthPollEnabled", true))
        {
            logger.LogInformation("Service health polling disabled");
            return;
        }

        logger.LogInformation("Polling registry health every {Seconds}s", _pollSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_pollSeconds));

        // The first sweep records the starting position without raising events.
        // Otherwise every restart would report every service as having just
        // changed, which is true of the poller and not of the services.
        var priming = true;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(priming, stoppingToken);
                priming = false;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning("Could not read registry health ({Reason})", ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Health sweep failed; continuing");
            }
        }
    }

    private async Task SweepAsync(bool priming, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("consul");
        var checks = await client.GetFromJsonAsync<List<ConsulHealthCheck>>("/v1/health/state/any", ct) ?? [];

        var changes = new List<SocEventRecord>();
        var now = DateTime.UtcNow;

        foreach (var check in checks)
        {
            // Consul's own "serfHealth" check describes the agent, not a service.
            if (string.IsNullOrWhiteSpace(check.ServiceName)) continue;

            var key = $"{check.ServiceId}/{check.CheckId}";

            if (_lastSeen.TryGetValue(key, out var previous) && previous == check.Status) continue;

            _lastSeen[key] = check.Status;
            if (priming) continue;

            var severity = check.Status switch
            {
                "passing"  => SocSeverity.Info,
                "warning"  => SocSeverity.Medium,
                "critical" => SocSeverity.High,
                _          => SocSeverity.Medium
            };

            var socEvent = new SocEvent
            {
                EventType = SocEventType.ServiceHealthChanged,
                Severity = severity,
                ServiceName = "soc-service",

                // Observed rather than requested, so there is no inbound
                // correlation id to inherit. One is minted so this still joins
                // to whatever else happens in the same window.
                CorrelationId = Guid.NewGuid().ToString("N")[..16],
                AffectedEntity = SocEntity.Ref(SocEntity.Service, check.ServiceName),
                Message = $"{check.ServiceName} moved from {previous ?? "unknown"} to {check.Status}",
                Metadata = new Dictionary<string, object?>
                {
                    ["observedService"] = check.ServiceName,
                    ["instanceId"] = check.ServiceId,
                    ["from"] = previous,
                    ["to"] = check.Status,
                    ["output"] = Truncate(check.Output)
                }
            };

            logger.Log(severity == SocSeverity.Info ? LogLevel.Information : LogLevel.Warning,
                "SOC {EventType} {@SocEvent}", socEvent.EventType, socEvent);

            changes.Add(ToRecord(socEvent, now));
        }

        if (changes.Count == 0) return;

        // Written straight to the store rather than posted back through the
        // ingestion endpoint. This service owns soc_db, so going out over HTTP
        // to reach its own database would be ceremony, not isolation.
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SocDbContext>();
        db.Events.AddRange(changes);
        await db.SaveChangesAsync(ct);
    }

    private static SocEventRecord ToRecord(SocEvent e, DateTime received) => new()
    {
        EventId = e.EventId,
        Timestamp = e.Timestamp.UtcDateTime,
        ReceivedAt = received,
        ServiceName = e.ServiceName,
        EventType = e.EventType,
        Severity = e.Severity,
        CorrelationId = e.CorrelationId,
        UserId = e.UserId,
        SourceIp = e.SourceIp,
        Endpoint = e.Endpoint,
        HttpMethod = e.HttpMethod,
        StatusCode = e.StatusCode,
        Message = e.Message,
        AffectedEntity = e.AffectedEntity,
        MetadataJson = e.Metadata is null ? null : JsonSerializer.Serialize(e.Metadata)
    };

    private static string? Truncate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null :
        value.Length <= 200 ? value.Trim() : value[..200].Trim();

    private sealed record ConsulHealthCheck(
        [property: JsonPropertyName("Node")]        string? Node,
        [property: JsonPropertyName("CheckID")]     string CheckId,
        [property: JsonPropertyName("Name")]        string? Name,
        [property: JsonPropertyName("Status")]      string Status,
        [property: JsonPropertyName("Output")]      string? Output,
        [property: JsonPropertyName("ServiceID")]   string? ServiceId,
        [property: JsonPropertyName("ServiceName")] string? ServiceName);
}
