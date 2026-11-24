using Microsoft.Extensions.Options;
using Neo4j.Driver;
using PandaPocket.Services.Graph.Configuration;
using PandaPocket.Services.Graph.Endpoints;
using PandaPocket.Services.Graph.Graph;
using PandaPocket.Services.Graph.Rag;
using PandaPocket.Services.Graph.Rag.Evaluation;
using PandaPocket.Shared.Contracts.Discovery;
using PandaPocket.Shared.Contracts.Observability;
using Serilog;

// ---------------------------------------------------------------------------
// The Graph service: the only thing that talks to Neo4j.
//
// Same shape as the other five, with one difference worth stating. It owns no
// source data at all. Everything in the graph is a projection of something
// another service already owns: the registry's catalogue, the gateway's routing
// table, the SOC service's events and alerts, and curated security knowledge.
// That makes the graph safe to drop and rebuild, which is what the load
// endpoints are for, and it keeps the database-per-service boundary intact
// rather than quietly turning Neo4j into a second home for everyone's data.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "Graph";

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithProperty("Service", ServiceName)
    .WriteTo.Console()
    .WriteTo.Seq(context.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341"));

builder.Services.Configure<GraphOptions>(builder.Configuration.GetSection(GraphOptions.SectionName));

// One driver for the lifetime of the process. It holds a connection pool, so
// creating one per request would open a new pool every time.
builder.Services.AddSingleton<IDriver>(sp =>
{
    var options = sp.GetRequiredService<IOptions<GraphOptions>>().Value;
    return GraphDatabase.Driver(
        options.Uri,
        AuthTokens.Basic(options.Username, options.Password),
        config => config.WithMaxConnectionPoolSize(20));
});

builder.Services.AddHttpClient("consul", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<GraphOptions>>().Value;
    client.BaseAddress = new Uri(options.ConsulUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});

// For reading each service's own /health. Short timeout: a service that is down
// should be recorded as down quickly, not hold up the whole load.
builder.Services.AddHttpClient("service", client => client.Timeout = TimeSpan.FromSeconds(5));

builder.Services.AddHttpClient("ollama", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<GraphOptions>>().Value;
    client.BaseAddress = new Uri(options.OllamaUrl);

    // Generous: the first call after an idle period loads the model from
    // disk and takes around twenty-five seconds on this hardware.
    client.Timeout = TimeSpan.FromSeconds(options.EmbeddingTimeoutSeconds);
});

builder.Services.AddHttpClient("soc", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<GraphOptions>>().Value;
    client.BaseAddress = new Uri(options.SocBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddScoped<TopologyLoader>();
builder.Services.AddScoped<SocLoader>();
builder.Services.AddScoped<KnowledgeLoader>();

// Retrieval. The embedding client is a singleton: it holds no state and
// the model it talks to is a process on the host.
builder.Services.AddSingleton<EmbeddingClient>();
builder.Services.AddScoped<Retriever>();
builder.Services.AddSingleton<TemplateGenerator>();
builder.Services.AddSingleton<ModelGenerator>();
builder.Services.AddScoped<AnswerService>();
builder.Services.AddScoped<EvaluationHarness>();
builder.Services.AddServiceRegistry(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddCheck<Neo4jHealthCheck>("neo4j", tags: ["ready", "db"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Panda Pocket Graph API",
        Version = "v1",
        Description =
            "The security knowledge graph. Holds the application's own topology read live from " +
            "the registry and the gateway routing table, the events and alerts the SOC layer " +
            "produced, and curated security knowledge. Owns no source data: everything here is " +
            "a projection, so the graph can be dropped and rebuilt at any time."
    });
});

var app = builder.Build();

app.UseCorrelationId();
app.UseSerilogRequestLogging();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Panda Pocket Graph API v1");
    c.DocumentTitle = "Panda Pocket Graph API";
});

app.MapGraphEndpoints();

app.MapHealthChecks("/health", new()
{
    ResponseWriter = HealthResponseWriter.WriteAsync
});

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

await PrepareGraphAsync(app);

try
{
    Log.Information("{Service} starting", ServiceName);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "{Service} terminated unexpectedly", ServiceName);
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Applies the schema, then loads the topology once so the graph is populated
/// the moment the service is up rather than waiting for somebody to ask.
/// </summary>
static async Task PrepareGraphAsync(WebApplication app)
{
    var options = app.Services.GetRequiredService<IOptions<GraphOptions>>().Value;
    var driver = app.Services.GetRequiredService<IDriver>();

    // Neo4j takes noticeably longer to accept connections than Postgres does,
    // and Compose only waits for the port. Retried rather than assumed.
    var delay = TimeSpan.FromSeconds(3);

    for (var attempt = 1; attempt <= 8; attempt++)
    {
        try
        {
            await driver.VerifyConnectivityAsync();

            var schemaLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GraphSchema");
            await GraphSchema.ApplyAsync(driver, schemaLogger, options.EmbeddingDimensions, default);
            break;
        }
        catch (Exception ex)
        {
            if (attempt == 8)
            {
                Log.Error(ex, "Could not reach Neo4j; the service will start but the graph is unavailable");
                return;
            }

            Log.Warning("Neo4j not ready (attempt {Attempt}): {Reason}", attempt, ex.Message);
            await Task.Delay(delay);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 20));
        }
    }

    if (!options.AutoLoadOnStartup) return;

    try
    {
        using var scope = app.Services.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<TopologyLoader>();
        var result = await loader.LoadAsync(default);

        foreach (var warning in result.Warnings) Log.Warning("Topology load: {Warning}", warning);
    }
    catch (Exception ex)
    {
        // Not fatal. The load endpoint can be called once whatever was missing
        // is back, and a graph service that refuses to start because one
        // dependency was slow is worse than one with an empty graph.
        Log.Warning(ex, "Initial topology load failed; call /api/graph/load/topology once dependencies are up");
    }
}

/// <summary>Reports the database as healthy only if it answers a real query.</summary>
public sealed class Neo4jHealthCheck(IDriver driver)
    : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = driver.AsyncSession();
            var count = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync("MATCH (n) RETURN count(n) AS nodes");
                var record = await cursor.SingleAsync();
                return record["nodes"].As<int>();
            });

            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(
                $"{count} nodes");
        }
        catch (Exception ex)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(
                "Neo4j unreachable", ex);
        }
    }
}

public partial class Program;
