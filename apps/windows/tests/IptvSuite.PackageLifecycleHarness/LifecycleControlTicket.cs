using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using IptvSuite.Domain;

namespace IptvSuite.PackageLifecycleHarness;

internal enum ControlTicketPhase : byte
{
    Creating = 1,
    Created = 2,
    Consuming = 3,
}

internal sealed class LifecycleControlTicket : IDisposable
{
    internal const int MaximumProtectedBytes = 4096;
    private const byte FormatVersion = 1;
    private const int MagicLength = 8;
    private const int NonceLength = 16;
    private const int DigestLength = 32;
    private const int ReferenceLength = 46;
    private const int FixedPlaintextLength =
        MagicLength + 1 + 1 + 2 + NonceLength + 16 + 16 + 16 + 2 + DigestLength;
    private static readonly byte[] Entropy =
        "protected-source-store/package-lifecycle-ticket/dpapi-current-user/v1"u8.ToArray();
    private static ReadOnlySpan<byte> Magic => "LCTKT001"u8;

    private byte[]? _nonce;
    private byte[]? _payloadDigest;
    private byte[]? _referenceBytes;

    private LifecycleControlTicket(
        ControlTicketPhase phase,
        byte[] nonce,
        Guid runId,
        Guid sourceId,
        Guid sourceConfigurationId,
        byte[] referenceBytes,
        byte[] payloadDigest)
    {
        Phase = phase;
        _nonce = nonce;
        RunId = runId;
        SourceId = sourceId;
        SourceConfigurationId = sourceConfigurationId;
        _referenceBytes = referenceBytes;
        _payloadDigest = payloadDigest;
    }

    internal ControlTicketPhase Phase { get; private set; }

    internal Guid RunId { get; }

    internal Guid SourceId { get; }

    internal Guid SourceConfigurationId { get; }

    internal ReadOnlyMemory<byte> PayloadDigest => GetPayloadDigest();

    internal static LifecycleControlTicket CreateCreating(
        Guid runId,
        Guid sourceId,
        Guid sourceConfigurationId,
        ReadOnlySpan<byte> payloadDigest)
    {
        ValidateNonEmptyGuid(runId);
        ValidateNonEmptyGuid(sourceId);
        ValidateNonEmptyGuid(sourceConfigurationId);

        if (payloadDigest.Length != DigestLength)
        {
            throw new ArgumentException("A SHA-256 payload digest is required.", nameof(payloadDigest));
        }

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        return new LifecycleControlTicket(
            ControlTicketPhase.Creating,
            nonce,
            runId,
            sourceId,
            sourceConfigurationId,
            [],
            payloadDigest.ToArray());
    }

