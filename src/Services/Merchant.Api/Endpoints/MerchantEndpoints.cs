using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PandaPocket.Services.Merchant.Contracts;
using PandaPocket.Services.Merchant.Domain;
using PandaPocket.Services.Merchant.Persistence;
using PandaPocket.Services.Merchant.Security;
using PandaPocket.Shared.Contracts.Observability;

namespace PandaPocket.Services.Merchant.Endpoints;

public static class MerchantEndpoints
{
    public static IEndpointRouteBuilder MapMerchantEndpoints(this IEndpointRouteBuilder app)
    {
        MapAuth(app);
        MapMerchants(app);
        MapApiKeys(app);
        MapInternal(app);
        return app;
    }

    // -----------------------------------------------------------------------
    // Authentication
    // -----------------------------------------------------------------------
    private static void MapAuth(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", async (
            LoginRequest request, MerchantService service, JwtIssuer jwt, HttpContext ctx, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem) return problem;

            var (user, error) = await service.AuthenticateAsync(
                request.Email, request.Password, ctx.GetCorrelationId(), ct);

            if (user is null)
            {
                return Results.Problem(title: "Authentication failed", detail: error,
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var (token, expiresAt) = jwt.Issue(user);
            return Results.Ok(new LoginResponse(token, expiresAt, user.MerchantId, user.Email, user.Role));
        })
        .WithName("Login")
        .WithSummary("Exchange dashboard credentials for a short-lived JWT")
        .Produces<LoginResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    // -----------------------------------------------------------------------
    // Merchants
    // -----------------------------------------------------------------------
    private static void MapMerchants(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/merchants").WithTags("Merchants");

        // Sign-up is necessarily anonymous: there is no account to authenticate
        // against yet.
        group.MapPost("/", async (CreateMerchantRequest request, MerchantService service, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem) return problem;

            var (merchant, error) = await service.CreateAsync(request, ct);

            return merchant is null
                ? Results.Problem(title: "Could not create merchant", detail: error, statusCode: StatusCodes.Status409Conflict)
                : Results.Created($"/api/merchants/{merchant.Id}", MerchantResponse.From(merchant));
        })
        .WithName("CreateMerchant")
        .WithSummary("Register a merchant and its owner account")
        .Produces<MerchantResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}", async (Guid id, MerchantDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (Forbidden(principal, id) is { } forbidden) return forbidden;

            var merchant = await db.Merchants.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);

            return merchant is null
                ? Results.Problem(title: "Merchant not found", statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(MerchantResponse.From(merchant));
        })
        .RequireAuthorization()
        .WithName("GetMerchant")
        .Produces<MerchantResponse>()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", async (
            Guid id, UpdateMerchantRequest request, MerchantService service,
            ClaimsPrincipal principal, HttpContext ctx, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem) return problem;
            if (Forbidden(principal, id) is { } forbidden) return forbidden;

            var (merchant, error) = await service.UpdateAsync(id, request, ctx.GetCorrelationId(), ct);

            return merchant is null
                ? Results.Problem(title: "Merchant not found", detail: error, statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(MerchantResponse.From(merchant));
        })
        .RequireAuthorization()
        .WithName("UpdateMerchant")
        .WithSummary("Update business details. Changing the webhook URL raises a security event.")
        .Produces<MerchantResponse>()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // -----------------------------------------------------------------------
    // API keys
    // -----------------------------------------------------------------------
    private static void MapApiKeys(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("API keys").RequireAuthorization();

        group.MapPost("/merchants/{id:guid}/api-keys", async (
            Guid id, CreateApiKeyRequest request, MerchantService service, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem) return problem;
            if (Forbidden(principal, id) is { } forbidden) return forbidden;

            var (key, error) = await service.CreateApiKeyAsync(id, request.Label, ct);

            return key is null
                ? Results.Problem(title: "Could not create key", detail: error, statusCode: StatusCodes.Status404NotFound)
                : Results.Created($"/api/merchants/{id}/api-keys/{key.Id}", key);
        })
        .WithName("CreateApiKey")
        .WithSummary("Issue an API key. The plaintext is returned once and never again.")
        .Produces<ApiKeyCreatedResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/merchants/{id:guid}/api-keys", async (
            Guid id, MerchantDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (Forbidden(principal, id) is { } forbidden) return forbidden;

            var keys = await db.ApiKeys.AsNoTracking()
                .Where(k => k.MerchantId == id)
                .OrderByDescending(k => k.CreatedAt)
                .ToListAsync(ct);

            // Prefixes only. There is no code path anywhere that returns a key.
            return Results.Ok(keys.Select(ApiKeyResponse.From).ToList());
        })
        .WithName("ListApiKeys")
        .Produces<List<ApiKeyResponse>>()
        .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapDelete("/api-keys/{keyId:guid}", async (
            Guid keyId, MerchantService service, MerchantDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == keyId, ct);
            if (key is null) return Results.Problem(title: "Key not found", statusCode: StatusCodes.Status404NotFound);

            // Checked against the key's owner, not the caller's assertion. Without
            // this, any authenticated merchant could revoke any other merchant's
            // keys simply by knowing an id.
            if (Forbidden(principal, key.MerchantId) is { } forbidden) return forbidden;

            return await service.RevokeApiKeyAsync(keyId, ct)
                ? Results.NoContent()
                : Results.Problem(title: "Key already revoked", statusCode: StatusCodes.Status409Conflict);
        })
        .WithName("RevokeApiKey")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // -----------------------------------------------------------------------
    // Internal
    // -----------------------------------------------------------------------
    private static void MapInternal(IEndpointRouteBuilder app)
    {
        // Called by the gateway on every authenticated request. Not routed
        // through Ocelot, so it is reachable only from inside the compose
        // network. A public /keys/validate would be an oracle letting anyone
        // test guessed keys at will.
        app.MapPost("/api/internal/keys/validate", async (
            ValidateKeyRequest request, MerchantService service, HttpContext ctx, CancellationToken ct) =>
        {
            var result = await service.ValidateKeyAsync(request.ApiKey, ctx.GetCorrelationId(), ct);
            return Results.Ok(result);
        })
        .WithTags("Internal")
        .WithName("ValidateApiKey")
        .WithSummary("Internal: resolve an API key to a merchant. Not exposed through the gateway.")
        .Produces<ValidateKeyResponse>();

        // Read by Settlement, which needs three things it does not own: the fee
        // percentage, the webhook URL and the signing secret. Copying those into
        // settlement_db would be faster and would go stale the moment a merchant
        // changed their webhook, so they are fetched from the owner instead.
        //
        // This returns the webhook secret, which is why it is internal-only and
        // not routed through Ocelot. Exposing it publicly would let anyone
        // forge a signed "you have been paid" callback.
        app.MapGet("/api/internal/merchants/{id:guid}", async (
            Guid id, MerchantDbContext db, CancellationToken ct) =>
        {
            var m = await db.Merchants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

            return m is null
                ? Results.Problem(title: "Merchant not found", statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(new
                {
                    id = m.Id,
                    businessName = m.BusinessName,
                    feePercent = m.FeePercent,
                    webhookUrl = m.WebhookUrl,
                    webhookSecret = m.WebhookSecret
                });
        })
        .WithTags("Internal")
        .WithName("GetMerchantInternal")
        .WithSummary("Internal: merchant fee and webhook configuration. Not exposed through the gateway.");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// 403, not 404, when a merchant asks for another merchant's resource.
    ///
    /// The resource exists and the caller is authenticated; what they lack is
    /// authorisation, and that is precisely what Forbidden means. Some systems
    /// prefer 404 here to avoid confirming existence, but these ids are
    /// unguessable v4 GUIDs, so there is no enumeration to protect against and
    /// the honest code is more useful to an integrator.
    /// </summary>
    private static IResult? Forbidden(ClaimsPrincipal principal, Guid merchantId)
    {
        var claim = principal.FindFirst(JwtIssuer.MerchantIdClaim)?.Value;

        if (Guid.TryParse(claim, out var callerMerchantId) && callerMerchantId == merchantId)
        {
            return null;
        }

        return Results.Problem(
            title: "Forbidden",
            detail: "This account may only access its own merchant record.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static IResult? Validate<T>(T model) where T : class
    {
        var context = new ValidationContext(model);
        var results = new List<ValidationResult>();

        if (Validator.TryValidateObject(model, context, results, validateAllProperties: true)) return null;

        var errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty), (r, n) => (n, r.ErrorMessage))
            .GroupBy(x => x.n, x => x.ErrorMessage ?? "Invalid")
            .ToDictionary(g => g.Key, g => g.ToArray());

        return Results.ValidationProblem(errors);
    }
}
