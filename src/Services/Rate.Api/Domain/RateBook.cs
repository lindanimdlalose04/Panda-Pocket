using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PandaPocket.Services.Rate.Configuration;

namespace PandaPocket.Services.Rate.Domain;

/// <summary>
/// The current rate for each configured pair, held in memory.
///
/// Quote reads are served from here rather than from Mongo. This is the call
/// Invoice makes on the critical path of creating an invoice, so it should not
/// wait on a database round trip to learn a number the service already knows.
/// Mongo holds the history; this holds the present.
///
/// A singleton written by the background generator and read concurrently by
/// request handlers, hence the concurrent dictionary.
/// </summary>
public sealed class RateBook
{
    private readonly ConcurrentDictionary<string, RateEntry> _rates = new(StringComparer.OrdinalIgnoreCase);

    public RateBook(IOptions<RateSimulatorOptions> options)
    {
        // Seed from configuration so a quote is available from the first
        // request, rather than 404ing until the generator has produced a tick.
        foreach (var pair in options.Value.Pairs)
        {
            _rates[pair.Pair] = new RateEntry(pair.Pair, pair.StartPrice, DateTime.UtcNow);
        }
    }

    public void Set(string pair, decimal rate, DateTime asOf) =>
        _rates[pair] = new RateEntry(pair, rate, asOf);

    public RateEntry? Get(string pair) =>
        _rates.TryGetValue(pair, out var entry) ? entry : null;

    public IReadOnlyList<RateEntry> GetAll() =>
        _rates.Values.OrderBy(r => r.Pair, StringComparer.Ordinal).ToList();
}

public sealed record RateEntry(string Pair, decimal Rate, DateTime AsOf);
