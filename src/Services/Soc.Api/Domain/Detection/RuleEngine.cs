using Microsoft.EntityFrameworkCore;
using Npgsql;
using PandaPocket.Services.Soc.Persistence;
using PandaPocket.Shared.Contracts.Soc;

namespace PandaPocket.Services.Soc.Domain.Detection;

/// <summary>
/// Evaluates every enabled rule on a timer and turns hits into alerts.
///
/// Suppression lives here rather than in the rules. A rule says "this condition
/// is true for this entity"; whether that is news is a separate question, and
/// answering it once means no rule can get it subtly wrong. A sustained attack
/// therefore produces one alert whose event count and last-seen time keep
/// moving, rather than one alert per cycle burying the finding in copies of
/// itself.
///
/// Evaluation is on a timer rather than on ingest. Rules are aggregates over a
/// window, so they have to run against accumulated data, and the alternative is
/// re-running every rule on every event. The cost is that an alert can lag by up
/// to one cycle, which the report states rather than glossing over.
/// </summary>
public sealed class RuleEngine(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<RuleEngine> logger) : BackgroundService
{
    private readonly int _intervalSeconds = configuration.GetValue("Soc:RuleIntervalSeconds", 20);

    private static readonly IDetectionRule[] Rules =
    [
        new CredentialProbingRule(),
        new QuotaAbuseRule(),
        new DependencyFailureRule(),
        new PayoutRedirectionRule(),
        new CheckoutEnumerationRule(),
        new RateManipulationRule()
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Soc:RulesEnabled", true))
        {
            logger.LogInformation("Detection rules disabled");
            return;
        }

        await SeedRulesAsync(stoppingToken);

        logger.LogInformation(
            "Detection engine started: {Count} rules, evaluating every {Seconds}s",
            Rules.Length, _intervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_intervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await EvaluateAllAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Never let the loop die. A detection engine that stops on the
                // first transient error stops detecting, and does it silently.
                logger.LogError(ex, "Rule evaluation cycle failed; continuing");
            }
        }
    }

    /// <summary>
    /// Writes the catalogue into the database, updating rows that already exist.
    ///
    /// Upserted rather than inserted once, so changing a threshold in the
    /// catalogue takes effect on the next start instead of being ignored because
    /// a row was already there.
    /// </summary>
    private async Task SeedRulesAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SocDbContext>();

        var existing = await db.Rules.ToDictionaryAsync(r => r.RuleId, ct);

        foreach (var rule in RuleCatalogue.All)
        {
            if (existing.TryGetValue(rule.RuleId, out var current))
            {
                current.Name = rule.Name;
                current.Purpose = rule.Purpose;
                current.InputEventTypes = rule.InputEventTypes;
                current.Condition = rule.Condition;
                current.Severity = rule.Severity;
                current.AlertType = rule.AlertType;
                current.RecommendedAction = rule.RecommendedAction;
                current.ThreatReference = rule.ThreatReference;
                current.WindowMinutes = rule.WindowMinutes;
                current.Threshold = rule.Threshold;
                current.SuppressionMinutes = rule.SuppressionMinutes;
            }
            else
            {
                db.Rules.Add(rule);
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Rule catalogue seeded: {Count} rules", RuleCatalogue.All.Count);
    }

    private async Task EvaluateAllAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SocDbContext>();

        var configs = await db.Rules.Where(r => r.Enabled).ToDictionaryAsync(r => r.RuleId, ct);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);

        var now = DateTime.UtcNow;
        var raised = 0;
        var extended = 0;

        foreach (var rule in Rules)
        {
            if (!configs.TryGetValue(rule.RuleId, out var config)) continue;

            IReadOnlyList<RuleHit> hits;
            try
            {
                hits = await rule.EvaluateAsync(connection, config, now, ct);
            }
            catch (Exception ex)
            {
                // One broken rule must not stop the other five.
                logger.LogError(ex, "Rule {RuleId} failed to evaluate", rule.RuleId);
                continue;
            }

            foreach (var hit in hits)
            {
                if (await ApplyAsync(db, config, hit, now, ct)) raised++;
                else extended++;
            }
        }

        if (raised > 0 || extended > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Detection cycle: {Raised} new alerts, {Extended} extended", raised, extended);
        }
    }

    /// <summary>
    /// Returns true when a new alert was raised, false when an open one absorbed
    /// the hit.
    /// </summary>
    private async Task<bool> ApplyAsync(
        SocDbContext db, DetectionRuleRecord config, RuleHit hit, DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddMinutes(-config.SuppressionMinutes);

        var open = await db.Alerts
            .Include(a => a.Events)
            .Where(a => a.SuppressionKey == hit.SuppressionKey
                        && a.Status == AlertStatus.Open
                        && a.LastSeenAt >= cutoff)
            .OrderByDescending(a => a.LastSeenAt)
            .FirstOrDefaultAsync(ct);

        if (open is not null)
        {
            var known = open.Events.Select(e => e.EventId).ToHashSet();
            var fresh = hit.EventIds.Where(id => !known.Contains(id)).ToList();

            // Nothing new. The window still overlaps the same events, so the
            // condition is technically true again without anything having
            // happened. Saying so would be noise.
            if (fresh.Count == 0) return false;

            foreach (var id in fresh) open.Events.Add(new AlertEventLink { AlertId = open.AlertId, EventId = id });

            open.LastSeenAt = now;
            open.EventCount = open.Events.Count;
            open.Description = hit.Description;
            return false;
        }

        var alert = new Alert
        {
            AlertId = Guid.NewGuid(),
            FirstSeenAt = now,
            LastSeenAt = now,
            RuleId = config.RuleId,
            RuleName = config.Name,
            Severity = config.Severity,
            Status = AlertStatus.Open,
            Description = hit.Description,
            RecommendedAction = config.RecommendedAction,
            AffectedService = hit.AffectedService,
            AffectedUserId = hit.UserId,
            AffectedEntity = hit.AffectedEntity,
            SourceIp = hit.SourceIp,
            SuppressionKey = hit.SuppressionKey,
            EventCount = hit.EventIds.Count,
            Events = hit.EventIds.Distinct()
                        .Select(id => new AlertEventLink { EventId = id })
                        .ToList()
        };

        db.Alerts.Add(alert);

        logger.LogWarning(
            "ALERT {RuleId} {Severity}: {Description}", config.RuleId, config.Severity, hit.Description);

        return true;
    }
}
