using PandaPocket.Services.Soc.Persistence;

namespace PandaPocket.Services.Soc.Domain.Detection;

/// <summary>
/// The rule definitions, seeded into the database at startup.
///
/// Every field the specification asks a rule to document is a column here:
/// name, purpose, input events, condition, severity, the alert it generates and
/// the recommended response. The report reads this table rather than restating
/// it, so the documented rule and the running rule cannot disagree.
///
/// Thresholds are deliberately modest. This runs against a demonstration volume
/// where an attack is a dozen requests, not a million, and a rule tuned for
/// production traffic would never fire on camera. The report states the figures
/// and says they are demo-scaled rather than presenting them as production
/// values somebody derived.
/// </summary>
public static class RuleCatalogue
{
    public static IReadOnlyList<DetectionRuleRecord> All =>
    [
        new()
        {
            RuleId = "R1",
            Name = "Credential probing",
            Purpose =
                "Detect one source working through API keys, whether guessed or taken from a list, " +
                "before one of them succeeds.",
            InputEventTypes = "API_KEY_INVALID",
            Condition = "5 or more invalid API keys from one source address within 5 minutes",
            Severity = "HIGH",
            AlertType = "CREDENTIAL_PROBING",
            RecommendedAction =
                "Confirm whether any key from this address later succeeded. If one did, revoke it and " +
                "issue a replacement. Consider blocking the address at the edge.",
            ThreatReference = "MITRE T1110.001, CWE-307",
            WindowMinutes = 5,
            Threshold = 5,
            SuppressionMinutes = 15
        },
        new()
        {
            RuleId = "R2",
            Name = "Quota abuse",
            Purpose =
                "Detect a merchant consuming far beyond its rate limit, which is either a runaway " +
                "integration or deliberate resource exhaustion.",
            InputEventTypes = "RATE_LIMIT_EXCEEDED",
            Condition = "10 or more throttled requests for one merchant within 2 minutes",
            Severity = "MEDIUM",
            AlertType = "POSSIBLE_DOS",
            RecommendedAction =
                "Check whether the merchant deployed recently. A retry loop and an attack look identical " +
                "here, so contact the merchant before restricting the account.",
            ThreatReference = "OWASP API4:2023, CWE-770",
            WindowMinutes = 2,
            Threshold = 10,
            SuppressionMinutes = 10
        },
        new()
        {
            RuleId = "R3",
            Name = "Dependency failure",
            Purpose =
                "Detect a service its dependants have cut off, or that the registry has marked critical, " +
                "before the degradation reaches merchants.",
            InputEventTypes = "CIRCUIT_OPENED, SERVICE_HEALTH_CHANGED",
            Condition = "2 or more failure signals for one service within 5 minutes",
            Severity = "HIGH",
            AlertType = "SERVICE_FAILURE",
            RecommendedAction =
                "Check the service's own health endpoint and its database. If it is the rate service, " +
                "check how stale the cached quotes have become, because pricing degrades with it.",
            ThreatReference = "Availability",
            WindowMinutes = 5,
            Threshold = 2,
            SuppressionMinutes = 10
        },
        new()
        {
            RuleId = "R5",
            Name = "Payout redirection",
            Purpose =
                "Detect a payout destination changed on an account whose credentials were recently under " +
                "pressure, which is account takeover with the money about to follow.",
            InputEventTypes = "MERCHANT_WEBHOOK_URL_CHANGED, AUTH_FAILED, API_KEY_INVALID, API_KEY_ISSUED",
            Condition =
                "A webhook URL change on an account with 3 or more credential events in the previous 24 hours",
            Severity = "CRITICAL",
            AlertType = "PAYOUT_REDIRECTION",
            RecommendedAction =
                "Suspend settlements for this merchant immediately. Verify the change with the merchant " +
                "through a channel other than the one that was changed. Revoke every key issued in the window.",
            ThreatReference = "MITRE T1110 leading to T1565",
            WindowMinutes = 60,
            Threshold = 3,
            SuppressionMinutes = 60
        },
        new()
        {
            RuleId = "R6",
            Name = "Checkout enumeration",
            Purpose =
                "Detect one source working through invoice ids it holds no link for, attempting to read " +
                "another merchant's payment pages.",
            InputEventTypes = "CHECKOUT_ENUMERATION",
            Condition = "10 or more checkout requests for non-existent invoice ids from one address within 5 minutes",
            Severity = "HIGH",
            AlertType = "OBJECT_ENUMERATION",
            RecommendedAction =
                "Block the address. Confirm no checkout id was actually hit. The ids are random enough that " +
                "guessing one is not realistically possible, so this records the attempt rather than a breach.",
            ThreatReference = "OWASP API1:2023, CWE-639",
            WindowMinutes = 5,
            Threshold = 10,
            SuppressionMinutes = 15
        },
        new()
        {
            RuleId = "R7",
            Name = "Rate manipulation exposure",
            Purpose =
                "Detect invoices being priced from a stale rate while the price feed is unavailable, which " +
                "leaves the platform carrying the difference at settlement.",
            InputEventTypes = "INVOICE_CREATED, RATE_FALLBACK_USED, CIRCUIT_OPENED",
            Condition = "3 or more invoices priced on a fallback rate within 10 minutes",
            Severity = "HIGH",
            AlertType = "RATE_EXPOSURE",
            RecommendedAction =
                "Restore the rate service. Review the invoices listed against the live rate and decide " +
                "whether to honour or re-quote them. If the outage was induced, treat it as an attack on pricing.",

            // No single published identifier fits a composite like this, and
            // implying otherwise would not survive a question. The attack leg is
            // resource exhaustion; the consequence is acting on expired data.
            ThreatReference = "OWASP API4:2023 (attack), CWE-672 (consequence). Mapping is ours, not standard.",
            WindowMinutes = 10,
            Threshold = 3,
            SuppressionMinutes = 15
        }
    ];
}
