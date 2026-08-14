using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using IptvSuite.Domain;

namespace IptvSuite.DpapiUserBoundaryHarness;

[Flags]
internal enum ProbeEvidence : ulong
{
    None = 0,
    ExpectedSecondarySid = 1UL << 0,
    DistinctFromCreatorSid = 1UL << 1,
    SecondaryIsNonAdministrator = 1UL << 2,
    RawInputDigestMatched = 1UL << 3,
    RecordInputDigestMatched = 1UL << 4,
    SecondaryRawRoundTripPassed = 1UL << 5,
    CreatorRawRejectedCryptographically = 1UL << 6,
    SecondaryAdapterRoundTripPassed = 1UL << 7,
    SecondaryStoreClean = 1UL << 8,
    CreatorRecordUnavailable = 1UL << 9,
    CreatorRecordLeaseAbsent = 1UL << 10,
    CreatorRecordImmutable = 1UL << 11,
    CanaryAbsent = 1UL << 12,

    Required = ExpectedSecondarySid |
        DistinctFromCreatorSid |
        SecondaryIsNonAdministrator |
        RawInputDigestMatched |
        RecordInputDigestMatched |
        SecondaryRawRoundTripPassed |
        CreatorRawRejectedCryptographically |
        SecondaryAdapterRoundTripPassed |
        SecondaryStoreClean |
        CreatorRecordUnavailable |
        CreatorRecordLeaseAbsent |
        CreatorRecordImmutable |
        CanaryAbsent,
}

internal sealed class BoundaryTicket : IDisposable
{
    internal const int MaximumEncodedBytes = 1024;
    internal const int MaximumProtectedFileBytes = 128 * 1024;
    internal const int DigestLength = 32;
    internal const int EntropyLength = 32;
    internal const int ReferenceLength = 46;
    internal const int RecordFileNameLength = 80;
    private const byte FormatVersion = 1;
    private const int HeaderLength = 12;
    private const int GuidLength = 16;
    private const int MaximumSidLength = 184;
    private static ReadOnlySpan<byte> Magic => "IPDUBT01"u8;
    private static ReadOnlySpan<byte> ReferencePrefix => "secret-ref-v1:"u8;

    private byte[]? _entropy;
    private byte[]? _rawDigest;
    private byte[]? _recordDigest;
    private byte[]? _reference;
    private byte[]? _ticketDigest;

    private BoundaryTicket(
        Guid runId,
        string creatorSid,
        string secondarySid,
        Guid sourceId,
        Guid sourceConfigurationId,
        byte[] reference,
        byte[] entropy,
        int rawLength,
        byte[] rawDigest,
        string recordFileName,
        int recordLength,
        byte[] recordDigest,
        byte[] ticketDigest)
    {
        RunId = runId;
        CreatorSid = creatorSid;
        SecondarySid = secondarySid;
        SourceId = sourceId;
        SourceConfigurationId = sourceConfigurationId;
        _reference = reference;
        _entropy = entropy;
        RawLength = rawLength;
        _rawDigest = rawDigest;
        RecordFileName = recordFileName;
        RecordLength = recordLength;
        _recordDigest = recordDigest;
        _ticketDigest = ticketDigest;
    }

    internal Guid RunId { get; }

    internal string CreatorSid { get; }

    internal string SecondarySid { get; }

    internal Guid SourceId { get; }

    internal Guid SourceConfigurationId { get; }

    internal ReadOnlySpan<byte> Entropy => GetBuffer(ref _entropy);

    internal int RawLength { get; }

    internal ReadOnlySpan<byte> RawDigest => GetBuffer(ref _rawDigest);

    internal string RecordFileName { get; }

    internal int RecordLength { get; }

    internal ReadOnlySpan<byte> RecordDigest => GetBuffer(ref _recordDigest);

    internal ReadOnlySpan<byte> TicketDigest
    {
        get
        {
            ReadOnlySpan<byte> digest = GetBuffer(ref _ticketDigest);

            if (digest.Length != DigestLength)
            {
                throw new InvalidOperationException("The boundary ticket has not been serialized.");
            }

            return digest;
        }
    }

