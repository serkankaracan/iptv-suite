using System.Security.Cryptography;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.IntegrationTests;

internal sealed class M4InMemorySecretStore : ISecretStore, IDisposable
{
    private readonly Dictionary<FakeRecordKey, byte[]> _records = [];
    private readonly List<byte[]> _retiredBuffers = [];
    private readonly object _sync = new();
    private bool _disposed;

    internal int ActiveRecordCount
    {
        get
        {
            lock (_sync)
            {
                return _records.Count;
            }
        }
    }

    internal bool RetiredBuffersAreZeroed
    {
        get
        {
            lock (_sync)
            {
                return _retiredBuffers.All(IsZeroed);
            }
        }
    }

    public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
        SourceId sourceId,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(sourceId);
        ValidateValue(value);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            SecretReference reference;
            FakeRecordKey key;
            do
            {
                reference = CreateSecretReference();
                key = ForCredentials(sourceId, reference);
            }
            while (_records.ContainsKey(key));

            _records.Add(key, value.ToArray());
            return ValueTask.FromResult(SecretReferenceCreationResult.Succeeded(reference));
        }
    }

    public ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(sourceId);
        ValidateLocatorPurpose(purpose);
        ValidateValue(value);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            ProtectedLocatorReference reference;
            FakeRecordKey key;
            do
            {
                reference = CreateLocatorReference();
                key = ForLocator(sourceId, purpose, reference);
            }
            while (_records.ContainsKey(key));

            _records.Add(key, value.ToArray());
            return ValueTask.FromResult(ProtectedLocatorReferenceCreationResult.Succeeded(reference));
        }
    }

    public ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
        SourceId sourceId,
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(sourceId);
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(ForCredentials(sourceId, reference)));
    }

    public ValueTask<SecretStoreReadResult> ReadLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(sourceId);
        ValidateLocatorPurpose(purpose);
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(ForLocator(sourceId, purpose, reference)));
    }

    public ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
        SourceId sourceId,
        SecretReference reference,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(sourceId);
        ArgumentNullException.ThrowIfNull(reference);
        ValidateValue(value);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Update(ForCredentials(sourceId, reference), value));
    }

    public ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(sourceId);
        ValidateLocatorPurpose(purpose);
        ArgumentNullException.ThrowIfNull(reference);
        ValidateValue(value);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Update(ForLocator(sourceId, purpose, reference), value));
    }

    public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
        SourceId sourceId,
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(sourceId);
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Delete(ForCredentials(sourceId, reference)));
    }

    public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(sourceId);
        ValidateLocatorPurpose(purpose);
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Delete(ForLocator(sourceId, purpose, reference)));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            foreach (byte[] value in _records.Values)
            {
                Retire(value);
            }

            _records.Clear();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    public override string ToString() => "[M4-IN-MEMORY-SECRET-STORE]";

    private static FakeRecordKey ForCredentials(SourceId sourceId, SecretReference reference) =>
        new(sourceId, ProtectedValuePurpose.SourceCredentials, reference);

    private static FakeRecordKey ForLocator(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference) => new(sourceId, purpose, reference);

    private static bool IsZeroed(byte[] buffer) => buffer.All(value => value == 0);

    private static SecretReference CreateSecretReference()
    {
        DomainResult<SecretReference> result = SecretReference.Parse($"secret-ref-v1:{Guid.NewGuid():N}");
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic secret reference could not be created.");
    }

    private static ProtectedLocatorReference CreateLocatorReference()
    {
        DomainResult<ProtectedLocatorReference> result =
            ProtectedLocatorReference.Parse($"locator-ref-v1:{Guid.NewGuid():N}");
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic locator reference could not be created.");
    }

    private static void ValidateSource(SourceId sourceId)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A non-empty source identifier is required.", nameof(sourceId));
        }
    }

    private static void ValidateLocatorPurpose(ProtectedValuePurpose purpose)
    {
        if (purpose is ProtectedValuePurpose.SourceCredentials || !Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), "A locator purpose is required.");
        }
    }

    private static void ValidateValue(ReadOnlyMemory<byte> value)
    {
        if (value.IsEmpty || value.Length > SecretStoreLimits.MaxProtectedValueBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Protected value length is outside allowed bounds.");
        }
    }

    private SecretStoreReadResult Read(FakeRecordKey key)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _records.TryGetValue(key, out byte[]? value)
                ? SecretStoreReadResult.Succeeded(SecretLease.CopyFrom(value))
                : SecretStoreReadResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable);
        }
    }

    private SecretStoreOperationResult Update(FakeRecordKey key, ReadOnlyMemory<byte> value)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_records.TryGetValue(key, out byte[]? previous))
            {
                return SecretStoreOperationResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable);
            }

            _records[key] = value.ToArray();
            Retire(previous);
            return SecretStoreOperationResult.Succeeded();
        }
    }

    private SecretStoreOperationResult Delete(FakeRecordKey key)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_records.Remove(key, out byte[]? value))
            {
                Retire(value);
            }

            return SecretStoreOperationResult.Succeeded();
        }
    }

    private void Retire(byte[] value)
    {
        CryptographicOperations.ZeroMemory(value);
        _retiredBuffers.Add(value);
    }

    private readonly record struct FakeRecordKey(
        SourceId SourceId,
        ProtectedValuePurpose Purpose,
        object Reference)
    {
        public override string ToString() => "[M4-FAKE-RECORD-KEY]";
    }
}