    internal static LifecycleControlTicket Unprotect(ReadOnlySpan<byte> protectedBytes, Guid expectedRunId)
    {
        if (protectedBytes.Length is <= 0 or > MaximumProtectedBytes)
        {
            throw new InvalidDataException("The protected control ticket length is invalid.");
        }

        byte[] protectedCopy = protectedBytes.ToArray();
        byte[]? plaintext = null;

        try
        {
            plaintext = ProtectedData.Unprotect(protectedCopy, Entropy, DataProtectionScope.CurrentUser);
            return ParsePlaintext(plaintext, expectedRunId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedCopy);

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    internal byte[] Protect()
    {
        byte[] plaintext = SerializePlaintext();

        try
        {
            byte[] protectedBytes = ProtectedData.Protect(
                plaintext,
                Entropy,
                DataProtectionScope.CurrentUser);

            if (protectedBytes.Length is <= 0 or > MaximumProtectedBytes)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                throw new InvalidDataException("The protected control ticket length is invalid.");
            }

            return protectedBytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal void MarkCreated(SecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (Phase is not ControlTicketPhase.Creating || GetReferenceBytes().Length != 0)
        {
            throw new InvalidOperationException("Only a creating ticket may be committed.");
        }

        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(reference);

        try
        {
            if (serialized.Length != ReferenceLength + 2 ||
                serialized[0] != (byte)'"' ||
                serialized[^1] != (byte)'"' ||
                !IsValidReference(serialized.AsSpan(1, ReferenceLength)))
            {
                throw new InvalidDataException("The opaque reference encoding is invalid.");
            }

            _referenceBytes = serialized.AsSpan(1, ReferenceLength).ToArray();
            Phase = ControlTicketPhase.Created;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
    }

    internal void MarkConsuming()
    {
        if (Phase is not ControlTicketPhase.Created || GetReferenceBytes().Length != ReferenceLength)
        {
            throw new InvalidOperationException("Only a committed ticket may be consumed.");
        }

        Phase = ControlTicketPhase.Consuming;
    }

    internal SecretReference GetReference()
    {
        ReadOnlySpan<byte> referenceBytes = GetReferenceBytes();

        if (Phase is ControlTicketPhase.Creating || !IsValidReference(referenceBytes))
        {
            throw new InvalidDataException("The control ticket has no valid reference.");
        }

        byte[] json = GC.AllocateUninitializedArray<byte>(referenceBytes.Length + 2);
        json[0] = (byte)'"';
        referenceBytes.CopyTo(json.AsSpan(1));
        json[^1] = (byte)'"';

        try
        {
            return JsonSerializer.Deserialize<SecretReference>(json) ??
                throw new InvalidDataException("The opaque reference could not be restored.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The opaque reference could not be restored.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    public void Dispose()
    {
        byte[]? nonce = Interlocked.Exchange(ref _nonce, null);
        byte[]? digest = Interlocked.Exchange(ref _payloadDigest, null);
        byte[]? reference = Interlocked.Exchange(ref _referenceBytes, null);

        if (nonce is not null)
        {
            CryptographicOperations.ZeroMemory(nonce);
        }

        if (digest is not null)
        {
            CryptographicOperations.ZeroMemory(digest);
        }

        if (reference is not null)
        {
            CryptographicOperations.ZeroMemory(reference);
        }
    }

    private byte[] SerializePlaintext()
    {
        byte[] nonce = GetNonce();
        byte[] digest = GetPayloadDigest();
        byte[] reference = GetReferenceBytes();
        int expectedReferenceLength = Phase is ControlTicketPhase.Creating ? 0 : ReferenceLength;

        if (reference.Length != expectedReferenceLength)
        {
            throw new InvalidDataException("The reference does not match the control-ticket phase.");
        }

        byte[] plaintext = GC.AllocateUninitializedArray<byte>(FixedPlaintextLength + reference.Length);
        Span<byte> destination = plaintext;
        Magic.CopyTo(destination);
        destination[MagicLength] = FormatVersion;
        destination[MagicLength + 1] = (byte)Phase;
        destination.Slice(MagicLength + 2, 2).Clear();
        int offset = MagicLength + 4;
        nonce.CopyTo(destination.Slice(offset, NonceLength));
        offset += NonceLength;
        WriteGuid(destination.Slice(offset, 16), RunId);
        offset += 16;
        WriteGuid(destination.Slice(offset, 16), SourceId);
        offset += 16;
        WriteGuid(destination.Slice(offset, 16), SourceConfigurationId);
        offset += 16;
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(offset, 2), checked((ushort)reference.Length));
        offset += 2;
        reference.CopyTo(destination.Slice(offset, reference.Length));
        offset += reference.Length;
        digest.CopyTo(destination.Slice(offset, DigestLength));
        return plaintext;
    }

    private static LifecycleControlTicket ParsePlaintext(ReadOnlySpan<byte> plaintext, Guid expectedRunId)
    {
        if (plaintext.Length < FixedPlaintextLength ||
            !plaintext[..MagicLength].SequenceEqual(Magic) ||
            plaintext[MagicLength] != FormatVersion ||
            plaintext[MagicLength + 2] != 0 ||
            plaintext[MagicLength + 3] != 0)
        {
            throw new InvalidDataException("The control ticket header is invalid.");
        }

        ControlTicketPhase phase = (ControlTicketPhase)plaintext[MagicLength + 1];

        if (phase is not ControlTicketPhase.Creating and
            not ControlTicketPhase.Created and
            not ControlTicketPhase.Consuming)
        {
            throw new InvalidDataException("The control ticket phase is invalid.");
        }

        int offset = MagicLength + 4;
        ReadOnlySpan<byte> nonce = plaintext.Slice(offset, NonceLength);
        offset += NonceLength;
        Guid runId = new(plaintext.Slice(offset, 16));
        offset += 16;
        Guid sourceId = new(plaintext.Slice(offset, 16));
        offset += 16;
        Guid sourceConfigurationId = new(plaintext.Slice(offset, 16));
        offset += 16;
        int referenceLength = BinaryPrimitives.ReadUInt16BigEndian(plaintext.Slice(offset, 2));
        offset += 2;
        int expectedReferenceLength = phase is ControlTicketPhase.Creating ? 0 : ReferenceLength;

        if (referenceLength != expectedReferenceLength ||
            plaintext.Length != checked(FixedPlaintextLength + referenceLength) ||
            runId != expectedRunId ||
            runId == Guid.Empty ||
            sourceId == Guid.Empty ||
            sourceConfigurationId == Guid.Empty ||
            IsAllZero(nonce))
        {
            throw new InvalidDataException("The control ticket binding is invalid.");
        }

        ReadOnlySpan<byte> reference = plaintext.Slice(offset, referenceLength);
        offset += referenceLength;
        ReadOnlySpan<byte> digest = plaintext.Slice(offset, DigestLength);

        if ((referenceLength != 0 && !IsValidReference(reference)) ||
            digest.Length != DigestLength ||
            IsAllZero(digest))
        {
            throw new InvalidDataException("The control ticket payload is invalid.");
        }

        return new LifecycleControlTicket(
            phase,
            nonce.ToArray(),
            runId,
            sourceId,
            sourceConfigurationId,
            reference.ToArray(),
            digest.ToArray());
    }

    private static bool IsValidReference(ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> prefix = "secret-ref-v1:"u8;

        if (value.Length != ReferenceLength || !value[..prefix.Length].SequenceEqual(prefix))
        {
            return false;
        }

        bool hasNonZeroDigit = false;

        foreach (byte character in value[prefix.Length..])
        {
            if (character is not (>= (byte)'0' and <= (byte)'9') and
                not (>= (byte)'a' and <= (byte)'f'))
            {
                return false;
            }

            hasNonZeroDigit |= character != (byte)'0';
        }

        return hasNonZeroDigit;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        byte combined = 0;

        foreach (byte item in value)
        {
            combined |= item;
        }

        return combined == 0;
    }

    private static void WriteGuid(Span<byte> destination, Guid value)
    {
        if (!value.TryWriteBytes(destination))
        {
            throw new InvalidOperationException("A GUID could not be encoded.");
        }
    }

    private static void ValidateNonEmptyGuid(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A non-empty identifier is required.", nameof(value));
        }
    }

    private byte[] GetNonce() =>
        Volatile.Read(ref _nonce) ?? throw new ObjectDisposedException(nameof(LifecycleControlTicket));

    private byte[] GetPayloadDigest() =>
        Volatile.Read(ref _payloadDigest) ?? throw new ObjectDisposedException(nameof(LifecycleControlTicket));

    private byte[] GetReferenceBytes() =>
        Volatile.Read(ref _referenceBytes) ?? throw new ObjectDisposedException(nameof(LifecycleControlTicket));
}