    internal static BoundaryTicket Create(
        Guid runId,
        string creatorSid,
        string secondarySid,
        Guid sourceId,
        Guid sourceConfigurationId,
        SecretReference reference,
        ReadOnlySpan<byte> entropy,
        int rawLength,
        ReadOnlySpan<byte> rawDigest,
        string recordFileName,
        int recordLength,
        ReadOnlySpan<byte> recordDigest)
    {
        ArgumentNullException.ThrowIfNull(reference);
        byte[] referenceBytes = SerializeReference(reference);

        try
        {
            ValidateFields(
                runId,
                creatorSid,
                secondarySid,
                sourceId,
                sourceConfigurationId,
                referenceBytes,
                entropy,
                rawLength,
                rawDigest,
                recordFileName,
                recordLength,
                recordDigest);

            byte[]? ownedReference = null;
            byte[]? ownedEntropy = null;
            byte[]? ownedRawDigest = null;
            byte[]? ownedRecordDigest = null;

            try
            {
                ownedReference = referenceBytes.ToArray();
                ownedEntropy = entropy.ToArray();
                ownedRawDigest = rawDigest.ToArray();
                ownedRecordDigest = recordDigest.ToArray();
                var ticket = new BoundaryTicket(
                    runId,
                    creatorSid,
                    secondarySid,
                    sourceId,
                    sourceConfigurationId,
                    ownedReference,
                    ownedEntropy,
                    rawLength,
                    ownedRawDigest,
                    recordFileName,
                    recordLength,
                    ownedRecordDigest,
                    []);
                ownedReference = null;
                ownedEntropy = null;
                ownedRawDigest = null;
                ownedRecordDigest = null;
                return ticket;
            }
            finally
            {
                Clear(ref ownedReference);
                Clear(ref ownedEntropy);
                Clear(ref ownedRawDigest);
                Clear(ref ownedRecordDigest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(referenceBytes);
        }
    }

    internal byte[] Serialize()
    {
        byte[] creatorSid = Encoding.ASCII.GetBytes(CreatorSid);
        byte[] secondarySid = Encoding.ASCII.GetBytes(SecondarySid);
        byte[] recordName = Encoding.ASCII.GetBytes(RecordFileName);
        ReadOnlySpan<byte> reference = GetBuffer(ref _reference);
        ReadOnlySpan<byte> entropy = GetBuffer(ref _entropy);
        ReadOnlySpan<byte> rawDigest = GetBuffer(ref _rawDigest);
        ReadOnlySpan<byte> recordDigest = GetBuffer(ref _recordDigest);
        int bodyLength = checked(
            HeaderLength + GuidLength +
            2 + creatorSid.Length +
            2 + secondarySid.Length +
            GuidLength + GuidLength +
            2 + reference.Length +
            EntropyLength +
            4 + DigestLength +
            2 + recordName.Length +
            4 + DigestLength);
        byte[] body = GC.AllocateUninitializedArray<byte>(bodyLength);
        byte[]? digest = null;

        try
        {
            var writer = new ProtocolWriter(body);
            writer.Write(Magic);
            writer.WriteByte(FormatVersion);
            writer.WriteZeros(3);
            writer.WriteGuid(RunId);
            writer.WriteLengthPrefixed(creatorSid);
            writer.WriteLengthPrefixed(secondarySid);
            writer.WriteGuid(SourceId);
            writer.WriteGuid(SourceConfigurationId);
            writer.WriteLengthPrefixed(reference);
            writer.Write(entropy);
            writer.WriteInt32(RawLength);
            writer.Write(rawDigest);
            writer.WriteLengthPrefixed(recordName);
            writer.WriteInt32(RecordLength);
            writer.Write(recordDigest);
            writer.EnsureComplete();

            digest = SHA256.HashData(body);
            byte[]? encoded = null;

            try
            {
                encoded = GC.AllocateUninitializedArray<byte>(checked(body.Length + DigestLength));
                body.CopyTo(encoded, 0);
                digest.CopyTo(encoded, body.Length);
                byte[] ticketDigest = digest.ToArray();
                byte[]? previousDigest = Interlocked.Exchange(ref _ticketDigest, ticketDigest);

                if (previousDigest is null)
                {
                    Interlocked.Exchange(ref _ticketDigest, null);
                    CryptographicOperations.ZeroMemory(ticketDigest);
                    throw new ObjectDisposedException(nameof(BoundaryTicket));
                }

                CryptographicOperations.ZeroMemory(previousDigest);
                byte[] result = encoded;
                encoded = null;
                return result;
            }
            finally
            {
                Clear(ref encoded);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(creatorSid);
            CryptographicOperations.ZeroMemory(secondarySid);
            CryptographicOperations.ZeroMemory(recordName);
            CryptographicOperations.ZeroMemory(body);

            if (digest is not null)
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }

    internal static BoundaryTicket Deserialize(ReadOnlySpan<byte> encoded, Guid expectedRunId)
    {
        if (encoded.Length is <= DigestLength or > MaximumEncodedBytes)
        {
            throw new InvalidDataException("The boundary ticket length is invalid.");
        }

        ReadOnlySpan<byte> body = encoded[..^DigestLength];
        ReadOnlySpan<byte> actualDigest = encoded[^DigestLength..];
        byte[] expectedDigest = SHA256.HashData(body);

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                throw new InvalidDataException("The boundary ticket digest is invalid.");
            }

            var reader = new ProtocolReader(body);

            if (!reader.Read(Magic.Length).SequenceEqual(Magic) ||
                reader.ReadByte() != FormatVersion ||
                !IsAllZero(reader.Read(3)))
            {
                throw new InvalidDataException("The boundary ticket header is invalid.");
            }

            byte[]? reference = null;
            byte[]? entropy = null;
            byte[]? rawDigest = null;
            byte[]? recordDigest = null;
            byte[]? ticketDigest = null;

            try
            {
                Guid runId = reader.ReadGuid();
                string creatorSid = ReadSid(ref reader);
                string secondarySid = ReadSid(ref reader);
                Guid sourceId = reader.ReadGuid();
                Guid sourceConfigurationId = reader.ReadGuid();
                reference = reader.ReadLengthPrefixed(ReferenceLength, ReferenceLength).ToArray();
                entropy = reader.Read(EntropyLength).ToArray();
                int rawLength = reader.ReadInt32();
                rawDigest = reader.Read(DigestLength).ToArray();
                string recordFileName = ReadAscii(ref reader, RecordFileNameLength, RecordFileNameLength);
                int recordLength = reader.ReadInt32();
                recordDigest = reader.Read(DigestLength).ToArray();
                reader.EnsureComplete();
                ValidateFields(
                    runId,
                    creatorSid,
                    secondarySid,
                    sourceId,
                    sourceConfigurationId,
                    reference,
                    entropy,
                    rawLength,
                    rawDigest,
                    recordFileName,
                    recordLength,
                    recordDigest);

                if (runId != expectedRunId)
                {
                    throw new InvalidDataException("The boundary ticket run binding is invalid.");
                }

                ticketDigest = actualDigest.ToArray();
                var ticket = new BoundaryTicket(
                    runId,
                    creatorSid,
                    secondarySid,
                    sourceId,
                    sourceConfigurationId,
                    reference,
                    entropy,
                    rawLength,
                    rawDigest,
                    recordFileName,
                    recordLength,
                    recordDigest,
                    ticketDigest);
                reference = null;
                entropy = null;
                rawDigest = null;
                recordDigest = null;
                ticketDigest = null;
                return ticket;
            }
            finally
            {
                Clear(ref reference);
                Clear(ref entropy);
                Clear(ref rawDigest);
                Clear(ref recordDigest);
                Clear(ref ticketDigest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedDigest);
        }
    }

    internal SecretReference GetReference()
    {
        ReadOnlySpan<byte> reference = GetBuffer(ref _reference);
        byte[] json = GC.AllocateUninitializedArray<byte>(reference.Length + 2);
        json[0] = (byte)'"';
        reference.CopyTo(json.AsSpan(1));
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
        Clear(ref _entropy);
        Clear(ref _rawDigest);
        Clear(ref _recordDigest);
        Clear(ref _reference);
        Clear(ref _ticketDigest);
    }

    private static byte[] SerializeReference(SecretReference reference)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(reference);

        try
        {
            if (json.Length != ReferenceLength + 2 || json[0] != (byte)'"' || json[^1] != (byte)'"')
            {
                throw new InvalidDataException("The opaque reference encoding is invalid.");
            }

            return json.AsSpan(1, ReferenceLength).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private static void ValidateFields(
        Guid runId,
        string creatorSid,
        string secondarySid,
        Guid sourceId,
        Guid sourceConfigurationId,
        ReadOnlySpan<byte> reference,
        ReadOnlySpan<byte> entropy,
        int rawLength,
        ReadOnlySpan<byte> rawDigest,
        string recordFileName,
        int recordLength,
        ReadOnlySpan<byte> recordDigest)
    {
        if (runId == Guid.Empty || sourceId == Guid.Empty || sourceConfigurationId == Guid.Empty ||
            !IdentityBoundary.IsCanonicalAccountSid(creatorSid) ||
            !IdentityBoundary.IsCanonicalAccountSid(secondarySid) ||
            string.Equals(creatorSid, secondarySid, StringComparison.Ordinal) ||
            !IsValidReference(reference) ||
            entropy.Length != EntropyLength || IsAllZero(entropy) ||
            rawLength is <= 0 or > MaximumProtectedFileBytes ||
            rawDigest.Length != DigestLength || IsAllZero(rawDigest) ||
            !IsValidRecordFileName(recordFileName) ||
            recordLength is <= 0 or > MaximumProtectedFileBytes ||
            recordDigest.Length != DigestLength || IsAllZero(recordDigest))
        {
            throw new InvalidDataException("The boundary ticket fields are invalid.");
        }
    }

    private static bool IsValidReference(ReadOnlySpan<byte> value)
    {
        if (value.Length != ReferenceLength || !value[..ReferencePrefix.Length].SequenceEqual(ReferencePrefix))
        {
            return false;
        }

        return IsLowerHex(value[ReferencePrefix.Length..], requireNonZero: true);
    }

    internal static bool IsValidRecordFileName(string value)
    {
        const string prefix = "record-v2-";
        const string suffix = ".dpapi";

        if (value.Length != RecordFileNameLength ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> digest = value.AsSpan(prefix.Length, DigestLength * 2);

        foreach (char character in digest)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadSid(ref ProtocolReader reader)
    {
        string sid = ReadAscii(ref reader, 7, MaximumSidLength);
        return IdentityBoundary.IsCanonicalAccountSid(sid)
            ? sid
            : throw new InvalidDataException("The boundary ticket SID is invalid.");
    }

    private static string ReadAscii(ref ProtocolReader reader, int minimumLength, int maximumLength)
    {
        ReadOnlySpan<byte> value = reader.ReadLengthPrefixed(minimumLength, maximumLength);

        foreach (byte character in value)
        {
            if (character > 0x7F)
            {
                throw new InvalidDataException("A boundary ticket string is not ASCII.");
            }
        }

        return Encoding.ASCII.GetString(value);
    }

    private static bool IsLowerHex(ReadOnlySpan<byte> value, bool requireNonZero)
    {
        bool hasNonZero = false;

        foreach (byte character in value)
        {
            if (character is not (>= (byte)'0' and <= (byte)'9') and
                not (>= (byte)'a' and <= (byte)'f'))
            {
                return false;
            }

            hasNonZero |= character != (byte)'0';
        }

        return !requireNonZero || hasNonZero;
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

    private static ReadOnlySpan<byte> GetBuffer(ref byte[]? buffer) =>
        Volatile.Read(ref buffer) ?? throw new ObjectDisposedException(nameof(BoundaryTicket));

    private static void Clear(ref byte[]? buffer)
    {
        byte[]? value = Interlocked.Exchange(ref buffer, null);

        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}

internal sealed class BoundaryProbeResult : IDisposable
{
    internal const int EncodedLength = 100;
    private const byte FormatVersion = 1;
    private static ReadOnlySpan<byte> Magic => "IPDUBR01"u8;
    private byte[]? _ticketDigest;

    internal BoundaryProbeResult(Guid runId, ReadOnlySpan<byte> ticketDigest, ProbeEvidence evidence)
    {
        if (runId == Guid.Empty || ticketDigest.Length != BoundaryTicket.DigestLength ||
            evidence == ProbeEvidence.None || (evidence & ~ProbeEvidence.Required) != 0)
        {
            throw new InvalidDataException("The probe result fields are invalid.");
        }

        RunId = runId;
        _ticketDigest = ticketDigest.ToArray();
        Evidence = evidence;
    }

    internal Guid RunId { get; }

    internal ProbeEvidence Evidence { get; }

    internal bool IsComplete => Evidence == ProbeEvidence.Required;

    internal ReadOnlySpan<byte> TicketDigest =>
        Volatile.Read(ref _ticketDigest) ?? throw new ObjectDisposedException(nameof(BoundaryProbeResult));

    internal byte[] Serialize()
    {
        byte[] encoded = GC.AllocateUninitializedArray<byte>(EncodedLength);
        Span<byte> body = encoded.AsSpan(0, EncodedLength - BoundaryTicket.DigestLength);
        var writer = new ProtocolWriter(body);
        writer.Write(Magic);
        writer.WriteByte(FormatVersion);
        writer.WriteZeros(3);
        writer.WriteGuid(RunId);
        writer.Write(TicketDigest);
        writer.WriteUInt64((ulong)Evidence);
        writer.EnsureComplete();
        byte[] digest = SHA256.HashData(body);

        try
        {
            digest.CopyTo(encoded, body.Length);
            return encoded;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal static BoundaryProbeResult Deserialize(
        ReadOnlySpan<byte> encoded,
        Guid expectedRunId,
        ReadOnlySpan<byte> expectedTicketDigest)
    {
        if (encoded.Length != EncodedLength || expectedTicketDigest.Length != BoundaryTicket.DigestLength)
        {
            throw new InvalidDataException("The probe result length is invalid.");
        }

        ReadOnlySpan<byte> body = encoded[..^BoundaryTicket.DigestLength];
        ReadOnlySpan<byte> actualDigest = encoded[^BoundaryTicket.DigestLength..];
        byte[] expectedDigest = SHA256.HashData(body);

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                throw new InvalidDataException("The probe result digest is invalid.");
            }

            var reader = new ProtocolReader(body);

            if (!reader.Read(Magic.Length).SequenceEqual(Magic) ||
                reader.ReadByte() != FormatVersion ||
                !IsZero(reader.Read(3)))
            {
                throw new InvalidDataException("The probe result header is invalid.");
            }

            Guid runId = reader.ReadGuid();
            ReadOnlySpan<byte> ticketDigest = reader.Read(BoundaryTicket.DigestLength);
            ProbeEvidence evidence = (ProbeEvidence)reader.ReadUInt64();
            reader.EnsureComplete();

            if (runId != expectedRunId ||
                !CryptographicOperations.FixedTimeEquals(ticketDigest, expectedTicketDigest) ||
                evidence == ProbeEvidence.None ||
                (evidence & ~ProbeEvidence.Required) != 0)
            {
                throw new InvalidDataException("The probe result binding is invalid.");
            }

            return new BoundaryProbeResult(runId, ticketDigest, evidence);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedDigest);
        }
    }

    public void Dispose()
    {
        byte[]? digest = Interlocked.Exchange(ref _ticketDigest, null);

        if (digest is not null)
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static bool IsZero(ReadOnlySpan<byte> value)
    {
        byte combined = 0;

        foreach (byte item in value)
        {
            combined |= item;
        }

        return combined == 0;
    }
}

internal static class BoundaryRelease
{
    internal const int EncodedLength = 92;
    private const byte FormatVersion = 1;
    private static ReadOnlySpan<byte> Magic => "IPDUBX01"u8;

    internal static byte[] Create(Guid runId, ReadOnlySpan<byte> ticketDigest)
    {
        if (runId == Guid.Empty || ticketDigest.Length != BoundaryTicket.DigestLength)
        {
            throw new InvalidDataException("The release fields are invalid.");
        }

        byte[] encoded = GC.AllocateUninitializedArray<byte>(EncodedLength);
        Span<byte> body = encoded.AsSpan(0, EncodedLength - BoundaryTicket.DigestLength);
        var writer = new ProtocolWriter(body);
        writer.Write(Magic);
        writer.WriteByte(FormatVersion);
        writer.WriteZeros(3);
        writer.WriteGuid(runId);
        writer.Write(ticketDigest);
        writer.EnsureComplete();
        byte[] digest = SHA256.HashData(body);

        try
        {
            digest.CopyTo(encoded, body.Length);
            return encoded;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal static bool IsValid(
        ReadOnlySpan<byte> encoded,
        Guid expectedRunId,
        ReadOnlySpan<byte> expectedTicketDigest)
    {
        if (encoded.Length != EncodedLength || expectedTicketDigest.Length != BoundaryTicket.DigestLength)
        {
            return false;
        }

        ReadOnlySpan<byte> body = encoded[..^BoundaryTicket.DigestLength];
        byte[] digest = SHA256.HashData(body);

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(encoded[^BoundaryTicket.DigestLength..], digest))
            {
                return false;
            }

            var reader = new ProtocolReader(body);
            bool valid = reader.Read(Magic.Length).SequenceEqual(Magic) &&
                reader.ReadByte() == FormatVersion &&
                IsZero(reader.Read(3)) &&
                reader.ReadGuid() == expectedRunId &&
                CryptographicOperations.FixedTimeEquals(
                    reader.Read(BoundaryTicket.DigestLength),
                    expectedTicketDigest);
            reader.EnsureComplete();
            return valid;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static bool IsZero(ReadOnlySpan<byte> value)
    {
        byte combined = 0;

        foreach (byte item in value)
        {
            combined |= item;
        }

        return combined == 0;
    }
}

internal ref struct ProtocolWriter
{
    private readonly Span<byte> _destination;
    private int _offset;

    internal ProtocolWriter(Span<byte> destination) => _destination = destination;

    internal void Write(ReadOnlySpan<byte> value)
    {
        EnsureAvailable(value.Length);
        value.CopyTo(_destination[_offset..]);
        _offset += value.Length;
    }

    internal void WriteByte(byte value)
    {
        EnsureAvailable(1);
        _destination[_offset++] = value;
    }

    internal void WriteZeros(int count)
    {
        EnsureAvailable(count);
        _destination.Slice(_offset, count).Clear();
        _offset += count;
    }

    internal void WriteGuid(Guid value)
    {
        EnsureAvailable(16);

        if (!value.TryWriteBytes(_destination.Slice(_offset, 16), bigEndian: true, out int written) || written != 16)
        {
            throw new InvalidOperationException("A protocol GUID could not be encoded.");
        }

        _offset += 16;
    }

    internal void WriteInt32(int value)
    {
        EnsureAvailable(4);
        BinaryPrimitives.WriteInt32BigEndian(_destination.Slice(_offset, 4), value);
        _offset += 4;
    }

    internal void WriteUInt64(ulong value)
    {
        EnsureAvailable(8);
        BinaryPrimitives.WriteUInt64BigEndian(_destination.Slice(_offset, 8), value);
        _offset += 8;
    }

    internal void WriteLengthPrefixed(ReadOnlySpan<byte> value)
    {
        if (value.Length > ushort.MaxValue)
        {
            throw new InvalidDataException("A protocol field exceeds its length bound.");
        }

        EnsureAvailable(2 + value.Length);
        BinaryPrimitives.WriteUInt16BigEndian(_destination.Slice(_offset, 2), checked((ushort)value.Length));
        _offset += 2;
        Write(value);
    }

    internal void EnsureComplete()
    {
        if (_offset != _destination.Length)
        {
            throw new InvalidDataException("The protocol writer did not fill its destination.");
        }
    }

    private void EnsureAvailable(int length)
    {
        if (length < 0 || _offset > _destination.Length - length)
        {
            throw new InvalidDataException("The protocol destination is too small.");
        }
    }
}

internal ref struct ProtocolReader
{
    private readonly ReadOnlySpan<byte> _source;
    private int _offset;

    internal ProtocolReader(ReadOnlySpan<byte> source) => _source = source;

    internal ReadOnlySpan<byte> Read(int length)
    {
        EnsureAvailable(length);
        ReadOnlySpan<byte> value = _source.Slice(_offset, length);
        _offset += length;
        return value;
    }

    internal byte ReadByte() => Read(1)[0];

    internal Guid ReadGuid() => new(Read(16), bigEndian: true);

    internal int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Read(4));

    internal ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(Read(8));

    internal ReadOnlySpan<byte> ReadLengthPrefixed(int minimumLength, int maximumLength)
    {
        int length = BinaryPrimitives.ReadUInt16BigEndian(Read(2));

        if (length < minimumLength || length > maximumLength)
        {
            throw new InvalidDataException("A protocol field length is invalid.");
        }

        return Read(length);
    }

    internal void EnsureComplete()
    {
        if (_offset != _source.Length)
        {
            throw new InvalidDataException("The protocol contains trailing data.");
        }
    }

    private void EnsureAvailable(int length)
    {
        if (length < 0 || _offset > _source.Length - length)
        {
            throw new InvalidDataException("The protocol is truncated.");
        }
    }
}

internal static class IdentityBoundary
{
    internal static bool IsCanonicalAccountSid(string value)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(value) || value.Length > 184)
        {
            return false;
        }

        try
        {
            var sid = new SecurityIdentifier(value);
            return sid.IsAccountSid() && string.Equals(sid.Value, value, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
