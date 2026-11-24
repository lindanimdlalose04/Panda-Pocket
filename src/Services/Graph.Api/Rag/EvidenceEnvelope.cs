namespace PandaPocket.Services.Graph.Rag;

/// <summary>Which route produced the evidence. Recorded on every answer.</summary>
public static class RetrievalRoute
{
    /// <summary>Question matched to a parameterised Cypher query. Exact paths.</summary>
    public const string Cypher = "cypher";

    /// <summary>Question embedded and matched against procedure text by similarity.</summary>
    public const string Vector = "vector";

    /// <summary>Similarity found the starting point, traversal supplied the context.</summary>
    public const string Hybrid = "hybrid";

    /// <summary>Nothing matched. Saying so is an answer.</summary>
    public const string None = "none";
}

/// <summary>One node that was retrieved, with enough to cite it.</summary>
public sealed record EvidenceNode(string Label, string Id, string Summary, double? Score = null);

/// <summary>One relationship that was walked, as a readable triple.</summary>
public sealed record EvidenceEdge(string From, string Type, string To);

/// <summary>
/// Everything retrieved for one question, assembled before any prose exists.
///
/// This is the specification's evidence requirement, and building it first is
/// what makes template generation legitimate rather than a shortcut: the
/// template fills in facts that are demonstrably in the graph, and the same
/// envelope is what a language model is given when one is enabled. Neither can
/// introduce a claim the graph does not support, because neither is shown
/// anything else.
///
/// The envelope is returned to the caller alongside the answer, not hidden
/// behind it. An answer whose evidence cannot be inspected is an assertion.
/// </summary>
public sealed record EvidenceEnvelope(
    string Question,
    string Route,
    IReadOnlyList<EvidenceNode> Nodes,
    IReadOnlyList<EvidenceEdge> Edges,
    IReadOnlyList<SourceDocument> Sources,
    IReadOnlyList<string> CypherQueries,
    double? TopSimilarity,
    string RetrievalExplanation)
{
    public bool IsEmpty => Nodes.Count == 0 && Sources.Count == 0;

    public static EvidenceEnvelope Nothing(string question, string explanation) =>
        new(question, RetrievalRoute.None, [], [], [], [], null, explanation);
}

/// <summary>
/// A retrieved passage, with what is needed to cite it properly: which document,
/// which revision, and which paragraph. Guidance that cannot be dated cannot be
/// trusted, so the revision travels with the text rather than being looked up
/// afterwards.
/// </summary>
public sealed record SourceDocument(
    string ProcedureId,
    string Title,
    int Revision,
    int Ordinal,
    string Text,
    double? Similarity);
