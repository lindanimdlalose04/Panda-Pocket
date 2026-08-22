namespace PandaPocket.Shared.Contracts.Soc;

/// <summary>
/// The security event catalogue. These strings become node and edge labels when
/// the SOC and Neo4j knowledge graph layer is added in the next phase, so they
/// are fixed constants rather than free text at each call site.
/// </summary>
public static class SocEventType
{
    // Authentication and access
    public const string AuthFailed          = "AUTH_FAILED";
    public const string ApiKeyInvalid       = "API_KEY_INVALID";
    public const string RateLimitExceeded   = "RATE_LIMIT_EXCEEDED";

    // Payment lifecycle
    public const string InvoiceCreated      = "INVOICE_CREATED";
    public const string PaymentConfirmed    = "PAYMENT_CONFIRMED";
    public const string PaymentUnderpaid    = "PAYMENT_UNDERPAID";
    public const string PaymentOnExpired    = "PAYMENT_ON_EXPIRED_INVOICE";
    public const string PaymentReplay       = "PAYMENT_REPLAY_ATTEMPT";

    // Delivery and resilience
    public const string WebhookFailed       = "WEBHOOK_DELIVERY_FAILED";
    public const string CircuitOpened       = "CIRCUIT_OPENED";

    // Account takeover indicators
    public const string WebhookUrlChanged   = "MERCHANT_WEBHOOK_URL_CHANGED";
}

public static class SocSeverity
{
    public const string Info     = "INFO";
    public const string Warning  = "WARNING";
    public const string Critical = "CRITICAL";
}
