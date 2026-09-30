using System.Globalization;
using Npgsql;
using PandaPocket.Services.Soc.Persistence;

namespace PandaPocket.Services.Soc.Domain.Detection;

/// <summary>Shared plumbing: run a query, read rows, nothing clever.</summary>
internal static class Sql
{
    public static async Task<List<T>> QueryAsync<T>(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlParameterCollection> bind,
        Func<NpgsqlDataReader, T> read,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        bind(command.Parameters);

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(read(reader));
        return rows;
    }

    /// <summary>array_agg of uuid, with nulls treated as an empty set.</summary>
    public static Guid[] Ids(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : reader.GetFieldValue<Guid[]>(ordinal);

    public static string Zar(decimal amount) =>
        "R" + amount.ToString("N2", CultureInfo.InvariantCulture);
}

// ---------------------------------------------------------------------------
// R1: credential probing
// ---------------------------------------------------------------------------

/// <summary>
/// One source address producing repeated invalid API keys.
///
/// Grouped by address rather than by merchant on purpose. A caller guessing keys
/// does not have a merchant identity yet, which is the whole point: there is
/// nothing to group by except where the attempts came from. It is also why the
/// gateway has to forward the real client address, because grouping every
/// caller under the gateway's own address would count one attacker and one
/// mistyped key identically.
/// </summary>
public sealed class CredentialProbingRule : IDetectionRule
{
    public string RuleId => "R1";

    public async Task<IReadOnlyList<RuleHit>> EvaluateAsync(
        NpgsqlConnection connection, DetectionRuleRecord config, DateTime now, CancellationToken ct)
    {
        const string sql = """
            SELECT source_ip,
                   count(*)          AS attempts,
                   array_agg(event_id) AS ids
            FROM   soc_events
            WHERE  event_type = 'API_KEY_INVALID'
              AND  timestamp >= @since
              AND  source_ip IS NOT NULL
            GROUP  BY source_ip
            HAVING count(*) >= @threshold
            """;

        var rows = await Sql.QueryAsync(connection, sql,
            p =>
            {
                p.AddWithValue("since", now.AddMinutes(-config.WindowMinutes));
                p.AddWithValue("threshold", config.Threshold);
            },
            r => (Ip: r.GetString(0), Attempts: r.GetInt64(1), Ids: Sql.Ids(r, 2)),
            ct);

        return rows.Select(row => new RuleHit(
            SuppressionKey: $"{RuleId}:{row.Ip}",
            Description:
                $"{row.Attempts} invalid API keys presented from {row.Ip} within {config.WindowMinutes} minutes.",
            EventIds: row.Ids,
            SourceIp: row.Ip,
            AffectedService: "gateway")).ToList();
    }
}

// ---------------------------------------------------------------------------
// R2: quota abuse
// ---------------------------------------------------------------------------

/// <summary>
/// One merchant repeatedly past its rate limit.
///
/// Grouped by merchant, not address, because the quota is per merchant: that is
/// what the gateway throttles on. MEDIUM rather than HIGH because a retry loop
/// and an attack produce the same events, and saying which is which is a
/// judgement this rule does not have the evidence to make.
/// </summary>
public sealed class QuotaAbuseRule : IDetectionRule
{
    public string RuleId => "R2";

    public async Task<IReadOnlyList<RuleHit>> EvaluateAsync(
        NpgsqlConnection connection, DetectionRuleRecord config, DateTime now, CancellationToken ct)
    {
        const string sql = """
            SELECT user_id,
                   count(*)          AS hits,
                   array_agg(event_id) AS ids,
                   min(endpoint)     AS endpoint
            FROM   soc_events
            WHERE  event_type = 'RATE_LIMIT_EXCEEDED'
              AND  timestamp >= @since
              AND  user_id IS NOT NULL
            GROUP  BY user_id
            HAVING count(*) >= @threshold
            """;

        var rows = await Sql.QueryAsync(connection, sql,
            p =>
            {
                p.AddWithValue("since", now.AddMinutes(-config.WindowMinutes));
                p.AddWithValue("threshold", config.Threshold);
            },
            r => (User: r.GetGuid(0), Hits: r.GetInt64(1), Ids: Sql.Ids(r, 2),
                  Endpoint: r.IsDBNull(3) ? null : r.GetString(3)),
            ct);

        return rows.Select(row => new RuleHit(
            SuppressionKey: $"{RuleId}:{row.User}",
            Description:
                $"Merchant {row.User} was throttled {row.Hits} times within {config.WindowMinutes} minutes" +
                (row.Endpoint is null ? "." : $", mostly against {row.Endpoint}."),
            EventIds: row.Ids,
            UserId: row.User,
            AffectedService: "gateway",
            AffectedEntity: $"merchant:{row.User}")).ToList();
    }
}

// ---------------------------------------------------------------------------
// R3: dependency failure
// ---------------------------------------------------------------------------

/// <summary>
/// A service that its dependants have cut off, or that the registry has marked
/// critical.
///
/// Two sources for one condition. A circuit opening is what a caller concluded;
/// a failing health check is what the registry observed from outside. Either
/// alone can mislead: a breaker can open because of a network blip, and a health
/// check can pass while the service is useless. Together they are the difference
/// between "a call failed" and "this dependency is down".
/// </summary>
public sealed class DependencyFailureRule : IDetectionRule
{
    public string RuleId => "R3";

