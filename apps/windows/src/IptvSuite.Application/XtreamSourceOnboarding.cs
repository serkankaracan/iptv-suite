using System.Diagnostics;

namespace IptvSuite.Application;

[DebuggerDisplay("[XTREAM-SOURCE-ONBOARDING-RESULT]")]
public sealed class XtreamSourceOnboardingResult
{
    internal XtreamSourceOnboardingResult(
        SourceId sourceId,
        ContentCatalogCounts counts,
        bool previousConfigurationCleanupPending = false)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A source identifier is required.", nameof(sourceId));
        }

        SourceId = sourceId;
        Counts = counts ?? throw new ArgumentNullException(nameof(counts));
        PreviousConfigurationCleanupPending = previousConfigurationCleanupPending;
    }

    public SourceId SourceId { get; }

    public ContentCatalogCounts Counts { get; }

    public bool PreviousConfigurationCleanupPending { get; }

    public override string ToString() => "[XTREAM-SOURCE-ONBOARDING-RESULT]";
}

public sealed class XtreamSourceOnboardingService
{
    private readonly ISecretStore _secretStore;
    private readonly IXtreamCatalogImportService _importer;
    private readonly TimeProvider _timeProvider;

    public XtreamSourceOnboardingService(
        ISecretStore secretStore,
        IXtreamCatalogImportService importer,
        TimeProvider timeProvider)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ValueTask<DomainResult<XtreamSourceOnboardingResult>> AddAsync(
        string? displayName,
        string? locator,
        string? username,
        string? password,
        CancellationToken cancellationToken = default) =>
        AddCoreAsync(
            displayName,
            locator,
            username,
            password,
            allowInsecureHttp: false,
            replacementSource: null,
            cancellationToken);

    public ValueTask<DomainResult<XtreamSourceOnboardingResult>>
        AddAllowingInsecureHttpAsync(
            string? displayName,
            string? locator,
            string? username,
            string? password,
            CancellationToken cancellationToken = default) =>
        AddCoreAsync(
            displayName,
            locator,
            username,
            password,
            allowInsecureHttp: true,
            replacementSource: null,
            cancellationToken);

    public ValueTask<DomainResult<XtreamSourceOnboardingResult>> ReplaceAsync(
        ContentSource source,
        string? displayName,
        string? locator,
        string? username,
        string? password,
        CancellationToken cancellationToken = default) =>
        AddCoreAsync(
            displayName,
            locator,
            username,
            password,
            allowInsecureHttp: false,
            replacementSource: source,
            cancellationToken);

    public ValueTask<DomainResult<XtreamSourceOnboardingResult>>
        ReplaceAllowingInsecureHttpAsync(
            ContentSource source,
            string? displayName,
            string? locator,
            string? username,
            string? password,
            CancellationToken cancellationToken = default) =>
        AddCoreAsync(
            displayName,
            locator,
            username,
            password,
            allowInsecureHttp: true,
            replacementSource: source,
            cancellationToken);

    private async ValueTask<DomainResult<XtreamSourceOnboardingResult>> AddCoreAsync(
        string? displayName,
        string? locator,
        string? username,
        string? password,
        bool allowInsecureHttp,
        ContentSource? replacementSource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (replacementSource is not null &&
            (replacementSource.Id.IsEmpty ||
             replacementSource.Kind != SourceKind.XtreamCompatible ||
             replacementSource.Status == ContentSourceStatus.DeletionPending))
        {
            return DomainResult.Failure<XtreamSourceOnboardingResult>(
                DomainErrorCode.DomainInvariantViolation);
        }

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

        SourceId sourceId = replacementSource?.Id ?? SourceId.Generate();
        DomainResult<ValidatedSourceDraft> protectedDraft;
        try
        {
            var protection = new SourceDraftProtectionService(_secretStore);
            protectedDraft = allowInsecureHttp
                ? await protection.ProtectXtreamAllowingInsecureHttpAsync(
                    sourceId,
                    displayName,
                    locator,
                    username,
                    password,
                    cancellationToken).ConfigureAwait(false)
                : await protection.ProtectXtreamAsync(
                    sourceId,
                    displayName,
                    locator,
                    username,
                    password,
                    cancellationToken).ConfigureAwait(false);
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
            return DomainResult.Failure<XtreamSourceOnboardingResult>(protectedDraft.Error!);
        }

        DomainResult<ContentSource> source = ContentSource.Create(
            protectedDraft.Value,
            ContentSourceStatus.Testing,
            now,
            now);
        if (!source.IsSuccess)
        {
            await TryDeleteProtectedConfigurationAsync(protectedDraft.Value!, now)
                .ConfigureAwait(false);
            return StorageUnavailable();
        }

        XtreamCatalogImportResult imported;
        try
        {
            imported = await _importer.ImportWithDispositionAsync(
                source.Value!,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // A storage commit exception can be indeterminate. Retaining the protected
            // configuration is safer than stranding an already committed catalog row.
            return StorageUnavailable();
        }

        if (imported is null)
        {
            return StorageUnavailable();
        }

        if (imported.Disposition == CatalogImportCommitDisposition.Committed &&
            imported.Counts is not null)
        {
            bool cleanupPending = replacementSource is not null &&
                !await TryDeleteProtectedConfigurationAsync(replacementSource, now)
                    .ConfigureAwait(false);
            return DomainResult.Success(new XtreamSourceOnboardingResult(
                sourceId,
                imported.Counts,
                cleanupPending));
        }

        if (imported.Disposition == CatalogImportCommitDisposition.Indeterminate)
        {
            return StorageUnavailable();
        }

        if (imported.Disposition != CatalogImportCommitDisposition.NotCommitted ||
            imported.Error is null)
        {
            return StorageUnavailable();
        }

        bool deleted = await TryDeleteProtectedConfigurationAsync(
            protectedDraft.Value!,
            now).ConfigureAwait(false);
        return deleted
            ? DomainResult.Failure<XtreamSourceOnboardingResult>(imported.Error)
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

    private ValueTask<bool> TryDeleteProtectedConfigurationAsync(
        ContentSource source,
        DateTimeOffset now)
    {
        var draft = new ValidatedSourceDraft(
            source.Id,
            source.DisplayName,
            source.Configuration);
        return TryDeleteProtectedConfigurationAsync(draft, now);
    }

    private static DomainResult<XtreamSourceOnboardingResult> StorageUnavailable() =>
        DomainResult.Failure<XtreamSourceOnboardingResult>(
            DomainErrorCode.StorageUnavailable);

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
