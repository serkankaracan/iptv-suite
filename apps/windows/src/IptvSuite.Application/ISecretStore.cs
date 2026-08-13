namespace IptvSuite.Application;

public interface ISecretStore
{
    /// <summary>
    /// Creates a protected source-credentials record and issues its opaque reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="value"/> is borrowed, caller-owned memory. The implementation may read it
    /// only until the returned operation completes; it must not mutate it or retain the memory,
    /// its backing storage, or any plaintext copy after completion. The caller must keep the value
    /// unchanged until completion and may zero or release it immediately afterwards.
    /// </para>
    /// <para>
    /// A successful result is the logical commit boundary. Its store-issued reference identifies
    /// the final protected record bound to the exact source, credentials purpose, and reference
    /// kind. Once that record is committed, cancellation requested concurrently must not change
    /// the outcome to a failed result or <see cref="OperationCanceledException"/>.
    /// </para>
    /// <para>
    /// A returned failed result, or <see cref="OperationCanceledException"/> observed by the
    /// implementation, guarantees that no final record was committed. Implementations must observe
    /// cancellation before commit and must not observe it after commit. An unexpected exception or
    /// process termination is an indeterminate outcome; callers must not assume rollback. A record
    /// committed before its result could be observed must be handled by a future startup
    /// reconciliation flow.
    /// </para>
    /// <para>
    /// Create is not idempotent: every successful invocation issues a new reference. Temporary
    /// artifacts remain the implementation's cleanup responsibility.
    /// </para>
    /// </remarks>
    ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
        SourceId sourceId,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a protected locator record for the exact purpose and issues its opaque reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="value"/> is borrowed, caller-owned memory. The implementation may read it
    /// only until the returned operation completes; it must not mutate it or retain the memory,
    /// its backing storage, or any plaintext copy after completion. The caller must keep the value
    /// unchanged until completion and may zero or release it immediately afterwards.
    /// </para>
    /// <para>
    /// A successful result is the logical commit boundary. Its store-issued reference identifies
    /// the final protected record bound to the exact source, <paramref name="purpose"/>, and
    /// reference kind. Once that record is committed, cancellation requested concurrently must not
    /// change the outcome to a failed result or <see cref="OperationCanceledException"/>.
    /// </para>
    /// <para>
    /// A returned failed result, or <see cref="OperationCanceledException"/> observed by the
    /// implementation, guarantees that no final record was committed. Implementations must observe
    /// cancellation before commit and must not observe it after commit. An unexpected exception or
    /// process termination is an indeterminate outcome; callers must not assume rollback. A record
    /// committed before its result could be observed must be handled by a future startup
    /// reconciliation flow.
    /// </para>
    /// <para>
    /// Create is not idempotent: every successful invocation issues a new reference. Temporary
    /// artifacts remain the implementation's cleanup responsibility.
    /// </para>
    /// </remarks>
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
