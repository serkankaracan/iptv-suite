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

    /// <summary>
    /// Reads the protected credentials bound to the exact source and opaque reference.
    /// </summary>
    /// <remarks>
    /// A successful result transfers ownership of exactly one plaintext <see cref="SecretLease"/>
    /// to the caller. The caller must dispose it as soon as the operation-specific consumer has
    /// finished and must not retain derived plaintext copies. A returned failure or observed
    /// <see cref="OperationCanceledException"/> carries no lease. An unexpected exception does not
    /// guarantee that no plaintext was produced or retained; callers must not log exception details
    /// or sensitive context.
    /// </remarks>
    ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
        SourceId sourceId,
        SecretReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the protected locator bound to the exact source, purpose, and opaque reference.
    /// </summary>
    /// <remarks>
    /// A successful result transfers ownership of exactly one plaintext <see cref="SecretLease"/>
    /// to the caller. The caller must dispose it as soon as the operation-specific consumer has
    /// finished and must not retain derived plaintext copies. A returned failure or observed
    /// <see cref="OperationCanceledException"/> carries no lease. An unexpected exception does not
    /// guarantee that no plaintext was produced or retained; callers must not log exception details
    /// or sensitive context.
    /// </remarks>
    ValueTask<SecretStoreReadResult> ReadLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the credentials record bound to the exact source and reference.
    /// </summary>
    /// <remarks>
    /// <paramref name="value"/> is borrowed, caller-owned memory. The implementation may read it
    /// only until completion; it must not mutate it or retain the memory, its backing storage, or
    /// any plaintext copy afterwards. The caller must keep it unchanged until completion and may
    /// zero or release it immediately afterwards. A successful result is the replacement commit
    /// boundary; cancellation must not be observed after commit. A returned failure or observed
    /// <see cref="OperationCanceledException"/> guarantees that the previous final record remains
    /// selected. An unexpected exception or process termination is indeterminate and requires
    /// future reconciliation rather than an assumed rollback.
    /// </remarks>
    ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
        SourceId sourceId,
        SecretReference reference,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the locator record bound to the exact source, purpose, and reference.
    /// </summary>
    /// <remarks>
    /// <paramref name="value"/> is borrowed, caller-owned memory. The implementation may read it
    /// only until completion; it must not mutate it or retain the memory, its backing storage, or
    /// any plaintext copy afterwards. The caller must keep it unchanged until completion and may
    /// zero or release it immediately afterwards. A successful result is the replacement commit
    /// boundary; cancellation must not be observed after commit. A returned failure or observed
    /// <see cref="OperationCanceledException"/> guarantees that the previous final record remains
    /// selected. An unexpected exception or process termination is indeterminate and requires
    /// future reconciliation rather than an assumed rollback.
    /// </remarks>
    ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently deletes the credentials record bound to the exact source and reference.
    /// </summary>
    /// <remarks>
    /// Success means the exact record is absent, including when it was already absent. Cancellation
    /// is observed before the delete commit. A failed result, unexpected exception, or process
    /// termination is an indeterminate deletion outcome and must remain fail-closed until an
    /// idempotent retry or future reconciliation confirms absence.
    /// </remarks>
    ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
        SourceId sourceId,
        SecretReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently deletes the locator bound to the exact source, purpose, and reference.
    /// </summary>
    /// <remarks>
    /// Success means the exact record is absent, including when it was already absent. Cancellation
    /// is observed before the delete commit. A failed result, unexpected exception, or process
    /// termination is an indeterminate deletion outcome and must remain fail-closed until an
    /// idempotent retry or future reconciliation confirms absence.
    /// </remarks>
    ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        CancellationToken cancellationToken = default);
}
