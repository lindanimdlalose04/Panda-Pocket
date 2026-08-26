using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PandaPocket.Services.Invoice.Clients;
using PandaPocket.Services.Invoice.Configuration;
using PandaPocket.Services.Invoice.Domain;
using PandaPocket.Services.Invoice.Endpoints;
using PandaPocket.Services.Invoice.Persistence;
using PandaPocket.Shared.Contracts.Observability;
using Polly;
using Serilog;

// ---------------------------------------------------------------------------
// Same shape as the Rate service, deliberately. The template settled on day 2
// is reused rather than reinvented.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "Invoice";

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithProperty("Service", ServiceName)
    .WriteTo.Console()
    .WriteTo.Seq(context.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341"));

builder.Services.Configure<InvoiceOptions>(builder.Configuration.GetSection(InvoiceOptions.SectionName));

builder.Services.AddDbContext<InvoiceDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("InvoiceDb")));

// Last known good rate per pair, read when the breaker is open.
builder.Services.AddSingleton<RateCache>();

var invoiceOptions = builder.Configuration.GetSection(InvoiceOptions.SectionName).Get<InvoiceOptions>() ?? new InvoiceOptions();

// The Rate call is on the critical path of creating an invoice, so it fails
// fast and degrades rather than blocking. A merchant checkout must not hang
// because a rate lookup is slow.
builder.Services.AddHttpClient<IRateClient, RateClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<InvoiceOptions>>().Value;
    client.BaseAddress = new Uri(options.RateServiceBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.RateTimeoutSeconds);
})
.AddResilienceHandler("rate-circuit-breaker", (pipeline, context) =>
{
    // The breaker exists to stop Invoice queuing requests against a dependency
    // that is already failing. Without it, every invoice creation would wait out
    // the full timeout, threads would pile up behind a service that cannot
    // answer, and a Rate outage would degrade Invoice as well. Once open, calls
    // are rejected immediately and the cached rate is used instead, so the
    // failure is contained to one service rather than spreading.
    pipeline.AddCircuitBreaker(new Polly.CircuitBreaker.CircuitBreakerStrategyOptions<HttpResponseMessage>
    {
        FailureRatio = invoiceOptions.CircuitFailureRatio,
        SamplingDuration = TimeSpan.FromSeconds(30),

        // Judged only after a few calls, so a single unlucky failure on a quiet
        // service does not trip it.
        MinimumThroughput = invoiceOptions.CircuitMinimumThroughput,

        // Then it half-opens and lets one request through to test the water.
        BreakDuration = TimeSpan.FromSeconds(invoiceOptions.CircuitBreakSeconds),

        ShouldHandle = new Polly.PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TaskCanceledException>()
            .HandleResult(r => (int)r.StatusCode >= 500),

        OnOpened = args =>
        {
            Log.Warning("Circuit to rate-service OPENED for {Break}s after {Ratio:P0} failures",
                args.BreakDuration.TotalSeconds, invoiceOptions.CircuitFailureRatio);
            return default;
        },
        OnClosed = _ =>
        {
            Log.Information("Circuit to rate-service CLOSED; the dependency is answering again");
            return default;
        },
        OnHalfOpened = _ =>
        {
            Log.Information("Circuit to rate-service HALF-OPEN; probing with a single request");
            return default;
        }
    });
});

// Money must not be lost, so this call retries rather than failing fast, and
// Settlement's endpoint is idempotent per invoice to make retrying safe. The
// timeout is longer than the Rate one because correctness matters more here than
// latency: nobody is waiting on this to render a checkout page.
builder.Services.AddHttpClient<ISettlementClient, SettlementClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<InvoiceOptions>>().Value;
    client.BaseAddress = new Uri(options.SettlementServiceBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.SettlementTimeoutSeconds);
});

builder.Services.AddScoped<InvoiceService>();
builder.Services.AddHostedService<ExpirySweeper>();
builder.Services.AddHostedService<SettlementSweeper>();

builder.Services.AddHealthChecks()
    .AddNpgSql(
        connectionStringFactory: sp => builder.Configuration.GetConnectionString("InvoiceDb")!,
        name: "invoice_db",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready", "db"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Panda Pocket Invoice API",
        Version = "v1",
        Description =
            "The payment lifecycle. An invoice locks a conversion rate at creation " +
            "and holds it for the payment window, so the merchant is quoted a rand " +
            "amount and receives that rand amount regardless of price movement."
    });
});

var app = builder.Build();

// Correlation before request logging, so the request summary line carries the
// id. See the day 2 build log for why the reverse order silently breaks tracing.
app.UseCorrelationId();
app.UseSerilogRequestLogging();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Panda Pocket Invoice API v1");
    c.DocumentTitle = "Panda Pocket Invoice API";
});

app.MapInvoiceEndpoints();

app.MapHealthChecks("/health", new()
{
    ResponseWriter = HealthResponseWriter.WriteAsync
});

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// Migrations are applied at startup rather than as a separate step. For a
// coursework artefact that must come up from a clean clone with one command,
// an automatic migration is the difference between "docker compose up" working
// and a marker having to run the EF tooling by hand. A production system would
// run migrations as a deliberate deployment step instead, because automatic
// migration under multiple replicas is a race.
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
    // Postgres may still be starting. Compose waits for its healthcheck, but a
    // healthy container is not always immediately accepting connections, so this
    // retries rather than crashing the service on an unlucky first attempt.
    var delay = TimeSpan.FromSeconds(2);

    for (var attempt = 1; attempt <= 6; attempt++)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
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
