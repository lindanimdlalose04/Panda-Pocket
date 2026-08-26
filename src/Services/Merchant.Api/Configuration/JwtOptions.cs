namespace PandaPocket.Services.Merchant.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// HMAC signing key. Committed as a development value so the stack comes up
    /// from a clean clone, and overridden by an environment variable anywhere
    /// real. A committed signing key is exactly as good as no authentication at
    /// all, because anyone holding it can mint a token for any merchant; the
    /// report says so rather than pretending otherwise.
    /// </summary>
    public string SigningKey { get; init; } = "panda-pocket-development-signing-key-not-for-production-use";

    public string Issuer { get; init; } = "pandapocket";
    public string Audience { get; init; } = "pandapocket-dashboard";
    public int ExpiryMinutes { get; init; } = 60;
}

public sealed class DemoSeedOptions
{
    public const string SectionName = "DemoSeed";

    /// <summary>
    /// Creates a merchant with a known API key at startup so the client works
    /// from a clean clone with nothing to configure. Off in any real deployment.
    /// </summary>
    public bool Enabled { get; init; } = true;

    public string BusinessName { get; init; } = "Demo Coffee Shop";
    public string Email { get; init; } = "owner@democoffee.co.za";
    public string Password { get; init; } = "demo-password-123";

    /// <summary>
    /// A fixed key, so the browser client can be shipped knowing it, and so the
    /// demo is reproducible. Every other key in the system is 256 bits from a
    /// cryptographic RNG; this one is a deliberate exception for seeding, and it
    /// is documented as such rather than hidden.
    /// </summary>
    public string ApiKey { get; init; } = "pk_live_demo0000000000000000000000000000000000";

    public Guid MerchantId { get; init; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
}
