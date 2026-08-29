using System.Security.Cryptography;

namespace IptvSuite.Application;

public sealed class SourceDraftProtectionService
{
    private readonly ISecretStore _secretStore;

    public SourceDraftProtectionService(ISecretStore secretStore)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        _secretStore = secretStore;
    }

    public async ValueTask<DomainResult<ValidatedSourceDraft>> ProtectXtreamAsync(
        SourceId sourceId,
        string? displayName,
        string? locator,
        string? username,
        string? password,
        CancellationToken cancellationToken = default) =>
        await ProtectXtreamCoreAsync(
            sourceId,
            displayName,
            locator,
            username,
            password,
            allowInsecureHttp: false,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<DomainResult<ValidatedSourceDraft>> ProtectXtreamAllowingInsecureHttpAsync(
        SourceId sourceId,
        string? displayName,
        string? locator,
        string? username,
        string? password,
        CancellationToken cancellationToken = default) =>
        await ProtectXtreamCoreAsync(
            sourceId,
            displayName,
            locator,
            username,
            password,
            allowInsecureHttp: true,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<DomainResult<ValidatedSourceDraft>> ProtectXtreamCoreAsync(
        SourceId sourceId,
        string? displayName,
        string? locator,
        string? username,
        string? password,
        bool allowInsecureHttp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSource(sourceId);
        DomainResult<PreparedXtreamSourceDraft> prepared = allowInsecureHttp
            ? SourceConfigurationValidator.PrepareXtreamAllowingInsecureHttp(
                displayName,
                locator,
                username,
                password)
            : SourceConfigurationValidator.PrepareXtream(
                displayName,
                locator,
                username,
                password);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Failure<ValidatedSourceDraft>(prepared.Error!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        SourceConfigurationId configurationId = SourceConfigurationId.Generate();
        ProtectedRecordOwner owner = ProtectedRecordOwner.ForSourceConfiguration(configurationId);
        byte[]? payload = null;
        try
        {
            payload = ProtectedSourcePayloadEncoder.EncodeXtreamSourceCredentials(
                locator!,
                username!,
                password!);
            SecretReferenceCreationResult created = await _secretStore.CreateCredentialsAsync(
                sourceId,
                owner,
                payload,
                cancellationToken).ConfigureAwait(false);
            if (!created.IsSuccess || created.Reference is null)
            {
                return StorageUnavailable();
            }

            // The protected record is committed when CreateCredentialsAsync succeeds.
            // Do not observe cancellation after this point: the directly awaited
            // successful result must retain the store-issued reference and source.
            ValidatedSourceDraft draft = SourceConfigurationValidator.CompleteXtream(
                sourceId,
                configurationId,
                prepared.Value!,
                created.Reference);
            return DomainResult.Success(draft);
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    public ValueTask<DomainResult<ValidatedSourceDraft>> ProtectRemotePlaylistAsync(
        SourceId sourceId,
        string? displayName,
        string? locator,
        CancellationToken cancellationToken = default) =>
        ProtectRemotePlaylistCoreAsync(
            sourceId,
            displayName,
            locator,
            allowInsecureHttp: false,
            cancellationToken: cancellationToken);

    public ValueTask<DomainResult<ValidatedSourceDraft>>
        ProtectRemotePlaylistAllowingInsecureHttpAsync(
            SourceId sourceId,
            string? displayName,
            string? locator,
            CancellationToken cancellationToken = default) =>
        ProtectRemotePlaylistCoreAsync(
            sourceId,
            displayName,
            locator,
            allowInsecureHttp: true,
            cancellationToken: cancellationToken);

    private async ValueTask<DomainResult<ValidatedSourceDraft>> ProtectRemotePlaylistCoreAsync(
        SourceId sourceId,
        string? displayName,
        string? locator,
        bool allowInsecureHttp,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSource(sourceId);
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared = allowInsecureHttp
            ? SourceConfigurationValidator.PrepareRemotePlaylistAllowingInsecureHttp(
                displayName,
                locator)
            : SourceConfigurationValidator.PrepareRemotePlaylist(displayName, locator);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Failure<ValidatedSourceDraft>(prepared.Error!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        SourceConfigurationId configurationId = SourceConfigurationId.Generate();
        ProtectedRecordOwner owner = ProtectedRecordOwner.ForSourceConfiguration(configurationId);
        byte[]? payload = null;
        try
        {
            payload = ProtectedSourcePayloadEncoder.EncodeRemotePlaylistLocator(locator!);
            ProtectedLocatorReferenceCreationResult created = await _secretStore.CreateLocatorAsync(
                sourceId,
                ProtectedValuePurpose.RemotePlaylistLocator,
                owner,
                payload,
                cancellationToken).ConfigureAwait(false);
            if (!created.IsSuccess || created.Reference is null)
            {
                return StorageUnavailable();
            }

            // See the credentials path above: success is the commit boundary, so the
            // directly awaited result must retain the committed record's binding.
            ValidatedSourceDraft draft = SourceConfigurationValidator.CompleteRemotePlaylist(
                sourceId,
                configurationId,
                prepared.Value!,
                created.Reference);
            return DomainResult.Success(draft);
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    private static DomainResult<ValidatedSourceDraft> StorageUnavailable() =>
        DomainResult.Failure<ValidatedSourceDraft>(DomainErrorCode.StorageUnavailable);

    private static void ValidateSource(SourceId sourceId)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A non-empty source identifier is required.", nameof(sourceId));
        }
    }
}
