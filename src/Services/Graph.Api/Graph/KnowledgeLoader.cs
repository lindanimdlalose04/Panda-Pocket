using Neo4j.Driver;

namespace PandaPocket.Services.Graph.Graph;

public sealed record KnowledgeLoadResult(
    int Threats, int Vulnerabilities, int Controls, int Procedures, int Chunks, int AlertsLinked);

/// <summary>
/// Loads the curated security knowledge and joins it to what the system
/// observed.
///
/// This is the load that makes the graph worth having. Before it, the graph can
/// say an alert happened and which events caused it, which a well-indexed table
/// could also do. After it, an alert reaches a threat, the threat reaches the
/// weakness it exploits and the controls that mitigate it, and the controls
/// reach the written procedure for responding. That path is the answer to "why
/// was this raised and what should be done", and every edge on it is typed, so
/// the answer can cite its reasoning rather than asserting it.
///
/// The join between the two halves is one small table: which rule detects which
/// threat. Everything the system observed hangs off a rule, and everything known
/// about security hangs off a threat, so that single mapping is what turns two
/// disconnected subgraphs into one.
/// </summary>
public sealed class KnowledgeLoader(
    IDriver driver,
    PandaPocket.Services.Graph.Rag.EmbeddingClient embeddings,
    ILogger<KnowledgeLoader> logger)
{
    public async Task<KnowledgeLoadResult> LoadAsync(CancellationToken ct)
    {
        await using var session = driver.AsyncSession();

        await LoadThreatsAsync(session);
        await LoadVulnerabilitiesAsync(session);
        await LoadControlsAsync(session);
        var chunks = await LoadProceduresAsync(session);
        await EmbedChunksAsync(session, ct);
        var linked = await LinkToObservationsAsync(session);

        logger.LogInformation(
            "Knowledge subgraph loaded: {Threats} threats, {Controls} controls, {Procedures} procedures, " +
            "{Chunks} chunks, {Linked} alerts linked to threats",
            KnowledgeSeed.Threats.Length, KnowledgeSeed.Controls.Length,
            KnowledgeSeed.Procedures.Length, chunks, linked);

        return new KnowledgeLoadResult(
            KnowledgeSeed.Threats.Length,
            KnowledgeSeed.Vulnerabilities.Length,
            KnowledgeSeed.Controls.Length,
            KnowledgeSeed.Procedures.Length,
            chunks,
            linked);
    }

    private static async Task LoadThreatsAsync(IAsyncSession session)
    {
        await session.RunAsync("""
            UNWIND $threats AS t
            MERGE (threat:Threat {id: t.id})
            SET   threat.name        = t.name,
                  threat.description = t.description,
                  threat.referenceId = t.referenceId,
                  threat.sourceUrl   = t.sourceUrl

            // A threat that names a service it targets gets the edge. Not every
            // one does, and inventing a target to make the graph look fuller
            // would make it less true.
            FOREACH (_ IN CASE WHEN t.targets IS NULL THEN [] ELSE [1] END |
                MERGE (s:Service {name: t.targets})
                MERGE (threat)-[:TARGETS]->(s)
            )
            """,
            new
            {
                threats = KnowledgeSeed.Threats.Select(t => new Dictionary<string, object?>
                {
                    ["id"] = t.Id,
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["referenceId"] = t.ReferenceId,
                    ["sourceUrl"] = t.SourceUrl,
                    ["targets"] = t.TargetsService
                }).ToList()
            });
    }

    private static async Task LoadVulnerabilitiesAsync(IAsyncSession session)
    {
        await session.RunAsync("""
            UNWIND $vulns AS v
            MERGE (x:Vulnerability {id: v.id})
            SET   x.name      = v.name,
                  x.sourceUrl = v.sourceUrl,
                  x.status    = 'reference weakness type'
            """,
            new
            {
                vulns = KnowledgeSeed.Vulnerabilities.Select(v => new Dictionary<string, object?>
                {
                    ["id"] = v.Id, ["name"] = v.Name, ["sourceUrl"] = v.SourceUrl
                }).ToList()
            });

        await session.RunAsync("""
            UNWIND $pairs AS p
            MATCH (t:Threat {id: p.threat})
            MATCH (v:Vulnerability {id: p.vuln})
            MERGE (t)-[:EXPLOITS]->(v)
            """,
            new
            {
                pairs = KnowledgeSeed.Exploits
                    .Select(e => new Dictionary<string, object?> { ["threat"] = e.ThreatId, ["vuln"] = e.VulnerabilityId })
                    .ToList()
            });
    }

    private static async Task LoadControlsAsync(IAsyncSession session)
    {
        // "implemented" is a real property, not a label. Several of these the
        // platform genuinely has and several it does not, and a graph that
        // presented a recommended control as an existing one would describe a
        // safer system than the one that is running.
        await session.RunAsync("""
            UNWIND $controls AS c
            MERGE (ctrl:Control {id: c.id})
            SET   ctrl.name        = c.name,
                  ctrl.description = c.description,
                  ctrl.implemented = c.implemented,
                  ctrl.note        = c.note
            """,
            new
            {
                controls = KnowledgeSeed.Controls.Select(c => new Dictionary<string, object?>
                {
                    ["id"] = c.Id,
                    ["name"] = c.Name,
                    ["description"] = c.Description,
                    ["implemented"] = c.Implemented,
                    ["note"] = c.Note
                }).ToList()
            });

        await session.RunAsync("""
            UNWIND $pairs AS p
            MATCH (c:Control {id: p.control})
            MATCH (t:Threat  {id: p.threat})
            MERGE (c)-[:MITIGATES]->(t)
            """,
            new
            {
                pairs = KnowledgeSeed.Mitigates
                    .Select(m => new Dictionary<string, object?> { ["control"] = m.ControlId, ["threat"] = m.ThreatId })
                    .ToList()
            });
    }

    /// <summary>
    /// Procedures, split into paragraph-sized chunks.
    ///
    /// The chunk is the retrieval unit, not the whole document. A procedure
    /// answers three different questions in three paragraphs, and embedding it
    /// whole would produce one vector that is a blurred average of all three and
    /// matches none of them well. Each chunk keeps its document and ordinal, so
    /// a retrieved paragraph can still be cited as "P-01 revision 2, step 2"
    /// rather than as a floating piece of text.
    /// </summary>
    private static async Task<int> LoadProceduresAsync(IAsyncSession session)
    {
        var chunks = 0;

        foreach (var procedure in KnowledgeSeed.Procedures)
        {
            await session.RunAsync("""
                MERGE (p:Procedure {id: $id})
                SET   p.title    = $title,
                      p.revision = $revision,
                      p.text     = $fullText
                """,
                new
                {
                    id = procedure.Id,
                    title = procedure.Title,
                    revision = procedure.Revision,
                    fullText = string.Join("\n\n", procedure.Paragraphs)
                });

            for (var i = 0; i < procedure.Paragraphs.Length; i++)
            {
                await session.RunAsync("""
                    MATCH (p:Procedure {id: $procedureId})
                    MERGE (c:Chunk {id: $chunkId})
                    SET   c.text     = $text,
                          c.ordinal  = $ordinal,
                          c.revision = $revision
                    MERGE (p)-[:HAS_CHUNK]->(c)
                    """,
                    new
                    {
                        procedureId = procedure.Id,
                        chunkId = $"{procedure.Id}-{i + 1}",
                        text = procedure.Paragraphs[i],
                        ordinal = i + 1,
                        revision = procedure.Revision
                    });

                chunks++;
            }

            foreach (var threatId in procedure.RespondsToThreats)
            {
                await session.RunAsync("""
                    MATCH (p:Procedure {id: $procedure})
                    MATCH (t:Threat    {id: $threat})
                    MERGE (p)-[:SUPPORTS_RESPONSE_TO]->(t)
                    WITH p, t
                    MATCH (p)-[:HAS_CHUNK]->(c:Chunk)
                    MERGE (c)-[:MENTIONS]->(t)
                    """,
                    new { procedure = procedure.Id, threat = threatId });
            }

            foreach (var controlId in procedure.DescribesControls)
            {
                await session.RunAsync("""
                    MATCH (p:Procedure {id: $procedure})
                    MATCH (c:Control   {id: $control})
                    MERGE (p)-[:DESCRIBES]->(c)
                    """,
                    new { procedure = procedure.Id, control = controlId });
            }
        }

        return chunks;
    }

    /// <summary>
    /// Gives every chunk a vector, so the vector index has something to search.
    ///
    /// Done here rather than at query time because the procedure text does not
    /// change between questions: embedding it once at load is nine calls, and
    /// embedding it per question would be nine calls per question for an
    /// identical result.
    ///
    /// Only chunks without a vector are embedded, so a reload after an edit
    /// costs one call rather than nine, and a reload with the model unavailable
    /// leaves the existing vectors alone rather than clearing them.
    /// </summary>
    private async Task EmbedChunksAsync(IAsyncSession session, CancellationToken ct)
    {
        var cursor = await session.RunAsync("""
            MATCH (c:Chunk)
            WHERE c.embedding IS NULL
            RETURN c.id AS id, c.text AS text
            """);

        var pending = await cursor.ToListAsync(r => (Id: r["id"].As<string>(), Text: r["text"].As<string>()));

        if (pending.Count == 0)
        {
            logger.LogInformation("All chunks already have embeddings");
            return;
        }

        var embedded = 0;

        foreach (var (id, text) in pending)
        {
            var vector = await embeddings.EmbedAsync(text, ct);

            if (vector is null)
            {
                // Reported once, not per chunk. Without vectors the vector and
                // hybrid routes return nothing, while the Cypher route is
                // unaffected, so this degrades retrieval rather than breaking it.
                logger.LogWarning(
                    "Could not embed chunk {ChunkId}; vector and hybrid retrieval will be unavailable " +
                    "until the embedding model is reachable and the knowledge load is re-run", id);
                return;
            }

            await session.RunAsync(
                "MATCH (c:Chunk {id: $id}) SET c.embedding = $embedding",
                new { id, embedding = vector });

            embedded++;
        }

        logger.LogInformation("Embedded {Count} procedure chunks", embedded);
    }

    /// <summary>
    /// Joins the two halves: every alert reaches the threat its rule detects.
    ///
    /// Derived through the rule rather than written on the alert, so a rule
    /// whose threat mapping changes updates every alert it ever raised instead
    /// of leaving the old ones pointing somewhere that is no longer true.
    /// </summary>
    private static async Task<int> LinkToObservationsAsync(IAsyncSession session)
    {
        await session.RunAsync("""
            UNWIND $pairs AS p
            MATCH (r:Rule   {id: p.rule})
            MATCH (t:Threat {id: p.threat})
            MERGE (r)-[:DETECTS]->(t)
            """,
            new
            {
                pairs = KnowledgeSeed.RuleDetects
                    .Select(d => new Dictionary<string, object?> { ["rule"] = d.RuleId, ["threat"] = d.ThreatId })
                    .ToList()
            });

        var cursor = await session.RunAsync("""
            MATCH (r:Rule)-[:DETECTS]->(t:Threat)
            MATCH (r)-[:RAISED]->(a:Alert)
            MERGE (a)-[:INDICATES]->(t)
            RETURN count(DISTINCT a) AS linked
            """);

        var records = await cursor.ToListAsync();
        return records.Count == 0 ? 0 : records[0]["linked"].As<int>();
    }
}
