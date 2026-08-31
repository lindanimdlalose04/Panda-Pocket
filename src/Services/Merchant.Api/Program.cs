using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using PandaPocket.Services.Merchant.Configuration;
using PandaPocket.Services.Merchant.Domain;
using PandaPocket.Services.Merchant.Endpoints;
using PandaPocket.Services.Merchant.Persistence;
using PandaPocket.Services.Merchant.Security;
using PandaPocket.Shared.Contracts.Discovery;
using PandaPocket.Shared.Contracts.Observability;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "Merchant";

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithProperty("Service", ServiceName)
    .WriteTo.Console()
    .WriteTo.Seq(context.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341"));

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<DemoSeedOptions>(builder.Configuration.GetSection(DemoSeedOptions.SectionName));

builder.Services.AddDbContext<MerchantDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("MerchantDb")));

// Announce this instance to Consul on startup and remove it on shutdown,
// with a health check Consul polls. Disabled unless configuration turns it
// on, so the service still runs outside Compose.
builder.Services.AddServiceRegistry(builder.Configuration);

builder.Services.AddScoped<MerchantService>();
builder.Services.AddSingleton<JwtIssuer>();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            // Every one of these is on deliberately. Turning off issuer or
            // audience validation is a common shortcut that lets a token minted
            // for another system be replayed against this one, and disabling
            // lifetime validation makes a stolen token valid for ever.
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),

            // Default is five minutes of leeway, which quietly extends every
            // token past its stated expiry.
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddHealthChecks()
    .AddNpgSql(
        connectionStringFactory: sp => builder.Configuration.GetConnectionString("MerchantDb")!,
        name: "merchant_db",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready", "db"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Panda Pocket Merchant API",
        Version = "v1",
        Description =
            "Merchant accounts, dashboard authentication and API keys. Keys are " +
            "stored only as SHA-256 hashes and the plaintext is returned exactly " +
            "once, at creation."
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the token returned by POST /api/auth/login."
    });

    // Swashbuckle 10 takes a factory here rather than the requirement itself,
    // because the requirement may need to reference the document being built.
    c.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        { new OpenApiSecuritySchemeReference("Bearer"), new List<string>() }
    });
});

var app = builder.Build();

app.UseCorrelationId();
app.UseSerilogRequestLogging();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Panda Pocket Merchant API v1");
    c.DocumentTitle = "Panda Pocket Merchant API";
});

app.UseAuthentication();
app.UseAuthorization();

app.MapMerchantEndpoints();

app.MapHealthChecks("/health", new()
{
    ResponseWriter = HealthResponseWriter.WriteAsync
});

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

await InitialiseDatabaseAsync(app);

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

static async Task InitialiseDatabaseAsync(WebApplication app)
{
    var delay = TimeSpan.FromSeconds(2);

    for (var attempt = 1; attempt <= 6; attempt++)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();
            await db.Database.MigrateAsync();

            var seedOptions = scope.ServiceProvider.GetRequiredService<IOptions<DemoSeedOptions>>().Value;
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<MerchantDbContext>>();
            await DemoSeeder.SeedAsync(db, seedOptions, logger);

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
