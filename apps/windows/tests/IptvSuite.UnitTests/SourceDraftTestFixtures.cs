namespace IptvSuite.UnitTests;

internal static class SourceDraftTestFixtures
{
    internal static SecretReference CreateSecretReference() =>
        ParseSecretReference($"secret-ref-v1:{Guid.NewGuid():N}");

    internal static ProtectedLocatorReference CreateLocatorReference() =>
        ParseLocatorReference($"locator-ref-v1:{Guid.NewGuid():N}");

    internal static ValidatedSourceDraft CreateRemoteDraft(
        SourceId sourceId,
        string displayName = "Synthetic Playlist",
        string locator = "https://fixtures.invalid/catalog.m3u")
    {
        var service = new SourceDraftProtectionService(new ReferenceIssuingSecretStore());
        DomainResult<ValidatedSourceDraft> result = service.ProtectRemotePlaylistAsync(
                sourceId,
                displayName,
                locator)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic protected source draft could not be created.");
    }

    private static SecretReference ParseSecretReference(string value)
    {
        DomainResult<SecretReference> result = SecretReference.Parse(value);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic secret reference could not be created.");
    }

    private static ProtectedLocatorReference ParseLocatorReference(string value)
    {
        DomainResult<ProtectedLocatorReference> result = ProtectedLocatorReference.Parse(value);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic locator reference could not be created.");
    }

    private sealed class ReferenceIssuingSecretStore : ISecretStore
    {
        public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
            SourceId sourceId,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                SecretReferenceCreationResult.Succeeded(CreateSecretReference()));
        }

        public ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                ProtectedLocatorReferenceCreationResult.Succeeded(CreateLocatorReference()));
        }

        public ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
            SourceId sourceId,
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreReadResult> ReadLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
            SourceId sourceId,
            SecretReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedLocatorReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
            SourceId sourceId,
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        private static InvalidOperationException UnexpectedOperation() =>
            new("A synthetic reference-issuing store received an unexpected operation.");
    }
}
