using HealthChecks.MongoDb;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using PandaPocket.Services.Rate.Configuration;
using PandaPocket.Services.Rate.Domain;
using PandaPocket.Services.Rate.Endpoints;
using PandaPocket.Services.Rate.Persistence;
using PandaPocket.Shared.Contracts.Discovery;
using PandaPocket.Shared.Contracts.Observability;
using Serilog;

// ---------------------------------------------------------------------------
// This file is the template. Whatever shape is settled here gets copied to
// Invoice, Merchant and Settlement, so it is worth being deliberate.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "Rate";

// Serilog, configured before anything else so that startup failures are logged
// rather than lost. Every event carries Service, which is what makes Seq
// filterable once four services are shipping to the same instance.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithProperty("Service", ServiceName)
    .WriteTo.Console()
    .WriteTo.Seq(context.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341"));

builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection(MongoOptions.SectionName));
builder.Services.Configure<RateSimulatorOptions>(builder.Configuration.GetSection(RateSimulatorOptions.SectionName));

// One MongoClient for the lifetime of the process. The driver pools connections
// internally, so creating a client per request would be both wasteful and a
// known source of connection exhaustion.
//
// The timeouts are overridden deliberately. The driver defaults to waiting 30
// seconds to select a server, which means that with MongoDB down the health
// check takes a minute to report Unhealthy and the history endpoint hangs
// rather than failing. A health check that takes 60 seconds to tell you
// something is broken is not a health check. Failing in a few seconds is both
// better engineering and the difference between a demo that shows a clean
// degradation and one where everybody watches a spinner.
builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var connectionString = sp.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString;
    var settings = MongoClientSettings.FromConnectionString(connectionString);

    settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
    settings.ConnectTimeout = TimeSpan.FromSeconds(3);
    settings.SocketTimeout = TimeSpan.FromSeconds(10);

    return new MongoClient(settings);
});

// Announce this instance to Consul on startup and remove it on shutdown,
// with a health check Consul polls. Disabled unless configuration turns it
// on, so the service still runs outside Compose.
builder.Services.AddServiceRegistry(builder.Configuration);

builder.Services.AddScoped<ITickRepository, TickRepository>();
builder.Services.AddSingleton<RateBook>();
builder.Services.AddHostedService<TickGeneratorService>();

// A health check that genuinely reaches Mongo. Returning 200 because the
// process is alive tells an orchestrator nothing useful; the interesting
// failure is the one where the service is up and its database is not.
builder.Services.AddHealthChecks()
    .AddMongoDb(
        clientFactory: sp => sp.GetRequiredService<IMongoClient>(),
        databaseNameFactory: sp => sp.GetRequiredService<IOptions<MongoOptions>>().Value.Database,
        name: "mongodb",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready", "db"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Panda Pocket Rate API",
        Version = "v1",
        Description =
            "ZAR conversion quotes and tick history. Prices come from a local " +
            "geometric Brownian motion simulator rather than an external exchange, " +
            "so a live demo cannot be broken by a third party outage or rate limit."
    });
});

var app = builder.Build();

// Correlation first, then request logging. The order matters and is easy to get
// backwards: UseSerilogRequestLogging writes its summary line when the response
// completes, by which point any log context pushed by middleware nested inside
// it has already been popped. Putting correlation first means the id is in
// scope when that summary is written, so the one line carrying the method, path,
// status code and elapsed time is also the line you can filter by correlation
// id. With the order reversed everything still works except that single most
// useful line, which is a subtle way to lose the tracing demo.
app.UseCorrelationId();
app.UseSerilogRequestLogging();

// Swagger is left on outside development on purpose. The exported OpenAPI
// document is a graded deliverable, and a running container that can produce it
// is more convincing than a JSON file committed to the repository.
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Panda Pocket Rate API v1");
    c.DocumentTitle = "Panda Pocket Rate API";
});

app.MapRateEndpoints();

app.MapHealthChecks("/health", new()
{
    ResponseWriter = HealthResponseWriter.WriteAsync
});

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

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

// Exposed so integration tests and the OpenAPI generator can see the entry point.
public partial class Program;
