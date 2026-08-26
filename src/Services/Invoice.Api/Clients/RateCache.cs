using System.Collections.Concurrent;

namespace PandaPocket.Services.Invoice.Clients;

/// <summary>
/// The last rate Rate successfully returned for each pair.
///
/// This is what makes the circuit breaker useful rather than merely defensive.
/// A breaker on its own converts a slow failure into a fast one, which helps the
/// system but not the merchant: their checkout still fails. With a cached rate
/// the breaker converts a failure into a slightly worse answer instead, and an
/// invoice can still be created while Rate is down.
///
/// Held in memory rather than in the database on purpose. It is a cache of
/// something another service owns, it is rebuilt within seconds of the first
/// successful quote after a restart, and persisting it would mean a container
/// could start up and confidently serve a rate from last week.
/// </summary>
public sealed class RateCache(ILogger<RateCache> logger)
{
    private readonly ConcurrentDictionary<string, RateQuote> _quotes = new(StringComparer.OrdinalIgnoreCase);

    public void Store(RateQuote quote)
    {
        _quotes[quote.Pair] = quote;
    }

    /// <summary>
    /// The last known good quote for a pair, or null if none has ever been seen.
    ///
    /// A cold start with Rate already down is the one case this cannot help
    /// with, and it should not pretend otherwise: there is no honest number to
    /// fall back to, so the caller returns 503.
    /// </summary>
    public RateQuote? Get(string pair)
    {
        if (_quotes.TryGetValue(pair, out var quote)) return quote;

        logger.LogWarning("No cached rate for {Pair}; nothing to fall back on", pair);
        return null;
    }

    public IReadOnlyCollection<RateQuote> All() => _quotes.Values.ToList();
}
