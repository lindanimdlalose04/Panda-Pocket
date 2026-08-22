namespace PandaPocket.Shared.Contracts;

/// <summary>
/// Header names shared by the gateway and every service. The correlation id is
/// generated once at the gateway and propagated on every downstream call, so
/// filtering Seq by a single id reconstructs one payment across four services.
/// </summary>
public static class CorrelationHeaders
{
    public const string CorrelationId = "X-Correlation-Id";
    public const string ApiKey        = "X-API-Key";

    /// <summary>Merchant id resolved by the gateway from a valid API key.</summary>
    public const string MerchantId    = "X-Merchant-Id";
}
