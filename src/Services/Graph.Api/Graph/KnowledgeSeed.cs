namespace PandaPocket.Services.Graph.Graph;

/// <summary>
/// The curated security knowledge: the third of the three sources the
/// specification requires.
///
/// Hand written, and small on purpose. Lecture 9 is explicit that CVE feeds and
/// automated extraction from long reports are unnecessary here, and that three
/// threats, three weaknesses, three controls and three procedures is the
/// expected scale. This covers six of each because the system has six detection
/// rules and leaving three of them pointing at nothing would be worse than a
/// smaller catalogue that is complete.
///
/// Two honesty rules apply throughout:
///
/// Every reference that exists is real and carries its source URL, so a claim
/// can be checked rather than taken on trust. Where no published identifier
/// genuinely fits, the entry says so instead of reaching for an approximate one;
/// a tidy-looking mapping that does not survive a question is worth less than an
/// admitted gap.
///
/// Controls record whether this system actually implements them. Several do,
/// because Phase 1 built them. Presenting a control the platform does not have
/// as though it were in place would make the graph describe a safer system than
/// the one that exists.
/// </summary>
public static class KnowledgeSeed
{
    public static readonly Threat[] Threats =
    [
        new("T-01", "Password spraying and credential stuffing",
            "An attacker works through credentials against one or many accounts, relying on volume rather than on knowing any particular secret.",
            "T1110.003", "https://attack.mitre.org/techniques/T1110/003/", "gateway"),

        new("T-02", "Resource exhaustion",
            "A caller consumes a costly operation far beyond normal volume, degrading the service for everyone else whether deliberately or through a runaway integration.",
            "OWASP API4:2023", "https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/", "gateway"),

        new("T-03", "Broken object level authorisation",
            "An attacker requests object identifiers they do not own, hoping the system will return one belonging to somebody else.",
            "OWASP API1:2023", "https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/", "invoice-service"),

        new("T-04", "Account takeover and payout redirection",
            "An attacker gains control of a merchant account and repoints where money is delivered, so the real merchant stops hearing about payments while the attacker starts.",
            "T1565", "https://attack.mitre.org/techniques/T1565/", "merchant-service"),

        new("T-05", "Dependency unavailability",
            "A service a payment path relies on stops answering, and the degradation propagates to callers that cannot complete without it.",
            null, null, "rate-service"),

        new("T-06", "Price feed manipulation",
            "An attacker degrades the exchange rate feed so that invoices are quoted from a stale price, leaving the platform carrying the difference at settlement.",
            null, null, "rate-service")
    ];

    public static readonly Vulnerability[] Vulnerabilities =
    [
        new("CWE-307", "Improper restriction of excessive authentication attempts",
            "https://cwe.mitre.org/data/definitions/307.html"),

        new("CWE-770", "Allocation of resources without limits or throttling",
            "https://cwe.mitre.org/data/definitions/770.html"),

        new("CWE-639", "Authorization bypass through user-controlled key",
            "https://cwe.mitre.org/data/definitions/639.html"),

        new("CWE-672", "Operation on a resource after expiration or release",
            "https://cwe.mitre.org/data/definitions/672.html")
    ];

    /// <summary>Threat exploits weakness. Not every threat has one that fits.</summary>
    public static readonly (string ThreatId, string VulnerabilityId)[] Exploits =
    [
        ("T-01", "CWE-307"),
        ("T-02", "CWE-770"),
        ("T-03", "CWE-639"),

        // The takeover is reached through the same weak restriction on repeated
        // authentication attempts that T-01 abuses. The redirection itself is a
        // missing control rather than a code weakness, which is why it is
        // recorded as C-04 below and not as a CWE here.
        ("T-04", "CWE-307"),

        // Quoting from a rate that has passed its validity window.
        ("T-06", "CWE-672")
    ];

    public static readonly Control[] Controls =
    [
        new("C-01", "Request throttling and key rotation",
            "Limit authentication attempts per source and rotate any key seen in a probing window.",
            Implemented: false,
            Note: "Rate limiting applies to authenticated write traffic only. Rejected keys never reach the limiter, so probing is detected and not throttled."),

        new("C-02", "Per-merchant request quotas",
            "Cap writes per merchant per minute at the gateway and answer 429 beyond it.",
            Implemented: true,
            Note: "Ocelot rate limiting, 30 writes a minute, keyed on the merchant the gateway resolved."),

        new("C-03", "Per-object authorisation checks",
            "Verify the caller's right to each object requested, rather than trusting possession of its identifier.",
            Implemented: true,
            Note: "Merchant-scoped endpoints check ownership. The checkout page is deliberately unauthenticated and relies on the identifier being unguessable."),

        new("C-04", "Step-up verification for payout changes",
            "Require a second factor, or confirmation through a separate channel, before a payout destination takes effect.",
            Implemented: false,
            Note: "Not implemented. A webhook change takes effect immediately on a valid session, which is what makes R5 a detection rather than a prevention."),

        new("C-05", "Circuit breaker with a bounded cached fallback",
            "Fail fast when a dependency is failing, serve the last known good value, and refuse once it is too stale to be honest.",
            Implemented: true,
            Note: "Polly breaker on the rate call, with a thirty minute ceiling on fallback age."),

        new("C-06", "Suspend settlement on an integrity failure",
            "Stop paying out when the stored balance and the ledger disagree, until the difference is explained.",
            Implemented: false,
            Note: "The reconciliation check exists and raises RECONCILE_MISMATCH. Acting on it automatically does not.")
    ];

