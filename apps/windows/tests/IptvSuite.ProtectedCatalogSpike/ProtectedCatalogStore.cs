using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace IptvSuite.ProtectedCatalogSpike;

/// <summary>
/// Defines the bounded, test-only prototype format. It is not a production persistence or
/// cryptographic design, and its controlled fault hooks do not prove power-loss durability.
/// </summary>
internal static class ProtectedCatalogFormat
{
    internal const int Version = 1;
    internal const int MaximumRecordCount = 50_000;
    internal const int MaximumPlaintextLength = 4_096;
    internal const int DekSize = 32;
    internal const int NonceSize = 12;
    internal const int TagSize = 16;
    internal const int AadSize = 112;
    internal const int FixedHeaderSize = 112;
    internal const int IndexEntrySize = 80;
    internal const int MaximumWrappedDekLength = 16_384;
    internal const uint AeadAlgorithmId = 1;
    internal const uint KeyWrapAlgorithmId = 1;
    internal const string EntropyContext = "protected-catalog-spike/v1/current-user/dek";

    internal static ReadOnlySpan<byte> Magic => "PCATS001"u8;

}

internal enum CatalogPurpose : uint
{
    SyntheticChannelLocator = 1,
}

internal readonly record struct SnapshotBinding(
    Guid SourceId,
    Guid SnapshotId,
    Guid KeyGenerationId,
    CatalogPurpose Purpose)
{
    internal static SnapshotBinding Create(Guid sourceId) =>
        new(sourceId, Guid.NewGuid(), Guid.NewGuid(), CatalogPurpose.SyntheticChannelLocator);
}

internal readonly record struct RecordBinding(Guid ChannelOwnerId, Guid ProtectedReferenceId, int Ordinal)
{
    internal static RecordBinding Create(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        Span<byte> input = stackalloc byte[8];
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(input, ordinal);
            BinaryPrimitives.WriteUInt32BigEndian(input[4..], 0x50434154);
            SHA256.HashData(input, digest);
            return new RecordBinding(
                new Guid(digest[..16], bigEndian: true),
                new Guid(digest[16..], bigEndian: true),
                ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(digest);
        }
    }
}

internal enum CatalogWriteCheckpoint
{
    BeforeStageCreate,
    AfterRecordEncrypted,
    BeforeActivation,
    AfterActivation,
}

internal sealed class InjectedCatalogFaultException : Exception
{
}

internal sealed class CatalogCommitOutcomeException(bool committed) : Exception
{
    internal bool Committed { get; } = committed;
}

internal sealed record CatalogWriteResult(
    long DiskBytes,
    int DpapiWrapCount,
    int PreActivationDpapiUnwrapCount,
    int PreActivationTagProbeCount,
    int NonceCount,
    int NonceCollisionRetryCount);

internal interface ICatalogNonceGenerator
{
    void Fill(Span<byte> destination);
}

internal sealed class RandomCatalogNonceGenerator : ICatalogNonceGenerator
{
    internal static RandomCatalogNonceGenerator Instance { get; } = new();

    private RandomCatalogNonceGenerator()
    {
    }

    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}

internal sealed class SecretBuffer : IDisposable
{
    private byte[]? _bytes;

    internal SecretBuffer(byte[] bytes) => _bytes = bytes;

    internal ReadOnlySpan<byte> Value => _bytes ??
        throw new ObjectDisposedException(nameof(SecretBuffer));

