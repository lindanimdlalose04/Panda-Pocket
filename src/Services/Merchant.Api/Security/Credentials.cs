using System.Security.Cryptography;
using System.Text;

namespace PandaPocket.Services.Merchant.Security;

/// <summary>
/// Generation and verification of API keys.
/// </summary>
public static class ApiKeys
{
    public const string Prefix = "pk_live_";

    /// <summary>Characters shown to the merchant after creation, including the prefix.</summary>
    private const int VisiblePrefixLength = 16;

    /// <summary>
    /// Creates a key with 256 bits of entropy from a cryptographic RNG.
    ///
    /// RandomNumberGenerator, not System.Random. Random is a deterministic
    /// pseudo-random generator seeded from the clock: predict the seed and you
    /// predict every key it will ever produce. That is fine for simulating a
    /// Bitcoin price, and catastrophic for issuing credentials.
    /// </summary>
    public static (string PlainText, string Hash, string KeyPrefix) Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);

        // Base64url so the key is safe in a header or a URL without escaping.
        var body = Convert.ToBase64String(bytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var plainText = Prefix + body;

        return (plainText, Hash(plainText), plainText[..VisiblePrefixLength]);
    }

    /// <summary>SHA-256, hex encoded. See ApiKey.KeyHash for why a fast hash is correct here.</summary>
    public static string Hash(string plainText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainText))).ToLowerInvariant();
}

/// <summary>
/// PBKDF2 password hashing, using only what the framework provides.
/// </summary>
public static class Passwords
{
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        // The iteration count travels with the hash so it can be raised later
        // without invalidating existing passwords.
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iterations)) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // Fixed-time comparison. A plain sequence equality returns as soon as it
        // finds a difference, so the time it takes leaks how many leading bytes
        // were correct, which is enough to reconstruct a hash byte by byte.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
