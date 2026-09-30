using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PandaPocket.Services.Soc.Domain;
using PandaPocket.Services.Soc.Domain.Detection;
using PandaPocket.Services.Soc.Endpoints;
using PandaPocket.Services.Soc.Persistence;
using PandaPocket.Shared.Contracts.Discovery;
using PandaPocket.Shared.Contracts.Observability;
using Serilog;

// ---------------------------------------------------------------------------
// The SOC service. Same shape as the other four, deliberately: own database,
// own login role, self-registration with Consul, migrations at startup.
//
// It differs in one respect. Every other service exists to serve merchants;
// this one exists to watch the services. It therefore takes no API key, because
// its callers are the other services and the dashboard, not merchants, and it
// is reached on the internal network rather than through the gateway.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "Soc";

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithProperty("Service", ServiceName)
    .WriteTo.Console()
    .WriteTo.Seq(context.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341"));

builder.Services.AddDbContext<SocDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("SocDb")));

builder.Services.AddServiceRegistry(builder.Configuration);

// Reuses the "consul" client the registry registers, so the health poller talks
// to the same agent this instance registered itself with.
builder.Services.AddHostedService<ServiceHealthPoller>();

// Evaluates the rule catalogue on a timer and turns hits into alerts,
// applying suppression so a sustained attack is one alert rather than one
// per cycle.
builder.Services.AddHostedService<RuleEngine>();

builder.Services.AddHealthChecks()
    .AddNpgSql(
        connectionStringFactory: sp => builder.Configuration.GetConnectionString("SocDb")!,
        name: "soc_db",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready", "db"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Panda Pocket SOC API",
        Version = "v1",
        Description =
            "Security and operational event collection. Services publish events here in " +
            "batches; detection rules read them back to raise alerts. Events are stored " +
            "with the fields a rule filters on as real columns, so a rule is an index " +
            "scan rather than a scan plus JSON parsing."
    });
});

var app = builder.Build();

app.UseCorrelationId();
app.UseSerilogRequestLogging();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Panda Pocket SOC API v1");
    c.DocumentTitle = "Panda Pocket SOC API";
});

app.MapSocEndpoints();
app.MapAlertEndpoints();

app.MapHealthChecks("/health", new()
{
    ResponseWriter = HealthResponseWriter.WriteAsync
});

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

await ApplyMigrationsAsync(app);

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

static async Task ApplyMigrationsAsync(WebApplication app)
{
    var delay = TimeSpan.FromSeconds(2);

    for (var attempt = 1; attempt <= 6; attempt++)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SocDbContext>();
            await db.Database.MigrateAsync();
            Log.Information("Database migrations applied");
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Migration attempt {Attempt} failed; retrying in {Delay}s", attempt, delay.TotalSeconds);
            await Task.Delay(delay);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 20));
        }
    }

    Log.Error("Could not apply migrations after several attempts; the service will start but is unlikely to work");
}

public partial class Program;