    public void Dispose()
    {
        byte[]? bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public override string ToString() => nameof(SecretBuffer);
}

/// <summary>
/// Writes immutable one-file source snapshots for comparative measurement only. This simulation
/// is not the preferred SQLite transaction design and is not production-ready.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProtectedCatalogStore
{
    private const string ActiveFileName = "active.snapshot";
    private const string StagedFileName = "staged.snapshot";

    private readonly string _directory;
    private readonly string _activePath;
    private readonly string _stagedPath;
    private readonly ICatalogNonceGenerator _nonceGenerator;

    internal ProtectedCatalogStore(
        string directory,
        ICatalogNonceGenerator? nonceGenerator = null)
    {
        _directory = Path.GetFullPath(directory);
        _activePath = GetContainedFile(ActiveFileName);
        _stagedPath = GetContainedFile(StagedFileName);
        _nonceGenerator = nonceGenerator ?? RandomCatalogNonceGenerator.Instance;
        EnsureSafeDirectory();
    }

    internal bool HasActiveSnapshot => File.Exists(_activePath);

    internal long ActiveSnapshotLength => new FileInfo(_activePath).Length;

    internal int TemporaryArtifactCount => File.Exists(_stagedPath) ? 1 : 0;

    internal async Task<CatalogWriteResult> StageAndActivateAsync(
        SnapshotBinding binding,
        int recordCount,
        Func<int, byte[]> payloadFactory,
        Action<CatalogWriteCheckpoint, int>? controlledHook,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recordCount, ProtectedCatalogFormat.MaximumRecordCount);
        ArgumentNullException.ThrowIfNull(payloadFactory);
        if (binding.SourceId == Guid.Empty ||
            binding.SnapshotId == Guid.Empty ||
            binding.KeyGenerationId == Guid.Empty ||
            binding.Purpose != CatalogPurpose.SyntheticChannelLocator)
        {
            throw new ArgumentException("The synthetic snapshot binding is invalid.", nameof(binding));
        }

        if (!AesGcm.IsSupported)
        {
            throw new PlatformNotSupportedException("The required authenticated cipher is unavailable.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        controlledHook?.Invoke(CatalogWriteCheckpoint.BeforeStageCreate, -1);
        cancellationToken.ThrowIfCancellationRequested();
        DeleteExactIfPresent(_stagedPath);

        byte[]? dek = null;
        byte[]? wrappedDek = null;
        byte[]? index = null;
        bool activated = false;
        int collisionRetries = 0;

        try
        {
            dek = GC.AllocateUninitializedArray<byte>(ProtectedCatalogFormat.DekSize);
            index = GC.AllocateUninitializedArray<byte>(
                checked(recordCount * ProtectedCatalogFormat.IndexEntrySize));
            var nonces = new HashSet<NonceKey>(recordCount);
            RandomNumberGenerator.Fill(dek);
            byte[] entropy = CreateDpapiEntropy(binding);
            try
            {
                wrappedDek = ProtectedData.Protect(dek, entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(entropy);
            }
            if (wrappedDek.Length is <= 0 or > ProtectedCatalogFormat.MaximumWrappedDekLength)
            {
                throw new CryptographicException("The wrapped key length is outside the fixed format bound.");
            }

            int headerLength = checked(ProtectedCatalogFormat.FixedHeaderSize + wrappedDek.Length);
            long ciphertextStart = checked(
                (long)headerLength + ((long)recordCount * ProtectedCatalogFormat.IndexEntrySize));

            await using (var stream = new FileStream(
                _stagedPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1_024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                byte[] header = CreateHeader(binding, recordCount, wrappedDek, headerLength, ciphertextStart);
                try
                {
                    await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(header);
                }

                stream.Position = ciphertextStart;
                using var aes = new AesGcm(dek, ProtectedCatalogFormat.TagSize);
                long nextCiphertextOffset = ciphertextStart;
                byte[] nonce = new byte[ProtectedCatalogFormat.NonceSize];
                byte[] tag = new byte[ProtectedCatalogFormat.TagSize];
                byte[] aad = new byte[ProtectedCatalogFormat.AadSize];

                try
                {
                    for (int ordinal = 0; ordinal < recordCount; ordinal++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        byte[] plaintext = payloadFactory(ordinal);
                        byte[]? ciphertext = null;
                        try
                        {
                            if (plaintext.Length is <= 0 or > ProtectedCatalogFormat.MaximumPlaintextLength)
                            {
                                throw new InvalidDataException(
                                    "A synthetic payload length is outside the format bound.");
                            }

                            ciphertext = GC.AllocateUninitializedArray<byte>(plaintext.Length);
                            NonceKey nonceKey;
                            bool accepted;
                            do
                            {
                                _nonceGenerator.Fill(nonce);
                                nonceKey = NonceKey.From(nonce);
                                accepted = nonces.Add(nonceKey);
                                if (!accepted)
                                {
                                    collisionRetries++;
                                }
                            }
                            while (!accepted);

                            RecordBinding recordBinding = RecordBinding.Create(ordinal);
                            WriteAad(aad, binding, recordBinding, plaintext.Length, recordCount);
                            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
                            WriteIndexEntry(
                                index.AsSpan(ordinal * ProtectedCatalogFormat.IndexEntrySize),
                                recordBinding,
                                plaintext.Length,
                                nextCiphertextOffset,
                                nonce,
                                tag);
                            await stream.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
                            nextCiphertextOffset = checked(nextCiphertextOffset + ciphertext.Length);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(plaintext);
                            if (ciphertext is not null)
                            {
                                CryptographicOperations.ZeroMemory(ciphertext);
                            }

                            CryptographicOperations.ZeroMemory(nonce);
                            CryptographicOperations.ZeroMemory(tag);
                            CryptographicOperations.ZeroMemory(aad);
                        }

                        controlledHook?.Invoke(CatalogWriteCheckpoint.AfterRecordEncrypted, ordinal);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);
                    CryptographicOperations.ZeroMemory(tag);
                    CryptographicOperations.ZeroMemory(aad);
                }

                stream.Position = headerLength;
                await stream.WriteAsync(index, cancellationToken).ConfigureAwait(false);
                stream.Position = nextCiphertextOffset;
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            int validationProbeCount = Math.Min(recordCount, 16);
            using (ProtectedCatalogReader stagedReader = ProtectedCatalogReader.Open(_stagedPath, binding))
            {
                foreach (int ordinal in CreateProbeIndexes(recordCount, validationProbeCount))
                {
                    using SecretBuffer ignored = stagedReader.Read(RecordBinding.Create(ordinal));
                }
            }

            controlledHook?.Invoke(CatalogWriteCheckpoint.BeforeActivation, recordCount);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(_stagedPath, _activePath, overwrite: true);
            activated = true;
            controlledHook?.Invoke(CatalogWriteCheckpoint.AfterActivation, recordCount);
            return new CatalogWriteResult(
                ActiveSnapshotLength,
                1,
                1,
                validationProbeCount,
                nonces.Count,
                collisionRetries);
        }
        catch when (activated)
        {
            throw new CatalogCommitOutcomeException(committed: true);
        }
        finally
        {
            if (dek is not null)
            {
                CryptographicOperations.ZeroMemory(dek);
            }

            if (index is not null)
            {
                CryptographicOperations.ZeroMemory(index);
            }
            if (wrappedDek is not null)
            {
                CryptographicOperations.ZeroMemory(wrappedDek);
            }

            if (!activated)
            {
                DeleteExactIfPresent(_stagedPath);
            }
        }
    }

    internal ProtectedCatalogReader OpenReader(SnapshotBinding expectedBinding) =>
        ProtectedCatalogReader.Open(_activePath, expectedBinding);

    internal void DeleteActiveSnapshot() => DeleteExactIfPresent(_activePath);

    internal byte[] ReadActiveSnapshotForControlledTest() => File.ReadAllBytes(_activePath);

    internal void ReplaceActiveSnapshotForControlledTest(ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(
            _activePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4_096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static byte[] CreateHeader(
        SnapshotBinding binding,
        int recordCount,
        ReadOnlySpan<byte> wrappedDek,
        int headerLength,
        long ciphertextStart)
    {
        byte[] header = GC.AllocateUninitializedArray<byte>(headerLength);
        try
        {
            Span<byte> span = header;
            ProtectedCatalogFormat.Magic.CopyTo(span);
            BinaryPrimitives.WriteUInt32BigEndian(span[8..], ProtectedCatalogFormat.Version);
            BinaryPrimitives.WriteInt32BigEndian(span[12..], headerLength);
            BinaryPrimitives.WriteInt32BigEndian(span[16..], recordCount);
            BinaryPrimitives.WriteInt32BigEndian(span[20..], wrappedDek.Length);
            BinaryPrimitives.WriteInt32BigEndian(span[24..], ProtectedCatalogFormat.IndexEntrySize);
            BinaryPrimitives.WriteUInt32BigEndian(span[28..], (uint)binding.Purpose);
            BinaryPrimitives.WriteInt64BigEndian(span[32..], ciphertextStart);
            WriteGuid(span[40..56], binding.SourceId);
            WriteGuid(span[56..72], binding.SnapshotId);
            WriteGuid(span[72..88], binding.KeyGenerationId);
            BinaryPrimitives.WriteUInt32BigEndian(span[88..], ProtectedCatalogFormat.AeadAlgorithmId);
            BinaryPrimitives.WriteUInt32BigEndian(span[92..], ProtectedCatalogFormat.KeyWrapAlgorithmId);
            BinaryPrimitives.WriteInt32BigEndian(span[96..], ProtectedCatalogFormat.DekSize);
            BinaryPrimitives.WriteInt32BigEndian(span[100..], ProtectedCatalogFormat.NonceSize);
            BinaryPrimitives.WriteInt32BigEndian(span[104..], ProtectedCatalogFormat.TagSize);
            span[108..112].Clear();
            wrappedDek.CopyTo(span[ProtectedCatalogFormat.FixedHeaderSize..]);
            return header;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(header);
            throw;
        }
    }

    private void EnsureSafeDirectory()
    {
        if (!Directory.Exists(_directory) ||
            (File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The protected-catalog directory is unsafe.");
        }
    }

    private string GetContainedFile(string fileName)
    {
        string candidate = Path.GetFullPath(Path.Combine(_directory, fileName));
        string prefix = Path.EndsInDirectorySeparator(_directory)
            ? _directory
            : _directory + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(candidate), fileName, StringComparison.Ordinal))
        {
            throw new IOException("The protected-catalog file escaped its fixed root.");
        }

        return candidate;
    }

    private static void WriteIndexEntry(
        Span<byte> destination,
        RecordBinding binding,
        int plaintextLength,
        long ciphertextOffset,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> tag)
    {
        WriteGuid(destination[..16], binding.ChannelOwnerId);
        WriteGuid(destination[16..32], binding.ProtectedReferenceId);
        BinaryPrimitives.WriteInt32BigEndian(destination[32..], binding.Ordinal);
        BinaryPrimitives.WriteInt32BigEndian(destination[36..], plaintextLength);
        BinaryPrimitives.WriteInt64BigEndian(destination[40..], ciphertextOffset);
        BinaryPrimitives.WriteInt32BigEndian(destination[48..], plaintextLength);
        nonce.CopyTo(destination[52..64]);
        tag.CopyTo(destination[64..80]);
    }

    internal static void WriteAad(
        Span<byte> destination,
        SnapshotBinding snapshot,
        RecordBinding record,
        int plaintextLength,
        int recordCount)
    {
        if (destination.Length != ProtectedCatalogFormat.AadSize)
        {
            throw new ArgumentException("The canonical AAD length is required.", nameof(destination));
        }

        ProtectedCatalogFormat.Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], ProtectedCatalogFormat.Version);
        WriteGuid(destination[12..28], snapshot.SourceId);
        WriteGuid(destination[28..44], snapshot.SnapshotId);
        WriteGuid(destination[44..60], snapshot.KeyGenerationId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[60..], (uint)snapshot.Purpose);
        WriteGuid(destination[64..80], record.ChannelOwnerId);
        WriteGuid(destination[80..96], record.ProtectedReferenceId);
        BinaryPrimitives.WriteInt32BigEndian(destination[96..], record.Ordinal);
        BinaryPrimitives.WriteInt32BigEndian(destination[100..], plaintextLength);
        BinaryPrimitives.WriteInt32BigEndian(destination[104..], recordCount);
        BinaryPrimitives.WriteUInt32BigEndian(destination[108..], ProtectedCatalogFormat.AeadAlgorithmId);
    }

    internal static void WriteGuid(Span<byte> destination, Guid value)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out int written) || written != 16)
        {
            throw new InvalidOperationException("A synthetic identifier could not be encoded.");
        }
    }

    internal static byte[] CreateDpapiEntropy(SnapshotBinding binding)
    {
        byte[] context = Encoding.UTF8.GetBytes(ProtectedCatalogFormat.EntropyContext);
        byte[] input = GC.AllocateUninitializedArray<byte>(checked(context.Length + 64));
        try
        {
            context.CopyTo(input, 0);
            Span<byte> suffix = input.AsSpan(context.Length);
            ProtectedCatalogFormat.Magic.CopyTo(suffix);
            BinaryPrimitives.WriteUInt32BigEndian(suffix[8..], ProtectedCatalogFormat.Version);
            WriteGuid(suffix[12..28], binding.SourceId);
            WriteGuid(suffix[28..44], binding.SnapshotId);
            WriteGuid(suffix[44..60], binding.KeyGenerationId);
            BinaryPrimitives.WriteUInt32BigEndian(suffix[60..], (uint)binding.Purpose);
            return SHA256.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(context);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static int[] CreateProbeIndexes(int recordCount, int probeCount)
    {
        var indexes = new int[probeCount];
        if (probeCount == 1)
        {
            return indexes;
        }

        for (int index = 0; index < probeCount; index++)
        {
            indexes[index] = checked((int)(((long)index * (recordCount - 1)) / (probeCount - 1)));
        }

        return indexes;
    }

    private static void DeleteExactIfPresent(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to mutate a reparse-point catalog artifact.");
        }

        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private readonly record struct NonceKey(ulong High, uint Low)
    {
        internal static NonceKey From(ReadOnlySpan<byte> nonce) => new(
            BinaryPrimitives.ReadUInt64BigEndian(nonce),
            BinaryPrimitives.ReadUInt32BigEndian(nonce[8..]));
    }
}

internal sealed record CatalogIndexEntry(
    RecordBinding Binding,
    int PlaintextLength,
    long CiphertextOffset,
    byte[] Nonce,
    byte[] Tag);

/// <summary>
/// Strict bounded reader for the test-only immutable container. Every structural or
/// authentication mismatch fails closed with a context-free error.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProtectedCatalogReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly SnapshotBinding _binding;
    private readonly CatalogIndexEntry[] _entries;
    private byte[]? _dek;

    private ProtectedCatalogReader(
        FileStream stream,
        SnapshotBinding binding,
        CatalogIndexEntry[] entries,
        byte[] dek)
    {
        _stream = stream;
        _binding = binding;
        _entries = entries;
        _dek = dek;
    }

    internal int Count => _entries.Length;

    internal static int DpapiUnwrapCount => 1;

    internal static ProtectedCatalogReader Open(string path, SnapshotBinding expectedBinding)
    {
        FileStream? stream = null;
        byte[]? dek = null;
        byte[]? wrappedDek = null;
        byte[]? index = null;
        try
        {
            if (!AesGcm.IsSupported)
            {
                throw new PlatformNotSupportedException("The required authenticated cipher is unavailable.");
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw InvalidContainer();
            }

            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> fixedHeader = stackalloc byte[ProtectedCatalogFormat.FixedHeaderSize];
            ReadExactly(stream, fixedHeader);
            if (!fixedHeader[..8].SequenceEqual(ProtectedCatalogFormat.Magic) ||
                BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[8..]) != ProtectedCatalogFormat.Version)
            {
                throw InvalidContainer();
            }

            int headerLength = BinaryPrimitives.ReadInt32BigEndian(fixedHeader[12..]);
            int count = BinaryPrimitives.ReadInt32BigEndian(fixedHeader[16..]);
            int wrappedLength = BinaryPrimitives.ReadInt32BigEndian(fixedHeader[20..]);
            int indexEntryLength = BinaryPrimitives.ReadInt32BigEndian(fixedHeader[24..]);
            CatalogPurpose purpose = (CatalogPurpose)BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[28..]);
            long ciphertextStart = BinaryPrimitives.ReadInt64BigEndian(fixedHeader[32..]);
            Guid sourceId = new(fixedHeader[40..56], bigEndian: true);
            Guid snapshotId = new(fixedHeader[56..72], bigEndian: true);
            Guid keyGenerationId = new(fixedHeader[72..88], bigEndian: true);
            uint aeadAlgorithm = BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[88..]);
            uint keyWrapAlgorithm = BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[92..]);
            int dekSize = BinaryPrimitives.ReadInt32BigEndian(fixedHeader[96..]);
            int nonceSize = BinaryPrimitives.ReadInt32BigEndian(fixedHeader[100..]);
            int tagSize = BinaryPrimitives.ReadInt32BigEndian(fixedHeader[104..]);
            if (!fixedHeader[108..112].SequenceEqual(stackalloc byte[4]) ||
                count is <= 0 or > ProtectedCatalogFormat.MaximumRecordCount ||
                wrappedLength is <= 0 or > ProtectedCatalogFormat.MaximumWrappedDekLength ||
                headerLength != ProtectedCatalogFormat.FixedHeaderSize + wrappedLength ||
                indexEntryLength != ProtectedCatalogFormat.IndexEntrySize ||
                purpose != CatalogPurpose.SyntheticChannelLocator ||
                aeadAlgorithm != ProtectedCatalogFormat.AeadAlgorithmId ||
                keyWrapAlgorithm != ProtectedCatalogFormat.KeyWrapAlgorithmId ||
                dekSize != ProtectedCatalogFormat.DekSize ||
                nonceSize != ProtectedCatalogFormat.NonceSize ||
                tagSize != ProtectedCatalogFormat.TagSize ||
                sourceId != expectedBinding.SourceId ||
                snapshotId != expectedBinding.SnapshotId ||
                keyGenerationId != expectedBinding.KeyGenerationId ||
                sourceId == Guid.Empty ||
                snapshotId == Guid.Empty ||
                keyGenerationId == Guid.Empty ||
                purpose != expectedBinding.Purpose)
            {
                throw InvalidContainer();
            }

