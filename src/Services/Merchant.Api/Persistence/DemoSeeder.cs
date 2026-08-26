using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PandaPocket.Services.Merchant.Configuration;
using PandaPocket.Services.Merchant.Domain;
using PandaPocket.Services.Merchant.Security;

namespace PandaPocket.Services.Merchant.Persistence;

/// <summary>
/// Creates one merchant with a known API key so the stack is usable immediately
/// after "docker compose up" with nothing to configure by hand.
///
/// The merchant id is fixed to the same GUID the Invoice service used as its
/// placeholder before authentication existed, so invoices created during
/// development still belong to a real merchant rather than being orphaned.
///
/// Idempotent: safe to run on every start.
/// </summary>
public static class DemoSeeder
{
    public static async Task SeedAsync(MerchantDbContext db, DemoSeedOptions options, ILogger logger, CancellationToken ct = default)
    {
        if (!options.Enabled) return;

        if (await db.Merchants.AnyAsync(m => m.Id == options.MerchantId, ct))
        {
            logger.LogInformation("Demo merchant already present, skipping seed");
            return;
        }

        var now = DateTime.UtcNow;
        var email = options.Email.ToLowerInvariant();

        var merchant = new Domain.Merchant
        {
            Id = options.MerchantId,
            BusinessName = options.BusinessName,
            Email = email,
            FeePercent = 1.0m,

            // Deliberately unreachable. Day 5 points a webhook here to watch the
            // retry and backoff behaviour produce a climbing attempt count.
            WebhookUrl = "http://localhost:9999/webhooks/pandapocket",
            WebhookSecret = ApiKeys.Generate().PlainText,
            Status = MerchantStatus.Active,
            CreatedAt = now
        };

        merchant.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            MerchantId = merchant.Id,
            Email = email,
            PasswordHash = Passwords.Hash(options.Password),
            Role = UserRoles.Owner,
            CreatedAt = now
        });

        // The seeded key is hashed exactly like any other. The plaintext is
        // known only because it came from configuration, not because the system
        // stores it anywhere.
        merchant.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(),
            MerchantId = merchant.Id,
            KeyHash = ApiKeys.Hash(options.ApiKey),
            KeyPrefix = options.ApiKey[..16],
            Label = "demo client",
            CreatedAt = now
        });

        db.Merchants.Add(merchant);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Seeded demo merchant {MerchantId} ({Business}) with login {Email} and one API key",
            merchant.Id, merchant.BusinessName, email);
    }
}
