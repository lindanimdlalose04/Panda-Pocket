namespace PandaPocket.Services.Rate.Configuration;

public sealed class RateSimulatorOptions
{
    public const string SectionName = "RateSimulator";

    /// <summary>How often a new live tick is produced for each pair.</summary>
    public int TickIntervalSeconds { get; init; } = 5;

    /// <summary>
    /// On first start, if the ticks collection is empty, generate this many
    /// hours of synthetic history at <see cref="BackfillIntervalMinutes"/>
    /// spacing. Without a backfill the history endpoint returns an empty array
    /// until the service has been running for hours, which makes it impossible
    /// to demonstrate and leaves the Mongo index with nothing to work against.
    /// </summary>
    public int BackfillHours { get; init; } = 24;
    public int BackfillIntervalMinutes { get; init; } = 1;

    /// <summary>
    /// Fixed seed so a demo is reproducible and two runs tell the same story.
    /// Set to null for a different walk on every start.
    /// </summary>
    public int? RandomSeed { get; init; } = 20260823;

    public List<PairOptions> Pairs { get; init; } = [];
}

public sealed class PairOptions
{
    /// <summary>For example BTCZAR.</summary>
    public required string Pair { get; init; }

    /// <summary>Starting price in ZAR.</summary>
    public required decimal StartPrice { get; init; }

    /// <summary>Annualised drift, mu. 0.15 means a 15 percent upward trend per year.</summary>
    public double Drift { get; init; }

    /// <summary>
    /// Annualised volatility, sigma. Crypto sits around 0.6 to 0.8. A stablecoin
    /// is near zero, which is why USDTZAR is configured differently to the
    /// others rather than having one blanket figure applied to everything.
    /// </summary>
    public double Volatility { get; init; }
}
