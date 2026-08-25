using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PandaPocket.Services.Rate.Persistence;

/// <summary>
/// One price observation. Append-only, schema-light and read by time range,
/// which is what makes MongoDB the right store for this one workload while the
/// other three services use Postgres. That is the polyglot persistence claim,
/// and it is a real choice rather than variety for its own sake.
/// </summary>
public sealed class Tick
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("pair")]
    public required string Pair { get; set; }

    /// <summary>
    /// Stored as a BSON decimal rather than a double. Money must not be subject
    /// to binary floating point error, and Mongo's Decimal128 is exact.
    /// </summary>
    [BsonElement("rate")]
    [BsonRepresentation(BsonType.Decimal128)]
    public required decimal Rate { get; set; }

    [BsonElement("source")]
    public required string Source { get; set; }

    [BsonElement("ts")]
    public required DateTime Ts { get; set; }
}

public static class TickSource
{
    /// <summary>Produced live by the background generator.</summary>
    public const string Simulator = "gbm-simulator";

    /// <summary>Generated in bulk at startup to give the history endpoint a past.</summary>
    public const string Backfill = "gbm-backfill";
}
