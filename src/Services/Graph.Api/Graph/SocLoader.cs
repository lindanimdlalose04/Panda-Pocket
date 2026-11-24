using System.Text.Json.Serialization;
using Neo4j.Driver;

namespace PandaPocket.Services.Graph.Graph;

public sealed record SocLoadResult(
    int Rules, int Events, int Alerts, int Incidents, int Users, int SourceIps, IReadOnlyList<string> Warnings);

/// <summary>
/// Projects the SOC service's events and alerts into the graph.
///
/// Read over HTTP rather than out of soc_db directly. The SOC service owns that
/// database and nothing else connects to it, which is the rule the whole system
/// is built on; reaching past the owner for convenience would undo in one class
/// what the isolation proves everywhere else.
///
/// Everything is MERGE on a stable id, so a reload updates in place rather than
/// duplicating. That matters because this runs repeatedly: the graph is a
/// projection and is expected to be rebuilt, not accumulated.
/// </summary>
public sealed class SocLoader(
    IDriver driver,
    IHttpClientFactory httpClientFactory,
    ILogger<SocLoader> logger)
{
    public async Task<SocLoadResult> LoadAsync(int maxEvents, CancellationToken ct)
    {
        var warnings = new List<string>();
        var client = httpClientFactory.CreateClient("soc");

        var rules = await FetchAsync<List<RuleDto>>(client, "/api/soc/rules", warnings, ct) ?? [];
        // Paged, not asked for in one go.
        //
        // The events endpoint caps pageSize at 200 and silently falls back to 50
        // when a caller asks for more, so requesting 500 loaded fifty events and
        // every alert whose evidence was older than those fifty lost its link to
        // the events behind it. The alert survived, the "why" did not, which is
        // the one thing the graph exists to answer.
        var events = await FetchEventsAsync(client, maxEvents, warnings, ct);
        var alerts = (await FetchAsync<AlertPage>(client, "/api/soc/alerts?pageSize=200", warnings, ct))?.Items ?? [];

        await using var session = driver.AsyncSession();

        await LoadRolesAsync(session);
        await LoadRulesAsync(session, rules);
        var (users, ips) = await LoadEventsAsync(session, events);
        var incidents = await LoadAlertsAsync(session, client, alerts, warnings, ct);

        logger.LogInformation(
            "SOC subgraph loaded: {Rules} rules, {Events} events, {Alerts} alerts, {Incidents} incidents",
            rules.Count, events.Count, alerts.Count, incidents);

        return new SocLoadResult(rules.Count, events.Count, alerts.Count, incidents, users, ips, warnings);
    }

    // -----------------------------------------------------------------------
    // Roles
    // -----------------------------------------------------------------------
    private static async Task LoadRolesAsync(IAsyncSession session)
    {
        // Two roles, because this system genuinely has two. A merchant owner
        // holds a dashboard login; a server holds an API key. The specification
        // asks for the node type and inventing a hierarchy that does not exist
        // would make the graph describe a system other than this one.
        await session.RunAsync("""
            MERGE (r:Role {id: 'merchant-owner'})
              SET r.name = 'Merchant owner', r.credential = 'password and JWT'
            MERGE (s:Role {id: 'service-account'})
              SET s.name = 'Service account', s.credential = 'API key'
            """);
    }

    // -----------------------------------------------------------------------
    // Rules
    // -----------------------------------------------------------------------
    private static async Task LoadRulesAsync(IAsyncSession session, List<RuleDto> rules)
    {
        if (rules.Count == 0) return;

        await session.RunAsync("""
            UNWIND $rules AS rule
            MERGE (r:Rule {id: rule.ruleId})
            SET   r.name              = rule.name,
                  r.purpose           = rule.purpose,
                  r.condition         = rule.condition,
                  r.severity          = rule.severity,
                  r.alertType         = rule.alertType,
                  r.recommendedAction = rule.recommendedAction,
                  r.threatReference   = rule.threatReference,
                  r.inputEventTypes   = rule.inputEventTypes
            """,
            new
            {
                rules = rules.Select(r => new Dictionary<string, object?>
                {
                    ["ruleId"] = r.RuleId,
                    ["name"] = r.Name,
                    ["purpose"] = r.Purpose,
                    ["condition"] = r.Condition,
                    ["severity"] = r.Severity,
                    ["alertType"] = r.AlertType,
                    ["recommendedAction"] = r.RecommendedAction,
                    ["threatReference"] = r.ThreatReference,
                    ["inputEventTypes"] = r.InputEventTypes
                }).ToList()
            });
    }

    // -----------------------------------------------------------------------
    // Events, and the actors behind them
    // -----------------------------------------------------------------------
    private static async Task<(int Users, int SourceIps)> LoadEventsAsync(
        IAsyncSession session, List<EventDto> events)
    {
        if (events.Count == 0) return (0, 0);

        // Batched. One round trip per event would be thousands of round trips,
        // and the driver would spend most of its time waiting rather than
        // writing.
        const int batchSize = 200;

        foreach (var batch in events.Chunk(batchSize))
        {
            await session.RunAsync("""
                UNWIND $events AS ev
                MERGE (e:Event {id: ev.id})
                SET   e.timestamp     = datetime(ev.timestamp),
                      e.eventType     = ev.eventType,
                      e.severity      = ev.severity,
                      e.correlationId = ev.correlationId,
                      e.message       = ev.message,
                      e.statusCode    = ev.statusCode,
                      e.endpoint      = ev.endpoint,
                      e.httpMethod    = ev.httpMethod

                // The observing service. MERGE rather than MATCH so an event
                // from a service the topology load has not seen still lands,
                // rather than being silently dropped.
                MERGE (s:Service {name: ev.serviceName})
                MERGE (e)-[:GENERATED_BY]->(s)

                // The merchant, where the event knows one.
                FOREACH (_ IN CASE WHEN ev.userId IS NULL THEN [] ELSE [1] END |
                    MERGE (u:User:Merchant {id: ev.userId})
                    MERGE (u)-[:TRIGGERED]->(e)
                    MERGE (role:Role {id: 'merchant-owner'})
                    MERGE (u)-[:HAS_ROLE]->(role)
                )

                // The calling address. Its own node, not a property, because an
                // attack spread across accounts is a fan out from one address
                // and that is a shape rather than a value.
                FOREACH (_ IN CASE WHEN ev.sourceIp IS NULL THEN [] ELSE [1] END |
                    MERGE (ip:SourceIp {address: ev.sourceIp})
                    ON CREATE SET ip.firstSeen = datetime(ev.timestamp)
                    SET ip.lastSeen = datetime(ev.timestamp)
                    MERGE (ip)-[:ORIGINATED]->(e)
                )
                """,
                new { events = batch.Select(ToMap).ToList() });

            // Endpoints are matched separately, because the path on an event is
            // concrete ("/api/invoices/abc") while the gateway's route is a
            // template ("/api/invoices/{everything}"). Comparing the concrete
            // path against the template's fixed prefix, longest first, picks the
            // most specific route rather than the first that happens to match.
            await session.RunAsync("""
                UNWIND $events AS ev
                MATCH (e:Event {id: ev.id})
                WITH e, ev WHERE ev.endpoint IS NOT NULL
                OPTIONAL MATCH (ep:Endpoint)
                  WHERE ep.method = ev.httpMethod
                    AND ev.endpoint STARTS WITH split(ep.path, '{')[0]
                WITH e, ep ORDER BY size(ep.path) DESC
                WITH e, head(collect(ep)) AS best
                FOREACH (_ IN CASE WHEN best IS NULL THEN [] ELSE [1] END |
                    MERGE (e)-[:TARGETED]->(best)
                )
                """,
                new { events = batch.Select(ToMap).ToList() });
        }

        var users = events.Select(e => e.UserId).Where(u => u is not null).Distinct().Count();
        var ips = events.Select(e => e.SourceIp).Where(i => i is not null).Distinct().Count();
        return (users, ips);
    }

    private static Dictionary<string, object?> ToMap(EventDto e) => new()
    {
        ["id"] = e.EventId.ToString(),
        ["timestamp"] = e.Timestamp.ToString("o"),
        ["serviceName"] = e.ServiceName,
        ["eventType"] = e.EventType,
        ["severity"] = e.Severity,
        ["correlationId"] = e.CorrelationId,
        ["userId"] = e.UserId?.ToString(),
        ["sourceIp"] = e.SourceIp,
        ["endpoint"] = e.Endpoint,
        ["httpMethod"] = e.HttpMethod,
        ["statusCode"] = e.StatusCode,
        ["message"] = e.Message
    };

    // -----------------------------------------------------------------------
    // Alerts, the events behind them, and incidents
    // -----------------------------------------------------------------------
    private async Task<int> LoadAlertsAsync(
        IAsyncSession session, HttpClient client, List<AlertDto> alerts,
        List<string> warnings, CancellationToken ct)
    {
        var incidents = 0;

        foreach (var alert in alerts)
        {
            await session.RunAsync("""
                MERGE (a:Alert {id: $id})
                SET   a.ruleId            = $ruleId,
                      a.ruleName          = $ruleName,
                      a.severity          = $severity,
                      a.status            = $status,
                      a.description       = $description,
                      a.recommendedAction = $recommendedAction,
                      a.eventCount        = $eventCount,
                      a.firstSeenAt       = datetime($firstSeenAt),
                      a.lastSeenAt        = datetime($lastSeenAt)

                MERGE (r:Rule {id: $ruleId})
                MERGE (r)-[:RAISED]->(a)

                FOREACH (_ IN CASE WHEN $affectedService IS NULL THEN [] ELSE [1] END |
                    MERGE (s:Service {name: $affectedService})
                    MERGE (a)-[:AFFECTS]->(s)
                )

                FOREACH (_ IN CASE WHEN $affectedUserId IS NULL THEN [] ELSE [1] END |
                    MERGE (u:User:Merchant {id: $affectedUserId})
                    MERGE (a)-[:CONCERNS]->(u)
                )

                FOREACH (_ IN CASE WHEN $sourceIp IS NULL THEN [] ELSE [1] END |
                    MERGE (ip:SourceIp {address: $sourceIp})
                    MERGE (a)-[:ATTRIBUTED_TO]->(ip)
                )
                """,
                new
                {
                    id = alert.AlertId.ToString(),
                    ruleId = alert.RuleId,
                    ruleName = alert.RuleName,
                    severity = alert.Severity,
                    status = alert.Status,
                    description = alert.Description,
                    recommendedAction = alert.RecommendedAction,
                    eventCount = alert.EventCount,
                    firstSeenAt = alert.FirstSeenAt.ToString("o"),
                    lastSeenAt = alert.LastSeenAt.ToString("o"),
                    affectedService = alert.AffectedService,
                    affectedUserId = alert.AffectedUserId?.ToString(),
                    sourceIp = alert.SourceIp
                });

            // The events behind the alert. This edge is the whole point: it is
            // what turns "an alert exists" into "here is why", and it is the
            // path the RAG layer walks to answer that question.
            var detail = await FetchAsync<AlertDetailDto>(
                client, $"/api/soc/alerts/{alert.AlertId}", warnings, ct);

            if (detail?.Events is { Count: > 0 } linked)
            {
                await session.RunAsync("""
                    MATCH (a:Alert {id: $alertId})
                    UNWIND $eventIds AS eid
                    MATCH (e:Event {id: eid})
                    MERGE (e)-[:CREATED_ALERT]->(a)
                    """,
                    new
                    {
                        alertId = alert.AlertId.ToString(),
                        eventIds = linked.Select(e => e.EventId.ToString()).ToList()
                    });
            }
        }

        // Incidents: several alerts that are one story.
        //
        // Grouped by the merchant they concern, and only when a CRITICAL alert
        // is among them. Two MEDIUM alerts about one account is a busy day; a
        // CRITICAL with corroborating alerts around it is an incident. Doing
        // this in the graph rather than in the SOC service is deliberate: it is
        // a question about how findings relate, which is what the graph is for.
        // "all" cannot be a variable name here. Cypher reserves it for the
        // all() predicate, so size(all) parsed as that function with the wrong
        // number of arguments and the whole load failed with "Insufficient
        // parameters for function 'size'", which names the symptom and not the
        // cause.
        var created = await session.RunAsync("""
            MATCH (critical:Alert {severity: 'CRITICAL', status: 'OPEN'})-[:CONCERNS]->(u:User)
            MATCH (related:Alert {status: 'OPEN'})-[:CONCERNS]->(u)
            WITH u, collect(DISTINCT related) AS grouped
            MERGE (i:Incident {id: 'incident-' + u.id})
            SET   i.title    = 'Suspected account takeover',
                  i.severity = 'CRITICAL',
                  i.status   = 'OPEN',
                  i.openedAt = datetime(),
                  i.summary  = toString(size(grouped)) +
                               ' alerts concerning one merchant, including a critical finding.'
            MERGE (i)-[:CONCERNS]->(u)
            WITH i, grouped
            UNWIND grouped AS a
            MERGE (i)-[:CONTAINS]->(a)
            RETURN count(DISTINCT i) AS incidents
            """);

        // No critical alert means no incident, and an empty cursor is the
        // correct answer rather than an error. SingleAsync would throw on it.
        var records = await created.ToListAsync();
        incidents = records.Count == 0 ? 0 : records[0]["incidents"].As<int>();

        return incidents;
    }

    /// <summary>
    /// Walks the event feed a page at a time until it has enough or runs out.
    /// </summary>
    private async Task<List<EventDto>> FetchEventsAsync(
        HttpClient client, int maxEvents, List<string> warnings, CancellationToken ct)
    {
        const int pageSize = 200;   // the endpoint's own ceiling

        var collected = new List<EventDto>();

        for (var page = 1; collected.Count < maxEvents; page++)
        {
            var batch = await FetchAsync<EventPage>(
                client, $"/api/soc/events?page={page}&pageSize={pageSize}", warnings, ct);

            if (batch?.Items is not { Count: > 0 }) break;

            collected.AddRange(batch.Items);

            // A short page is the last page.
            if (batch.Items.Count < pageSize) break;
        }

        return collected.Take(maxEvents).ToList();
    }

    private async Task<T?> FetchAsync<T>(
        HttpClient client, string path, List<string> warnings, CancellationToken ct)
    {
        try
        {
            return await client.GetFromJsonAsync<T>(path, ct);
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not read {path}: {ex.Message}");
            logger.LogWarning("SOC read failed for {Path}: {Reason}", path, ex.Message);
            return default;
        }
    }

    // -----------------------------------------------------------------------
    // What the SOC service returns
    // -----------------------------------------------------------------------
    private sealed record RuleDto(
        [property: JsonPropertyName("ruleId")] string RuleId,
        string Name, string Purpose, string Condition, string Severity,
        string AlertType, string RecommendedAction, string? ThreatReference, string InputEventTypes);

    private sealed record EventPage(List<EventDto> Items);

    private sealed record EventDto(
        Guid EventId, DateTime Timestamp, string ServiceName, string EventType, string Severity,
        string CorrelationId, Guid? UserId, string? SourceIp, string? Endpoint, string? HttpMethod,
        int? StatusCode, string? Message, string? AffectedEntity, string? Metadata);

    private sealed record AlertPage(List<AlertDto> Items);

    private sealed record AlertDto(
        Guid AlertId, DateTime FirstSeenAt, DateTime LastSeenAt, string RuleId, string RuleName,
        string Severity, string Status, string Description, string RecommendedAction,
        string? AffectedService, Guid? AffectedUserId, string? AffectedEntity, string? SourceIp,
        int EventCount, Guid? IncidentId);

    private sealed record AlertDetailDto(AlertDto Alert, RuleDto? Rule, List<EventDto> Events);
}
