using System.ComponentModel.DataAnnotations;

namespace PandaPocket.Services.Merchant.Contracts;

public sealed class CreateMerchantRequest
{
    [Required, StringLength(200, MinimumLength = 2)]
    public string BusinessName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(200, MinimumLength = 8, ErrorMessage = "password must be at least 8 characters.")]
    public string Password { get; set; } = string.Empty;

    [Range(0, 10, ErrorMessage = "feePercent must be between 0 and 10.")]
    public decimal FeePercent { get; set; } = 1.0m;

    [Url, StringLength(500)]
    public string? WebhookUrl { get; set; }
}

public sealed class UpdateMerchantRequest
{
    [StringLength(200, MinimumLength = 2)]
    public string? BusinessName { get; set; }

    [Range(0, 10)]
    public decimal? FeePercent { get; set; }

    [Url, StringLength(500)]
    public string? WebhookUrl { get; set; }
}

public sealed class LoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

public sealed class CreateApiKeyRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string Label { get; set; } = string.Empty;
}

public sealed class ValidateKeyRequest
{
    [Required]
    public string ApiKey { get; set; } = string.Empty;
}

public sealed record MerchantResponse(
    Guid Id, string BusinessName, string Email, decimal FeePercent,
    string? WebhookUrl, string Status, DateTime CreatedAt)
{
    public static MerchantResponse From(Domain.Merchant m) =>
        new(m.Id, m.BusinessName, m.Email, m.FeePercent, m.WebhookUrl, m.Status.ToString(), m.CreatedAt);
}

/// <summary>
/// Returned once, at creation, and never again. <see cref="ApiKey"/> holds the
/// only copy of the plaintext the merchant will ever see.
/// </summary>
public sealed record ApiKeyCreatedResponse(
    Guid Id, string ApiKey, string KeyPrefix, string Label, DateTime CreatedAt,
    string Warning = "Store this key now. It is hashed on the server and cannot be shown again.");

/// <summary>The safe view: prefix only, never the key itself.</summary>
public sealed record ApiKeyResponse(
    Guid Id, string KeyPrefix, string Label, DateTime CreatedAt,
    DateTime? RevokedAt, DateTime? LastUsedAt, bool IsActive)
{
    public static ApiKeyResponse From(Domain.ApiKey k) =>
        new(k.Id, k.KeyPrefix + "...", k.Label, k.CreatedAt, k.RevokedAt, k.LastUsedAt, k.IsActive);
}

public sealed record LoginResponse(string Token, DateTime ExpiresAt, Guid MerchantId, string Email, string Role);

/// <summary>
/// The gateway's answer to "is this key real, and whose is it".
///
/// Deliberately minimal. The gateway needs an identity and a fee, not a merchant
/// record, and sending less over an internal hop means less to leak.
/// </summary>
public sealed record ValidateKeyResponse(
    bool Valid, Guid? MerchantId, string? BusinessName, decimal? FeePercent, string? Reason);
