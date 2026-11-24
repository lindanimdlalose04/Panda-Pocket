namespace PandaPocket.Services.Graph.Rag.Evaluation;

/// <summary>
/// One question with the evidence a correct retrieval must find.
/// </summary>
/// <param name="MustRetrieve">
/// Identifiers that have to appear in the envelope. Written before any route was
/// run, from what the graph is known to contain, so the scoring is against an
/// expectation rather than against whatever the system happened to return.
/// </param>
/// <param name="ExpectedBestRoute">
/// Which route this question was written to favour. Recorded as a prediction, so
/// the results can disagree with it. A harness that cannot embarrass its author
/// is not measuring anything.
/// </param>
public sealed record GoldQuestion(
    string Id,
    string Question,
    string[] MustRetrieve,
    string ExpectedBestRoute,
    string Rationale);

/// <summary>
/// The question set.
///
/// Deliberately mixed. Four questions name something the graph holds and should
/// favour Cypher; three are phrased the way an analyst actually speaks, using no
/// identifier at all, and should favour similarity; one needs both, which is the
/// case the hybrid route exists for.
///
/// Small, because every expected answer was written by hand against the graph's
/// real contents. A larger set generated from the graph itself would measure
/// whether the retriever agrees with the generator that produced the questions,
/// which is not the same as measuring whether it is right.
/// </summary>
public static class GoldSet
{
    public static readonly GoldQuestion[] Questions =
    [
        new("G1",
            "Why was the critical alert raised and what should be done?",
            ["R5", "T-04", "C-04", "P-03", "merchant-service"],
            RetrievalRoute.Cypher,
            "Names a severity the graph indexes. There is one correct alert and an exact path to it."),

        new("G2",
            "What controls mitigate credential attacks?",
            ["T-01", "C-01"],
            RetrievalRoute.Cypher,
            "Asks for a specific relationship, MITIGATES, over a named threat class."),

        new("G3",
            "What is the impact if the rate service fails?",
            ["rate-service", "invoice-service", "gateway"],
            RetrievalRoute.Cypher,
            "A path query over DEPENDS_ON. Similarity cannot compute reachability."),

        new("G4",
            "Which merchants are linked to high severity alerts?",
            ["11111111-1111-1111-1111-111111111111"],
            RetrievalRoute.Cypher,
            "A filter and a join. Exactly what a query language is for."),

        new("G5",
            "What should I check first after repeated login failures?",
            ["P-01"],
            RetrievalRoute.Vector,
            "Names no entity in the graph. The answer is a paragraph of written guidance, " +
            "and the matching paragraph shares almost no words with the question."),

        new("G6",
            "How do I tell whether someone actually got in?",
            ["P-01"],
            RetrievalRoute.Vector,
            "Phrased the way a person speaks. No keyword overlap with the procedure at all."),

        new("G7",
            "Someone changed where our money gets sent. What now?",
            ["P-03", "T-04", "C-04"],
            RetrievalRoute.Hybrid,
            "Conversational, so Cypher cannot match it, but the useful answer needs this " +
            "system's specifics and not only the general guidance. This is the case hybrid exists for."),

        new("G8",
            "What threats target the merchant service?",
            ["T-04", "merchant-service"],
            RetrievalRoute.Cypher,
            "Names a service and asks for an edge type the graph holds directly.")
    ];
}
