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

    internal async ValueTask DeleteAsync(
        ContentSource deletionPendingSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deletionPendingSource);
        if (deletionPendingSource.Status != ContentSourceStatus.DeletionPending)
        {
            throw new ArgumentException("Source must be deletion-pending.", nameof(deletionPendingSource));
        }

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = EXTRA;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        string sourceId = deletionPendingSource.Id.Value.ToString("N");
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE snapshot_keys
            SET wrapped_dek = NULL, key_state = 2
            WHERE snapshot_id IN (SELECT snapshot_id FROM snapshots WHERE source_id = $source);
            """,
            sourceId,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM sources WHERE source_id = $source;",
            sourceId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$source", sourceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
