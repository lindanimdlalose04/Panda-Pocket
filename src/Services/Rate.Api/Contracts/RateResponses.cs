namespace PandaPocket.Services.Rate.Contracts;

/// <summary>
/// A current quote.
///
/// AsOf is not padding. On day 6 the Invoice service puts a circuit breaker in
/// front of this call and falls back to the last cached rate when Rate is
/// unavailable. AsOf is how Invoice knows, and can report, how stale that
/// fallback is.
/// </summary>
public sealed record RateQuoteResponse(string Pair, decimal Rate, DateTime AsOf);

public sealed record RateHistoryResponse(
    string Pair,
    DateTime From,
    DateTime To,
    int Count,
    IReadOnlyList<RateHistoryPoint> Points);

public sealed record RateHistoryPoint(decimal Rate, DateTime Ts, string Source);
