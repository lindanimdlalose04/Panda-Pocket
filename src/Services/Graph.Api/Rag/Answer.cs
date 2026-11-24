namespace PandaPocket.Services.Graph.Rag;

/// <summary>
/// A generated answer, with the evidence it was built from.
///
/// Three sections rather than a paragraph, following lecture 9. Separating what
/// was observed from what it might mean is the difference between an SOC
/// assistant and a chatbot: the events are facts, the threat is a hypothesis,
/// and an analyst acting on the second needs to know which is which. A single
/// fluent paragraph hides that distinction precisely when it matters most.
/// </summary>
public sealed record Answer(
    string Question,

    /// <summary>What the system actually recorded. Facts, with ids.</summary>
    string Observed,

    /// <summary>What it may indicate. Explicitly a hypothesis.</summary>
    string Interpretation,

    /// <summary>What to do, and which written procedure says so.</summary>
    string Response,

    /// <summary>"template" or the model name. Recorded so an answer can be attributed.</summary>
    string Generator,

    EvidenceEnvelope Evidence,

    long GenerationMs)
{
    /// <summary>The three sections as one block, for a terminal or a log.</summary>
    public string AsText() =>
        $"Observed.       {Observed}\n\n" +
        $"Interpretation. {Interpretation}\n\n" +
        $"Response.       {Response}";
}

public interface IAnswerGenerator
{
    string Name { get; }

    Task<(string Observed, string Interpretation, string Response)> GenerateAsync(
        EvidenceEnvelope evidence, CancellationToken ct);
}
