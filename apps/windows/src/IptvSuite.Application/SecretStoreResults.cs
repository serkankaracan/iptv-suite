using System.Diagnostics;
using System.Text.Json.Serialization;

namespace IptvSuite.Application;

public enum SecretStoreFailure
{
    None = 0,
    ProtectedRecordUnavailable = 1,
    StorageUnavailable = 2,
}

[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class SecretStoreInitializationResult
{
    private SecretStoreInitializationResult(ISecretStore? store, SecretStoreFailure failure)
    {
        Store = store;
        Failure = failure;
    }

    public bool IsSuccess => Store is not null && Failure is SecretStoreFailure.None;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    [JsonIgnore]
    public ISecretStore? Store { get; }

    public SecretStoreFailure Failure { get; }

    public static SecretStoreInitializationResult Succeeded(ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new SecretStoreInitializationResult(store, SecretStoreFailure.None);
    }

    public static SecretStoreInitializationResult Failed(SecretStoreFailure failure) =>
        failure is SecretStoreFailure.StorageUnavailable
            ? new SecretStoreInitializationResult(null, failure)
            : throw new ArgumentOutOfRangeException(
                nameof(failure),
                failure,
                "The storage-unavailable failure status is required.");

    public override string ToString() => DebuggerDisplay;

    private string DebuggerDisplay =>
        $"secret-store-initialization;success={IsSuccess};failure={Failure}";
}

public sealed record SecretReferenceCreationResult
{
    private SecretReferenceCreationResult(SecretReference? reference, SecretStoreFailure failure)
    {
        Reference = reference;
        Failure = failure;
    }

    public bool IsSuccess => Reference is not null && Failure is SecretStoreFailure.None;

    public SecretReference? Reference { get; }

    public SecretStoreFailure Failure { get; }

    public static SecretReferenceCreationResult Succeeded(SecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new SecretReferenceCreationResult(reference, SecretStoreFailure.None);
    }

    public static SecretReferenceCreationResult Failed(SecretStoreFailure failure) =>
        new(null, ValidateFailure(failure));

    private static SecretStoreFailure ValidateFailure(SecretStoreFailure failure) =>
        failure is SecretStoreFailure.ProtectedRecordUnavailable or SecretStoreFailure.StorageUnavailable
            ? failure
            : throw new ArgumentOutOfRangeException(nameof(failure), failure, "A failure status is required.");
}

public sealed record ProtectedLocatorReferenceCreationResult
{
    private ProtectedLocatorReferenceCreationResult(
        ProtectedLocatorReference? reference,
        SecretStoreFailure failure)
    {
        Reference = reference;
        Failure = failure;
    }

    public bool IsSuccess => Reference is not null && Failure is SecretStoreFailure.None;

    public ProtectedLocatorReference? Reference { get; }

    public SecretStoreFailure Failure { get; }

    public static ProtectedLocatorReferenceCreationResult Succeeded(ProtectedLocatorReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new ProtectedLocatorReferenceCreationResult(reference, SecretStoreFailure.None);
    }

    public static ProtectedLocatorReferenceCreationResult Failed(SecretStoreFailure failure) =>
        new(null, ValidateFailure(failure));

    private static SecretStoreFailure ValidateFailure(SecretStoreFailure failure) =>
        failure is SecretStoreFailure.ProtectedRecordUnavailable or SecretStoreFailure.StorageUnavailable
            ? failure
            : throw new ArgumentOutOfRangeException(nameof(failure), failure, "A failure status is required.");
}

public sealed record SecretStoreReadResult
{
    private SecretStoreReadResult(SecretLease? lease, SecretStoreFailure failure)
    {
        Lease = lease;
        Failure = failure;
    }

    public bool IsSuccess => Lease is not null && Failure is SecretStoreFailure.None;

    public SecretLease? Lease { get; }

    public SecretStoreFailure Failure { get; }

    public static SecretStoreReadResult Succeeded(SecretLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new SecretStoreReadResult(lease, SecretStoreFailure.None);
    }

    public static SecretStoreReadResult Failed(SecretStoreFailure failure) =>
        new(null, ValidateFailure(failure));

    private static SecretStoreFailure ValidateFailure(SecretStoreFailure failure) =>
        failure is SecretStoreFailure.ProtectedRecordUnavailable or SecretStoreFailure.StorageUnavailable
            ? failure
            : throw new ArgumentOutOfRangeException(nameof(failure), failure, "A failure status is required.");
}

public sealed record SecretStoreOperationResult
{
    private SecretStoreOperationResult(bool isSuccess, SecretStoreFailure failure)
    {
        IsSuccess = isSuccess;
        Failure = failure;
    }

    public bool IsSuccess { get; }

    public SecretStoreFailure Failure { get; }

    public static SecretStoreOperationResult Succeeded() => new(true, SecretStoreFailure.None);

    public static SecretStoreOperationResult Failed(SecretStoreFailure failure) =>
        failure is SecretStoreFailure.ProtectedRecordUnavailable or SecretStoreFailure.StorageUnavailable
            ? new SecretStoreOperationResult(false, failure)
            : throw new ArgumentOutOfRangeException(nameof(failure), failure, "A failure status is required.");
}