    public async Task<IReadOnlyList<RuleHit>> EvaluateAsync(
        NpgsqlConnection connection, DetectionRuleRecord config, DateTime now, CancellationToken ct)
    {
        const string sql = """
            SELECT coalesce(metadata_json->>'dependency',
                            metadata_json->>'observedService') AS service,
                   count(*)            AS signals,
                   array_agg(event_id) AS ids,
                   count(*) FILTER (WHERE event_type = 'CIRCUIT_OPENED') AS breaks
            FROM   soc_events
            WHERE  timestamp >= @since
              AND  ( event_type = 'CIRCUIT_OPENED'
                  OR (event_type = 'SERVICE_HEALTH_CHANGED'
                      AND metadata_json->>'to' = 'critical') )
            GROUP  BY 1
            HAVING count(*) >= @threshold
               AND coalesce(metadata_json->>'dependency',
                            metadata_json->>'observedService') IS NOT NULL
            """;

        var rows = await Sql.QueryAsync(connection, sql,
            p =>
            {
                p.AddWithValue("since", now.AddMinutes(-config.WindowMinutes));
                p.AddWithValue("threshold", config.Threshold);
            },
            r => (Service: r.GetString(0), Signals: r.GetInt64(1), Ids: Sql.Ids(r, 2), Breaks: r.GetInt64(3)),
            ct);

        return rows.Select(row => new RuleHit(
            SuppressionKey: $"{RuleId}:{row.Service}",
            Description:
                $"{row.Service} produced {row.Signals} failure signals within {config.WindowMinutes} minutes" +
                (row.Breaks > 0 ? $", including {row.Breaks} circuit opening(s)." : ", from failing health checks."),
            EventIds: row.Ids,
            AffectedService: row.Service,
            AffectedEntity: $"service:{row.Service}")).ToList();
    }
}

// ---------------------------------------------------------------------------
// R6: checkout enumeration
// ---------------------------------------------------------------------------

/// <summary>
/// One address working through invoice ids it does not hold links for.
///
/// The checkout id is the bearer token for a payment page, so a miss is somebody
/// holding a link that was never issued. One is a stale link. Forty in five
/// minutes is enumeration: CWE-639, OWASP API1:2023.
///
/// Worth noting what this cannot do. The ids are version 4 GUIDs, so guessing
/// one is not realistically possible, and this rule will catch the attempt
/// rather than prevent a breach. Detection is not prevention, and the report
/// says so rather than implying the rule is a control.
/// </summary>
public sealed class CheckoutEnumerationRule : IDetectionRule
{
    public string RuleId => "R6";

    public async Task<IReadOnlyList<RuleHit>> EvaluateAsync(
        NpgsqlConnection connection, DetectionRuleRecord config, DateTime now, CancellationToken ct)
    {
        const string sql = """
            SELECT source_ip,
                   count(*)                          AS misses,
                   count(DISTINCT affected_entity)   AS distinct_ids,
                   array_agg(event_id)               AS ids
            FROM   soc_events
            WHERE  event_type = 'CHECKOUT_ENUMERATION'
              AND  timestamp >= @since
              AND  source_ip IS NOT NULL
            GROUP  BY source_ip
            HAVING count(*) >= @threshold
            """;

        var rows = await Sql.QueryAsync(connection, sql,
            p =>
            {
                p.AddWithValue("since", now.AddMinutes(-config.WindowMinutes));
                p.AddWithValue("threshold", config.Threshold);
            },
            r => (Ip: r.GetString(0), Misses: r.GetInt64(1), Distinct: r.GetInt64(2), Ids: Sql.Ids(r, 3)),
            ct);

        return rows.Select(row => new RuleHit(
            SuppressionKey: $"{RuleId}:{row.Ip}",
            Description:
                $"{row.Ip} requested {row.Misses} checkout pages for {row.Distinct} invoice ids that do not exist, " +
                $"within {config.WindowMinutes} minutes.",
            EventIds: row.Ids,
            SourceIp: row.Ip,
            AffectedService: "invoice-service")).ToList();
    }
}

// ---------------------------------------------------------------------------
// R5: payout redirection
// ---------------------------------------------------------------------------

/// <summary>
/// A merchant's payout destination changed shortly after its credentials were
/// under pressure.
///
/// This is the rule the graph exists for. Changing a webhook URL is a normal
/// administrative act. Failing a login is normal too. Issuing a key is normal.
/// None of the three is worth waking anybody for, and the sequence of them
/// against one merchant is an account takeover in progress with the money about
/// to be redirected.
///
/// The prior events are collected per change rather than counted globally, so
/// the alert can name exactly which failures preceded which change.
/// </summary>
public sealed class PayoutRedirectionRule : IDetectionRule
{
    public string RuleId => "R5";