    /// <summary>Control mitigates threat.</summary>
    public static readonly (string ControlId, string ThreatId)[] Mitigates =
    [
        ("C-01", "T-01"),
        ("C-01", "T-04"),
        ("C-02", "T-02"),
        ("C-03", "T-03"),
        ("C-04", "T-04"),
        ("C-05", "T-05"),
        ("C-05", "T-06"),
        ("C-06", "T-06")
    ];

    /// <summary>
    /// Which rule detects which threat. This is the join that lets an alert be
    /// traced from the rule that raised it to the threat it may indicate, and on
    /// to the controls and procedures that answer it.
    /// </summary>
    public static readonly (string RuleId, string ThreatId)[] RuleDetects =
    [
        ("R1", "T-01"),
        ("R2", "T-02"),
        ("R3", "T-05"),
        ("R5", "T-04"),
        ("R6", "T-03"),
        ("R7", "T-06")
    ];

    /// <summary>
    /// Three authored response procedures, as lecture 9 asks for. Each carries a
    /// revision, because guidance that cannot be dated cannot be trusted, and
    /// the paragraphs are the retrieval unit the RAG layer will embed.
    /// </summary>
    public static readonly Procedure[] Procedures =
    [
        new("P-01", "Investigate repeated authentication failures", 2, ["T-01", "T-04"], ["C-01"],
        [
            "Establish the spread before the volume. Count the distinct accounts touched from the source address, not just the number of attempts. Many attempts against one account is a person who has forgotten a password or a misconfigured integration retrying; a handful of attempts against many accounts is spraying, and the second is far more serious at a fraction of the volume.",
            "Check whether anything succeeded. Search the same window for a successful authentication from the same source address. A probing alert with no success behind it is an attempt; one with a success behind it is an incident, and the account involved should be treated as compromised until shown otherwise.",
            "Rotate before blocking. If a key from the probing window later succeeded, revoke that key and issue a replacement before restricting the address, because blocking first tells the attacker they were seen while leaving them a credential that still works."
        ]),

        new("P-02", "Investigate object enumeration against checkout links", 1, ["T-03"], ["C-03"],
        [
            "Confirm nothing was actually reached. Every recorded attempt is a miss by definition, since the event is only raised on a request for an identifier that does not exist. Query the same source address for successful checkout requests in the window. If there are none, no merchant's data was exposed.",
            "Judge the rate against the space. Checkout identifiers are version 4 GUIDs, so guessing one is not realistically achievable and a burst of misses is reconnaissance rather than a breach in progress. Record it, block the source, and do not escalate as a data exposure without a successful request to point at.",
            "Treat a success during an enumeration window as an exposure. If a checkout request from the enumerating address did succeed, the identifier came from somewhere rather than being guessed. Investigate how the link leaked: a shared screenshot, a referrer header, or a logged URL."
        ]),

        new("P-03", "Investigate a payout destination change", 2, ["T-04"], ["C-04", "C-06"],
        [
            "Suspend settlement for the merchant first, before investigating. A payout change on an account with recent credential pressure is the step immediately before money moves, and the investigation is worth more than the delay. Settlements can be released once the change is confirmed.",
            "Confirm the change through a channel that was not changed. Contact the merchant using details recorded before the change, never the new webhook destination and never an address updated in the same session. An attacker holding the session can answer anything sent to the account.",
            "Review every credential issued in the window. List API keys created for this merchant since the first credential event preceding the change, and revoke any the merchant does not recognise. A key issued during a takeover outlives the session that created it and is how an attacker returns."
        ])
    ];

    public sealed record Threat(
        string Id, string Name, string Description, string? ReferenceId, string? SourceUrl, string? TargetsService);

    public sealed record Vulnerability(string Id, string Name, string SourceUrl);

    public sealed record Control(string Id, string Name, string Description, bool Implemented, string Note);

    public sealed record Procedure(
        string Id, string Title, int Revision,
        string[] RespondsToThreats, string[] DescribesControls, string[] Paragraphs);
}
