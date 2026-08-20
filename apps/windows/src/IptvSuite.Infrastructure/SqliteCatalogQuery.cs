using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

internal sealed record CatalogChannelSummary(
    ChannelId ChannelId,
    CategoryId CategoryId,
    string StableKey,
    string Name,
    int? Number,
    bool IsAdult);

internal sealed class SqliteCatalogQuery
{
    internal const int MaximumPageSize = 200;
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;

    internal SqliteCatalogQuery(string databasePath)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    internal async ValueTask<IReadOnlyList<CatalogChannelSummary>> ReadChannelPageAsync(
        SourceId sourceId,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty || offset < 0 || limit is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A bounded catalog page is required.");
        }

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.channel_id, c.category_id, c.stable_key, c.display_name,
                   c.channel_number, c.is_adult
            FROM channels AS c
            JOIN sources AS s ON s.active_snapshot_id = c.snapshot_id
            WHERE s.source_id = $source
            ORDER BY c.display_name COLLATE NOCASE, c.channel_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N"));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var rows = new List<CatalogChannelSummary>(limit);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParseExact(reader.GetString(0), "N", out Guid channelGuid) ||
                !Guid.TryParseExact(reader.GetString(1), "N", out Guid categoryGuid))
            {
                throw new InvalidDataException("Catalog identifier is invalid.");
            }

            DomainResult<ChannelId> channelId = ChannelId.Create(channelGuid);
            DomainResult<CategoryId> categoryId = CategoryId.Create(categoryGuid);
            if (!channelId.IsSuccess || !categoryId.IsSuccess)
            {
                throw new InvalidDataException("Catalog identifier is invalid.");
            }

            rows.Add(new(
                channelId.Value,
                categoryId.Value,
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetInt32(5) == 1));
        }

        return rows;
    }
}
