using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PandaPocket.Shared.Contracts.Soc;

public interface ISocEventPublisher
{
    /// <summary>
    /// Records one event. Never throws and never blocks the caller, because
    /// every call site is on a request path that matters more than the telemetry
    /// does.
    /// </summary>
    void Publish(SocEvent socEvent);
}

/// <summary>
/// Writes each event twice, to two stores with different jobs.
///
/// Serilog, and therefore Seq, is the durable human-readable record. The SOC
/// service is the queryable copy that detection rules and the graph loader read.
/// Keeping both is not duplication for its own sake: Seq answers "show me what
/// happened around 14:03" for a person, and soc_db answers "count failures by
/// source address in a five-minute window" for a rule. Neither does the other's
/// job well.
///
/// Shipping is batched and out of band. A call site is on a payment request, and
/// an SOC service that has gone away must not be able to slow down or fail a
/// payment. The queue is bounded and drops rather than grows, so the worst case
/// for a telemetry outage is a gap in the analysis copy, not memory exhaustion
/// in a service that takes money.
/// </summary>
public sealed class SocEventPublisher : BackgroundService, ISocEventPublisher
{
    private readonly SocOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHttpContextAccessor _httpContext;
    private readonly ILogger<SocEventPublisher> _logger;
    private readonly Channel<SocEvent> _queue;

    private int _dropped;

    public SocEventPublisher(
        IOptions<SocOptions> options,
        IHttpClientFactory httpClientFactory,
        IHttpContextAccessor httpContext,
        ILogger<SocEventPublisher> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _httpContext = httpContext;
        _logger = logger;

        _queue = Channel.CreateBounded<SocEvent>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            // The newest events describe what is happening now, which is what an
            // analyst is looking at. Dropping the oldest keeps the tail useful.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
    }

