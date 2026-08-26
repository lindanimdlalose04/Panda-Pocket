using Microsoft.EntityFrameworkCore;
using PandaPocket.Services.Merchant.Contracts;
using PandaPocket.Services.Merchant.Persistence;
using PandaPocket.Services.Merchant.Security;
using PandaPocket.Shared.Contracts.Soc;

namespace PandaPocket.Services.Merchant.Domain;

public sealed class MerchantService(MerchantDbContext db, ILogger<MerchantService> logger)
{
    public async Task<(Merchant? Merchant, string? Error)> CreateAsync(CreateMerchantRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        if (await db.Merchants.AnyAsync(m => m.Email == email, ct))
        {
            return (null, $"A merchant with email '{email}' already exists.");
        }

        var now = DateTime.UtcNow;

        var merchant = new Merchant
        {
            Id = Guid.NewGuid(),
            BusinessName = request.BusinessName.Trim(),
            Email = email,
            FeePercent = request.FeePercent,
            WebhookUrl = request.WebhookUrl,

            // Generated now rather than on first webhook, so the merchant can
            // configure signature verification before any callback arrives.
            WebhookSecret = ApiKeys.Generate().PlainText,
            Status = MerchantStatus.Active,
            CreatedAt = now
        };

        merchant.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            MerchantId = merchant.Id,
            Email = email,
            PasswordHash = Passwords.Hash(request.Password),
            Role = UserRoles.Owner,
            CreatedAt = now
        });

        db.Merchants.Add(merchant);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Merchant {MerchantId} created for {Email}", merchant.Id, email);
        return (merchant, null);
    }

    public async Task<(Merchant? Merchant, string? Error)> UpdateAsync(
        Guid id, UpdateMerchantRequest request, string correlationId, CancellationToken ct)
    {
        var merchant = await db.Merchants.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (merchant is null) return (null, "Merchant not found.");

        if (request.BusinessName is { } name) merchant.BusinessName = name.Trim();
        if (request.FeePercent is { } fee) merchant.FeePercent = fee;

        if (request.WebhookUrl is { } url && url != merchant.WebhookUrl)
        {
            var previous = merchant.WebhookUrl;
            merchant.WebhookUrl = url;

            // Changing where payment notifications are delivered is a classic
            // account takeover step: take over the account, repoint the webhook,
            // and the real merchant stops hearing about payments while the
            // attacker starts. It is logged as a security event for that reason,
            // with both URLs, so the SOC layer can correlate it with the login
            // that preceded it.
            LogSoc(SocEventType.WebhookUrlChanged, SocSeverity.Warning, correlationId, merchant.Id, new()
            {
                ["previousUrl"] = previous,
                ["newUrl"] = url
            });
        }

        merchant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return (merchant, null);
    }

    // -----------------------------------------------------------------------
    // API keys
    // -----------------------------------------------------------------------
    public async Task<(ApiKeyCreatedResponse? Key, string? Error)> CreateApiKeyAsync(
        Guid merchantId, string label, CancellationToken ct)
    {
        var merchant = await db.Merchants.FirstOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant is null) return (null, "Merchant not found.");

        var (plainText, hash, prefix) = ApiKeys.Generate();

        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            MerchantId = merchantId,
            KeyHash = hash,
            KeyPrefix = prefix,
            Label = label.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("API key {KeyId} ({Prefix}...) issued for merchant {MerchantId}",
            key.Id, prefix, merchantId);

        // The plaintext is returned here and then goes out of scope for ever.
        // Only the hash was persisted.
        return (new ApiKeyCreatedResponse(key.Id, plainText, prefix, key.Label, key.CreatedAt), null);
    }

    public async Task<bool> RevokeApiKeyAsync(Guid keyId, CancellationToken ct)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.RevokedAt == null, ct);
        if (key is null) return false;

        // Marked, not deleted. "Which key signed this transaction" stays
        // answerable after the key is retired.
        key.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("API key {KeyId} revoked", keyId);
        return true;
    }

    /// <summary>
    /// The gateway calls this on every authenticated request.
    ///
    /// The lookup is by hash, so the plaintext key is never compared against
    /// anything stored and never appears in a query. A failed validation says
    /// only that the key is invalid: distinguishing "no such key" from "revoked"
    /// would tell an attacker which of their guesses had once been real.
    /// </summary>
    public async Task<ValidateKeyResponse> ValidateKeyAsync(string plainTextKey, string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plainTextKey) || !plainTextKey.StartsWith(ApiKeys.Prefix, StringComparison.Ordinal))
        {
            LogSoc(SocEventType.ApiKeyInvalid, SocSeverity.Warning, correlationId, null, new()
            {
                ["reason"] = "malformed"
            });
            return new ValidateKeyResponse(false, null, null, null, "Invalid API key.");
        }

        var hash = ApiKeys.Hash(plainTextKey);

        var key = await db.ApiKeys
            .Include(k => k.Merchant)
            .FirstOrDefaultAsync(k => k.KeyHash == hash, ct);

        if (key is null || !key.IsActive || key.Merchant is null)
        {
            LogSoc(SocEventType.ApiKeyInvalid, SocSeverity.Warning, correlationId, key?.MerchantId, new()
            {
                ["reason"] = key is null ? "unknown" : "revoked",
                ["keyPrefix"] = key?.KeyPrefix
            });
            return new ValidateKeyResponse(false, null, null, null, "Invalid API key.");
        }

        if (key.Merchant.Status != MerchantStatus.Active)
        {
            LogSoc(SocEventType.AuthFailed, SocSeverity.Warning, correlationId, key.MerchantId, new()
            {
                ["reason"] = "merchant_suspended"
            });
            return new ValidateKeyResponse(false, null, null, null, "Merchant account is not active.");
        }

        // Written without awaiting a full save on the request path would be
        // nicer still, but at this scale one extra update is not worth the
        // complexity of a background writer.
        key.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new ValidateKeyResponse(true, key.MerchantId, key.Merchant.BusinessName, key.Merchant.FeePercent, null);
    }

    // -----------------------------------------------------------------------
    // Authentication
    // -----------------------------------------------------------------------
    public async Task<(User? User, string? Error)> AuthenticateAsync(
        string email, string password, string correlationId, CancellationToken ct)
    {
        var normalised = email.Trim().ToLowerInvariant();

        var user = await db.Users
            .Include(u => u.Merchant)
            .FirstOrDefaultAsync(u => u.Email == normalised, ct);

        // The password is verified even when no user was found, against a dummy
        // hash. Returning immediately on an unknown email makes the response
        // measurably faster than for a known one, which turns login into an
        // oracle for enumerating valid accounts.
        var storedHash = user?.PasswordHash ?? DummyHash.Value;
        var passwordOk = Passwords.Verify(password, storedHash);

        if (user is null || !passwordOk)
        {
            LogSoc(SocEventType.AuthFailed, SocSeverity.Warning, correlationId, user?.MerchantId, new()
            {
                ["email"] = normalised,
                ["reason"] = user is null ? "unknown_user" : "bad_password"
            });

            // One message for both cases, for the same reason.
            return (null, "Invalid email or password.");
        }

        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return (user, null);
    }

    /// <summary>
    /// A real PBKDF2 hash of a random value, computed once. Verifying against it
    /// costs the same as verifying a genuine password, which is the point.
    /// </summary>
    private static class DummyHash
    {
        public static readonly string Value = Passwords.Hash(Guid.NewGuid().ToString());
    }

    private void LogSoc(string eventType, string severity, string correlationId,
        Guid? merchantId, Dictionary<string, object?> metadata)
    {
        var soc = SocEvent.Create(eventType, severity, correlationId, merchantId, null, metadata);
        logger.LogWarning("SOC {EventType} {@SocEvent}", eventType, soc);
    }
}
