namespace IptvSuite.Application;

public interface ISecretStore
{
    ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
        SourceId sourceId,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default);

    ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default);

    ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
        SourceId sourceId,
        SecretReference reference,
        CancellationToken cancellationToken = default);

    ValueTask<SecretStoreReadResult> ReadLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        CancellationToken cancellationToken = default);

    ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
        SourceId sourceId,
        SecretReference reference,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default);

    ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default);

    ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
        SourceId sourceId,
        SecretReference reference,
        CancellationToken cancellationToken = default);

    ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        CancellationToken cancellationToken = default);
}
