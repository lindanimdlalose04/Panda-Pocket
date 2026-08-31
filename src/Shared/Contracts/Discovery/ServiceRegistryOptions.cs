namespace PandaPocket.Shared.Contracts.Discovery;

/// <summary>
/// How this service announces itself to the registry.
/// </summary>
public sealed class ServiceRegistryOptions
{
    public const string SectionName = "ServiceRegistry";

    /// <summary>
    /// Off by default so the service still runs outside Compose, where there is
    /// no registry to talk to. Enabled by environment variable in docker-compose.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Where the Consul agent lives.</summary>
    public string ConsulUrl { get; init; } = "http://localhost:8500";

    /// <summary>
    /// The logical name other services look up, for example "invoice-service".
    /// Several instances may share this name; each gets its own instance id.
    /// </summary>
    public string ServiceName { get; init; } = "unnamed-service";

    /// <summary>
    /// The address other services should use to reach this instance. Inside
    /// Compose this is the container name; on a real host it would be the
    /// routable IP.
    /// </summary>
    public string ServiceAddress { get; init; } = "localhost";

    public int ServicePort { get; init; } = 8080;

    /// <summary>Path Consul polls to decide whether this instance is healthy.</summary>
    public string HealthCheckPath { get; init; } = "/health";

    public int HealthCheckIntervalSeconds { get; init; } = 10;

    /// <summary>
    /// How long an instance may stay critical before Consul removes it
    /// altogether. This is what stops a crashed container that never
    /// deregistered from lingering in the catalogue for ever.
    /// </summary>
    public int DeregisterAfterMinutes { get; init; } = 1;

    public string[] Tags { get; init; } = [];
}
