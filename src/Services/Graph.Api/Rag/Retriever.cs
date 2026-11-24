using Microsoft.Extensions.Options;
using Neo4j.Driver;
using PandaPocket.Services.Graph.Configuration;

namespace PandaPocket.Services.Graph.Rag;

/// <summary>
/// Gathers evidence for a question. Produces no prose.
///
/// Three routes, because they answer different shapes of question and the
/// specification asks for the difference to be demonstrated rather than
/// asserted:
///
///   Cypher   exact paths for questions with a precise answer. "Which control
///            mitigates the threat behind A-42" has one correct result and a
///            similarity search would only approximate it.
///
///   Vector   similarity over written procedure text, for questions phrased in
///            words the graph does not contain. "What should I check first"
///            matches a paragraph about what to establish before volume, with
///            no keyword in common.
///
///   Hybrid   similarity finds where to start, traversal supplies the specifics.
///            This is the one that answers "why was this raised and what should
///            be done", because the passage alone is generic and the graph alone
///            has no prose.
///
/// Intent matching is keyword and pattern based on purpose. The specification
/// requires the retrieval process to be explainable rather than delegated, and a
/// matcher whose rules can be read is worth more here than a classifier whose
/// behaviour can only be described statistically.
/// </summary>
public sealed class Retriever(
    IDriver driver,
    EmbeddingClient embeddings,
    IOptions<GraphOptions> options,
    ILogger<Retriever> logger)
{
    private readonly GraphOptions _options = options.Value;

    public async Task<EvidenceEnvelope> RetrieveAsync(string question, string? preferredRoute, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return EvidenceEnvelope.Nothing(question ?? "", "No question was asked.");
        }

        var normalised = question.Trim();

        // Honoured when given, so the evaluation can compare the three routes on
        // the same questions rather than on whatever each would have chosen.
        if (preferredRoute is RetrievalRoute.Vector) return await VectorAsync(normalised, ct);
        if (preferredRoute is RetrievalRoute.Hybrid) return await HybridAsync(normalised, ct);

        if (preferredRoute is RetrievalRoute.Cypher)
        {
            return await TryCypherAsync(normalised, ct)
                   ?? EvidenceEnvelope.Nothing(normalised,
                       "No Cypher intent matched this question, and the Cypher route was requested explicitly.");
        }

        // Automatic. Cypher first: when a question has an exact answer, an exact
        // answer is better than a near one.
        var exact = await TryCypherAsync(normalised, ct);
        if (exact is not null) return exact;

        var hybrid = await HybridAsync(normalised, ct);
        if (!hybrid.IsEmpty) return hybrid;

        return await VectorAsync(normalised, ct);
    }

    // =======================================================================
    // Route 1: Cypher
    // =======================================================================
    private async Task<EvidenceEnvelope?> TryCypherAsync(string question, CancellationToken ct)
    {
        var q = question.ToLowerInvariant();

        if (Mentions(q, "why", "explain", "raised", "caused", "behind")
            && Mentions(q, "alert", "a-", "r1", "r2", "r3", "r5", "r6", "r7", "critical"))
        {
            return await ExplainAlertAsync(question, ct);
        }

        if (Mentions(q, "control", "mitigate", "fix", "prevent", "stop"))
        {
            return await ControlsForThreatAsync(question, ct);
        }

        if (Mentions(q, "depend", "impact", "affected", "blast", "fails", "goes down", "breaks"))
        {
            return await DependencyImpactAsync(question, ct);
        }

        if (Mentions(q, "user", "merchant", "who", "account") && Mentions(q, "alert", "high", "critical"))
        {
            return await UsersWithSevereAlertsAsync(question, ct);
        }

        if (Mentions(q, "threat") && Mentions(q, "service", "target"))
        {
            return await ThreatsForServiceAsync(question, ct);
        }

        return null;
    }

    private async Task<EvidenceEnvelope> ExplainAlertAsync(string question, CancellationToken ct)
    {
        const string cypher = """
            MATCH (r:Rule)-[:RAISED]->(a:Alert)
            WHERE ($ruleId IS NULL OR a.ruleId = $ruleId)
              AND ($severity IS NULL OR a.severity = $severity)
            WITH r, a ORDER BY a.lastSeenAt DESC LIMIT 1
            MATCH (e:Event)-[:CREATED_ALERT]->(a)
            OPTIONAL MATCH (a)-[:INDICATES]->(t:Threat)
            OPTIONAL MATCH (t)-[:EXPLOITS]->(v:Vulnerability)
            OPTIONAL MATCH (c:Control)-[:MITIGATES]->(t)
            OPTIONAL MATCH (p:Procedure)-[:SUPPORTS_RESPONSE_TO]->(t)

            // The first step, so the answer can quote the procedure rather than
            // only cite it. Without this the Cypher route named P-03 and could
            // not say what P-03 tells an analyst to do, which is most of its
            // value.
            OPTIONAL MATCH (p)-[:HAS_CHUNK]->(step:Chunk {ordinal: 1})
            OPTIONAL MATCH (a)-[:AFFECTS]->(s:Service)
            OPTIONAL MATCH (a)-[:CONCERNS]->(u:User)
            OPTIONAL MATCH (a)-[:ATTRIBUTED_TO]->(ip:SourceIp)
            RETURN a.id            AS alertId,
                   a.ruleId        AS ruleId,
                   a.ruleName      AS ruleName,
                   a.severity      AS severity,
                   a.description   AS description,
                   a.recommendedAction AS recommendedAction,
                   r.condition     AS condition,
                   r.threatReference AS ruleReference,
                   count(DISTINCT e) AS eventCount,
                   collect(DISTINCT e.eventType) AS eventTypes,
                   collect(DISTINCT e.id)[0..6]  AS sampleEventIds,
                   head(collect(DISTINCT t.name))        AS threat,
                   head(collect(DISTINCT t.id))          AS threatId,
                   head(collect(DISTINCT t.referenceId)) AS threatReference,
                   collect(DISTINCT {id: v.id, name: v.name}) AS weaknesses,
                   collect(DISTINCT {id: c.id, name: c.name, implemented: c.implemented}) AS controls,
                   collect(DISTINCT {id: p.id, title: p.title, revision: p.revision,
                                     firstStep: step.text})                              AS procedures,
                   head(collect(DISTINCT s.name))  AS service,
                   head(collect(DISTINCT u.id))    AS merchant,
                   head(collect(DISTINCT ip.address)) AS sourceIp
            """;

        var ruleId = ExtractRuleId(question);
        var severity = question.Contains("critical", StringComparison.OrdinalIgnoreCase) ? "CRITICAL" : null;

        await using var session = driver.AsyncSession();
        var rows = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(cypher, new { ruleId, severity });
            return await cursor.ToListAsync();
        });

        if (rows.Count == 0)
        {
            return EvidenceEnvelope.Nothing(question,
                "A Cypher intent matched (explain an alert) but no alert in the graph fits the question.");
        }

        var row = rows[0];
        var nodes = new List<EvidenceNode>();
        var edges = new List<EvidenceEdge>();
        var sources = new List<SourceDocument>();

        var alertId = row["alertId"].As<string>();
        var ruleRef = row["ruleId"].As<string>();

        nodes.Add(new EvidenceNode("Alert", alertId,
            $"{ruleRef} {row["severity"].As<string>()}: {row["description"].As<string>()}"));

        nodes.Add(new EvidenceNode("Rule", ruleRef,
            $"{row["ruleName"].As<string>()}. Condition: {row["condition"].As<string?>()}"));

        edges.Add(new EvidenceEdge($"Rule {ruleRef}", "RAISED", $"Alert {alertId}"));

        var eventTypes = row["eventTypes"].As<List<object>>().Select(t => t?.ToString() ?? "").ToList();
        var eventCount = row["eventCount"].As<int>();

        // Real event ids, not a summary node.
        //
        // This emitted one node whose id was the string "4 events", which is not
        // an identifier of anything. Any check that every cited id exists in the
        // graph would correctly call that a fabricated citation, and an analyst
        // following the evidence could not look the events up. A sample of the
        // actual ids is cited, with the full count stated alongside.
        foreach (var id in row["sampleEventIds"].As<List<object>>().Where(i => i is not null))
        {
            nodes.Add(new EvidenceNode("Event", id.ToString()!,
                $"One of {eventCount} events behind this alert, of type {string.Join(", ", eventTypes)}"));
            edges.Add(new EvidenceEdge(id.ToString()!, "CREATED_ALERT", alertId));
        }

        if (row["threat"].As<string?>() is { } threatName)
        {
            var threatId = row["threatId"].As<string?>();
            var reference = row["threatReference"].As<string?>();

            nodes.Add(new EvidenceNode("Threat", threatId ?? threatName,
                reference is null ? threatName : $"{threatName} ({reference})"));
            edges.Add(new EvidenceEdge($"Alert {alertId}", "INDICATES", threatName));

            // The weakness's real name, not its status.
            //
            // This carried the literal string "Reference weakness type" for
            // every weakness, so the evidence said CWE-307 was a reference
            // weakness type and never what CWE-307 actually is. The template
            // repeated it and so did the model, faithfully: bad evidence
            // produces a confident wrong answer through both generators, which
            // is the clearest argument there is for inspecting the envelope
            // rather than only reading the prose.
            foreach (var weakness in row["weaknesses"].As<List<IDictionary<string, object>>>())
            {
                if (weakness["id"] is null) continue;

                var id = weakness["id"].ToString()!;
                nodes.Add(new EvidenceNode("Vulnerability", id, weakness["name"]?.ToString() ?? id));
                edges.Add(new EvidenceEdge(threatName, "EXPLOITS", id));
            }

            foreach (var control in row["controls"].As<List<IDictionary<string, object>>>())
            {
                if (control["id"] is null) continue;

                var implemented = control["implemented"] is bool b && b;
                nodes.Add(new EvidenceNode("Control", control["id"].ToString()!,
                    $"{control["name"]}{(implemented ? " (implemented)" : " (NOT implemented)")}"));
                edges.Add(new EvidenceEdge(control["id"].ToString()!, "MITIGATES", threatName));
            }

            foreach (var procedure in row["procedures"].As<List<IDictionary<string, object>>>())
            {
                if (procedure["id"] is null) continue;

                var id = procedure["id"].ToString()!;
                var revision = Convert.ToInt32(procedure["revision"]);

                nodes.Add(new EvidenceNode("Procedure", id, $"{procedure["title"]} (revision {revision})"));
                edges.Add(new EvidenceEdge(id, "SUPPORTS_RESPONSE_TO", threatName));

                sources.Add(new SourceDocument(
                    id,
                    procedure["title"].ToString()!,
                    revision,
                    1,
                    procedure.TryGetValue("firstStep", out var step) ? step?.ToString() ?? "" : "",
                    null));
            }
        }

        if (row["service"].As<string?>() is { } affected)
        {
            nodes.Add(new EvidenceNode("Service", affected, "Affected service"));
            edges.Add(new EvidenceEdge($"Alert {alertId}", "AFFECTS", affected));
        }

        if (row["merchant"].As<string?>() is { } merchant)
        {
            nodes.Add(new EvidenceNode("User", merchant, "Merchant concerned"));
            edges.Add(new EvidenceEdge($"Alert {alertId}", "CONCERNS", merchant));
        }

        if (row["sourceIp"].As<string?>() is { } ip)
        {
            nodes.Add(new EvidenceNode("SourceIp", ip, "Originating address"));
            edges.Add(new EvidenceEdge($"Alert {alertId}", "ATTRIBUTED_TO", ip));
        }

        return new EvidenceEnvelope(
            question, RetrievalRoute.Cypher, nodes, edges, sources, [cypher], null,
            "Matched the question to the alert explanation query, then walked the typed path from the " +
            "alert to its events, the threat it indicates, the weakness that threat exploits, the " +
            "controls that mitigate it and the procedures that answer it.");
    }

    private async Task<EvidenceEnvelope> ControlsForThreatAsync(string question, CancellationToken ct)
    {
        const string cypher = """
            MATCH (c:Control)-[:MITIGATES]->(t:Threat)
            WHERE $term IS NULL
               OR toLower(t.name) CONTAINS $term
               OR toLower(t.description) CONTAINS $term
               OR toLower(coalesce(t.referenceId, '')) CONTAINS $term
            OPTIONAL MATCH (p:Procedure)-[:DESCRIBES]->(c)
            RETURN t.id AS threatId, t.name AS threat, t.referenceId AS reference,
                   collect(DISTINCT {id: c.id, name: c.name, implemented: c.implemented, note: c.note}) AS controls,
                   collect(DISTINCT {id: p.id, title: p.title, revision: p.revision}) AS procedures
            ORDER BY threat
            """;

        var term = ExtractSubject(question);

        await using var session = driver.AsyncSession();
        var rows = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(cypher, new { term });
            return await cursor.ToListAsync();
        });

        if (rows.Count == 0) return EvidenceEnvelope.Nothing(question, "No threat matched that description.");

        var nodes = new List<EvidenceNode>();
        var edges = new List<EvidenceEdge>();
        var sources = new List<SourceDocument>();

        foreach (var row in rows)
        {
            var threat = row["threat"].As<string>();
            nodes.Add(new EvidenceNode("Threat", row["threatId"].As<string>(),
                row["reference"].As<string?>() is { } r ? $"{threat} ({r})" : threat));

            foreach (var control in row["controls"].As<List<IDictionary<string, object>>>())
            {
                if (control["id"] is null) continue;

                var implemented = control["implemented"] is bool b && b;
                nodes.Add(new EvidenceNode("Control", control["id"].ToString()!,
                    $"{control["name"]}{(implemented ? " (implemented)" : " (NOT implemented)")}. {control["note"]}"));
                edges.Add(new EvidenceEdge(control["id"].ToString()!, "MITIGATES", threat));
            }

            foreach (var procedure in row["procedures"].As<List<IDictionary<string, object>>>())
            {
                if (procedure["id"] is null) continue;
                sources.Add(new SourceDocument(
                    procedure["id"].ToString()!, procedure["title"].ToString()!,
                    Convert.ToInt32(procedure["revision"]), 0, "", null));
            }
        }

        return new EvidenceEnvelope(
            question, RetrievalRoute.Cypher, nodes, edges, sources, [cypher], null,
            "Matched the question to the control lookup, then returned every control that mitigates the " +
            "matching threat, marked by whether this system actually implements it.");
    }

    private async Task<EvidenceEnvelope> DependencyImpactAsync(string question, CancellationToken ct)
    {
        const string cypher = """
            MATCH (target:Service)
            WHERE toLower(target.name) CONTAINS $service
            OPTIONAL MATCH (dependant:Service)-[:DEPENDS_ON*1..3]->(target)
            OPTIONAL MATCH (target)-[:OWNS]->(a:Asset)
            OPTIONAL MATCH (target)-[:EXPOSES]->(e:Endpoint)
            RETURN target.name   AS service,
                   target.status AS status,
                   collect(DISTINCT dependant.name) AS dependants,
                   collect(DISTINCT a.name)         AS assets,
                   count(DISTINCT e)                AS endpoints
            """;

        var service = ExtractService(question);
        if (service is null)
        {
            return EvidenceEnvelope.Nothing(question,
                "The question asks about impact but names no service the graph knows.");
        }

        await using var session = driver.AsyncSession();
        var rows = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(cypher, new { service });
            return await cursor.ToListAsync();
        });

        if (rows.Count == 0) return EvidenceEnvelope.Nothing(question, $"No service matching '{service}'.");

        var row = rows[0];
        var name = row["service"].As<string>();
        var dependants = row["dependants"].As<List<object>>().Where(d => d is not null).Select(d => d.ToString()!).ToList();

        var nodes = new List<EvidenceNode>
        {
            new("Service", name, $"Status {row["status"].As<string?>() ?? "unknown"}, {row["endpoints"].As<int>()} endpoints")
        };
        var edges = new List<EvidenceEdge>();

        foreach (var dependant in dependants)
        {
            nodes.Add(new EvidenceNode("Service", dependant, "Depends on the affected service"));
            edges.Add(new EvidenceEdge(dependant, "DEPENDS_ON", name));
        }

        foreach (var asset in row["assets"].As<List<object>>().Where(a => a is not null))
        {
            nodes.Add(new EvidenceNode("Asset", asset.ToString()!, "Owned by the affected service"));
            edges.Add(new EvidenceEdge(name, "OWNS", asset.ToString()!));
        }

        return new EvidenceEnvelope(
            question, RetrievalRoute.Cypher, nodes, edges, [], [cypher], null,
            "Matched the question to the dependency impact query and walked DEPENDS_ON edges inbound to " +
            "the named service, to a depth of three, which is what a recursive join would have to express " +
            "in SQL.");
    }

    private async Task<EvidenceEnvelope> UsersWithSevereAlertsAsync(string question, CancellationToken ct)
    {
        const string cypher = """
            MATCH (a:Alert)-[:CONCERNS]->(u:User)
            WHERE a.severity IN ['HIGH', 'CRITICAL']
            OPTIONAL MATCH (u)-[:HAS_ROLE]->(role:Role)
            RETURN u.id AS merchant,
                   head(collect(DISTINCT role.name)) AS role,
                   collect(DISTINCT {rule: a.ruleId, severity: a.severity, description: a.description}) AS alerts
            """;

        await using var session = driver.AsyncSession();
        var rows = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(cypher);
            return await cursor.ToListAsync();
        });

        if (rows.Count == 0)
        {
            return EvidenceEnvelope.Nothing(question, "No merchant is currently linked to a high or critical alert.");
        }

        var nodes = new List<EvidenceNode>();
        var edges = new List<EvidenceEdge>();

        foreach (var row in rows)
        {
            var merchant = row["merchant"].As<string>();
            nodes.Add(new EvidenceNode("User", merchant, $"Role: {row["role"].As<string?>() ?? "unknown"}"));

            foreach (var alert in row["alerts"].As<List<IDictionary<string, object>>>())
            {
                nodes.Add(new EvidenceNode("Alert", alert["rule"].ToString()!,
                    $"{alert["severity"]}: {alert["description"]}"));
                edges.Add(new EvidenceEdge(alert["rule"].ToString()!, "CONCERNS", merchant));
            }
        }

        return new EvidenceEnvelope(
            question, RetrievalRoute.Cypher, nodes, edges, [], [cypher], null,
            "Matched the question to the severe alert lookup and returned every merchant linked to a HIGH " +
            "or CRITICAL alert, with the role they hold.");
    }

    private async Task<EvidenceEnvelope> ThreatsForServiceAsync(string question, CancellationToken ct)
    {
        const string cypher = """
            MATCH (t:Threat)-[:TARGETS]->(s:Service)
            WHERE $service IS NULL OR toLower(s.name) CONTAINS $service
            OPTIONAL MATCH (c:Control)-[:MITIGATES]->(t)
            OPTIONAL MATCH (r:Rule)-[:DETECTS]->(t)
            RETURN s.name AS service, t.id AS threatId, t.name AS threat,
                   t.referenceId AS reference,
                   collect(DISTINCT c.id) AS controls,
                   collect(DISTINCT r.id) AS detectedBy
            """;

        var service = ExtractService(question);

        await using var session = driver.AsyncSession();
        var rows = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(cypher, new { service });
            return await cursor.ToListAsync();
        });

        if (rows.Count == 0) return EvidenceEnvelope.Nothing(question, "No threat in the graph targets that service.");

        var nodes = new List<EvidenceNode>();
        var edges = new List<EvidenceEdge>();

        foreach (var row in rows)
        {
            var threat = row["threat"].As<string>();
            var target = row["service"].As<string>();

            nodes.Add(new EvidenceNode("Threat", row["threatId"].As<string>(),
                row["reference"].As<string?>() is { } r ? $"{threat} ({r})" : threat));
            edges.Add(new EvidenceEdge(threat, "TARGETS", target));

            foreach (var rule in row["detectedBy"].As<List<object>>().Where(x => x is not null))
            {
                edges.Add(new EvidenceEdge(rule.ToString()!, "DETECTS", threat));
            }

            foreach (var control in row["controls"].As<List<object>>().Where(x => x is not null))
            {
                edges.Add(new EvidenceEdge(control.ToString()!, "MITIGATES", threat));
            }
        }

        return new EvidenceEnvelope(
            question, RetrievalRoute.Cypher, nodes, edges, [], [cypher], null,
            "Matched the question to the threat lookup and returned every threat targeting the named " +
            "service, with the rules that detect it and the controls that mitigate it.");
    }

    // =======================================================================
    // Route 2: Vector
    // =======================================================================
    private async Task<EvidenceEnvelope> VectorAsync(string question, CancellationToken ct)
    {
        var vector = await embeddings.EmbedAsync(question, ct);

        if (vector is null)
        {
            return EvidenceEnvelope.Nothing(question,
                "Vector retrieval was unavailable: the embedding model could not be reached.");
        }

        var (chunks, cypher) = await SearchChunksAsync(vector, ct);

        if (chunks.Count == 0)
        {
            return EvidenceEnvelope.Nothing(question, "No procedure passage was close enough to the question.");
        }

        var nodes = chunks
            .Select(c => new EvidenceNode("Chunk", $"{c.ProcedureId}-{c.Ordinal}", c.Text, c.Similarity))
            .ToList();

        return new EvidenceEnvelope(
            question, RetrievalRoute.Vector, nodes, [], chunks, [cypher],
            chunks.Max(c => c.Similarity),
            "Embedded the question with the same model used for the procedure text, then returned the " +
            "nearest passages by cosine similarity. No graph edges were walked, so the passages are " +
            "relevant guidance but carry no link to what this system actually observed.");
    }

    // =======================================================================
    // Route 3: Hybrid
    // =======================================================================
    private async Task<EvidenceEnvelope> HybridAsync(string question, CancellationToken ct)
    {
        var vector = await embeddings.EmbedAsync(question, ct);

        if (vector is null)
        {
            return EvidenceEnvelope.Nothing(question,
                "Hybrid retrieval was unavailable: the embedding model could not be reached.");
        }

        var (chunks, searchCypher) = await SearchChunksAsync(vector, ct);

        if (chunks.Count == 0)
        {
            return EvidenceEnvelope.Nothing(question, "No procedure passage anchored the question.");
        }

        // The traversal. Similarity found a passage; the typed edges turn it from
        // general guidance into this system's situation.
        // sourceChunks is carried through deliberately.
        //
        // Grouping by threat alone loses which chunk produced it, and an earlier
        // version then emitted a MENTIONS edge from every retrieved chunk to
        // every threat found. That put relationships in the evidence envelope
        // that do not exist in the graph: the object-enumeration procedure was
        // shown as mentioning account takeover. For a system whose claim is that
        // its answers cite real evidence, inventing an edge is the worst
        // available bug, and it was invisible because the answer still read
        // plausibly.
        const string expansion = """
            UNWIND $chunkIds AS cid
            MATCH (c:Chunk {id: cid})-[:MENTIONS]->(t:Threat)
            WITH t, collect(DISTINCT c.id) AS sourceChunks
            OPTIONAL MATCH (ctrl:Control)-[:MITIGATES]->(t)
            OPTIONAL MATCH (a:Alert)-[:INDICATES]->(t)
            OPTIONAL MATCH (e:Event)-[:CREATED_ALERT]->(a)
            OPTIONAL MATCH (a)-[:AFFECTS]->(s:Service)
            RETURN t.id AS threatId, t.name AS threat, t.referenceId AS reference,
                   sourceChunks,
                   collect(DISTINCT {id: ctrl.id, name: ctrl.name, implemented: ctrl.implemented}) AS controls,
                   collect(DISTINCT {id: a.id, rule: a.ruleId, severity: a.severity,
                                     description: a.description, service: s.name}) AS alerts,
                   count(DISTINCT e) AS eventCount
            """;

        await using var session = driver.AsyncSession();
        var rows = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(expansion,
                new { chunkIds = chunks.Select(c => $"{c.ProcedureId}-{c.Ordinal}").ToList() });
            return await cursor.ToListAsync();
        });

        var nodes = chunks
            .Select(c => new EvidenceNode("Chunk", $"{c.ProcedureId}-{c.Ordinal}", c.Text, c.Similarity))
            .ToList();
        var edges = new List<EvidenceEdge>();

        foreach (var row in rows)
        {
            var threat = row["threat"].As<string>();
            var threatId = row["threatId"].As<string>();
            var reference = row["reference"].As<string?>();

            nodes.Add(new EvidenceNode("Threat", threatId,
                reference is null ? threat : $"{threat} ({reference})"));

            // Only the chunks that actually carry the edge.
            foreach (var chunkId in row["sourceChunks"].As<List<object>>().Where(c => c is not null))
            {
                edges.Add(new EvidenceEdge(chunkId.ToString()!, "MENTIONS", threat));
            }

            foreach (var control in row["controls"].As<List<IDictionary<string, object>>>())
            {
                if (control["id"] is null) continue;
                var implemented = control["implemented"] is bool b && b;
                nodes.Add(new EvidenceNode("Control", control["id"].ToString()!,
                    $"{control["name"]}{(implemented ? " (implemented)" : " (NOT implemented)")}"));
                edges.Add(new EvidenceEdge(control["id"].ToString()!, "MITIGATES", threat));
            }

            foreach (var alert in row["alerts"].As<List<IDictionary<string, object>>>())
            {
                if (alert["id"] is null) continue;
                nodes.Add(new EvidenceNode("Alert", alert["rule"]?.ToString() ?? alert["id"].ToString()!,
                    $"{alert["severity"]}: {alert["description"]}"));
                edges.Add(new EvidenceEdge(alert["rule"]?.ToString() ?? "Alert", "INDICATES", threat));

                if (alert["service"] is { } svc && svc is not null)
                {
                    edges.Add(new EvidenceEdge(alert["rule"]?.ToString() ?? "Alert", "AFFECTS", svc.ToString()!));
                }
            }
        }

        return new EvidenceEnvelope(
            question, RetrievalRoute.Hybrid, nodes, edges, chunks, [searchCypher, expansion],
            chunks.Max(c => c.Similarity),
            "Embedded the question and found the nearest procedure passages by cosine similarity, then " +
            "walked from each passage to the threat it mentions, the controls that mitigate that threat, " +
            "and any alert this system has actually raised for it. Similarity chose where to start; the " +
            "typed edges supplied what is specific to this system.");
    }

    // =======================================================================
    // Shared
    // =======================================================================
    private async Task<(List<SourceDocument> Chunks, string Cypher)> SearchChunksAsync(
        float[] vector, CancellationToken ct)
    {
        const string cypher = """
            CALL db.index.vector.queryNodes('procedure_chunks', $k, $embedding)
            YIELD node, score
            MATCH (p:Procedure)-[:HAS_CHUNK]->(node)
            RETURN p.id AS procedureId, p.title AS title, p.revision AS revision,
                   node.ordinal AS ordinal, node.text AS text, score AS similarity
            ORDER BY similarity DESC
            """;

        try
        {
            await using var session = driver.AsyncSession();
            return (await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(cypher, new { k = _options.VectorTopK, embedding = vector });
                return await cursor.ToListAsync(r => new SourceDocument(
                    r["procedureId"].As<string>(),
                    r["title"].As<string>(),
                    r["revision"].As<int>(),
                    r["ordinal"].As<int>(),
                    r["text"].As<string>(),
                    r["similarity"].As<double>()));
            }), cypher);
        }
        catch (Exception ex)
        {
            // Most often the index does not exist yet, because the knowledge
            // load has not run. Reported rather than thrown: the other routes
            // still work without it.
            logger.LogWarning("Vector search failed ({Reason}); has the knowledge load run?", ex.Message);
            return ([], cypher);
        }
    }

    private static bool Mentions(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pulls "R5" out of a question that names a rule.</summary>
    private static string? ExtractRuleId(string question)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            question, @"\bR([1-9])\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success ? $"R{match.Groups[1].Value}" : null;
    }

    /// <summary>
    /// Matches a question against the service names the graph actually holds,
    /// rather than guessing from the wording. A name that is not in the graph
    /// cannot be answered about, and saying so is better than searching for it.
    /// </summary>
    private string? ExtractService(string question)
    {
        var q = question.ToLowerInvariant();

        foreach (var known in new[]
                 { "rate-service", "invoice-service", "merchant-service", "settlement-service",
                   "soc-service", "graph-service", "gateway" })
        {
            if (q.Contains(known)) return known;

            // "the rate service" as well as "rate-service".
            var spaced = known.Replace("-", " ");
            if (q.Contains(spaced)) return known;
        }

        return null;
    }

    /// <summary>The topic a control question is about, lowercased for CONTAINS.</summary>
    private static string? ExtractSubject(string question)
    {
        var q = question.ToLowerInvariant();

        foreach (var topic in new[]
                 { "spraying", "credential", "takeover", "payout", "enumeration", "object",
                   "exhaustion", "resource", "price", "rate", "availability", "dependency" })
        {
            if (q.Contains(topic)) return topic;
        }

        return null;
    }
}