    public void Publish(SocEvent socEvent)
    {
        var stamped = socEvent.ServiceName == "unknown"
            ? socEvent with { ServiceName = _options.ServiceName }
            : socEvent;

        stamped = WithRequestContext(stamped);

        // The @ destructures the record into indexed properties rather than
        // flattening it to a string, so a Seq query filters on structured data
        // instead of searching message text.
        _logger.Log(LevelFor(stamped.Severity), "SOC {EventType} {@SocEvent}", stamped.EventType, stamped);

        if (!_options.Enabled) return;

        if (!_queue.Writer.TryWrite(stamped))
        {
            // Only reachable if the channel is completed, since DropOldest never
            // refuses a write.
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>
    /// Fills in the request fields a call site did not set.
    ///
    /// A rejected login inside the Merchant service knows the merchant but not
    /// the address it came from, so AUTH_FAILED was arriving with a null
    /// source_ip. Rule R1 counts failures per address, so those events were
    /// invisible to the rule that exists to catch them. Rather than threading an
    /// HttpContext through every domain method, the publisher reads the ambient
    /// request when there is one.
    ///
    /// Only nulls are filled. The gateway sets these explicitly and its values
    /// win, and a background service has no ambient request, so its events keep
    /// null fields rather than borrowing somebody else's.
    /// </summary>
    private SocEvent WithRequestContext(SocEvent socEvent)
    {
        var context = _httpContext.HttpContext;
        if (context is null) return socEvent;

        // The forwarded address first: behind the gateway the socket address is
        // the gateway's, which is the same for every caller and useless to a
        // rule that counts per address.
        var forwarded = context.Request.Headers[CorrelationHeaders.ForwardedFor].FirstOrDefault();

        return socEvent with
        {
            SourceIp = socEvent.SourceIp
                       ?? (string.IsNullOrWhiteSpace(forwarded) ? null : forwarded.Split(',')[0].Trim())
                       ?? context.Connection.RemoteIpAddress?.ToString(),
            Endpoint = socEvent.Endpoint ?? context.Request.Path.Value,
            HttpMethod = socEvent.HttpMethod ?? context.Request.Method
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SOC event shipping disabled; events go to the log only");
            return;
        }

        _logger.LogInformation(
            "SOC event shipping to {Url}, batches of up to {Batch} every {Flush}s",
            _options.IngestBaseUrl, _options.BatchSize, _options.FlushSeconds);

        var batch = new List<SocEvent>(_options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FillBatchAsync(batch, stoppingToken);
                if (batch.Count > 0) await ShipAsync(batch, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the loop die. A publisher that stopped on the first
                // transient failure would silently take SOC collection with it.
                _logger.LogWarning(ex, "SOC shipping loop error; continuing");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            finally
            {
                batch.Clear();
            }
        }

        // Best effort on shutdown: whatever is still queued gets one chance.
        await FlushRemainingAsync();
    }

    /// <summary>
    /// Waits for the first event, then takes whatever else is already waiting up
    /// to the batch size, or until the flush window expires. This is what turns a
    /// trickle of events into occasional requests without adding latency when
    /// traffic is heavy.
    /// </summary>
    private async Task FillBatchAsync(List<SocEvent> batch, CancellationToken stoppingToken)
    {
        if (!await _queue.Reader.WaitToReadAsync(stoppingToken)) return;

        using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        window.CancelAfter(TimeSpan.FromSeconds(_options.FlushSeconds));

        while (batch.Count < _options.BatchSize)
        {
            if (_queue.Reader.TryRead(out var next))
            {
                batch.Add(next);
                continue;
            }

            if (batch.Count > 0) break;

            try
            {
                if (!await _queue.Reader.WaitToReadAsync(window.Token)) break;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ShipAsync(List<SocEvent> batch, CancellationToken stoppingToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("soc-ingest");
            var response = await client.PostAsJsonAsync("/api/soc/events", batch, stoppingToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "SOC service rejected a batch of {Count} events with {Status}",
                    batch.Count, (int)response.StatusCode);
                return;
            }

            var dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
            {
                _logger.LogWarning("{Dropped} SOC events were dropped from the queue", dropped);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The SOC service is down or slow. The events are already in Seq, so
            // this is a gap in the analysis copy rather than lost evidence, and
            // saying so plainly is more useful than an exception nobody reads.
            _logger.LogWarning(
                "Could not ship {Count} SOC events ({Reason}); they remain in the log only",
                batch.Count, ex.Message);
        }
    }

    private async Task FlushRemainingAsync()
    {
        var remaining = new List<SocEvent>();
        while (remaining.Count < _options.BatchSize && _queue.Reader.TryRead(out var e)) remaining.Add(e);

        if (remaining.Count == 0) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await ShipAsync(remaining, timeout.Token);
    }

    private static LogLevel LevelFor(string severity) => severity switch
    {
        SocSeverity.Info                        => LogLevel.Information,
        SocSeverity.Low or SocSeverity.Medium   => LogLevel.Warning,
        _                                       => LogLevel.Error
    };
}

public static class SocEventPublisherExtensions
{
    /// <summary>
    /// Wires up SOC publishing. Called identically by the gateway and all four
    /// services. One instance serves three roles: the interface call sites
    /// depend on, the background shipper, and the singleton holding the queue.
    /// </summary>
    public static IServiceCollection AddSocEvents(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SocOptions>(configuration.GetSection(SocOptions.SectionName));

        // So the publisher can fill in the caller's address and path for events
        // raised deep inside a service, where no HttpContext was passed down.
        services.AddHttpContextAccessor();

        services.AddHttpClient("soc-ingest", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<SocOptions>>().Value;
            client.BaseAddress = new Uri(options.IngestBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        });

        services.AddSingleton<SocEventPublisher>();
        services.AddSingleton<ISocEventPublisher>(sp => sp.GetRequiredService<SocEventPublisher>());
        services.AddHostedService(sp => sp.GetRequiredService<SocEventPublisher>());

        return services;
    }
}
