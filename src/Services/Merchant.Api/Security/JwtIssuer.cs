using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PandaPocket.Services.Merchant.Configuration;
using PandaPocket.Services.Merchant.Domain;

namespace PandaPocket.Services.Merchant.Security;

/// <summary>
/// Issues dashboard tokens.
///
/// The merchant id is carried as a claim inside the signed token rather than
/// being read from the request. That is the whole point: a claim cannot be
/// altered without invalidating the signature, so a user cannot ask for another
/// merchant's data by changing a parameter.
/// </summary>
public sealed class JwtIssuer(IOptions<JwtOptions> options)
{
    public const string MerchantIdClaim = "merchant_id";

    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTime ExpiresAt) Issue(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // Short lived on purpose. This token authorises dashboard actions
        // including issuing API keys, so a copy lifted from a browser should stop
        // working quickly. Servers use API keys, which are long lived by design,
        // because a server cannot re-enter a password every hour.
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.ExpiryMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(MerchantIdClaim, user.MerchantId.ToString()),
            new(ClaimTypes.Role, user.Role)
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
