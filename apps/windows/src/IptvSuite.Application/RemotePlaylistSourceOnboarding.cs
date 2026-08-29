using System.Diagnostics;

namespace IptvSuite.Application;

public enum CatalogImportCommitDisposition
{
    NotCommitted,
    Committed,
    Indeterminate,
}

[DebuggerDisplay("[REMOTE-PLAYLIST-CATALOG-IMPORT-RESULT]")]
public sealed class RemotePlaylistCatalogImportResult
{
    private RemotePlaylistCatalogImportResult(
        CatalogImportCommitDisposition disposition,
        int? importedChannelCount,
        int? warningCount,
        bool entryLimitReached,
        DomainError? error)
    {
        Disposition = disposition;
        ImportedChannelCount = importedChannelCount;
        WarningCount = warningCount;
        EntryLimitReached = entryLimitReached;
        Error = error;
    }

    public CatalogImportCommitDisposition Disposition { get; }

    public int? ImportedChannelCount { get; }

    public int? WarningCount { get; }

    public bool EntryLimitReached { get; }

    public DomainError? Error { get; }

    public static RemotePlaylistCatalogImportResult Committed(
        int importedChannelCount,
        int warningCount,
        bool entryLimitReached = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(importedChannelCount);
        ArgumentOutOfRangeException.ThrowIfNegative(warningCount);

        return new RemotePlaylistCatalogImportResult(
            CatalogImportCommitDisposition.Committed,
            importedChannelCount,
            warningCount,
            entryLimitReached,
            null);
    }

    public static RemotePlaylistCatalogImportResult NotCommitted(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new RemotePlaylistCatalogImportResult(
            CatalogImportCommitDisposition.NotCommitted,
            null,
            null,
            entryLimitReached: false,
            error: error);
    }

    public static RemotePlaylistCatalogImportResult Indeterminate(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new RemotePlaylistCatalogImportResult(
            CatalogImportCommitDisposition.Indeterminate,
            null,
            null,
            entryLimitReached: false,
            error: error);
    }

    public override string ToString() => "[REMOTE-PLAYLIST-CATALOG-IMPORT-RESULT]";
}

public interface IRemotePlaylistCatalogImporter
{
    ValueTask<RemotePlaylistCatalogImportResult> ImportAsync(
        ContentSource source,
        CancellationToken cancellationToken = default);
}

[DebuggerDisplay("[REMOTE-PLAYLIST-SOURCE-ONBOARDING-RESULT]")]
public sealed class RemotePlaylistSourceOnboardingResult
{
    internal RemotePlaylistSourceOnboardingResult(
        SourceId sourceId,
        int importedChannelCount,
        int warningCount,
        bool entryLimitReached)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A non-empty source identifier is required.", nameof(sourceId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(importedChannelCount);
        ArgumentOutOfRangeException.ThrowIfNegative(warningCount);

        SourceId = sourceId;
        ImportedChannelCount = importedChannelCount;
        WarningCount = warningCount;
        EntryLimitReached = entryLimitReached;
    }

    public SourceId SourceId { get; }

    public int ImportedChannelCount { get; }

    public int WarningCount { get; }

    public bool EntryLimitReached { get; }

    public override string ToString() => "[REMOTE-PLAYLIST-SOURCE-ONBOARDING-RESULT]";
}

public sealed class RemotePlaylistSourceOnboardingService
{
    private readonly ISecretStore _secretStore;
    private readonly IRemotePlaylistCatalogImporter _importer;
    private readonly TimeProvider _timeProvider;

    public RemotePlaylistSourceOnboardingService(
        ISecretStore secretStore,
        IRemotePlaylistCatalogImporter importer,
        TimeProvider timeProvider)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ValueTask<DomainResult<RemotePlaylistSourceOnboardingResult>> AddAsync(
        string? displayName,
        string? locator,
        CancellationToken cancellationToken = default) =>
        AddCoreAsync(
            displayName,
            locator,
            allowInsecureHttp: false,
            cancellationToken: cancellationToken);

    public ValueTask<DomainResult<RemotePlaylistSourceOnboardingResult>>
        AddAllowingInsecureHttpAsync(
            string? displayName,
            string? locator,
            CancellationToken cancellationToken = default) =>
        AddCoreAsync(
            displayName,
            locator,
            allowInsecureHttp: true,
            cancellationToken: cancellationToken);

