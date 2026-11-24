using Neo4j.Driver;

namespace PandaPocket.Services.Graph.Graph;

/// <summary>
/// Uniqueness constraints on every node type's stable id.
///
/// These are not decoration. Every loader uses MERGE rather than CREATE so it
/// can be re-run without duplicating anything, and MERGE without a backing index
/// is a full label scan: on every node, on every load. The constraint creates
/// that index and makes the uniqueness a guarantee rather than a hope, so a
/// second loader run updates the node it already made instead of quietly
/// producing a twin that every later query then double counts.
/// </summary>
public static class GraphSchema
{
    private static readonly (string Name, string Label, string Property)[] Constraints =
    [
        // Application topology
        ("service_name",     "Service",       "name"),
        ("endpoint_id",      "Endpoint",      "id"),
        ("asset_id",         "Asset",         "id"),
        ("role_id",          "Role",          "id"),

        // Actors
        ("user_id",          "User",          "id"),
        ("sourceip_address", "SourceIp",      "address"),

        // SOC
        ("event_id",         "Event",         "id"),
        ("alert_id",         "Alert",         "id"),
        ("rule_id",          "Rule",          "id"),
        ("incident_id",      "Incident",      "id"),

        // Security knowledge
        ("threat_id",        "Threat",        "id"),
        ("vulnerability_id", "Vulnerability", "id"),
        ("control_id",       "Control",       "id"),
        ("procedure_id",     "Procedure",     "id"),
        ("chunk_id",         "Chunk",         "id")
    ];

    // Fully qualified: Neo4j.Driver also defines an ILogger, and an unqualified
    // one here resolves to whichever using happens to win.
    public static async Task ApplyAsync(
        IDriver driver, Microsoft.Extensions.Logging.ILogger logger, int dimensions, CancellationToken ct)
    {
        await using var session = driver.AsyncSession();

        foreach (var (name, label, property) in Constraints)
        {
            // IF NOT EXISTS, so startup is idempotent and a restart is not an
            // error. The identifier cannot be parameterised in DDL, and these
            // are fixed constants in this file rather than anything a caller can
            // reach, so interpolation here is not an injection surface.
            var cypher =
                $"CREATE CONSTRAINT {name} IF NOT EXISTS " +
                $"FOR (n:{label}) REQUIRE n.{property} IS UNIQUE";

            await session.RunAsync(cypher);
        }

        // Timestamps are filtered by range constantly ("alerts in the last
        // hour"), and a range scan over an unindexed property is a scan of every
        // node carrying it.
        await session.RunAsync("CREATE INDEX event_time IF NOT EXISTS FOR (e:Event) ON (e.timestamp)");
        await session.RunAsync("CREATE INDEX alert_time IF NOT EXISTS FOR (a:Alert) ON (a.lastSeenAt)");
        await session.RunAsync("CREATE INDEX event_type IF NOT EXISTS FOR (e:Event) ON (e.eventType)");

        // The vector index over procedure text.
        //
        // Dimensions must match the embedding model exactly. A mismatch is not
        // an error at any point: the index simply never matches anything, and
        // retrieval returns nothing for reasons that look like missing data. The
        // figure comes from configuration alongside the model name, so the two
        // cannot drift apart.
        await session.RunAsync(
            "CREATE VECTOR INDEX procedure_chunks IF NOT EXISTS " +
            "FOR (c:Chunk) ON c.embedding " +
            "OPTIONS {indexConfig: { " +
            "  `vector.dimensions`: " + dimensions + ", " +
            "  `vector.similarity_function`: 'cosine' " +
            "}}");

        logger.LogInformation(
            "Graph schema applied: {Constraints} constraints, 3 indexes, " +
            "1 vector index at {Dimensions} dimensions", Constraints.Length, dimensions);
    }
}
