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

        return Build(sourceId, "m3u-tvg-id", occurrenceDiscriminator, normalizedTvgId);
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
}
