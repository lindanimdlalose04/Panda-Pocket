using System.Diagnostics;
using Microsoft.Extensions.Options;
using PandaPocket.Services.Graph.Configuration;

namespace PandaPocket.Services.Graph.Rag;

/// <summary>
/// Retrieve, then generate. The whole RAG pipeline in one place.
///
/// The order matters and is the point of the pattern: retrieval decides what is
/// true, generation only decides how to say it. Neither generator is shown
/// anything beyond the envelope, so neither can introduce a claim the graph does
/// not support.
///
/// The template is the default and the model is opt in, which is the reverse of
/// how this is usually built. On this hardware the model takes around thirty
/// seconds and was observed dropping supplied evidence, so making it the
/// fallback rather than the primary is a measured decision rather than caution.
/// </summary>
public sealed class AnswerService(
    Retriever retriever,
    TemplateGenerator template,
    ModelGenerator model,
    IOptions<GraphOptions> options,
    ILogger<AnswerService> logger)
{
    private readonly GraphOptions _options = options.Value;

    public async Task<Answer> AskAsync(
        string question, string? route, bool? useModel, CancellationToken ct)
    {
        var evidence = await retriever.RetrieveAsync(question, route, ct);

        var wantsModel = useModel ?? _options.UseModelGeneration;

        // Never for an empty envelope. Asking a language model to write about
        // nothing is precisely how an answer gets invented, and "the graph has
        // nothing on this" is a complete and useful answer on its own.
        if (wantsModel && evidence.IsEmpty)
        {
            logger.LogInformation("Evidence is empty; answering from the template rather than the model");
            wantsModel = false;
        }

        var stopwatch = Stopwatch.StartNew();

        if (wantsModel)
        {
            try
            {
                var (observed, interpretation, response) = await model.GenerateAsync(evidence, ct);
                stopwatch.Stop();

                return new Answer(
                    question, observed, interpretation, response,
                    model.Name, evidence, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                // Down, slow, or unusable output. The template produces the same
                // facts from the same envelope, so the answer survives losing
                // the model entirely.
                logger.LogWarning(
                    "Model generation failed ({Reason}); falling back to the template", ex.Message);
            }
        }

        var templated = await template.GenerateAsync(evidence, ct);
        stopwatch.Stop();

        return new Answer(
            question, templated.Observed, templated.Interpretation, templated.Response,
            template.Name, evidence, stopwatch.ElapsedMilliseconds);
    }
}