    private async ValueTask<DomainResult<RemotePlaylistSourceOnboardingResult>> AddCoreAsync(
        string? displayName,
        string? locator,
        bool allowInsecureHttp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared = allowInsecureHttp
            ? SourceConfigurationValidator.PrepareRemotePlaylistAllowingInsecureHttp(
                displayName,
                locator)
            : SourceConfigurationValidator.PrepareRemotePlaylist(displayName, locator);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Failure<RemotePlaylistSourceOnboardingResult>(prepared.Error!);
        }

        if (!Uri.TryCreate(locator, UriKind.Absolute, out Uri? requestUri))
        {
            return DomainResult.Failure<RemotePlaylistSourceOnboardingResult>(
                DomainErrorCode.EndpointMalformed);
        }

        if (!string.IsNullOrEmpty(requestUri.UserInfo))
        {
            return DomainResult.Failure<RemotePlaylistSourceOnboardingResult>(
                DomainErrorCode.EndpointUserInfoNotAllowed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now;
        try
        {
            now = _timeProvider.GetUtcNow();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return StorageUnavailable();
        }

        if (now == default)
        {
            return StorageUnavailable();
        }

        SourceId sourceId = SourceId.Generate();
        DomainResult<ValidatedSourceDraft> protectedDraft;
        try
        {
            var protection = new SourceDraftProtectionService(_secretStore);
            ValueTask<DomainResult<ValidatedSourceDraft>> protectionOperation =
                allowInsecureHttp
                ? protection.ProtectRemotePlaylistAllowingInsecureHttpAsync(
                    sourceId,
                    prepared.Value!.NormalizedDisplayName,
                    locator,
                    cancellationToken)
                : protection.ProtectRemotePlaylistAsync(
                    sourceId,
                    prepared.Value!.NormalizedDisplayName,
                    locator,
                    cancellationToken);
            protectedDraft = await protectionOperation.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return StorageUnavailable();
        }

        if (!protectedDraft.IsSuccess)
        {
            return DomainResult.Failure<RemotePlaylistSourceOnboardingResult>(protectedDraft.Error!);
        }

        DomainResult<ContentSource> testingSource = ContentSource.Create(
            protectedDraft.Value,
            ContentSourceStatus.Testing,
            now,
            now);
        if (!testingSource.IsSuccess)
        {
            await TryDeleteProtectedConfigurationAsync(protectedDraft.Value!, now).ConfigureAwait(false);
            return StorageUnavailable();
        }

        RemotePlaylistCatalogImportResult import;
        try
        {
            import = await _importer.ImportAsync(testingSource.Value!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // The catalog transaction's commit state is unknown after an exception.
            // Deleting the protected locator could strand a committed catalog source.
            return StorageUnavailable();
        }

        if (import is null)
        {
            return StorageUnavailable();
        }

        if (import.Disposition is CatalogImportCommitDisposition.Committed)
        {
            return DomainResult.Success(
                new RemotePlaylistSourceOnboardingResult(
                    sourceId,
                    import.ImportedChannelCount!.Value,
                    import.WarningCount!.Value,
                    import.EntryLimitReached));
        }

        if (import.Disposition is CatalogImportCommitDisposition.Indeterminate)
        {
            return StorageUnavailable();
        }

        if (import.Disposition is not CatalogImportCommitDisposition.NotCommitted ||
            import.Error is null)
        {
            return StorageUnavailable();
        }

        bool deleted = await TryDeleteProtectedConfigurationAsync(
            protectedDraft.Value!,
            now).ConfigureAwait(false);
        return deleted
            ? DomainResult.Failure<RemotePlaylistSourceOnboardingResult>(import.Error)
            : StorageUnavailable();
    }

    private async ValueTask<bool> TryDeleteProtectedConfigurationAsync(
        ValidatedSourceDraft draft,
        DateTimeOffset now)
    {
        DomainResult<ContentSource> deletionPending = ContentSource.Create(
            draft,
            ContentSourceStatus.DeletionPending,
            now,
            now);
        if (!deletionPending.IsSuccess)
        {
            return false;
        }

        try
        {
            SecretStoreOperationResult deleted =
                await new SourceConfigurationProtectedRecordDeletionService(_secretStore)
                    .DeleteAsync(deletionPending.Value!, CancellationToken.None)
                    .ConfigureAwait(false);
            return deleted.IsSuccess;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private static DomainResult<RemotePlaylistSourceOnboardingResult> StorageUnavailable() =>
        DomainResult.Failure<RemotePlaylistSourceOnboardingResult>(
            DomainErrorCode.StorageUnavailable);

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
