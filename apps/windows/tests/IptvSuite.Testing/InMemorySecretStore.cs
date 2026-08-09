using System.Security.Cryptography;

namespace IptvSuite.Testing;

public sealed class InMemorySecretStore : IDisposable
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    private readonly List<SecretStoreOperation> _operations = [];
    private readonly object _sync = new();
    private bool _disposed;

    public IReadOnlyList<SecretStoreOperation> Operations
    {
        get
        {
            lock (_sync)
            {
                return [.. _operations];
            }
        }
    }

    public ValueTask WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_values.Remove(key, out byte[]? previous))
            {
                CryptographicOperations.ZeroMemory(previous);
            }

            _values.Add(key, value.ToArray());
            _operations.Add(new SecretStoreOperation(SecretStoreOperationType.Write, key));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _operations.Add(new SecretStoreOperation(SecretStoreOperationType.Read, key));
            return ValueTask.FromResult(_values.TryGetValue(key, out byte[]? value) ? value.ToArray() : null);
        }
    }

    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool removed = _values.Remove(key, out byte[]? value);
            if (value is not null)
            {
                CryptographicOperations.ZeroMemory(value);
            }

            _operations.Add(new SecretStoreOperation(SecretStoreOperationType.Delete, key));
            return ValueTask.FromResult(removed);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            foreach (byte[] value in _values.Values)
            {
                CryptographicOperations.ZeroMemory(value);
            }

            _values.Clear();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}

public enum SecretStoreOperationType
{
    Write,
    Read,
    Delete,
}

public sealed record SecretStoreOperation(SecretStoreOperationType Type, string Key);
