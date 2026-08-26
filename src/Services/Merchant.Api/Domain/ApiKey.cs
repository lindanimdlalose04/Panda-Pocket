namespace PandaPocket.Services.Merchant.Domain;

/// <summary>
/// A credential a merchant's server uses to call the API.
///
/// The plaintext key is never stored. Only its hash is, so a dump of this table
/// yields nothing usable: an attacker holding key_hash cannot reverse it into a
/// working credential. The key is returned to the merchant exactly once, at
/// creation, and after that only the prefix is ever shown.
///
/// That is the same reasoning as storing password hashes, and it is why the
/// "show key again" button that merchants always ask for cannot exist. Losing a
/// key means issuing a new one and revoking the old.
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }

    /// <summary>
    /// SHA-256 of the plaintext key, hex encoded.
    ///
    /// A fast hash is the right choice here, unlike for passwords. API keys are
    /// 256 bits of cryptographic randomness, so there is no dictionary to attack
    /// and no need for a deliberately slow function; and this hash is computed
    /// on every authenticated request, where a PBKDF2 with 100 000 iterations
    /// would add real latency to every single API call. Passwords, which are
    /// low entropy and human chosen, use PBKDF2 instead. See <see cref="User"/>.
    /// </summary>
    public required string KeyHash { get; set; }

    /// <summary>
    /// The first few characters, kept in clear so a merchant can tell their keys
    /// apart in a list without the full value ever being retrievable.
    /// </summary>
    public required string KeyPrefix { get; set; }

    /// <summary>A human label, for example "production server" or "staging".</summary>
    public required string Label { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Set rather than deleting the row. A revoked key that vanishes takes its
    /// history with it, and "which key was used for this transaction" is a
    /// question worth being able to answer after the key is gone.
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Updated on use. A key that has never been used, or has not been used in
    /// months, is a candidate for revocation, and a sudden change in the pattern
    /// is a SOC signal.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    public Merchant? Merchant { get; set; }

    public bool IsActive => RevokedAt is null;
}
