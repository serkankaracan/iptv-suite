using System.Runtime.Versioning;
using System.Security.Cryptography;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class DpapiCurrentUserSecretStore : ISecretStore
{
    private const int FileBufferSize = 4096;
    private const int MaxMoveAttempts = 8;
    // Coordinates adapter instances in this process; this is not a cross-process lock.
    private static readonly SecretStoreKeyedGate SharedOperationGate = new();
    private readonly string _storageDirectoryPath;
    private readonly string _storageDirectoryPrefix;

    public DpapiCurrentUserSecretStore(string storageDirectoryPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The protected store requires Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectoryPath);

        if (!Path.IsPathFullyQualified(storageDirectoryPath))
        {
            throw new ArgumentException("The protected-store directory must be fully qualified.", nameof(storageDirectoryPath));
        }

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageDirectoryPath));
        string? volumeRoot = Path.GetPathRoot(fullPath);

        if (volumeRoot is null ||
            string.Equals(
                Path.TrimEndingDirectorySeparator(volumeRoot),
                fullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A dedicated protected-store directory is required.", nameof(storageDirectoryPath));
        }

        _storageDirectoryPath = fullPath;
        _storageDirectoryPrefix = Path.EndsInDirectorySeparator(fullPath)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;

        EnsureStorageDirectoryForWrite();
    }

    public async ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
        SourceId sourceId,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        ValidateValue(value);
        (SecretReference reference, SecretStoreKey key) = SecretStoreKey.IssueCredentials(sourceId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await CreateRecordAsync(key, value, cancellationToken).ConfigureAwait(false);
            return SecretReferenceCreationResult.Succeeded(reference);
        }
        catch (CryptographicException)
        {
            return SecretReferenceCreationResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return SecretReferenceCreationResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
        catch (IOException)
        {
            return SecretReferenceCreationResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
    }

    public async ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        ValidateValue(value);
        (ProtectedLocatorReference reference, SecretStoreKey key) = SecretStoreKey.IssueLocator(sourceId, purpose);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await CreateRecordAsync(key, value, cancellationToken).ConfigureAwait(false);
            return ProtectedLocatorReferenceCreationResult.Succeeded(reference);
        }
        catch (CryptographicException)
        {
            return ProtectedLocatorReferenceCreationResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return ProtectedLocatorReferenceCreationResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
        catch (IOException)
        {
            return ProtectedLocatorReferenceCreationResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
    }

    public ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
        SourceId sourceId,
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        SecretStoreKey key = SecretStoreKey.ForCredentials(sourceId, reference);
        return ReadRecordAsync(key, cancellationToken);
    }

    public ValueTask<SecretStoreReadResult> ReadLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        CancellationToken cancellationToken = default)
    {
        SecretStoreKey key = SecretStoreKey.ForLocator(sourceId, purpose, reference);
        return ReadRecordAsync(key, cancellationToken);
    }

    public ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
        SourceId sourceId,
        SecretReference reference,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        ValidateValue(value);
        SecretStoreKey key = SecretStoreKey.ForCredentials(sourceId, reference);
        return UpdateRecordAsync(key, value, cancellationToken);
    }

    public ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        ValidateValue(value);
        SecretStoreKey key = SecretStoreKey.ForLocator(sourceId, purpose, reference);
        return UpdateRecordAsync(key, value, cancellationToken);
    }

    public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
        SourceId sourceId,
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        SecretStoreKey key = SecretStoreKey.ForCredentials(sourceId, reference);
        return DeleteRecordAsync(key, cancellationToken);
    }

    public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        CancellationToken cancellationToken = default)
    {
        SecretStoreKey key = SecretStoreKey.ForLocator(sourceId, purpose, reference);
        return DeleteRecordAsync(key, cancellationToken);
    }

    private async ValueTask CreateRecordAsync(
        SecretStoreKey key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        using SecretStoreKeyedGate.Releaser operationGate = await SharedOperationGate.EnterAsync(
            _storageDirectoryPath,
            key,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStorageDirectoryForWrite();
        string recordPath = GetRecordPath(key);

        if (TryEnsureRegularFile(recordPath))
        {
            throw new IOException("The protected record already exists.");
        }

        byte[]? protectedRecord = null;

        try
        {
            protectedRecord = DpapiProtectedEnvelopeCodec.Protect(key, value.Span);
            await WriteTemporaryAndCommitAsync(
                recordPath,
                protectedRecord,
                replaceExisting: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (protectedRecord is not null)
            {
                CryptographicOperations.ZeroMemory(protectedRecord);
            }
        }
    }

    private async ValueTask<SecretStoreReadResult> ReadRecordAsync(
        SecretStoreKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SecretStoreKeyedGate.Releaser operationGate = await SharedOperationGate.EnterAsync(
            _storageDirectoryPath,
            key,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? protectedRecord = null;
        byte[]? ownedValue = null;

        try
        {
            protectedRecord = await TryReadProtectedRecordAsync(key, cancellationToken).ConfigureAwait(false);

            if (protectedRecord is null)
            {
                return SecretStoreReadResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable);
            }

            if (!DpapiProtectedEnvelopeCodec.TryUnprotect(key, protectedRecord, out ownedValue) || ownedValue is null)
            {
                return SecretStoreReadResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable);
            }

            cancellationToken.ThrowIfCancellationRequested();
            SecretLease lease = SecretLease.TakeOwnership(ownedValue);
            ownedValue = null;
            return SecretStoreReadResult.Succeeded(lease);
        }
        catch (CryptographicException)
        {
            return SecretStoreReadResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable);
        }
        catch (FileNotFoundException)
        {
            return SecretStoreReadResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable);
        }
        catch (DirectoryNotFoundException)
        {
            return SecretStoreReadResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return SecretStoreReadResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
        catch (IOException)
        {
            return SecretStoreReadResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
        finally
        {
            if (protectedRecord is not null)
            {
                CryptographicOperations.ZeroMemory(protectedRecord);
            }

            if (ownedValue is not null)
            {
                CryptographicOperations.ZeroMemory(ownedValue);
            }
        }
    }

    private async ValueTask<SecretStoreOperationResult> UpdateRecordAsync(
        SecretStoreKey key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SecretStoreKeyedGate.Releaser operationGate = await SharedOperationGate.EnterAsync(
            _storageDirectoryPath,
            key,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? protectedRecord = null;

        try
        {
            if (!TryEnsureExistingStorageDirectory() || !TryEnsureRegularFile(GetRecordPath(key)))
            {
                return SecretStoreOperationResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable);
            }

            protectedRecord = DpapiProtectedEnvelopeCodec.Protect(key, value.Span);
            await WriteTemporaryAndCommitAsync(
                GetRecordPath(key),
                protectedRecord,
                replaceExisting: true,
                cancellationToken).ConfigureAwait(false);
            return SecretStoreOperationResult.Succeeded();
        }
        catch (CryptographicException)
        {
            return SecretStoreOperationResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
        catch (FileNotFoundException)
        {
            return SecretStoreOperationResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable);
        }
        catch (DirectoryNotFoundException)
        {
            return SecretStoreOperationResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return SecretStoreOperationResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
        catch (IOException)
        {
            return SecretStoreOperationResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
        finally
        {
            if (protectedRecord is not null)
            {
                CryptographicOperations.ZeroMemory(protectedRecord);
            }
        }
    }

    private async ValueTask<SecretStoreOperationResult> DeleteRecordAsync(
        SecretStoreKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SecretStoreKeyedGate.Releaser operationGate = await SharedOperationGate.EnterAsync(
            _storageDirectoryPath,
            key,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!TryEnsureExistingStorageDirectory())
            {
                return SecretStoreOperationResult.Succeeded();
            }

            string recordPath = GetRecordPath(key);

            if (!TryEnsureRegularFile(recordPath))
            {
                return SecretStoreOperationResult.Succeeded();
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(recordPath);
            return SecretStoreOperationResult.Succeeded();
        }
        catch (FileNotFoundException)
        {
            return SecretStoreOperationResult.Succeeded();
        }
        catch (DirectoryNotFoundException)
        {
            return SecretStoreOperationResult.Succeeded();
        }
        catch (UnauthorizedAccessException)
        {
            return SecretStoreOperationResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
        catch (IOException)
        {
            return SecretStoreOperationResult.Failed(SecretStoreFailure.StorageUnavailable);
        }
    }

    private async ValueTask<byte[]?> TryReadProtectedRecordAsync(
        SecretStoreKey key,
        CancellationToken cancellationToken)
    {
        if (!TryEnsureExistingStorageDirectory())
        {
            return null;
        }

        string recordPath = GetRecordPath(key);

        if (!TryEnsureRegularFile(recordPath))
        {
            return null;
        }

        var options = new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = FileBufferSize,
        };

        await using var stream = new FileStream(recordPath, options);
        long recordLength = stream.Length;

        if (recordLength is <= 0 or > DpapiProtectedEnvelopeCodec.MaxProtectedRecordBytes)
        {
            return null;
        }

        byte[] buffer = GC.AllocateUninitializedArray<byte>((int)recordLength);
        bool ownershipTransferred = false;

        try
        {
            int offset = 0;

            while (offset < buffer.Length)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    return null;
                }

                offset += bytesRead;
            }

            ownershipTransferred = true;
            return buffer;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private async ValueTask WriteTemporaryAndCommitAsync(
        string recordPath,
        ReadOnlyMemory<byte> protectedRecord,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        string temporaryPath = GetContainedPath($"temporary-v1-{Guid.NewGuid():N}.tmp");
        bool temporaryCreated = false;

        try
        {
            var options = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                BufferSize = FileBufferSize,
            };

            await using (var stream = new FileStream(temporaryPath, options))
            {
                temporaryCreated = true;
                await stream.WriteAsync(protectedRecord, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureExistingStorageDirectory();

            bool recordExists = TryEnsureRegularFile(recordPath);

            if (replaceExisting)
            {
                if (!recordExists)
                {
                    throw new FileNotFoundException("The protected record is unavailable.");
                }

                await MoveWithBoundedRetryAsync(
                    temporaryPath,
                    recordPath,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (recordExists)
                {
                    throw new IOException("The protected record already exists.");
                }

                File.Move(temporaryPath, recordPath);
            }
        }
        finally
        {
            if (temporaryCreated)
            {
                TryDeleteExactTemporaryFile(temporaryPath);
            }
        }
    }

    private string GetRecordPath(SecretStoreKey key) =>
        GetContainedPath(DpapiProtectedEnvelopeCodec.GetRecordFileName(key));

    private string GetContainedPath(string fileName)
    {
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A protected-store file name is invalid.");
        }

        string candidate = Path.GetFullPath(Path.Combine(_storageDirectoryPath, fileName));

        if (!candidate.StartsWith(_storageDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The protected-store path is outside its storage directory.");
        }

        return candidate;
    }

    private void EnsureStorageDirectoryForWrite()
    {
        EnsureNoExistingReparsePoints(_storageDirectoryPath);
        Directory.CreateDirectory(_storageDirectoryPath);
        EnsureExistingStorageDirectory();
    }

    private bool TryEnsureExistingStorageDirectory()
    {
        EnsureNoExistingReparsePoints(_storageDirectoryPath);

        if (!TryGetAttributes(_storageDirectoryPath, out FileAttributes attributes))
        {
            return false;
        }

        EnsureDirectoryAttributes(attributes);
        return true;
    }

    private void EnsureExistingStorageDirectory()
    {
        if (!TryEnsureExistingStorageDirectory())
        {
            throw new DirectoryNotFoundException("The protected-store directory is unavailable.");
        }
    }

    private static bool TryEnsureRegularFile(string path)
    {
        if (!TryGetAttributes(path, out FileAttributes attributes))
        {
            return false;
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("The protected record is not a regular file.");
        }

        return true;
    }

    private static void EnsureNoExistingReparsePoints(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
        {
            if (TryGetAttributes(current.FullName, out FileAttributes attributes) &&
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The protected-store path contains a reparse point.");
            }
        }
    }

    private static void EnsureDirectoryAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The protected-store path is not a regular directory.");
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void TryDeleteExactTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async ValueTask MoveWithBoundedRetryAsync(
        string temporaryPath,
        string recordPath,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxMoveAttempts; attempt++)
        {
            try
            {
                File.Move(temporaryPath, recordPath, overwrite: true);
                return;
            }
            catch (IOException exception) when (
                attempt < MaxMoveAttempts && IsTransientMoveFailure(exception))
            {
                await DelayBeforeMoveRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException exception) when (
                attempt < MaxMoveAttempts && IsTransientMoveFailure(exception))
            {
                await DelayBeforeMoveRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static ValueTask DelayBeforeMoveRetryAsync(
        int attempt,
        CancellationToken cancellationToken) =>
        new(Task.Delay(TimeSpan.FromMilliseconds(10 * attempt), cancellationToken));

    private static bool IsTransientMoveFailure(Exception exception)
    {
        int windowsError = exception.HResult & 0xFFFF;
        return windowsError is 5 or 32 or 33;
    }

    private static void ValidateValue(ReadOnlyMemory<byte> value)
    {
        if (value.IsEmpty || value.Length > SecretStoreLimits.MaxProtectedValueBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "The protected value length is outside the supported bounds.");
        }
    }
}
