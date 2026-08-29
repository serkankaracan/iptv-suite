using System.Globalization;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

public sealed class SqliteSourceManagementCatalog : ISourceManagementCatalog
{
    public const int MaximumSourceCount = 100;
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;

    public SqliteSourceManagementCatalog(string databasePath)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public async ValueTask<IReadOnlyList<SourceManagementSummary>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken).ConfigureAwait(false);
        return await ReadCoreAsync(connection, sourceId: null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DomainResult<ContentSource>> ReadConfigurationAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty)
        {
            return DomainResult.Failure<ContentSource>(
                DomainErrorCode.DomainInvariantViolation);
        }

        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenAsync(
                SqliteOpenMode.ReadOnly,
                cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT configuration_id, display_name, endpoint_scheme, endpoint_host,
                       endpoint_port, configuration_reference, source_kind
                FROM sources
                WHERE source_id = $source
                  AND status <> $deletionPending;
                """;
            command.Parameters.AddWithValue("$source", Id(sourceId.Value));
            command.Parameters.AddWithValue(
                "$deletionPending",
                (int)ContentSourceStatus.DeletionPending);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return DomainResult.Failure<ContentSource>(
                    DomainErrorCode.RemoteResourceNotFound);
            }

            if (!Guid.TryParseExact(reader.GetString(0), "N", out Guid configurationGuid))
            {
                return DomainResult.Failure<ContentSource>(
                    DomainErrorCode.StorageUnavailable);
            }

            DomainResult<SourceConfigurationId> configurationId =
                SourceConfigurationId.Create(configurationGuid);
            if (!configurationId.IsSuccess)
            {
                return DomainResult.Failure<ContentSource>(configurationId.Error!);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string scheme = reader.GetString(2);
            SourceKind kind = (SourceKind)reader.GetInt32(6);
            return kind switch
            {
                SourceKind.XtreamCompatible => RestoreXtreamConfiguration(
                    sourceId,
                    configurationId.Value,
                    reader,
                    scheme,
                    now),
                SourceKind.RemotePlaylist => RestoreRemotePlaylistConfiguration(
                    sourceId,
                    configurationId.Value,
                    reader,
                    now),
                _ => DomainResult.Failure<ContentSource>(
                    DomainErrorCode.DomainInvariantViolation),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException)
        {
            return DomainResult.Failure<ContentSource>(DomainErrorCode.StorageUnavailable);
        }
    }

    public async ValueTask<DomainResult<SourceManagementSummary>> RenameAsync(
        SourceId sourceId,
        string? displayName,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty || changedAt == default)
        {
            return DomainResult.Failure<SourceManagementSummary>(
                DomainErrorCode.DomainInvariantViolation);
        }

        DomainResult<SourceDisplayName> normalized = SourceDisplayName.Create(displayName);
        if (!normalized.IsSuccess)
        {
            return DomainResult.Failure<SourceManagementSummary>(normalized.Error!);
        }

        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenAsync(
                SqliteOpenMode.ReadWrite,
                cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE sources
                SET display_name = $name, updated_utc = $changed
                WHERE source_id = $source
                  AND status <> $deletionPending;
                """;
            command.Parameters.AddWithValue("$name", normalized.Value.Value);
            command.Parameters.AddWithValue("$changed", Timestamp(changedAt));
            command.Parameters.AddWithValue("$source", Id(sourceId.Value));
            command.Parameters.AddWithValue("$deletionPending", (int)ContentSourceStatus.DeletionPending);
            int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return DomainResult.Failure<SourceManagementSummary>(
                    DomainErrorCode.RemoteResourceNotFound);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SourceManagementSummary> rows = await ReadCoreAsync(
                connection,
                sourceId,
                cancellationToken).ConfigureAwait(false);
            return rows.Count == 1
                ? DomainResult.Success(rows[0])
                : DomainResult.Failure<SourceManagementSummary>(DomainErrorCode.StorageUnavailable);
        }
        catch (SqliteException)
        {
            return DomainResult.Failure<SourceManagementSummary>(DomainErrorCode.StorageUnavailable);
        }
    }

    private static async Task<IReadOnlyList<SourceManagementSummary>> ReadCoreAsync(
        SqliteConnection connection,
        SourceId? sourceId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT source.source_id, source.display_name, source.source_kind, source.status,
                   source.endpoint_scheme,
                   (SELECT count(*) FROM channels AS item
                    WHERE item.snapshot_id = source.active_snapshot_id),
                   (SELECT count(*) FROM movies AS item
                    WHERE item.snapshot_id = source.active_snapshot_id),
                   (SELECT count(*) FROM series AS item
                    WHERE item.snapshot_id = source.active_snapshot_id),
                   (SELECT count(*) FROM episodes AS item
                    WHERE item.snapshot_id = source.active_snapshot_id)
            FROM sources AS source
            WHERE source.status <> $deletionPending
              AND ($source IS NULL OR source.source_id = $source)
            ORDER BY source.display_name COLLATE NOCASE, source.source_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$deletionPending", (int)ContentSourceStatus.DeletionPending);
        command.Parameters.AddWithValue("$source", sourceId.HasValue
            ? Id(sourceId.Value.Value)
            : DBNull.Value);
        command.Parameters.AddWithValue("$limit", MaximumSourceCount + 1);
        var rows = new List<SourceManagementSummary>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == MaximumSourceCount)
            {
                throw new InvalidDataException("Source count exceeds the management limit.");
            }

            SourceKind kind = (SourceKind)reader.GetInt32(2);
            ContentSourceStatus status = (ContentSourceStatus)reader.GetInt32(3);
            string scheme = reader.GetString(4);
            if (!Enum.IsDefined(kind) || !Enum.IsDefined(status) ||
                (!string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
                 !string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Source metadata is invalid.");
            }

            rows.Add(new SourceManagementSummary(
                ParseSourceId(reader.GetString(0)),
                reader.GetString(1),
                kind,
                status,
                string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.Ordinal),
                new ContentCatalogCounts(
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8))));
        }

        return rows;
    }

    private async ValueTask<SqliteConnection> OpenAsync(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (mode != SqliteOpenMode.ReadOnly)
            {
                await using SqliteCommand pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static DomainResult<ContentSource> RestoreXtreamConfiguration(
        SourceId sourceId,
        SourceConfigurationId configurationId,
        SqliteDataReader reader,
        string scheme,
        DateTimeOffset now)
    {
        DomainResult<SecretReference> reference =
            SecretReference.Parse(reader.GetString(5));
        return reference.IsSuccess
            ? StoredXtreamSourceFactory.RestoreForRefresh(
                sourceId,
                configurationId,
                reader.GetString(1),
                scheme,
                reader.GetString(3),
                reader.GetInt32(4),
                reference.Value!,
                allowsInsecureTransport: string.Equals(
                    scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.Ordinal),
                now)
            : DomainResult.Failure<ContentSource>(reference.Error!);
    }

    private static DomainResult<ContentSource> RestoreRemotePlaylistConfiguration(
        SourceId sourceId,
        SourceConfigurationId configurationId,
        SqliteDataReader reader,
        DateTimeOffset now)
    {
        DomainResult<ProtectedLocatorReference> reference =
            ProtectedLocatorReference.Parse(reader.GetString(5));
        return reference.IsSuccess
            ? StoredRemotePlaylistSourceFactory.RestoreForRefresh(
                sourceId,
                configurationId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reference.Value!,
                now)
            : DomainResult.Failure<ContentSource>(reference.Error!);
    }

    private static SourceId ParseSourceId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out Guid guid))
        {
            throw new InvalidDataException("Source identifier is invalid.");
        }

        DomainResult<SourceId> result = SourceId.Create(guid);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidDataException("Source identifier is invalid.");
    }

    private static string Id(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
