namespace PandaPocket.Services.Merchant.Domain;

/// <summary>
/// A human who signs in to the merchant dashboard.
///
/// Deliberately separate from <see cref="ApiKey"/>, because the two authenticate
/// different things and deserve different treatment. A user is a person typing a
/// password into a browser and gets a short-lived JWT. An API key is a server
/// calling an API and is long-lived. Conflating them would mean either giving
/// servers passwords or giving people non-expiring credentials.
/// </summary>
public sealed class User
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }
    public required string Email { get; set; }

    /// <summary>
    /// PBKDF2-SHA256 with a per-user random salt, stored as
    /// iterations.salt.hash.
    ///
    /// Slow on purpose, unlike the API key hash. Passwords are low entropy and
    /// human chosen, so an attacker with the table can run a dictionary against
    /// them; the iteration count is what makes that expensive. This is computed
    /// once per login rather than once per request, so the cost is affordable.
    /// </summary>
    public required string PasswordHash { get; set; }

    public string Role { get; set; } = UserRoles.Owner;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public Merchant? Merchant { get; set; }
}

public static class UserRoles
{
    public const string Owner = "owner";
    public const string Staff = "staff";
}
