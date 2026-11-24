using Neo4j.Driver;
using PandaPocket.Services.Graph.Graph;
using PandaPocket.Services.Graph.Rag;
using PandaPocket.Services.Graph.Rag.Evaluation;

namespace PandaPocket.Services.Graph.Endpoints;

public static class GraphEndpoints
{
    public static IEndpointRouteBuilder MapGraphEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/graph").WithTags("Graph");

        // -------------------------------------------------------------------
        // Load the application subgraph from the running system
        // -------------------------------------------------------------------
        group.MapPost("/load/topology", async (TopologyLoader loader, CancellationToken ct) =>
        {
            var result = await loader.LoadAsync(ct);
            return Results.Ok(result);
        })
        .WithName("LoadTopology")
        .WithSummary("Rebuild the application subgraph from Consul, the gateway routing table and service health")
        .Produces<TopologyResult>();

        // -------------------------------------------------------------------
        // Load the SOC subgraph: events, alerts, rules, incidents
        // -------------------------------------------------------------------
        group.MapPost("/load/soc", async (SocLoader loader, int? maxEvents, CancellationToken ct) =>
        {
            // Bounded, because the event store grows without limit and a load
            // that pulls everything gets slower every day it runs.
            var limit = maxEvents is null or <= 0 or > 2000 ? 500 : maxEvents.Value;
            return Results.Ok(await loader.LoadAsync(limit, ct));
        })
        .WithName("LoadSoc")
        .WithSummary("Project the SOC service's events, alerts and rules into the graph")
        .Produces<SocLoadResult>();

        // -------------------------------------------------------------------
        // Load the curated security knowledge
        // -------------------------------------------------------------------
        group.MapPost("/load/knowledge", async (KnowledgeLoader loader, CancellationToken ct) =>
            Results.Ok(await loader.LoadAsync(ct)))
        .WithName("LoadKnowledge")
        .WithSummary("Seed threats, weaknesses, controls and response procedures, and join them to the rules")
        .Produces<KnowledgeLoadResult>();

        // -------------------------------------------------------------------
        // Everything, in dependency order
        // -------------------------------------------------------------------
        group.MapPost("/load/all", async (
            TopologyLoader topology, SocLoader soc, KnowledgeLoader knowledge, CancellationToken ct) =>
        {
            // Topology first. Events attach to services and endpoints, so
            // loading them the other way round leaves the attachments to be
            // made by MERGE against nodes that carry no properties yet.
            var t = await topology.LoadAsync(ct);
            var s = await soc.LoadAsync(500, ct);

            // Knowledge last. It joins alerts to threats through the rules, so
            // the rules and alerts have to exist before the join can be made.
            var k = await knowledge.LoadAsync(ct);

            return Results.Ok(new { Topology = t, Soc = s, Knowledge = k });
        })
        .WithName("LoadAll")
        .WithSummary("Rebuild the whole graph, topology first");

        // -------------------------------------------------------------------
        // Retrieval only. No prose: this returns the evidence an answer would
        // be built from, which is what makes the answer inspectable.
        // -------------------------------------------------------------------
        // -------------------------------------------------------------------
        // The full pipeline: retrieve, then generate. The answer and the
        // evidence it was built from are returned together, because an answer
        // whose evidence cannot be inspected is an assertion.
        // -------------------------------------------------------------------
        // -------------------------------------------------------------------
        // Evaluation: all three routes over the gold question set.
        //
        // Retrieval and answer quality are scored separately, because they are
        // separate failures. A route can find exactly the right evidence and a
        // generator still write poorly from it.
        // -------------------------------------------------------------------
        group.MapPost("/evaluate", async (
            EvaluationHarness harness, bool? includeModel, CancellationToken ct) =>
        {
            // Off by default: a model answer costs around two minutes here, and
            // the retrieval comparison is the part that runs in seconds.
            return Results.Ok(await harness.RunAsync(includeModel ?? false, ct));
        })
        .WithName("Evaluate")
        .WithSummary("Compare the three retrieval routes on a fixed question set, with citation integrity")
        .Produces<EvaluationReport>();

        group.MapPost("/ask", async (
            AskRequest request, AnswerService answers, CancellationToken ct) =>
        {
            var answer = await answers.AskAsync(request.Question, request.Route, request.UseModel, ct);
            return Results.Ok(answer);
        })
        .WithName("Ask")
        .WithSummary("Ask a security question and get an answer with the graph evidence behind it")
        .Produces<Answer>();

