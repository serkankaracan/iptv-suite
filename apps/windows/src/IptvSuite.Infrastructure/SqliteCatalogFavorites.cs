using System.Globalization;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

internal sealed class SqliteCatalogFavorites
{
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;

    internal SqliteCatalogFavorites(string databasePath)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    internal async ValueTask SetAsync(
        SourceId sourceId,
        int stableKeyVersion,
        string stableKey,
        bool isFavorite,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty || stableKeyVersion <= 0 || string.IsNullOrWhiteSpace(stableKey) ||
            stableKey.Length > 512 || changedAt == default)
        {
            throw new ArgumentException("A valid stable favorite key is required.");
        }

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = isFavorite
            ? """
              INSERT INTO favorites(source_id, stable_key_version, stable_key, created_utc)
              SELECT $source, $version, $key, $created
              WHERE EXISTS (
                  SELECT 1 FROM channels AS c
                  JOIN sources AS s ON s.active_snapshot_id = c.snapshot_id
                  WHERE s.source_id = $source
                    AND c.stable_key_version = $version
                    AND c.stable_key = $key
              )
              ON CONFLICT(source_id, stable_key_version, stable_key) DO NOTHING;
              """
            : "DELETE FROM favorites WHERE source_id = $source AND stable_key_version = $version AND stable_key = $key;";
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N"));
        command.Parameters.AddWithValue("$version", stableKeyVersion);
        command.Parameters.AddWithValue("$key", stableKey);
        command.Parameters.AddWithValue("$created", changedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (isFavorite && affected == 0 && !await ExistsAsync(
                connection,
                sourceId,
                stableKeyVersion,
                stableKey,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Favorite target is not in the active catalog.");
        }
    }

    internal async ValueTask<bool> IsFavoriteAsync(
        SourceId sourceId,
        int stableKeyVersion,
        string stableKey,
        CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ExistsAsync(connection, sourceId, stableKeyVersion, stableKey, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SourceId sourceId,
        int stableKeyVersion,
        string stableKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM favorites
                WHERE source_id = $source AND stable_key_version = $version AND stable_key = $key
            );
            """;
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N"));
        command.Parameters.AddWithValue("$version", stableKeyVersion);
        command.Parameters.AddWithValue("$key", stableKey);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }
}
