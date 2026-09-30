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

    /// <summary>
    /// The address the request actually came from, stamped by the gateway.
    ///
    /// A service behind the gateway sees the gateway's container address on
    /// every request, so without this each service recorded 172.18.0.11 as the
    /// source of everything. Detection rules count failures per address, so all
    /// callers collapsed into one and the rules were counting nothing.
    ///
    /// Stripped from inbound requests before it is set, for the same reason
    /// X-Merchant-Id is: otherwise a caller could name any address it liked and
    /// attribute its own attempts to somebody else.
    /// </summary>
    public const string ForwardedFor  = "X-Forwarded-For";
}
