using Microsoft.Extensions.Options;
using PandaPocket.Services.Rate.Configuration;
using PandaPocket.Services.Rate.Persistence;

namespace PandaPocket.Services.Rate.Domain;

/// <summary>
/// Drives the price simulation. Two responsibilities:
///
/// On startup, if the ticks collection is empty, generate a synthetic past so
/// the history endpoint has something to return immediately. Without this the
/// endpoint returns an empty array until the service has been running for
/// hours, which cannot be demonstrated and leaves the compound index with
/// nothing to work against.
///
/// Thereafter, produce one live tick per pair per interval, updating the in
/// memory rate book and appending to Mongo.
/// </summary>
public sealed class TickGeneratorService(
    IServiceProvider services,
    RateBook rateBook,
    IOptions<RateSimulatorOptions> options,
    ILogger<TickGeneratorService> logger) : BackgroundService
{
    private readonly RateSimulatorOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var simulators = BuildSimulators();

        if (simulators.Count == 0)
        {
            logger.LogWarning("No pairs configured; the rate generator has nothing to do");
            return;
        }

        // Mongo may not be reachable yet, or at all. An unhandled exception here
        // would stop the entire host, because .NET's default
        // BackgroundServiceExceptionBehavior is StopHost. That is the wrong
        // outcome: the rate book is already seeded from configuration, so the
        // service can keep answering quotes while its database is down. Losing
        // the whole service because history is unavailable would turn a partial
        // outage into a total one.
        //
        // So initialisation retries in the background, and the health check is
        // what reports the degraded state.
        if (!await InitialiseWithRetryAsync(simulators, stoppingToken))
        {
            return; // shutting down
        }

        await GenerateLiveAsync(simulators, stoppingToken);
    }

    /// <summary>
    /// Ensure the index, then either backfill or resume. Retries with a capped
    /// backoff so that a database which is slow to start, or briefly down, is
    /// survivable rather than fatal.
    /// </summary>
    private async Task<bool> InitialiseWithRetryAsync(
        Dictionary<string, GbmPriceSimulator> simulators,
        CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromSeconds(30);
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;
            try
            {
                using var scope = services.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<ITickRepository>();

                await repository.EnsureIndexesAsync(ct);

                if (await repository.IsEmptyAsync(ct))
                {
                    await BackfillAsync(repository, simulators, ct);
                }
                else
                {
                    await RestoreLatestAsync(repository, simulators, ct);
                }

                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Rate history store unavailable on attempt {Attempt}; quotes continue from the in-memory book. Retrying in {Delay}s",
                    attempt, delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
            }
        }

        return false;
    }

    private Dictionary<string, GbmPriceSimulator> BuildSimulators()
    {
        var simulators = new Dictionary<string, GbmPriceSimulator>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < _options.Pairs.Count; i++)
        {
            var pair = _options.Pairs[i];

            // Offset the seed per pair, otherwise every pair walks in lockstep
            // and the chart shows three identically shaped lines, which looks
            // wrong to anyone paying attention.
            var seed = _options.RandomSeed is { } s ? s + i : (int?)null;

            simulators[pair.Pair] = new GbmPriceSimulator(pair.StartPrice, pair.Drift, pair.Volatility, seed);
        }

        return simulators;
    }

    /// <summary>
    /// Walk the simulation forward from BackfillHours ago to now, in
    /// BackfillIntervalMinutes steps, and write the result as one bulk insert
    /// per pair. The walk is the same mathematics as the live generator, so the
    /// history and the live data form one continuous series rather than
    /// history being obviously fabricated and then jumping.
    /// </summary>
    private async Task BackfillAsync(
        ITickRepository repository,
        Dictionary<string, GbmPriceSimulator> simulators,
        CancellationToken ct)
    {
        var step = TimeSpan.FromMinutes(_options.BackfillIntervalMinutes);
        var start = DateTime.UtcNow - TimeSpan.FromHours(_options.BackfillHours);
        var steps = (int)(TimeSpan.FromHours(_options.BackfillHours) / step);

        logger.LogInformation(
            "Ticks collection is empty; backfilling {Hours}h of history at {Interval}min intervals for {PairCount} pairs",
            _options.BackfillHours, _options.BackfillIntervalMinutes, simulators.Count);

        foreach (var (pair, simulator) in simulators)
        {
            var ticks = new List<Tick>(steps);
            var ts = start;

            for (var i = 0; i < steps; i++)
            {
                var rate = simulator.Advance(step);
                ticks.Add(new Tick { Pair = pair, Rate = rate, Source = TickSource.Backfill, Ts = ts });
                ts += step;
            }

            await repository.InsertManyAsync(ticks, ct);

            // Leave the book holding the end of the walk, not the configured
            // start price, so the first live quote continues the series.
            rateBook.Set(pair, simulator.CurrentPrice, ts);

            logger.LogInformation("Backfilled {Count} ticks for {Pair}, ending at {Rate}",
                ticks.Count, pair, simulator.CurrentPrice);
        }
    }

    /// <summary>
    /// The collection already has data, from a previous run against the same
    /// Mongo volume. Continue from the last known price for each pair rather
    /// than restarting at the configured start price, which would put a visible
    /// discontinuity in the series every time the container restarts.
    /// </summary>
    private async Task RestoreLatestAsync(
        ITickRepository repository,
        Dictionary<string, GbmPriceSimulator> simulators,
        CancellationToken ct)
    {
        foreach (var pair in simulators.Keys)
        {
            var latest = await repository.GetLatestAsync(pair, ct);
            if (latest is null) continue;

            var config = _options.Pairs.First(p => string.Equals(p.Pair, pair, StringComparison.OrdinalIgnoreCase));
            var index = _options.Pairs.IndexOf(config);
            var seed = _options.RandomSeed is { } s ? s + index : (int?)null;

            simulators[pair] = new GbmPriceSimulator(latest.Rate, config.Drift, config.Volatility, seed);
            rateBook.Set(pair, latest.Rate, latest.Ts);

            logger.LogInformation("Resumed {Pair} from last stored rate {Rate} at {Ts}", pair, latest.Rate, latest.Ts);
        }
    }

    private async Task GenerateLiveAsync(Dictionary<string, GbmPriceSimulator> simulators, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_options.TickIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        logger.LogInformation("Live tick generation started at {IntervalSeconds}s intervals", _options.TickIntervalSeconds);

        while (await SafeWaitAsync(timer, ct))
        {
            using var scope = services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ITickRepository>();

            foreach (var (pair, simulator) in simulators)
            {
                var rate = simulator.Advance(interval);
                var now = DateTime.UtcNow;

                rateBook.Set(pair, rate, now);

                try
                {
                    await repository.InsertAsync(
                        new Tick { Pair = pair, Rate = rate, Source = TickSource.Simulator, Ts = now }, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // A failed write must not kill the generator. The in-memory
                    // book is already updated, so quotes keep working and only
                    // the history gains a gap.
                    logger.LogError(ex, "Failed to persist tick for {Pair}", pair);
                }
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
