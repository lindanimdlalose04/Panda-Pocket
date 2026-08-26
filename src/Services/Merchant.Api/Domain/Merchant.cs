namespace PandaPocket.Services.Merchant.Domain;

/// <summary>
/// A business accepting crypto and receiving ZAR.
/// </summary>
public sealed class Merchant
{
    public Guid Id { get; set; }
    public required string BusinessName { get; set; }
    public required string Email { get; set; }

    /// <summary>
    /// The platform's cut, taken from each settlement. Around one percent
    /// against the two and a half to three and a half a card processor charges,
    /// which is the commercial argument for the whole product.
    /// </summary>
    public decimal FeePercent { get; set; } = 1.0m;

    /// <summary>Where signed payment notifications are POSTed.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Shared secret used to HMAC-sign webhook payloads, so the merchant can
    /// verify a callback genuinely came from us and was not forged by anyone who
    /// happened to learn their endpoint URL.
    /// </summary>
    public string? WebhookSecret { get; set; }

    public MerchantStatus Status { get; set; } = MerchantStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public List<ApiKey> ApiKeys { get; set; } = [];
    public List<User> Users { get; set; } = [];
}

public enum MerchantStatus
{
    Active,

    /// <summary>Suspended merchants keep their data but cannot transact.</summary>
    Suspended
}
