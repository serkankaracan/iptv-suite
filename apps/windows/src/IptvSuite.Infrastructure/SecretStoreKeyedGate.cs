using IptvSuite.Application;

namespace IptvSuite.Infrastructure;

internal sealed class SecretStoreKeyedGate
{
    private readonly Dictionary<OperationKey, Entry> _entries = [];
    private readonly object _sync = new();

    internal async ValueTask<Releaser> EnterAsync(
        string storageDirectoryPath,
        SecretStoreKey key,
        CancellationToken cancellationToken)
    {
        var operationKey = new OperationKey(storageDirectoryPath, key);
        Entry entry;

        lock (_sync)
        {
            if (!_entries.TryGetValue(operationKey, out entry!))
            {
                entry = new Entry();
                _entries.Add(operationKey, entry);
            }

            entry.ReferenceCount++;
        }

        bool entered = false;

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return new Releaser(this, operationKey, entry);
        }
        catch
        {
            Release(operationKey, entry, entered);
            throw;
        }
    }

    private void Release(OperationKey key, Entry entry, bool entered)
    {
        bool disposeEntry;

        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out Entry? registeredEntry) ||
                !ReferenceEquals(registeredEntry, entry) ||
                entry.ReferenceCount <= 0)
            {
                throw new InvalidOperationException("The protected-store operation gate is inconsistent.");
            }

            if (entered)
            {
                entry.Semaphore.Release();
            }

            entry.ReferenceCount--;
            disposeEntry = entry.ReferenceCount == 0;

            if (disposeEntry)
            {
                _entries.Remove(key);
            }
        }

        if (disposeEntry)
        {
            entry.Dispose();
        }
    }

    internal sealed class Releaser : IDisposable
    {
        private readonly Entry _entry;
        private readonly OperationKey _key;
        private SecretStoreKeyedGate? _owner;

        internal Releaser(SecretStoreKeyedGate owner, OperationKey key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            SecretStoreKeyedGate? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_key, _entry, entered: true);
        }
    }

    internal sealed class Entry : IDisposable
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int ReferenceCount { get; set; }

        public void Dispose() => Semaphore.Dispose();
    }

    internal readonly struct OperationKey : IEquatable<OperationKey>
    {
        internal OperationKey(string storageDirectoryPath, SecretStoreKey key)
        {
            StorageDirectoryPath = storageDirectoryPath;
            Key = key;
        }

        private string StorageDirectoryPath { get; }

        private SecretStoreKey Key { get; }

        public bool Equals(OperationKey other) =>
            StringComparer.OrdinalIgnoreCase.Equals(StorageDirectoryPath, other.StorageDirectoryPath) &&
            Key.Equals(other.Key);

        public override bool Equals(object? obj) => obj is OperationKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(StorageDirectoryPath),
            Key);

        public override string ToString() => "[PROTECTED-STORE-OPERATION-KEY]";
    }
}
