namespace PandaPocket.Shared.Contracts.Soc;

/// <summary>
/// The security event catalogue. These strings become node and edge labels in
/// the Neo4j knowledge graph, so they are fixed constants rather than free text
/// at each call site. A typo at a call site would otherwise become a new node
/// label that quietly never matches anything.
/// </summary>
public static class SocEventType
{
    // ---- Authentication and access ----------------------------------------
    public const string AuthFailed          = "AUTH_FAILED";
    public const string ApiKeyInvalid       = "API_KEY_INVALID";
    public const string ApiKeyIssued        = "API_KEY_ISSUED";
    public const string RateLimitExceeded   = "RATE_LIMIT_EXCEEDED";

    // ---- Object access -----------------------------------------------------
    /// <summary>
    /// A checkout page was requested for an invoice id that does not exist.
    ///
    /// The checkout GUID is the bearer token for a payment page, so a caller
    /// working through unknown ids is enumerating other merchants' invoices.
    /// One of these is meaningless; a burst from one address is not.
    /// </summary>
    public const string CheckoutEnumeration = "CHECKOUT_ENUMERATION";

    // ---- Payment lifecycle -------------------------------------------------
    public const string InvoiceCreated      = "INVOICE_CREATED";
    public const string PaymentConfirmed    = "PAYMENT_CONFIRMED";
    public const string PaymentUnderpaid    = "PAYMENT_UNDERPAID";
    public const string PaymentOnExpired    = "PAYMENT_ON_EXPIRED_INVOICE";
    public const string PaymentReplay       = "PAYMENT_REPLAY_ATTEMPT";

    // ---- Delivery and resilience -------------------------------------------
    public const string WebhookFailed       = "WEBHOOK_DELIVERY_FAILED";
    public const string CircuitOpened       = "CIRCUIT_OPENED";

    /// <summary>
    /// An invoice was priced from a cached rate because the Rate service could
    /// not be reached.
    ///
    /// Individually this is the system degrading gracefully, which is what it
    /// was built to do. In volume, while the price feed is down, it means
    /// invoices are being quoted against a stale market and the platform is
    /// carrying the difference. That is an economic exposure, not merely an
    /// availability blip.
    /// </summary>
    public const string RateFallbackUsed    = "RATE_FALLBACK_USED";

    /// <summary>A registry health transition, observed rather than self-reported.</summary>
    public const string ServiceHealthChanged = "SERVICE_HEALTH_CHANGED";

    // ---- Integrity ---------------------------------------------------------
    /// <summary>
    /// The stored merchant balance disagrees with the sum of its ledger.
    ///
    /// This should never fire. If it does, the books do not add up, which is
    /// either a defect or tampering, and either way warrants stopping payouts.
    /// </summary>
    public const string ReconcileMismatch   = "RECONCILE_MISMATCH";

    // ---- Account takeover indicators ---------------------------------------
    public const string WebhookUrlChanged   = "MERCHANT_WEBHOOK_URL_CHANGED";
}

/// <summary>
/// Five ordered levels. The specification's worked examples use HIGH and MEDIUM,
/// so this matches that vocabulary rather than inventing a parallel one.
///
/// Event severity and alert severity are deliberately different things. A single
/// <see cref="SocEventType.CheckoutEnumeration"/> is LOW, because one dead link
/// is meaningless; the rule that spots forty of them from one address in five
/// minutes raises a HIGH alert. Grading events by how alarming they are in
/// isolation is what lets a detection rule say something the events do not.
/// </summary>
public static class SocSeverity
{
    public const string Info     = "INFO";
    public const string Low      = "LOW";
    public const string Medium   = "MEDIUM";
    public const string High     = "HIGH";
    public const string Critical = "CRITICAL";

    /// <summary>Rank, for ordering and for "at least this severe" filters.</summary>
    public static int Rank(string? severity) => severity switch
    {
        Info     => 0,
        Low      => 1,
        Medium   => 2,
        High     => 3,
        Critical => 4,
        _        => 0
    };
}
