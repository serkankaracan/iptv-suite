using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IptvSuite.Domain;

public readonly record struct LocatorFingerprint
{
    private readonly string? value;

    private LocatorFingerprint(string value) => this.value = value;

    internal bool IsEmpty => string.IsNullOrEmpty(value);

    internal string Value => value ?? string.Empty;

    public static DomainResult<LocatorFingerprint> Create(string? sha256Hex) =>
        DomainText.TryNormalizeSha256(sha256Hex, out string normalized)
            ? DomainResult.Success(new LocatorFingerprint(normalized))
            : DomainResult.Failure<LocatorFingerprint>(DomainErrorCode.DomainInvariantViolation);

    public override string ToString() => "[LOCATOR-FINGERPRINT]";
}

public readonly record struct ChannelStableKey
{
    private readonly string? value;

    internal ChannelStableKey(SourceId sourceId, int algorithmVersion, string value)
    {
        SourceId = sourceId;
        AlgorithmVersion = algorithmVersion;
        this.value = value;
    }

    public SourceId SourceId { get; }

    public int AlgorithmVersion { get; }

    public string Value => value ?? string.Empty;

    public bool IsEmpty => SourceId.IsEmpty || AlgorithmVersion <= 0 || string.IsNullOrEmpty(value);

    public override string ToString() => "[CHANNEL-STABLE-KEY]";
}

public static class ChannelStableKeyBuilder
{
    public const int AlgorithmVersion = 2;
    public const int MaximumProviderKindLength = 64;
    public const int MaximumProviderIdentifierLength = 512;
    public const int MaximumChannelNameLength = 256;
    public const int MaximumGroupNameLength = 256;

    public static DomainResult<ChannelStableKey> FromProviderStreamId(
        SourceId sourceId,
        string? providerKind,
        string? providerStreamId,
        int occurrenceDiscriminator = 0)
    {
        if (sourceId.IsEmpty || occurrenceDiscriminator < 0 ||
            !DomainText.TryNormalizeRequired(
                providerKind,
                MaximumProviderKindLength,
                out string normalizedProviderKind) ||
            !DomainText.TryNormalizeRequiredProviderIdentifier(
                providerStreamId,
                MaximumProviderIdentifierLength,
                out string normalizedProviderStreamId))
        {
            return DomainResult.Failure<ChannelStableKey>(DomainErrorCode.DomainInvariantViolation);
        }

        return Build(
            sourceId,
            "provider-stream-id",
            occurrenceDiscriminator,
            normalizedProviderKind,
            normalizedProviderStreamId);
    }

    public static DomainResult<ChannelStableKey> FromM3uTvgId(
        SourceId sourceId,
        string? tvgId,
        int occurrenceDiscriminator = 0)
    {
        if (sourceId.IsEmpty || occurrenceDiscriminator < 0 ||
            !DomainText.TryNormalizeRequired(
                tvgId,
                MaximumProviderIdentifierLength,
                out string normalizedTvgId))
        {
            return DomainResult.Failure<ChannelStableKey>(DomainErrorCode.DomainInvariantViolation);
        }

        return BuildM3uTvgId(sourceId, occurrenceDiscriminator, normalizedTvgId);
    }

    private static DomainResult<ChannelStableKey> BuildM3uTvgId(
        SourceId sourceId,
        int occurrenceDiscriminator,
        string normalizedTvgId)
    {
        Span<byte> material = stackalloc byte[4096];
        int offset = 0;
        AppendPart(material, ref offset, "CHANNEL-STABLE-KEY");
        AppendPart(material, ref offset, "2");
        Span<char> sourceText = stackalloc char[36];
        sourceId.Value.TryFormat(sourceText, out int sourceLength, "D");
        AppendPart(material, ref offset, sourceText[..sourceLength]);
        AppendPart(material, ref offset, "m3u-tvg-id");
        AppendPart(material, ref offset, normalizedTvgId);
        Span<char> occurrenceText = stackalloc char[11];
        occurrenceDiscriminator.TryFormat(
            occurrenceText,
            out int occurrenceLength,
            provider: CultureInfo.InvariantCulture);
        AppendPart(material, ref offset, occurrenceText[..occurrenceLength]);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(material[..offset], digest);
        return DomainResult.Success(new ChannelStableKey(
            sourceId,
            AlgorithmVersion,
            Convert.ToHexString(digest)));
    }

    public static DomainResult<ChannelStableKey> FromFallback(
        SourceId sourceId,
        string? channelName,
        string? groupName,
        LocatorFingerprint locatorFingerprint,
        int occurrenceDiscriminator = 0)
    {
        if (sourceId.IsEmpty || locatorFingerprint.IsEmpty || occurrenceDiscriminator < 0 ||
            !DomainText.TryNormalizeRequired(
                channelName,
                MaximumChannelNameLength,
                out string normalizedChannelName) ||
            !DomainText.TryNormalizeRequired(
                groupName,
                MaximumGroupNameLength,
                out string normalizedGroupName))
        {
            return DomainResult.Failure<ChannelStableKey>(DomainErrorCode.DomainInvariantViolation);
        }

        return Build(
            sourceId,
            "fallback",
            occurrenceDiscriminator,
            normalizedChannelName,
            normalizedGroupName,
            locatorFingerprint.Value);
    }

    private static DomainResult<ChannelStableKey> Build(
        SourceId sourceId,
        string discriminator,
        int occurrenceDiscriminator,
        params string[] identityParts)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendPart(hash, "CHANNEL-STABLE-KEY");
        AppendPart(hash, AlgorithmVersion.ToString(CultureInfo.InvariantCulture));
        AppendPart(hash, sourceId.Value.ToString("D", CultureInfo.InvariantCulture));
        AppendPart(hash, discriminator);

        foreach (string identityPart in identityParts)
        {
            AppendPart(hash, identityPart);
        }

        AppendPart(hash, occurrenceDiscriminator.ToString(CultureInfo.InvariantCulture));
        string value = Convert.ToHexString(hash.GetHashAndReset());
        return DomainResult.Success(new ChannelStableKey(sourceId, AlgorithmVersion, value));
    }

    private static void AppendPart(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendPart(Span<byte> destination, ref int offset, ReadOnlySpan<char> value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset, sizeof(int)), byteCount);
        offset += sizeof(int);
        offset += Encoding.UTF8.GetBytes(value, destination[offset..]);
    }
}
