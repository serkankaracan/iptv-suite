using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using IptvSuite.Application;

namespace IptvSuite.Infrastructure;

[SupportedOSPlatform("windows")]
internal static class DpapiProtectedEnvelopeCodec
{
    private const byte EnvelopeVersion = 1;
    private const int GuidSize = 16;
    private const int ContextSize = 4 + (2 * GuidSize);
    private const int PayloadLengthSize = sizeof(int);
    private const int EnvelopeHeaderSize = 8 + ContextSize + PayloadLengthSize;

    internal const int MaxProtectedRecordBytes = 128 * 1024;

    private static ReadOnlySpan<byte> EnvelopeMagic => "IPTVSEC!"u8;

    private static ReadOnlySpan<byte> EntropyDomain => "iptv-suite/dpapi-current-user/entropy/v1"u8;

    private static ReadOnlySpan<byte> FileNameDomain => "iptv-suite/dpapi-current-user/file-name/v1"u8;

    internal static byte[] Protect(SecretStoreKey key, ReadOnlySpan<byte> value)
    {
        ValidateKey(key);

        if (value.IsEmpty || value.Length > SecretStoreLimits.MaxProtectedValueBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "The protected value length is outside the supported bounds.");
        }

        byte[] envelope = CreateEnvelope(key, value);
        byte[]? entropy = null;

        try
        {
            entropy = CreateContextDigest(key, EntropyDomain);
            byte[] protectedRecord = ProtectedData.Protect(
                envelope,
                DataProtectionScope.CurrentUser,
                entropy);

            if (protectedRecord.Length is 0 or > MaxProtectedRecordBytes)
            {
                CryptographicOperations.ZeroMemory(protectedRecord);
                throw new CryptographicException("The protected record has an invalid size.");
            }

            return protectedRecord;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);

            if (entropy is not null)
            {
                CryptographicOperations.ZeroMemory(entropy);
            }
        }
    }

    internal static bool TryUnprotect(
        SecretStoreKey key,
        ReadOnlySpan<byte> protectedRecord,
        out byte[]? ownedValue)
    {
        ValidateKey(key);
        ownedValue = null;

        if (protectedRecord.Length is 0 or > MaxProtectedRecordBytes)
        {
            return false;
        }

        byte[] entropy = CreateContextDigest(key, EntropyDomain);
        byte[]? envelope = null;

        try
        {
            envelope = ProtectedData.Unprotect(
                protectedRecord,
                DataProtectionScope.CurrentUser,
                entropy);

            return TryReadEnvelope(key, envelope, out ownedValue);
        }
        finally
        {
            if (envelope is not null)
            {
                CryptographicOperations.ZeroMemory(envelope);
            }

            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    internal static string GetRecordFileName(SecretStoreKey key)
    {
        ValidateKey(key);
        byte[] digest = CreateContextDigest(key, FileNameDomain);

        try
        {
            return $"record-v1-{Convert.ToHexString(digest)}.dpapi";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static byte[] CreateEnvelope(SecretStoreKey key, ReadOnlySpan<byte> value)
    {
        byte[] envelope = GC.AllocateUninitializedArray<byte>(EnvelopeHeaderSize + value.Length);
        Span<byte> destination = envelope;

        EnvelopeMagic.CopyTo(destination);
        WriteContext(key, destination.Slice(EnvelopeMagic.Length, ContextSize));
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.Slice(EnvelopeMagic.Length + ContextSize, PayloadLengthSize),
            value.Length);
        value.CopyTo(destination[EnvelopeHeaderSize..]);

        return envelope;
    }

    private static bool TryReadEnvelope(
        SecretStoreKey expectedKey,
        ReadOnlySpan<byte> envelope,
        out byte[]? ownedValue)
    {
        ownedValue = null;

        if (envelope.Length < EnvelopeHeaderSize ||
            envelope.Length > EnvelopeHeaderSize + SecretStoreLimits.MaxProtectedValueBytes ||
            !envelope[..EnvelopeMagic.Length].SequenceEqual(EnvelopeMagic))
        {
            return false;
        }

        Span<byte> expectedContext = stackalloc byte[ContextSize];

        try
        {
            WriteContext(expectedKey, expectedContext);
            ReadOnlySpan<byte> actualContext = envelope.Slice(EnvelopeMagic.Length, ContextSize);

            if (!CryptographicOperations.FixedTimeEquals(actualContext, expectedContext))
            {
                return false;
            }

            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
                envelope.Slice(EnvelopeMagic.Length + ContextSize, PayloadLengthSize));

            if (payloadLength <= 0 ||
                payloadLength > SecretStoreLimits.MaxProtectedValueBytes ||
                envelope.Length != EnvelopeHeaderSize + payloadLength)
            {
                return false;
            }

            byte[] value = GC.AllocateUninitializedArray<byte>(payloadLength);
            envelope[EnvelopeHeaderSize..].CopyTo(value);
            ownedValue = value;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedContext);
        }
    }

    private static byte[] CreateContextDigest(SecretStoreKey key, ReadOnlySpan<byte> domain)
    {
        Span<byte> context = stackalloc byte[ContextSize];
        byte[] digest;

        WriteContext(key, context);
        byte[] input = GC.AllocateUninitializedArray<byte>(domain.Length + ContextSize);

        try
        {
            domain.CopyTo(input);
            context.CopyTo(input.AsSpan(domain.Length));
            digest = SHA256.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(context);
            CryptographicOperations.ZeroMemory(input);
        }

        return digest;
    }

    private static void WriteContext(SecretStoreKey key, Span<byte> destination)
    {
        if (destination.Length < ContextSize)
        {
            throw new ArgumentException("The context destination is too small.", nameof(destination));
        }

        destination[0] = EnvelopeVersion;
        destination[1] = (byte)key.ReferenceKind;
        destination[2] = (byte)key.Purpose;
        destination[3] = 0;
        WriteGuid(key.SourceId.Value, destination.Slice(4, GuidSize));
        WriteGuid(key.RecordIdentifier, destination.Slice(4 + GuidSize, GuidSize));
    }

    private static void WriteGuid(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out int bytesWritten) || bytesWritten != GuidSize)
        {
            throw new InvalidOperationException("A protected-store context identifier could not be encoded.");
        }
    }

    private static void ValidateKey(SecretStoreKey key)
    {
        bool validCredentialsKey =
            key.ReferenceKind is ProtectedReferenceKind.Secret &&
            key.Purpose is ProtectedValuePurpose.SourceCredentials;
        bool validLocatorKey =
            key.ReferenceKind is ProtectedReferenceKind.Locator &&
            key.Purpose is not ProtectedValuePurpose.SourceCredentials &&
            Enum.IsDefined(key.Purpose);

        if (key.SourceId.IsEmpty || key.RecordIdentifier == Guid.Empty ||
            (!validCredentialsKey && !validLocatorKey))
        {
            throw new ArgumentException("The protected-store key is invalid.", nameof(key));
        }
    }
}
