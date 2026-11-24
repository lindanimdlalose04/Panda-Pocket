using System.Diagnostics;
using Neo4j.Driver;

namespace PandaPocket.Services.Graph.Rag.Evaluation;

public sealed record RouteScore(
    string Route,
    double EvidenceRecall,
    double Precision,
    int Retrieved,
    int Expected,
    int Matched,
    string[] Missed,
    int FabricatedCitations,
    long LatencyMs);

public sealed record QuestionResult(
    string Id, string Question, string ExpectedBestRoute, string ActualBestRoute,
    bool PredictionHeld, IReadOnlyList<RouteScore> Routes);

public sealed record GeneratorScore(
    string Generator, long LatencyMs, int UnsupportedIdentifiers,
    bool ProducedAllSections, string[] MissingSections);

public sealed record EvaluationReport(
    DateTime RunAt,
    IReadOnlyList<QuestionResult> Questions,
    IReadOnlyDictionary<string, double> MeanRecallByRoute,
    IReadOnlyDictionary<string, double> MeanLatencyByRoute,
    int TotalFabricatedCitations,
    IReadOnlyList<GeneratorScore> Generators,
    string Summary);

/// <summary>
/// Runs every gold question through all three retrieval routes and scores them.
///
/// Lecture 9 asks for the three configurations to be compared on one question
/// set, and for retrieval and answer quality to be judged separately. They are
/// separate failures: a route can retrieve exactly the right evidence and a
/// generator still write a poor answer from it, and a fluent answer can rest on
/// evidence that was never retrieved. Scoring them together would hide both.
///
/// Four measures, each chosen because it can actually be computed rather than
/// estimated:
///
///   Evidence recall      did the route find what a correct answer needs
///   Precision            how much of what it returned was relevant
///   Fabricated citations how many cited ids do not exist in the graph
///   Latency              wall clock
///
/// The fabricated citation count is the one that matters most. It is zero when
/// the system is honest, and the only measure here that detects an answer which
/// reads perfectly and cites something that was never there.
/// </summary>
public sealed class EvaluationHarness(
    Retriever retriever,
    AnswerService answers,
    IDriver driver,
    ILogger<EvaluationHarness> logger)
{
    private static readonly string[] Routes =
        [RetrievalRoute.Cypher, RetrievalRoute.Vector, RetrievalRoute.Hybrid];

    public async Task<EvaluationReport> RunAsync(bool includeModel, CancellationToken ct)
    {
        // A discarded pass before anything is timed.
        //
        // Without it the first route measured pays for everything cold: the
        // embedding model loading from disk, Neo4j planning each query for the
        // first time, and the driver opening its pool. Routes were measured in a
        // fixed order, so that cost landed entirely on Cypher and produced the
        // impossible result that the hybrid route, which does a vector search
        // and then traverses, was four times faster than the vector search
        // alone. The ordering was being measured, not the routes.
        await WarmAsync(ct);

        var results = new List<QuestionResult>();
        var fabricatedTotal = 0;

        foreach (var gold in GoldSet.Questions)
        {
            var scores = new List<RouteScore>();

            foreach (var route in Routes)
            {
                var stopwatch = Stopwatch.StartNew();
                var envelope = await retriever.RetrieveAsync(gold.Question, route, ct);
                stopwatch.Stop();

                var retrieved = IdentifiersIn(envelope);
                var matched = gold.MustRetrieve.Where(e => retrieved.Any(r => Matches(r, e))).ToArray();
                var missed = gold.MustRetrieve.Except(matched).ToArray();

                var fabricated = await CountFabricatedAsync(envelope, ct);
                fabricatedTotal += fabricated;

                scores.Add(new RouteScore(
                    Route: route,
                    EvidenceRecall: gold.MustRetrieve.Length == 0 ? 1 : (double)matched.Length / gold.MustRetrieve.Length,
                    Precision: retrieved.Count == 0 ? 0 : (double)matched.Length / retrieved.Count,
                    Retrieved: retrieved.Count,
                    Expected: gold.MustRetrieve.Length,
                    Matched: matched.Length,
                    Missed: missed,
                    FabricatedCitations: fabricated,
                    LatencyMs: stopwatch.ElapsedMilliseconds));
            }

            // Recall first, latency only to break a tie. A faster route that
            // found less is not better.
            var best = scores
                .OrderByDescending(s => s.EvidenceRecall)
                .ThenBy(s => s.LatencyMs)
                .First();

            results.Add(new QuestionResult(
                gold.Id, gold.Question, gold.ExpectedBestRoute, best.Route,
                best.Route == gold.ExpectedBestRoute, scores));
        }

        var generators = includeModel
            ? await ScoreGeneratorsAsync(ct)
            : await ScoreTemplateOnlyAsync(ct);

        var meanRecall = Routes.ToDictionary(
            r => r,
            r => Math.Round(results.Average(q => q.Routes.First(s => s.Route == r).EvidenceRecall), 3));

        var meanLatency = Routes.ToDictionary(
            r => r,
            r => Math.Round(results.Average(q => (double)q.Routes.First(s => s.Route == r).LatencyMs), 1));

        var held = results.Count(r => r.PredictionHeld);

        var summary =
            $"{results.Count} questions across three routes. " +
            $"Mean evidence recall: cypher {meanRecall[RetrievalRoute.Cypher]:P0}, " +
            $"vector {meanRecall[RetrievalRoute.Vector]:P0}, " +
            $"hybrid {meanRecall[RetrievalRoute.Hybrid]:P0}. " +
            $"The predicted best route held for {held} of {results.Count}. " +
            $"Fabricated citations: {fabricatedTotal}.";

        logger.LogInformation("Evaluation complete. {Summary}", summary);

        return new EvaluationReport(
            DateTime.UtcNow, results, meanRecall, meanLatency, fabricatedTotal, generators, summary);
    }

    /// <summary>
    /// Runs one question through every route and throws the results away, so
    /// that what is measured afterwards is the route and not the cold start.
    /// </summary>
    private async Task WarmAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        foreach (var route in Routes)
        {
            try
            {
                await retriever.RetrieveAsync(GoldSet.Questions[0].Question, route, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Warm-up on route {Route} failed ({Reason})", route, ex.Message);
            }
        }

        stopwatch.Stop();
        logger.LogInformation("Warm-up pass took {Ms} ms and is excluded from the results", stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Every identifier the envelope cites: node ids, chunk ids and the
    /// procedures the sources came from.
    /// </summary>
    private static HashSet<string> IdentifiersIn(EvidenceEnvelope envelope)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in envelope.Nodes) ids.Add(node.Id);
        foreach (var source in envelope.Sources) ids.Add(source.ProcedureId);

        return ids;
    }

    /// <summary>
    /// A chunk id of "P-01-2" satisfies an expectation of "P-01", because the
    /// paragraph is part of that procedure. Anything else must match exactly.
    /// </summary>
    private static bool Matches(string retrieved, string expected) =>
        retrieved.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
        retrieved.StartsWith(expected + "-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Counts cited identifiers that do not exist in the graph.
    ///
    /// This is the integrity check. An id in an envelope is a claim that the
    /// graph holds that node, and a claim about something absent is the one
    /// failure that reading the answer cannot catch, because fabricated
    /// citations look exactly like real ones.
    /// </summary>
    private async Task<int> CountFabricatedAsync(EvidenceEnvelope envelope, CancellationToken ct)
    {
        var ids = IdentifiersIn(envelope).ToList();
        if (ids.Count == 0) return 0;

        await using var session = driver.AsyncSession();

        // Services key on name and source addresses on address, so all three
        // are checked rather than assuming every node carries an id.
        var cursor = await session.RunAsync("""
            UNWIND $ids AS wanted
            OPTIONAL MATCH (n)
              WHERE n.id = wanted OR n.name = wanted OR n.address = wanted
            WITH wanted, count(n) AS found
            WHERE found = 0
            RETURN collect(wanted) AS missing
            """,
            new { ids });

        var records = await cursor.ToListAsync();
        if (records.Count == 0) return 0;

        var missing = records[0]["missing"].As<List<object>>();

        if (missing.Count > 0)
        {
            logger.LogWarning(
                "Envelope cited {Count} identifier(s) absent from the graph: {Ids}",
                missing.Count, string.Join(", ", missing.Select(m => m?.ToString())));
        }

        return missing.Count;
    }

    // -----------------------------------------------------------------------
    // Answer quality, judged separately from retrieval
    // -----------------------------------------------------------------------
    private async Task<List<GeneratorScore>> ScoreTemplateOnlyAsync(CancellationToken ct) =>
        [await ScoreOneAsync(useModel: false, ct)];

    private async Task<List<GeneratorScore>> ScoreGeneratorsAsync(CancellationToken ct) =>
        [await ScoreOneAsync(useModel: false, ct), await ScoreOneAsync(useModel: true, ct)];

    /// <summary>
    /// Scores a generator on the first gold question.
    ///
    /// One question, because a model answer costs around two minutes on this
    /// hardware and eight of them would make the harness unusable during a
    /// demonstration. The measures are what can be checked rather than judged:
    /// whether every identifier in the prose came from the envelope, and whether
    /// all three sections were produced at all.
    /// </summary>
    private async Task<GeneratorScore> ScoreOneAsync(bool useModel, CancellationToken ct)
    {
        var gold = GoldSet.Questions[0];

        var stopwatch = Stopwatch.StartNew();
        var answer = await answers.AskAsync(gold.Question, RetrievalRoute.Cypher, useModel, ct);
        stopwatch.Stop();

        var supported = IdentifiersIn(answer.Evidence);
        var prose = $"{answer.Observed} {answer.Interpretation} {answer.Response}";

        // Identifier-shaped tokens in the prose: R5, T-04, C-01, CWE-307, P-03.
        // Anything matching that shape and absent from the envelope was not
        // retrieved, so the generator introduced it.
        var cited = System.Text.RegularExpressions.Regex
            .Matches(prose, @"\b(?:[A-Z]{1,4}-\d{1,4}|R[1-9])\b")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unsupported = cited.Count(c => !supported.Any(s => Matches(s, c) || s.Equals(c, StringComparison.OrdinalIgnoreCase)));

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(answer.Observed)) missing.Add("OBSERVED");
        if (string.IsNullOrWhiteSpace(answer.Interpretation)) missing.Add("INTERPRETATION");
        if (string.IsNullOrWhiteSpace(answer.Response)) missing.Add("RESPONSE");

        return new GeneratorScore(
            answer.Generator, stopwatch.ElapsedMilliseconds, unsupported,
            missing.Count == 0, missing.ToArray());
    }
}
