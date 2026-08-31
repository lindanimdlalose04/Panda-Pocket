using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PandaPocket.Services.Settlement.Clients;
using PandaPocket.Services.Settlement.Configuration;
using PandaPocket.Services.Settlement.Domain;
using PandaPocket.Services.Settlement.Endpoints;
using PandaPocket.Services.Settlement.Persistence;
using PandaPocket.Shared.Contracts.Discovery;
using PandaPocket.Shared.Contracts.Observability;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "Settlement";

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithProperty("Service", ServiceName)
    .WriteTo.Console()
    .WriteTo.Seq(context.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341"));

builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.SectionName));

builder.Services.AddDbContext<SettlementDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("SettlementDb")));

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IMemoryCacheAdapter, MemoryCacheAdapter>();

builder.Services.AddHttpClient<IMerchantClient, MerchantClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:MerchantBaseUrl"] ?? "http://localhost:5001");
    client.Timeout = TimeSpan.FromSeconds(5);
});

// A separate client for merchant endpoints, with its own timeout. These are
// untrusted third-party URLs: a merchant whose server accepts the connection and
// then never responds must not tie up a dispatcher slot indefinitely.
builder.Services.AddHttpClient("webhook", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<WebhookOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
});

// Announce this instance to Consul on startup and remove it on shutdown,
// with a health check Consul polls. Disabled unless configuration turns it
// on, so the service still runs outside Compose.
builder.Services.AddServiceRegistry(builder.Configuration);

builder.Services.AddScoped<SettlementService>();
builder.Services.AddHostedService<WebhookDispatcher>();

builder.Services.AddHealthChecks()
    .AddNpgSql(
        connectionStringFactory: sp => builder.Configuration.GetConnectionString("SettlementDb")!,
        name: "settlement_db",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready", "db"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Panda Pocket Settlement API",
        Version = "v1",
        Description =
            "The merchant ZAR ledger, platform fee income and webhook delivery. " +
            "Settling an invoice writes two ledger entries, a credit for the gross " +
            "amount and a negative fee, so where the commission went is visible " +
            "rather than netted away."
    });
});

var app = builder.Build();

app.UseCorrelationId();
app.UseSerilogRequestLogging();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Panda Pocket Settlement API v1");
    c.DocumentTitle = "Panda Pocket Settlement API";
});

app.MapSettlementEndpoints();

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
            var db = scope.ServiceProvider.GetRequiredService<SettlementDbContext>();
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
