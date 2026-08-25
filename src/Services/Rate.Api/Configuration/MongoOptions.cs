namespace PandaPocket.Services.Rate.Configuration;

public sealed class MongoOptions
{
    public const string SectionName = "Mongo";

    /// <summary>
    /// Note the authSource. Compose creates the user through
    /// MONGO_INITDB_ROOT_USERNAME, which places it in the admin database, so
    /// authenticating against rate_db fails with what looks like a wrong
    /// password unless authSource=admin is present.
    /// </summary>
    public string ConnectionString { get; init; } = "mongodb://localhost:27017/?authSource=admin";
    public string Database { get; init; } = "rate_db";
    public string TicksCollection { get; init; } = "ticks";
}
