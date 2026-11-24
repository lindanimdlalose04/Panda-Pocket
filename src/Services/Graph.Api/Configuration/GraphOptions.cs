namespace PandaPocket.Services.Graph.Configuration;

public sealed class GraphOptions
{
    public const string SectionName = "Graph";

    public string Uri { get; init; } = "bolt://localhost:7687";
    public string Username { get; init; } = "neo4j";
    public string Password { get; init; } = "panda_graph_dev";

    /// <summary>Where the registry is, for reading the live service catalogue.</summary>
    public string ConsulUrl { get; init; } = "http://localhost:8500";

    /// <summary>
    /// The gateway's own routing table, mounted read-only.
    ///
    /// The same file Ocelot routes from, not a copy. A second copy would be one
    /// more thing to keep in step, and a graph claiming the system exposes an
    /// endpoint it no longer does is worse than a graph with a gap in it.
    /// </summary>
    public string OcelotConfigPath { get; init; } = "/app/config/ocelot.json";

    /// <summary>The SOC service, for events and alerts.</summary>
    public string SocBaseUrl { get; init; } = "http://localhost:5005";

    /// <summary>How often the topology is refreshed from the registry.</summary>
    public int TopologyRefreshSeconds { get; init; } = 60;

    public bool AutoLoadOnStartup { get; init; } = true;

    // ---- Retrieval ---------------------------------------------------------

    /// <summary>
    /// Ollama, run natively on the host rather than as a container.
    ///
    /// host.docker.internal rather than localhost: inside a container localhost
    /// is the container. Docker Desktop proxies this through to the host's
    /// loopback, so Ollama binding to 127.0.0.1 is reachable without opening it
    /// to the network.
    /// </summary>
    public string OllamaUrl { get; init; } = "http://host.docker.internal:11434";

    /// <summary>
    /// 1024 dimensions, which the vector index must be created to match. A
    /// mismatch is not a runtime error, it is a silent failure to retrieve
    /// anything, so the dimension is recorded here beside the model that
    /// produces it rather than left in two unrelated places.
    /// </summary>
    public string EmbeddingModel { get; init; } = "mxbai-embed-large";

    public int EmbeddingDimensions { get; init; } = 1024;

    /// <summary>Seconds. The first call after an idle period loads the model and is slow.</summary>
    public int EmbeddingTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// How long Ollama should keep the embedding model resident after a call.
    ///
    /// Its default is five minutes, after which the model is evicted and the
    /// next question pays the cold load again. Measured here after a restart:
    /// 54 seconds cold, then 1.6, then 1.0. Warming once at startup is
    /// therefore not enough on its own, because a quiet half hour undoes it,
    /// and a question that takes a minute looks like a system that has hung
    /// rather than one loading a model.
    /// </summary>
    public string EmbeddingKeepAlive { get; init; } = "30m";

    /// <summary>How many chunks a vector search returns before traversal expands them.</summary>
    public int VectorTopK { get; init; } = 4;

    /// <summary>
    /// Off by default. The template answers from the same evidence in
    /// milliseconds and cannot drop any of it; the model takes around thirty
    /// seconds on this hardware and was measured omitting supplied facts. It is
    /// switched on per request for the comparison, not relied on.
    /// </summary>
    public bool UseModelGeneration { get; init; }

    public string GenerationModel { get; init; } = "llama3.2:1b";
}
