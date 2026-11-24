using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PandaPocket.Services.Graph.Configuration;

namespace PandaPocket.Services.Graph.Rag;

/// <summary>
/// Writes the answer with a local language model, from the evidence envelope and
/// nothing else.
///
/// An enhancement, not a dependency. The model is given the retrieved facts and
/// told to answer only from them, so it is acting as a writer rather than a
/// source: retrieval already decided what is true, and this decides how to say
/// it. If it is unreachable, slow, or returns something unusable, the caller
/// falls back to the template and the answer is still correct.
///
/// Measured honestly on this hardware: llama3.2:1b takes around thirty seconds
/// for a short answer, and in testing it stayed grounded but DROPPED evidence,
/// omitting the threat and control from a prompt that contained both. That is
/// the finding the evaluation reports, and it is why templates are primary.
/// </summary>
public sealed class ModelGenerator(
    IHttpClientFactory httpClientFactory,
    IOptions<GraphOptions> options,
    ILogger<ModelGenerator> logger) : IAnswerGenerator
{
    private readonly GraphOptions _options = options.Value;

    public string Name => _options.GenerationModel;

    public async Task<(string Observed, string Interpretation, string Response)> GenerateAsync(
        EvidenceEnvelope evidence, CancellationToken ct)
    {
        var prompt = BuildPrompt(evidence);

        var client = httpClientFactory.CreateClient("ollama");

        var response = await client.PostAsJsonAsync("/api/generate", new
        {
            model = _options.GenerationModel,
            prompt,
            stream = false,

            // Low temperature. This is a summarisation task over supplied facts,
            // not a creative one, and variation here means variation in what an
            // analyst is told about the same evidence.
            options = new { temperature = 0.2, num_predict = 400 }
        }, ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GenerateResponse>(cancellationToken: ct);
        var text = result?.Response?.Trim();

        logger.LogInformation(
            "Model produced {Tokens} tokens from {Nodes} evidence nodes",
            result?.EvalCount ?? 0, evidence.Nodes.Count);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("The model returned an empty answer.");
        }

        return Split(text);
    }

    /// <summary>
    /// The prompt is the envelope, flattened, plus an instruction to use nothing
    /// else. Nothing about the question's phrasing reaches the model beyond the
    /// question itself, and no facts reach it that retrieval did not supply.
    /// </summary>
    private static string BuildPrompt(EvidenceEnvelope evidence)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a security operations assistant. Answer ONLY from the evidence below.");
        sb.AppendLine("Do not add facts. If the evidence does not support something, do not say it.");
        sb.AppendLine();
        sb.AppendLine("Answer in exactly three sections, each on one line, with these labels:");
        sb.AppendLine("OBSERVED: what the system recorded, with identifiers.");
        sb.AppendLine("INTERPRETATION: what it may indicate, stated as a hypothesis and not as fact.");
        sb.AppendLine("RESPONSE: what to do, naming the procedure and controls given.");
        sb.AppendLine();
        sb.AppendLine($"QUESTION: {evidence.Question}");
        sb.AppendLine();
        sb.AppendLine("EVIDENCE, retrieved from a security knowledge graph:");

        foreach (var node in evidence.Nodes)
        {
            sb.AppendLine($"- ({node.Label}) {node.Id}: {node.Summary}");
        }

        if (evidence.Edges.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("RELATIONSHIPS:");
            foreach (var edge in evidence.Edges.Take(20))
            {
                sb.AppendLine($"- {edge.From} -[{edge.Type}]-> {edge.To}");
            }
        }

        if (evidence.Sources.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("WRITTEN PROCEDURE:");
            foreach (var source in evidence.Sources.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
            {
                sb.AppendLine($"- {source.ProcedureId} revision {source.Revision}, step {source.Ordinal}: {source.Text}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Pulls the three sections back out.
    ///
    /// A small model does not reliably honour an output format, so a missing
    /// label is expected rather than exceptional. Anything that cannot be split
    /// is returned whole in the observed section instead of being discarded,
    /// because a badly formatted answer is still more use than none.
    /// </summary>
    private static (string Observed, string Interpretation, string Response) Split(string text)
    {
        string Section(string label)
        {
            var start = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";

            start += label.Length;

            var ends = new[] { "OBSERVED:", "INTERPRETATION:", "RESPONSE:" }
                .Select(l => text.IndexOf(l, start, StringComparison.OrdinalIgnoreCase))
                .Where(i => i > 0)
                .DefaultIfEmpty(text.Length)
                .Min();

            return text[start..ends].Trim(' ', '\n', '\r', '*', '-');
        }

        var observed = Section("OBSERVED:");
        var interpretation = Section("INTERPRETATION:");
        var response = Section("RESPONSE:");

        if (observed.Length == 0 && interpretation.Length == 0 && response.Length == 0)
        {
            return (text, "", "");
        }

        return (observed, interpretation, response);
    }

    private sealed record GenerateResponse(
        [property: JsonPropertyName("response")] string? Response,
        [property: JsonPropertyName("eval_count")] int? EvalCount);
}
