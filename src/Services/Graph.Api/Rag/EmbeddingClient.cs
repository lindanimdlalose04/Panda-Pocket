using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PandaPocket.Services.Graph.Configuration;

namespace PandaPocket.Services.Graph.Rag;

/// <summary>
/// Turns text into a vector, using a model running locally.
///
/// An embedding is a list of numbers positioned so that text meaning similar
/// things lands close together. "Repeated login failures across accounts" and
/// "investigate password spraying" share almost no words and sit near each
/// other, which is exactly the match a keyword search cannot make and the reason
/// vector retrieval is worth having alongside Cypher.
///
/// Local rather than a hosted API: the demonstration has to work offline and
/// without a key that could expire or be rate limited mid-recording. The cost is
/// that the first call after an idle period loads the model and takes tens of
/// seconds, which is why it is warmed at startup and why every request asks
/// Ollama to keep the model resident rather than evicting it after five
/// minutes of quiet.
/// </summary>
public sealed class EmbeddingClient(
    IHttpClientFactory httpClientFactory,
    IOptions<GraphOptions> options,
    ILogger<EmbeddingClient> logger)
{
    private readonly GraphOptions _options = options.Value;

    public int Dimensions => _options.EmbeddingDimensions;

    /// <summary>
    /// Returns null rather than throwing when the model is unreachable.
    ///
    /// Vector retrieval is one of three routes. If it is unavailable the Cypher
    /// route still answers, so a failure here should degrade the answer rather
    /// than refuse to produce one.
    /// </summary>
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            var client = httpClientFactory.CreateClient("ollama");

            // keep_alive holds the model in memory between questions. Without
            // it Ollama evicts after five minutes and the next caller waits
            // through a cold load, which during a demonstration reads as a
            // hang rather than as a model being fetched from disk.
            var response = await client.PostAsJsonAsync(
                "/api/embed",
                new
                {
                    model = _options.EmbeddingModel,
                    input = text,
                    keep_alive = _options.EmbeddingKeepAlive
                },
                ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Embedding model answered {Status}; vector retrieval unavailable",
                    (int)response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: ct);
            var vector = result?.Embeddings?.FirstOrDefault();

            if (vector is null) return null;

            if (vector.Length != _options.EmbeddingDimensions)
            {
                // A dimension mismatch does not throw anywhere downstream: the
                // vector index simply never matches, and retrieval returns
                // nothing for reasons that look like bad data. Saying it loudly
                // here is the difference between a five minute fix and an hour.
                logger.LogError(
                    "Embedding model returned {Actual} dimensions but the index expects {Expected}. " +
                    "Vector retrieval will silently match nothing until these agree.",
                    vector.Length, _options.EmbeddingDimensions);
                return null;
            }

            return vector;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Could not reach the embedding model ({Reason})", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Loads the model so the first real question does not pay for it. Measured
    /// after a host restart at 54 seconds cold, then 1.6 and 1.0 warm.
    /// </summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var vector = await EmbedAsync("warm up", ct);
        var elapsed = DateTimeOffset.UtcNow - started;

        if (vector is null) logger.LogWarning("Embedding model did not warm; vector retrieval is unavailable");
        else logger.LogInformation("Embedding model warm in {Seconds:N1}s", elapsed.TotalSeconds);
    }

    private sealed record EmbedResponse(
        [property: JsonPropertyName("embeddings")] float[][]? Embeddings);
}