    public async Task<IReadOnlyList<RuleHit>> EvaluateAsync(
        NpgsqlConnection connection, DetectionRuleRecord config, DateTime now, CancellationToken ct)
    {
        const string sql = """
            SELECT w.user_id,
                   w.event_id,
                   ( SELECT array_agg(p.event_id)
                     FROM   soc_events p
                     WHERE  p.user_id = w.user_id
                       AND  p.event_type IN ('AUTH_FAILED', 'API_KEY_INVALID', 'API_KEY_ISSUED')
                       AND  p.timestamp <= w.timestamp
                       AND  p.timestamp >= w.timestamp - (@lookback_minutes * interval '1 minute')
                   ) AS prior_ids
            FROM   soc_events w
            WHERE  w.event_type = 'MERCHANT_WEBHOOK_URL_CHANGED'
              AND  w.timestamp >= @since
              AND  w.user_id IS NOT NULL
            """;

        var rows = await Sql.QueryAsync(connection, sql,
            p =>
            {
                p.AddWithValue("since", now.AddMinutes(-config.WindowMinutes));

                // The change is recent; the credential pressure that preceded it
                // need not be. An attacker who got in yesterday and moves the
                // payout address today is the case this has to catch.
                p.AddWithValue("lookback_minutes", 24 * 60);
            },
            r => (User: r.GetGuid(0), ChangeEvent: r.GetGuid(1), Prior: Sql.Ids(r, 2)),
            ct);

        return rows
            .Where(row => row.Prior.Length >= config.Threshold)
            .Select(row => new RuleHit(
                SuppressionKey: $"{RuleId}:{row.User}",
                Description:
                    $"Merchant {row.User} changed its payout webhook after {row.Prior.Length} credential " +
                    "events on the same account within the previous 24 hours.",
                EventIds: new[] { row.ChangeEvent }.Concat(row.Prior).Distinct().ToList(),
                UserId: row.User,
                AffectedService: "merchant-service",
                AffectedEntity: $"merchant:{row.User}"))
            .ToList();
    }
}

// ---------------------------------------------------------------------------
// R7: rate manipulation
// ---------------------------------------------------------------------------

/// <summary>
/// Invoices priced from a stale rate while the price feed was unavailable.
///
/// The Invoice service will quote from a cached rate for up to thirty minutes
/// when Rate cannot be reached, which is a deliberate degradation and the right
/// behaviour. It is also an exposure: someone who can keep the price feed down
/// can keep the platform quoting yesterday's number, and the platform absorbs
/// the difference at settlement.
///
/// No separate correlation is needed. Each INVOICE_CREATED already records
/// whether it was priced on a fallback and how stale that rate was, so the
/// condition reads exactly as it is written, and the alert can put a rand figure
/// on what is at stake rather than describing the shape of the problem.
/// </summary>
public sealed class RateManipulationRule : IDetectionRule
{
    public string RuleId => "R7";

    public async Task<IReadOnlyList<RuleHit>> EvaluateAsync(
        NpgsqlConnection connection, DetectionRuleRecord config, DateTime now, CancellationToken ct)
    {
        const string sql = """
            SELECT count(*)                                                      AS invoices,
                   coalesce(sum((metadata_json->>'amountZar')::numeric), 0)       AS exposed_zar,
                   coalesce(max((metadata_json->>'rateStalenessSeconds')::numeric), 0) AS max_stale,
                   array_agg(event_id)                                           AS ids
            FROM   soc_events
            WHERE  event_type = 'INVOICE_CREATED'
              AND  timestamp >= @since
              AND  (metadata_json->>'rateWasFallback')::boolean IS TRUE
            HAVING count(*) >= @threshold
            """;

        var rows = await Sql.QueryAsync(connection, sql,
            p =>
            {
                p.AddWithValue("since", now.AddMinutes(-config.WindowMinutes));
                p.AddWithValue("threshold", config.Threshold);
            },
            r => (Invoices: r.GetInt64(0), Zar: r.GetDecimal(1), MaxStale: r.GetDecimal(2), Ids: Sql.Ids(r, 3)),
            ct);

        return rows.Select(row => new RuleHit(
            SuppressionKey: $"{RuleId}:rate-service",
            Description:
                $"{row.Invoices} invoices worth {Sql.Zar(row.Zar)} were priced from a cached rate while the " +
                $"price feed was unavailable. The stalest quote was {row.MaxStale:N0} seconds old.",
            EventIds: row.Ids,
            AffectedService: "rate-service",
            AffectedEntity: "service:rate-service")).ToList();
    }
}
