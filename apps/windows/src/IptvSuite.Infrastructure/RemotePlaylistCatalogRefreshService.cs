using System.Globalization;
using System.Runtime.Versioning;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class RemotePlaylistCatalogRefreshService : ISourceRefreshCoordinator
{
    private readonly string _databasePath;
    private readonly SqliteRemotePlaylistCatalogImporter _importer;
    private readonly TimeProvider _timeProvider;

    public RemotePlaylistCatalogRefreshService(
        string databasePath,
        ISecretStore secretStore,
        IStreamingHttpTransport transport,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(transport);
        _databasePath = Path.GetFullPath(databasePath);
        _importer = new SqliteRemotePlaylistCatalogImporter(
            _databasePath,
            secretStore,
            transport);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<DomainResult<ContentCatalogCounts>>
        RefreshFromStoredConfigurationAsync(
            SourceId sourceId,
            CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty)
        {
            return DomainResult.Failure<ContentCatalogCounts>(
                DomainErrorCode.DomainInvariantViolation);
        }

        DomainResult<ContentSource> source = await ReadStoredSourceAsync(
            sourceId,
            cancellationToken).ConfigureAwait(false);
        if (!source.IsSuccess)
        {
            return DomainResult.Failure<ContentCatalogCounts>(source.Error!);
        }

        RemotePlaylistCatalogImportResult result = await _importer.ImportAsync(
            source.Value!,
            cancellationToken).ConfigureAwait(false);
        return result.Disposition == CatalogImportCommitDisposition.Committed
            ? DomainResult.Success(new ContentCatalogCounts(
                result.ImportedChannelCount!.Value,
                0,
                0,
                0))
            : DomainResult.Failure<ContentCatalogCounts>(
                result.Error ?? DomainError.Create(DomainErrorCode.StorageUnavailable));
    }

    private async ValueTask<DomainResult<ContentSource>> ReadStoredSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        var database = new SqliteCatalogDatabase(_databasePath);
        try
        {
            await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
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
                SELECT configuration_id, display_name, endpoint_scheme, endpoint_host,
                       endpoint_port, configuration_reference, source_kind
                FROM sources
                WHERE source_id = $source
                  AND status <> $deletionPending;
                """;
            command.Parameters.AddWithValue(
                "$source",
                sourceId.Value.ToString("N", CultureInfo.InvariantCulture));
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

            if ((SourceKind)reader.GetInt32(6) != SourceKind.RemotePlaylist ||
                !Guid.TryParseExact(reader.GetString(0), "N", out Guid configurationGuid))
            {
                return DomainResult.Failure<ContentSource>(
                    DomainErrorCode.DomainInvariantViolation);
            }

            DomainResult<SourceConfigurationId> configurationId =
                SourceConfigurationId.Create(configurationGuid);
            DomainResult<ProtectedLocatorReference> reference =
                ProtectedLocatorReference.Parse(reader.GetString(5));
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (!configurationId.IsSuccess || !reference.IsSuccess || now == default)
            {
                return DomainResult.Failure<ContentSource>(
                    DomainErrorCode.StorageUnavailable);
            }

            return StoredRemotePlaylistSourceFactory.RestoreForRefresh(
                sourceId,
                configurationId.Value,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reference.Value!,
                now);
        }
        catch (SqliteException)
        {
            return DomainResult.Failure<ContentSource>(
                DomainErrorCode.StorageUnavailable);
        }
    }
}
