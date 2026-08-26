using System.Runtime.Versioning;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class SqliteRemotePlaylistCatalogImporter : IRemotePlaylistCatalogImporter
{
    private readonly string _databasePath;
    private readonly ISecretStore _secretStore;
    private readonly IStreamingHttpTransport _transport;
    private readonly SqliteRemoteImportFaultPoint _faultPoint;

    public SqliteRemotePlaylistCatalogImporter(
        string databasePath,
        ISecretStore secretStore,
        IStreamingHttpTransport transport)
        : this(
            databasePath,
            secretStore,
            transport,
            SqliteRemoteImportFaultPoint.None)
    {
    }

    internal SqliteRemotePlaylistCatalogImporter(
        string databasePath,
        ISecretStore secretStore,
        IStreamingHttpTransport transport,
        SqliteRemoteImportFaultPoint faultPoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Enum.IsDefined(faultPoint))
        {
            throw new ArgumentOutOfRangeException(nameof(faultPoint));
        }

        _databasePath = Path.GetFullPath(databasePath);
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _faultPoint = faultPoint;
    }

    public async ValueTask<RemotePlaylistCatalogImportResult> ImportAsync(
        ContentSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var sink = new SqliteRemoteM3uImportSink(
            _databasePath,
            measureWriteAllocations: false,
            _faultPoint);
        RemotePlaylistCatalogImportResult result;
        try
        {
            result = await ImportCoreAsync(sink, source, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await sink.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException) when (IsRecoverable(cleanupException))
            {
                // Preserve the original catastrophic or programming failure.
            }

            throw;
        }

        return await FinalizeResultAsync(sink, result).ConfigureAwait(false);
    }

    private async ValueTask<RemotePlaylistCatalogImportResult> ImportCoreAsync(
        SqliteRemoteM3uImportSink sink,
        ContentSource source,
        CancellationToken cancellationToken)
    {
        var loader = new RemotePlaylistCatalogLoader(_secretStore, _transport, sink);
        DomainResult<RemoteM3uParseResult> loaded;
        try
        {
            loaded = await loader.LoadAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DomainErrorCode code = cancellationToken.IsCancellationRequested
                ? DomainErrorCode.OperationCancelled
                : DomainErrorCode.RequestTimedOut;
            return await FinalizeFailureAsync(sink, DomainError.Create(code))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return await FinalizeFailureAsync(
                sink,
                DomainError.Create(DomainErrorCode.StorageUnavailable)).ConfigureAwait(false);
        }

        if (sink.CommitDisposition == SqliteImportCommitDisposition.Committed)
        {
            return MapCommitted(sink);
        }

        if (!loaded.IsSuccess)
        {
            return await FinalizeFailureAsync(sink, loaded.Error!).ConfigureAwait(false);
        }

        if (loaded.Value!.ContentKind != PlaylistContentKind.ExtendedM3uCatalog)
        {
            return await FinalizeFailureAsync(
                sink,
                DomainError.Create(DomainErrorCode.UnsupportedPlaylistFormat)).ConfigureAwait(false);
        }

        return await FinalizeFailureAsync(
            sink,
            DomainError.Create(DomainErrorCode.StorageUnavailable)).ConfigureAwait(false);
    }

    private static RemotePlaylistCatalogImportResult MapCommitted(SqliteRemoteM3uImportSink sink)
    {
        if (!sink.CommittedChannelCount.HasValue || !sink.CommittedWarningCount.HasValue)
        {
            return RemotePlaylistCatalogImportResult.Indeterminate(
                DomainError.Create(DomainErrorCode.StorageUnavailable));
        }

        return RemotePlaylistCatalogImportResult.Committed(
            sink.CommittedChannelCount.Value,
            sink.CommittedWarningCount.Value);
    }

    private static RemotePlaylistCatalogImportResult MapFailure(
        SqliteRemoteM3uImportSink sink,
        DomainError error) => sink.CommitDisposition switch
        {
            SqliteImportCommitDisposition.Committed => MapCommitted(sink),
            SqliteImportCommitDisposition.Indeterminate =>
                RemotePlaylistCatalogImportResult.Indeterminate(error),
            _ => RemotePlaylistCatalogImportResult.NotCommitted(error),
        };

    private static async ValueTask<RemotePlaylistCatalogImportResult> FinalizeFailureAsync(
        SqliteRemoteM3uImportSink sink,
        DomainError error)
    {
        try
        {
            await sink.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            RemotePlaylistCatalogImportResult mapped = MapFailure(
                sink,
                DomainError.Create(DomainErrorCode.StorageUnavailable));
            return mapped.Disposition is CatalogImportCommitDisposition.Committed
                ? mapped
                : RemotePlaylistCatalogImportResult.Indeterminate(
                    DomainError.Create(DomainErrorCode.StorageUnavailable));
        }

        return MapFailure(sink, error);
    }

    private static async ValueTask<RemotePlaylistCatalogImportResult> FinalizeResultAsync(
        SqliteRemoteM3uImportSink sink,
        RemotePlaylistCatalogImportResult result)
    {
        try
        {
            await sink.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            RemotePlaylistCatalogImportResult mapped = MapFailure(
                sink,
                DomainError.Create(DomainErrorCode.StorageUnavailable));
            return mapped.Disposition is CatalogImportCommitDisposition.Committed
                ? mapped
                : RemotePlaylistCatalogImportResult.Indeterminate(
                    DomainError.Create(DomainErrorCode.StorageUnavailable));
        }

        return sink.CommitDisposition is SqliteImportCommitDisposition.Committed
            ? MapCommitted(sink)
            : result;
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
