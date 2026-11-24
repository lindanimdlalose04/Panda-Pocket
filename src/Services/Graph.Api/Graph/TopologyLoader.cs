using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using PandaPocket.Services.Graph.Configuration;

namespace PandaPocket.Services.Graph.Graph;

public sealed record TopologyResult(
    int Services, int Endpoints, int Assets, int Dependencies, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds the application subgraph by reading the running system.
///
/// This is the first of the three sources the specification requires, and it is
/// the one most projects type in by hand. Nothing here is typed. Services and
/// their health come from the Consul catalogue, endpoints come from the
/// gateway's own routing table, and each service's assets come from the checks
/// its /health endpoint reports. Stop a service and the next load records it as
/// critical, because the graph is describing the system rather than a diagram of
/// it.
///
/// One thing genuinely cannot be discovered: which service calls which. Nothing
/// in the registry knows that a service depends on another until a call fails,
/// so those edges are declared in configuration and labelled as declared. The
/// alternative was to infer them from circuit-breaker events, which would mean
/// the graph only learns about a dependency at the moment it breaks.
/// </summary>
public sealed class TopologyLoader(
    IDriver driver,
    IHttpClientFactory httpClientFactory,
    IOptions<GraphOptions> options,
    IConfiguration configuration,
    ILogger<TopologyLoader> logger)
{
    private readonly GraphOptions _options = options.Value;

    public async Task<TopologyResult> LoadAsync(CancellationToken ct)
    {
        var warnings = new List<string>();

        var services = await ReadServicesFromConsulAsync(warnings, ct);
        var endpoints = ReadEndpointsFromGateway(warnings);
        var assets = await ReadAssetsFromHealthAsync(services, warnings, ct);
        var dependencies = ReadDeclaredDependencies();

        await using var session = driver.AsyncSession();

        // Services, with whatever the registry currently says about them.
        foreach (var service in services)
        {
            await session.RunAsync("""
                MERGE (s:Service {name: $name})
                SET   s.status      = $status,
                      s.instances   = $instances,
                      s.lastSeenAt  = datetime(),
                      s.source      = 'consul'
                REMOVE s.absentSince
                """,
                new { name = service.Name, status = service.Status, instances = service.Instances });
        }

        // Endpoints, and the service that serves each. The route names a
        // service rather than a host, which is the same indirection the gateway
        // relies on at run time.
        foreach (var endpoint in endpoints)
        {
            await session.RunAsync("""
                MERGE (e:Endpoint {id: $id})
                SET   e.path         = $path,
                      e.method       = $method,
                      e.isPublic     = $isPublic,
                      e.rateLimited  = $rateLimited,
                      e.source       = 'ocelot.json'
                WITH e
                MERGE (s:Service {name: $service})
                MERGE (s)-[:EXPOSES]->(e)
                """,
                new
                {
                    id = endpoint.Id,
                    path = endpoint.Path,
                    method = endpoint.Method,
                    isPublic = endpoint.IsPublic,
                    rateLimited = endpoint.RateLimited,
                    service = endpoint.ServiceName
                });
        }

        foreach (var asset in assets)
        {
            await session.RunAsync("""
                MERGE (a:Asset {id: $id})
                SET   a.name   = $name,
                      a.kind   = $kind,
                      a.source = 'health check'
                WITH a
                MERGE (s:Service {name: $service})
                MERGE (s)-[:OWNS]->(a)
                """,
                new { id = asset.Id, name = asset.Name, kind = asset.Kind, service = asset.ServiceName });
        }

        // Services the registry no longer lists.
        //
        // A service shut down gracefully deregisters itself, so it does not
        // appear as critical in the catalogue, it disappears from it. Loading
        // only what Consul returns therefore left a stopped service sitting in
        // the graph still marked "passing", which is worse than not recording it
        // at all: the graph was confidently stating something false.
        //
        // Only nodes this loader created are touched. The gateway and Consul
        // itself are reached through declared dependencies rather than
        // discovered, and marking those absent would be wrong.
        await session.RunAsync("""
            MATCH (s:Service)
            WHERE s.source = 'consul' AND NOT s.name IN $seen
            SET   s.status     = 'absent',
                  s.absentSince = coalesce(s.absentSince, datetime())
            """,
            new { seen = services.Select(x => x.Name).ToList() });

        foreach (var (from, to) in dependencies)
        {
            await session.RunAsync("""
                MERGE (a:Service {name: $from})
                MERGE (b:Service {name: $to})
                MERGE (a)-[d:DEPENDS_ON]->(b)
                SET   d.source = 'declared'
                """,
                new { from, to });
        }

        logger.LogInformation(
            "Topology loaded: {Services} services, {Endpoints} endpoints, {Assets} assets, {Deps} dependencies",
            services.Count, endpoints.Count, assets.Count, dependencies.Count);

        return new TopologyResult(services.Count, endpoints.Count, assets.Count, dependencies.Count, warnings);
    }

    // -----------------------------------------------------------------------
    // Consul: which services exist and whether they are well
    // -----------------------------------------------------------------------
    private async Task<List<ServiceNode>> ReadServicesFromConsulAsync(List<string> warnings, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("consul");

        try
        {
            var checks = await client.GetFromJsonAsync<List<ConsulHealthCheck>>("/v1/health/state/any", ct) ?? [];

            return checks
                .Where(c => !string.IsNullOrWhiteSpace(c.ServiceName))
                .GroupBy(c => c.ServiceName!)
                .Select(g => new ServiceNode(
                    g.Key,
                    // The worst status across the instances. A service with one
                    // healthy and one critical instance is degraded, and saying
                    // "passing" because one of them answers would be a lie by
                    // omission.
                    g.Any(c => c.Status == "critical") ? "critical"
                        : g.Any(c => c.Status == "warning") ? "warning" : "passing",
                    g.Select(c => c.ServiceId).Distinct().Count()))
                .OrderBy(s => s.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not read the Consul catalogue: {ex.Message}");
            logger.LogWarning("Consul unreachable while loading topology: {Reason}", ex.Message);
            return [];
        }
    }

    // -----------------------------------------------------------------------
    // The gateway's routing table: which endpoints exist and who serves them
    // -----------------------------------------------------------------------
    private List<EndpointNode> ReadEndpointsFromGateway(List<string> warnings)
    {
        if (!File.Exists(_options.OcelotConfigPath))
        {
            warnings.Add($"Gateway routing table not found at {_options.OcelotConfigPath}; no endpoints loaded.");
            return [];
        }

        try
        {
            // ocelot.json carries "//" comment keys, which are legal JSON object
            // members but would otherwise be read as routes.
            var json = File.ReadAllText(_options.OcelotConfigPath);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            if (!document.RootElement.TryGetProperty("Routes", out var routes)) return [];

            var endpoints = new List<EndpointNode>();

            foreach (var route in routes.EnumerateArray())
            {
                if (!route.TryGetProperty("UpstreamPathTemplate", out var pathElement)) continue;
                if (!route.TryGetProperty("ServiceName", out var serviceElement)) continue;

                var path = pathElement.GetString()!;
                var service = serviceElement.GetString()!;

                var rateLimited =
                    route.TryGetProperty("RateLimitOptions", out var limit) &&
                    limit.TryGetProperty("EnableRateLimiting", out var enabled) &&
                    enabled.ValueKind == JsonValueKind.True;

                // A route the gateway does not rate limit is one it also does
                // not require a key for, in this system: both are the public
                // surface. Recorded as a property so the graph can answer "what
                // is reachable without a credential" without anyone re-deriving
                // it from the route list.
                var isPublic = !rateLimited;

                var methods = route.TryGetProperty("UpstreamHttpMethod", out var methodsElement)
                    ? methodsElement.EnumerateArray().Select(m => m.GetString()!).ToArray()
                    : ["GET"];

                foreach (var method in methods)
                {
                    endpoints.Add(new EndpointNode(
                        Id: $"{method} {path}",
                        Path: path,
                        Method: method,
                        ServiceName: service,
                        IsPublic: isPublic,
                        RateLimited: rateLimited));
                }
            }

            return endpoints;
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not parse the gateway routing table: {ex.Message}");
            logger.LogWarning(ex, "Failed to read {Path}", _options.OcelotConfigPath);
            return [];
        }
    }

    // -----------------------------------------------------------------------
    // Each service's /health: which stores it actually depends on
    // -----------------------------------------------------------------------
    private async Task<List<AssetNode>> ReadAssetsFromHealthAsync(
        List<ServiceNode> services, List<string> warnings, CancellationToken ct)
    {
        var assets = new List<AssetNode>();
        var client = httpClientFactory.CreateClient("service");

        foreach (var service in services)
        {
            try
            {
                // Inside the Compose network a service is reachable by its
                // registered name, which is also how the gateway reaches it.
                var health = await client.GetFromJsonAsync<HealthResponse>(
                    $"http://{service.Name}:8080/health", ct);

                if (health?.Checks is null) continue;

                foreach (var check in health.Checks)
                {
                    // Self checks and liveness probes are not assets.
                    if (string.IsNullOrWhiteSpace(check.Name)) continue;

                    var kind = check.Name.Contains("mongo", StringComparison.OrdinalIgnoreCase)
                        ? "document store"
                        : check.Name.EndsWith("_db", StringComparison.OrdinalIgnoreCase)
                            ? "relational database"
                            : "dependency";

                    assets.Add(new AssetNode(check.Name, check.Name, kind, service.Name));
                }
            }
            catch (Exception ex)
            {
                // A service that is down cannot report its assets, which is
                // expected rather than exceptional: the graph keeps whatever the
                // last successful load recorded.
                warnings.Add($"Could not read health for {service.Name}: {ex.Message}");
            }
        }

        return assets.DistinctBy(a => a.Id).ToList();
    }

    // -----------------------------------------------------------------------
    // Declared, because nothing observable knows it
    // -----------------------------------------------------------------------
    private List<(string From, string To)> ReadDeclaredDependencies()
    {
        var declared = configuration.GetSection("Graph:Dependencies").Get<string[]>() ?? [];

        return declared
            .Select(entry => entry.Split("->", StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .Select(parts => (parts[0], parts[1]))
            .ToList();
    }

    private sealed record ServiceNode(string Name, string Status, int Instances);

    private sealed record EndpointNode(
        string Id, string Path, string Method, string ServiceName, bool IsPublic, bool RateLimited);

    private sealed record AssetNode(string Id, string Name, string Kind, string ServiceName);

    private sealed record ConsulHealthCheck(
        [property: JsonPropertyName("Status")]      string Status,
        [property: JsonPropertyName("ServiceID")]   string? ServiceId,
        [property: JsonPropertyName("ServiceName")] string? ServiceName);

    private sealed record HealthResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("checks")] List<HealthCheck>? Checks);

    private sealed record HealthCheck(
        [property: JsonPropertyName("name")]   string? Name,
        [property: JsonPropertyName("status")] string? Status);
}
