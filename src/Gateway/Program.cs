using PandaPocket.Gateway.Authentication;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;
using PandaPocket.Shared.Contracts.Observability;
using Serilog;

// ---------------------------------------------------------------------------
// The API gateway. One public entry point in front of every service.
//
// It routes by path to the right service, mints the correlation id that ties a
// request together across services, and serves the client. API key
// authentication is added on day 4 and rate limiting on day 6.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "Gateway";

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithProperty("Service", ServiceName)
    .WriteTo.Console()
    .WriteTo.Seq(context.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341"));

// ocelot.json holds the deployed routing, with compose service names as hosts.
// ocelot.Development.json repoints those at localhost when the gateway runs
// outside Docker. Loading the environment-specific file second lets it win.
builder.Configuration
    .AddJsonFile("ocelot.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"ocelot.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

builder.Services.AddOcelot(builder.Configuration);

// Used by the API key middleware to reach the Merchant service. The base
// address is a compose service name, resolved by Docker DNS like every other
// service-to-service call.
builder.Services.AddHttpClient("merchant", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:MerchantBaseUrl"] ?? "http://localhost:5001");

    // Short, because this sits in front of every authenticated request. A slow
    // Merchant service should produce a fast rejection, not a hung API.
    client.Timeout = TimeSpan.FromSeconds(5);
});

// Backs the short-lived cache of validated keys.
builder.Services.AddMemoryCache();

// The browser client is served from the gateway's own origin, so its fetch
// calls are same-origin and no CORS configuration is needed. That is a
// deliberate simplification: the alternative is opening CORS on four services.
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

app.UseCors();

// The gateway is where a correlation id is born. Everything downstream finds
// one already on the request, and Ocelot forwards it because RequestIdKey in
// ocelot.json names the same header.
app.UseCorrelationId();
app.UseSerilogRequestLogging();

// Serve the client from wwwroot. Visiting the gateway root gives a working
// checkout rather than a 404.
app.UseDefaultFiles();
app.UseStaticFiles();

// Authentication sits between the static files and Ocelot: the client itself is
// public, everything it calls is not. This middleware also strips any inbound
// X-Merchant-Id, which is what allows downstream services to trust that header.
app.UseApiKeyAuthentication();

// The gateway's own liveness.
//
// Written as a Map branch rather than app.MapGet, and this distinction matters.
// Ocelot is terminal middleware: when no route matches it returns 404 itself
// rather than calling the next middleware, so endpoint routing never runs and a
// MapGet("/health") is silently unreachable. A Map branch short-circuits before
// Ocelot is ever reached. The same reasoning puts UseStaticFiles above.
//
// It deliberately does not aggregate downstream health: a gateway reporting
// itself unhealthy because one service is down would be pulled out of service by
// an orchestrator exactly when it is still perfectly able to route to the
// others that are working.
app.Map("/health", branch => branch.Run(async context =>
{
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new
    {
        status = "Healthy",
        service = ServiceName,
        time = DateTime.UtcNow
    });
}));

// Ocelot is terminal middleware and must be registered last: it matches the
// request against its routes and forwards, so anything after it never runs.
await app.UseOcelot();

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
