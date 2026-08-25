using Microsoft.Extensions.Options;
using MongoDB.Driver;
using PandaPocket.Services.Rate.Configuration;

namespace PandaPocket.Services.Rate.Persistence;

public interface ITickRepository
{
    Task EnsureIndexesAsync(CancellationToken ct);
    Task<bool> IsEmptyAsync(CancellationToken ct);
    Task InsertAsync(Tick tick, CancellationToken ct);
    Task InsertManyAsync(IEnumerable<Tick> ticks, CancellationToken ct);
    Task<IReadOnlyList<Tick>> GetHistoryAsync(string pair, DateTime from, DateTime to, int limit, CancellationToken ct);
    Task<Tick?> GetLatestAsync(string pair, CancellationToken ct);
}

public sealed class TickRepository : ITickRepository
{
    private readonly IMongoCollection<Tick> _ticks;
    private readonly ILogger<TickRepository> _logger;

    public TickRepository(IMongoClient client, IOptions<MongoOptions> options, ILogger<TickRepository> logger)
    {
        var o = options.Value;
        _ticks = client.GetDatabase(o.Database).GetCollection<Tick>(o.TicksCollection);
        _logger = logger;
    }

    /// <summary>
    /// The compound index on { pair: 1, ts: -1 } is not decorative. Every read
    /// this service performs is "one pair, newest first, within a time range",
    /// which is exactly a prefix match on that index followed by an ordered
    /// range scan. Mongo can satisfy both the filter and the sort from the index
    /// alone, with no in-memory sort stage.
    ///
    /// Creating an index that already exists with the same specification is a
    /// no-op in Mongo, so this is safe to run on every startup.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        var keys = Builders<Tick>.IndexKeys
            .Ascending(t => t.Pair)
            .Descending(t => t.Ts);

        var model = new CreateIndexModel<Tick>(keys, new CreateIndexOptions { Name = "pair_1_ts_-1" });
        await _ticks.Indexes.CreateOneAsync(model, cancellationToken: ct);

        _logger.LogInformation("Ensured index {IndexName} on {Collection}", "pair_1_ts_-1", _ticks.CollectionNamespace.CollectionName);
    }

    public async Task<bool> IsEmptyAsync(CancellationToken ct) =>
        await _ticks.CountDocumentsAsync(FilterDefinition<Tick>.Empty, new CountOptions { Limit = 1 }, ct) == 0;

    public Task InsertAsync(Tick tick, CancellationToken ct) =>
        _ticks.InsertOneAsync(tick, cancellationToken: ct);

    public Task InsertManyAsync(IEnumerable<Tick> ticks, CancellationToken ct) =>
        _ticks.InsertManyAsync(ticks, cancellationToken: ct);

    public async Task<IReadOnlyList<Tick>> GetHistoryAsync(string pair, DateTime from, DateTime to, int limit, CancellationToken ct)
    {
        var filter = Builders<Tick>.Filter.And(
            Builders<Tick>.Filter.Eq(t => t.Pair, pair),
            Builders<Tick>.Filter.Gte(t => t.Ts, from),
            Builders<Tick>.Filter.Lte(t => t.Ts, to));

        return await _ticks.Find(filter)
            .SortByDescending(t => t.Ts)
            .Limit(limit)
            .ToListAsync(ct);
    }

    public async Task<Tick?> GetLatestAsync(string pair, CancellationToken ct) =>
        await _ticks.Find(Builders<Tick>.Filter.Eq(t => t.Pair, pair))
            .SortByDescending(t => t.Ts)
            .Limit(1)
            .FirstOrDefaultAsync(ct);
}