        group.MapPost("/retrieve", async (
            RetrieveRequest request, Retriever retriever, CancellationToken ct) =>
        {
            var envelope = await retriever.RetrieveAsync(request.Question, request.Route, ct);
            return Results.Ok(envelope);
        })
        .WithName("Retrieve")
        .WithSummary("Gather graph evidence for a question, without generating an answer")
        .Produces<EvidenceEnvelope>();

        // -------------------------------------------------------------------
        // What the graph currently holds
        // -------------------------------------------------------------------
        group.MapGet("/stats", async (IDriver driver, CancellationToken ct) =>
        {
            await using var session = driver.AsyncSession();

            var nodes = await session.ExecuteReadAsync(async tx =>
            {
                // labels() returns a list because a node may carry several, and
                // User:Merchant deliberately does. Unwinding counts it under
                // both, which is what makes the dual label useful rather than
                // confusing.
                var cursor = await tx.RunAsync("""
                    MATCH (n)
                    UNWIND labels(n) AS label
                    RETURN label, count(*) AS count
                    ORDER BY count DESC
                    """);

                return await cursor.ToListAsync(r => new LabelCount(r["label"].As<string>(), r["count"].As<int>()));
            });

            var relationships = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync("""
                    MATCH ()-[r]->()
                    RETURN type(r) AS type, count(*) AS count
                    ORDER BY count DESC
                    """);

                return await cursor.ToListAsync(r => new TypeCount(r["type"].As<string>(), r["count"].As<int>()));
            });

            return Results.Ok(new GraphStats(
                nodes.Sum(n => n.Count),
                relationships.Sum(r => r.Count),
                nodes,
                relationships));
        })
        .WithName("GraphStats")
        .WithSummary("Node and relationship counts by label and type")
        .Produces<GraphStats>();

        // -------------------------------------------------------------------
        // The live topology, as the graph now holds it
        // -------------------------------------------------------------------
        group.MapGet("/topology", async (IDriver driver, CancellationToken ct) =>
        {
            await using var session = driver.AsyncSession();

            var services = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync("""
                    MATCH (s:Service)
                    OPTIONAL MATCH (s)-[:EXPOSES]->(e:Endpoint)
                    OPTIONAL MATCH (s)-[:OWNS]->(a:Asset)
                    OPTIONAL MATCH (s)-[:DEPENDS_ON]->(d:Service)
                    RETURN s.name                        AS name,
                           s.status                      AS status,
                           s.instances                   AS instances,
                           count(DISTINCT e)             AS endpoints,
                           collect(DISTINCT a.name)      AS assets,
                           collect(DISTINCT d.name)      AS dependsOn
                    ORDER BY name
                    """);

                return await cursor.ToListAsync(r => new ServiceView(
                    r["name"].As<string>(),
                    r["status"].As<string?>(),
                    r["instances"].As<int?>(),
                    r["endpoints"].As<int>(),
                    r["assets"].As<List<object>>().Where(a => a is not null).Select(a => a.ToString()!).ToList(),
                    r["dependsOn"].As<List<object>>().Where(d => d is not null).Select(d => d.ToString()!).ToList()));
            });

            return Results.Ok(services);
        })
        .WithName("GetTopology")
        .WithSummary("Every service with its endpoints, assets and declared dependencies")
        .Produces<List<ServiceView>>();

        return app;
    }
}

public sealed record LabelCount(string Label, int Count);
public sealed record TypeCount(string Type, int Count);

public sealed record GraphStats(
    int TotalNodes, int TotalRelationships,
    IReadOnlyList<LabelCount> NodesByLabel, IReadOnlyList<TypeCount> RelationshipsByType);

public sealed record ServiceView(
    string Name, string? Status, int? Instances, int Endpoints,
    IReadOnlyList<string> Assets, IReadOnlyList<string> DependsOn);

/// <summary>Route may be null, which lets the retriever choose.</summary>
public sealed record RetrieveRequest(string Question, string? Route);

/// <summary>Route and UseModel are both optional; null lets the service decide.</summary>
public sealed record AskRequest(string Question, string? Route, bool? UseModel);
