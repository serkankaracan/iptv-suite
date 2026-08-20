using System.Globalization;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

internal sealed record CatalogSyncRunSummary(
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? ResultCode,
    int ParsedCount,
    int PersistedCount,
    int WarningCount,
    DomainErrorCode? FailureCode);

internal sealed class SqliteCatalogSyncHistory
{
    internal const int MaximumResultCount = 100;

    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;

    internal SqliteCatalogSyncHistory(string databasePath)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    internal async ValueTask<IReadOnlyList<CatalogSyncRunSummary>> ReadRecentAsync(
        SourceId sourceId,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty || maximumResults is < 1 or > MaximumResultCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
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
            SELECT started_utc, completed_utc, result_code, parsed_count, persisted_count,
                warning_count, failure_code
            FROM sync_runs
            WHERE source_id = $source
            ORDER BY started_utc DESC, sync_run_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", maximumResults);

        var results = new List<CatalogSyncRunSummary>(maximumResults);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CatalogSyncRunSummary(
                ParseTimestamp(reader.GetString(0)),
                reader.IsDBNull(1) ? null : ParseTimestamp(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : (DomainErrorCode)reader.GetInt32(6)));
        }

        return results;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();
}
