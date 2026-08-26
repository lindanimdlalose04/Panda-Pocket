namespace PandaPocket.Services.Settlement.Configuration;

public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>How often the dispatcher looks for due deliveries.</summary>
    public int PollSeconds { get; init; } = 5;

    /// <summary>Deliveries handled per sweep, so one backlog cannot monopolise it.</summary>
    public int BatchSize { get; init; } = 20;

    /// <summary>
    /// Attempts before dead-lettering. Six with a doubling backoff spans roughly
    /// two minutes at these settings, which is long enough to ride out a restart
    /// and short enough to demonstrate. A production system would spread the
    /// same six attempts over hours.
    /// </summary>
    public int MaxAttempts { get; init; } = 6;

    /// <summary>First retry delay. Each subsequent failure roughly doubles it.</summary>
    public double BackoffBaseSeconds { get; init; } = 3;

    /// <summary>Ceiling, so late attempts do not drift hours out.</summary>
    public double MaxBackoffSeconds { get; init; } = 45;

    /// <summary>
    /// Random spread added to each delay. Without it, deliveries that failed
    /// together retry together for ever, arriving as synchronised bursts.
    /// </summary>
    public double JitterSeconds { get; init; } = 2;

    /// <summary>Per-attempt HTTP timeout. A merchant hanging must not hang us.</summary>
    public int RequestTimeoutSeconds { get; init; } = 10;
}
