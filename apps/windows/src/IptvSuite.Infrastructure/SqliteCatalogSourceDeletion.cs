using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

internal sealed class SqliteCatalogSourceDeletion
{
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;

    internal SqliteCatalogSourceDeletion(string databasePath)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    internal async ValueTask ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            DELETE FROM snapshots
            WHERE state = 0
              AND snapshot_id NOT IN (
                  SELECT active_snapshot_id FROM sources WHERE active_snapshot_id IS NOT NULL
              );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask PruneRetiredSnapshotsAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A non-empty source identifier is required.", nameof(sourceId));
        }

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            DELETE FROM snapshots
            WHERE source_id = $source
              AND snapshot_id <> (
                  SELECT active_snapshot_id FROM sources WHERE source_id = $source
              );
            """;
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

}