            long expectedCiphertextStart = checked((long)headerLength + ((long)count * indexEntryLength));
            if (ciphertextStart != expectedCiphertextStart || ciphertextStart > stream.Length)
            {
                throw InvalidContainer();
            }

            wrappedDek = GC.AllocateUninitializedArray<byte>(wrappedLength);
            ReadExactly(stream, wrappedDek);
            byte[] entropy = ProtectedCatalogStore.CreateDpapiEntropy(expectedBinding);
            try
            {
                dek = ProtectedData.Unprotect(wrappedDek, entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(entropy);
            }
            if (dek.Length != ProtectedCatalogFormat.DekSize)
            {
                throw InvalidContainer();
            }

            index = GC.AllocateUninitializedArray<byte>(checked(count * indexEntryLength));
            ReadExactly(stream, index);
            var entries = new CatalogIndexEntry[count];
            var nonces = new HashSet<(ulong High, uint Low)>(count);
            var owners = new HashSet<Guid>(count);
            var references = new HashSet<Guid>(count);
            long nextOffset = ciphertextStart;
            for (int ordinal = 0; ordinal < count; ordinal++)
            {
                ReadOnlySpan<byte> entry = index.AsSpan(ordinal * indexEntryLength, indexEntryLength);
                var binding = new RecordBinding(
                    new Guid(entry[..16], bigEndian: true),
                    new Guid(entry[16..32], bigEndian: true),
                    BinaryPrimitives.ReadInt32BigEndian(entry[32..]));
                int plaintextLength = BinaryPrimitives.ReadInt32BigEndian(entry[36..]);
                long ciphertextOffset = BinaryPrimitives.ReadInt64BigEndian(entry[40..]);
                int ciphertextLength = BinaryPrimitives.ReadInt32BigEndian(entry[48..]);
                byte[] nonce = entry[52..64].ToArray();
                byte[] tag = entry[64..80].ToArray();
                var nonceKey = (
                    BinaryPrimitives.ReadUInt64BigEndian(nonce),
                    BinaryPrimitives.ReadUInt32BigEndian(nonce.AsSpan(8)));

                if (binding.Ordinal != ordinal ||
                    binding.ChannelOwnerId == Guid.Empty ||
                    binding.ProtectedReferenceId == Guid.Empty ||
                    !owners.Add(binding.ChannelOwnerId) ||
                    !references.Add(binding.ProtectedReferenceId) ||
                    plaintextLength is <= 0 or > ProtectedCatalogFormat.MaximumPlaintextLength ||
                    ciphertextLength != plaintextLength ||
                    ciphertextOffset != nextOffset ||
                    !nonces.Add(nonceKey))
                {
                    CryptographicOperations.ZeroMemory(nonce);
                    CryptographicOperations.ZeroMemory(tag);
                    throw InvalidContainer();
                }

                nextOffset = checked(nextOffset + ciphertextLength);
                if (nextOffset > stream.Length)
                {
                    throw InvalidContainer();
                }

                entries[ordinal] = new CatalogIndexEntry(
                    binding,
                    plaintextLength,
                    ciphertextOffset,
                    nonce,
                    tag);
            }

            if (nextOffset != stream.Length)
            {
                throw InvalidContainer();
            }

            var reader = new ProtectedCatalogReader(stream, expectedBinding, entries, dek);
            stream = null;
            dek = null;
            return reader;
        }
        catch (Exception exception) when (exception is not PlatformNotSupportedException)
        {
            throw InvalidContainer();
        }
        finally
        {
            stream?.Dispose();
            if (dek is not null)
            {
                CryptographicOperations.ZeroMemory(dek);
            }

            if (wrappedDek is not null)
            {
                CryptographicOperations.ZeroMemory(wrappedDek);
            }

            if (index is not null)
            {
                CryptographicOperations.ZeroMemory(index);
            }
        }
    }

    internal SecretBuffer Read(RecordBinding expectedRecord)
    {
        byte[] dek = _dek ?? throw new ObjectDisposedException(nameof(ProtectedCatalogReader));
        if (expectedRecord.Ordinal < 0 || expectedRecord.Ordinal >= _entries.Length)
        {
            throw InvalidContainer();
        }

        CatalogIndexEntry entry = _entries[expectedRecord.Ordinal];
        if (entry.Binding != expectedRecord)
        {
            throw InvalidContainer();
        }

        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        Span<byte> aad = stackalloc byte[ProtectedCatalogFormat.AadSize];
        try
        {
            ciphertext = GC.AllocateUninitializedArray<byte>(entry.PlaintextLength);
            plaintext = GC.AllocateUninitializedArray<byte>(entry.PlaintextLength);
            _stream.Position = entry.CiphertextOffset;
            ReadExactly(_stream, ciphertext);
            ProtectedCatalogStore.WriteAad(
                aad,
                _binding,
                expectedRecord,
                entry.PlaintextLength,
                _entries.Length);
            using var aes = new AesGcm(dek, ProtectedCatalogFormat.TagSize);
            aes.Decrypt(entry.Nonce, ciphertext, entry.Tag, plaintext, aad);
            var result = new SecretBuffer(plaintext);
            plaintext = [];
            return result;
        }
        catch (Exception exception) when (exception is not ObjectDisposedException)
        {
            throw InvalidContainer();
        }
        finally
        {
            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            CryptographicOperations.ZeroMemory(aad);
        }
    }

    public void Dispose()
    {
        byte[]? dek = Interlocked.Exchange(ref _dek, null);
        if (dek is not null)
        {
            CryptographicOperations.ZeroMemory(dek);
        }

        foreach (CatalogIndexEntry entry in _entries)
        {
            CryptographicOperations.ZeroMemory(entry.Nonce);
            CryptographicOperations.ZeroMemory(entry.Tag);
        }

        _stream.Dispose();
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = stream.Read(destination[offset..]);
            if (read == 0)
            {
                throw InvalidContainer();
            }

            offset += read;
        }
    }

    private static InvalidDataException InvalidContainer() =>
        new("The protected catalog is invalid.");
}
