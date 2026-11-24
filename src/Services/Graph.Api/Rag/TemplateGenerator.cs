using System.Text;

namespace PandaPocket.Services.Graph.Rag;

/// <summary>
/// Builds the answer from the evidence envelope, deterministically.
///
/// This is the primary generator, not a fallback for when something better is
/// unavailable. It cannot hallucinate, because it has no capacity to produce a
/// sentence that is not assembled from a retrieved node; it cannot drop
/// evidence, because every section is driven by what is in the envelope; and it
/// answers in milliseconds rather than half a minute, which matters when the
/// demonstration is being recorded.
///
/// The specification explicitly permits template generation, with one condition:
/// the answer must be clearly generated from graph evidence. Assembling it from
/// the envelope and returning the envelope alongside it meets that condition in
/// a way a reader can check rather than take on trust.
///
/// What it cannot do is phrase an answer the templates did not anticipate. That
/// is the honest limitation, and it is what the model generator is for.
/// </summary>
public sealed class TemplateGenerator : IAnswerGenerator
{
    public string Name => "template";

    public Task<(string Observed, string Interpretation, string Response)> GenerateAsync(
        EvidenceEnvelope evidence, CancellationToken ct)
    {
        if (evidence.IsEmpty)
        {
            return Task.FromResult((
                "Nothing in the graph matches this question.",
                "No interpretation is offered, because there is no evidence to interpret. " +
                "Answering anyway would be invention.",
                evidence.RetrievalExplanation));
        }

        return Task.FromResult((
            Observed(evidence),
            Interpretation(evidence),
            Response(evidence)));
    }

    // -----------------------------------------------------------------------
    // Observed: facts only, with identifiers
    // -----------------------------------------------------------------------
    private static string Observed(EvidenceEnvelope e)
    {
        var text = new StringBuilder();

        var alert = First(e, "Alert");
        var events = First(e, "Event");
        var rule = First(e, "Rule");
        var service = First(e, "Service");
        var merchant = First(e, "User");
        var ip = First(e, "SourceIp");

        if (alert is not null)
        {
            text.Append($"Alert {Short(alert.Id)} ({alert.Summary}). ");

            if (rule is not null) text.Append($"Raised by rule {rule.Id}, {rule.Summary} ");
            var eventNodes = All(e, "Event").ToList();
            if (eventNodes.Count > 0)
            {
                text.Append($"{eventNodes.Count} of the events behind it are {string.Join(", ", eventNodes.Take(3).Select(n => Short(n.Id)))}");
                text.Append(eventNodes.Count > 3 ? " and others. " : ". ");
            }
            if (ip is not null) text.Append($"The traffic came from {ip.Id}. ");
            if (merchant is not null) text.Append($"The account concerned is {merchant.Id}. ");
            if (service is not null) text.Append($"The affected service is {service.Id}. ");
        }
        else if (e.Sources.Count > 0)
        {
            // A vector-only retrieval has written guidance and no observation.
            // Saying so plainly is more useful than implying the system saw
            // something it did not.
            text.Append(
                "No alert in this system was matched to the question. The passages below are written " +
                "guidance, not something the platform observed. ");
        }
        else
        {
            var services = All(e, "Service").ToList();
            if (services.Count > 0)
            {
                text.Append($"{services.Count} service(s) are involved: ");
                text.Append(string.Join(", ", services.Select(s => s.Id)));
                text.Append(". ");
            }
        }

        return text.Length == 0 ? "No observation was retrieved." : text.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    // Interpretation: explicitly a hypothesis
    // -----------------------------------------------------------------------
    private static string Interpretation(EvidenceEnvelope e)
    {
        var threat = First(e, "Threat");
        var weaknesses = All(e, "Vulnerability").ToList();

        if (threat is null)
        {
            return "The graph links no threat to this evidence, so no interpretation is offered.";
        }

        var text = new StringBuilder();
        text.Append($"This may indicate {threat.Summary}. ");

        if (weaknesses.Count > 0)
        {
            text.Append(weaknesses.Count == 1
                ? $"The weakness it exploits is {weaknesses[0].Id}, {weaknesses[0].Summary}. "
                : $"The weaknesses it exploits are " +
                  $"{string.Join(" and ", weaknesses.Select(w => $"{w.Id} ({w.Summary})"))}. ");
        }

        // The hedge is not padding. A rule firing is a pattern matching, not a
        // finding confirmed, and an answer that states the threat as fact
        // invites an analyst to act on an inference without checking it.
        text.Append(
            "This is a hypothesis drawn from the rule that fired, not a confirmed finding. " +
            "Verify it against the events listed before escalating.");

        return text.ToString();
    }

    // -----------------------------------------------------------------------
    // Response: what to do, and what says so
    // -----------------------------------------------------------------------
    private static string Response(EvidenceEnvelope e)
    {
        var text = new StringBuilder();

        var controls = All(e, "Control").ToList();
        var implemented = controls.Where(c => c.Summary.Contains("(implemented)")).ToList();
        var missing = controls.Where(c => c.Summary.Contains("(NOT implemented)")).ToList();

        if (e.Sources.Count > 0)
        {
            var best = e.Sources.OrderByDescending(s => s.Similarity ?? 0).First();

            text.Append($"Follow {best.ProcedureId} revision {best.Revision}, {best.Title}. ");

            // The retrieved paragraph itself, not a paraphrase of it. A
            // procedure summarised by a generator is no longer the procedure
            // that was reviewed and approved.
            if (!string.IsNullOrWhiteSpace(best.Text))
            {
                text.Append($"The relevant step reads: \"{best.Text}\" ");
            }
        }

        if (implemented.Count > 0)
        {
            text.Append($"Controls already in place: {string.Join("; ", implemented.Select(c => $"{c.Id} {ControlName(c)}"))}. ");
        }

        if (missing.Count > 0)
        {
            // The gap is the finding. A control that does not exist is why the
            // rule is a detection rather than a prevention, and burying that
            // would make the system sound safer than it is.
            text.Append(
                $"Controls that would mitigate this but are NOT implemented here: " +
                $"{string.Join("; ", missing.Select(c => $"{c.Id} {ControlName(c)}"))}. " +
                "This is detected rather than prevented. ");
        }

        if (text.Length == 0)
        {
            text.Append("No control or procedure in the graph answers this evidence.");
        }

        return text.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    private static EvidenceNode? First(EvidenceEnvelope e, string label) =>
        e.Nodes.FirstOrDefault(n => n.Label == label);

    private static IEnumerable<EvidenceNode> All(EvidenceEnvelope e, string label) =>
        e.Nodes.Where(n => n.Label == label);

    /// <summary>Control summaries carry an "(implemented)" suffix; the name is what precedes it.</summary>
    private static string ControlName(EvidenceNode control)
    {
        var cut = control.Summary.IndexOf(" (", StringComparison.Ordinal);
        return cut > 0 ? control.Summary[..cut] : control.Summary;
    }

    /// <summary>GUIDs are unreadable in prose; the first segment identifies well enough.</summary>
    private static string Short(string id) =>
        id.Length > 12 && id.Contains('-') ? id[..id.IndexOf('-')] : id;
}
