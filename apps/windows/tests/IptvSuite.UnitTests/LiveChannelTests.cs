using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class LiveChannelTests
{
    private static readonly SourceId Source = SourceId.Generate();
    private static readonly SnapshotId Snapshot = SnapshotId.Generate();
    private static readonly CategoryId Category = CategoryId.Generate();
    private static readonly ChannelStableKey StableKey = CreateStableKey();

    [TestMethod]
    public void RemoteChannelFactoryNormalizesMetadataAndRetainsOnlyOpaqueLocators()
    {
        ProtectedLocatorReference streamReference = SourceDraftTestFixtures.CreateLocatorReference();
        ProtectedLocatorReference logoReference = SourceDraftTestFixtures.CreateLocatorReference();

        DomainResult<LiveChannel> result = LiveChannel.Create(
            ChannelId.Generate(),
            StableKey,
            Snapshot,
            Category,
            providerKey: null,
            providerPlaybackKey: null,
            name: "  Cafe\u0301 News  ",
            number: 42,
            logoReference,
            streamReference,
            ChannelContainerHint.Hls,
            isAdultHint: false,
            ChannelNormalizationWarnings.MissingGroup);

        Assert.IsTrue(result.IsSuccess);
        LiveChannel channel = result.Value!;
        Assert.AreEqual("Caf\u00E9 News", channel.Name);
        Assert.AreEqual(42, channel.Number);
        Assert.AreSame(streamReference, channel.StreamReference);
        Assert.AreSame(logoReference, channel.LogoReference);
        Assert.AreEqual(ChannelContainerHint.Hls, channel.ContainerHint);
        Assert.AreEqual(ChannelNormalizationWarnings.MissingGroup, channel.NormalizationWarnings);
        Assert.AreEqual("[PROTECTED-LOCATOR-REFERENCE]", channel.StreamReference!.ToString());
        StringAssert.StartsWith(channel.ToString(), "[LIVE-CHANNEL:");
    }

    [TestMethod]
    public void ProviderChannelCanUseBoundedProviderItemKeyInsteadOfLocatorReference()
    {
        DomainResult<ProviderItemKey> providerItemKey = ProviderItemKey.Create("  synthetic-stream-42  ");
        Assert.IsTrue(providerItemKey.IsSuccess);

        DomainResult<LiveChannel> result = CreateValidChannel(
            providerKey: "synthetic-channel-id",
            providerPlaybackKey: providerItemKey.Value,
            streamReference: null);

        Assert.IsTrue(result.IsSuccess);
        LiveChannel channel = result.Value!;
        Assert.AreEqual("synthetic-channel-id", channel.ProviderKey);
        Assert.AreEqual("synthetic-stream-42", channel.ProviderPlaybackKey!.Value.Value);
        Assert.AreEqual("[PROVIDER-ITEM-KEY]", channel.ProviderPlaybackKey.Value.ToString());
        Assert.IsNull(channel.StreamReference);

        DomainResult<ProviderItemKey> locatorShaped = ProviderItemKey.Create(
            "https://fixtures.invalid/private/item");
        DomainResult<ProviderItemKey> protocolRelative = ProviderItemKey.Create(
            "//fixtures.invalid/private/item");
        DomainResult<ProviderItemKey> activeScheme = ProviderItemKey.Create(
            "javascript:synthetic-item");
        DomainResult<ProviderItemKey> maximum = ProviderItemKey.Create(
            new string('p', ProviderItemKey.MaximumLength));
        DomainResult<ProviderItemKey> oversized = ProviderItemKey.Create(
            new string('p', ProviderItemKey.MaximumLength + 1));

        Assert.IsFalse(locatorShaped.IsSuccess);
        Assert.IsTrue(locatorShaped.Value.IsEmpty);
        Assert.IsFalse(protocolRelative.IsSuccess);
        Assert.IsFalse(activeScheme.IsSuccess);
        Assert.IsTrue(maximum.IsSuccess);
        Assert.IsFalse(oversized.IsSuccess);
    }

    [TestMethod]
    public void FactoryRejectsUnplayableOrUnboundedMetadataWithoutPublishingRawCaseData()
    {
        (string CaseId, string? ProviderKey, string? Name, int? Number, ProtectedLocatorReference? Stream)[] cases =
        [
            ("missing-playback-reference", null, "Synthetic Channel", 1, null),
            ("empty-name", "synthetic-provider", string.Empty, 1, null),
            ("control-name", "synthetic-provider", "Synthetic\0Channel", 1, null),
            ("oversized-name", "synthetic-provider", new string('C', LiveChannel.MaximumNameLength + 1), 1, null),
            ("oversized-provider", new string('P', LiveChannel.MaximumProviderKeyLength + 1), "Synthetic Channel", 1, null),
            ("control-provider", "synthetic\tprovider", "Synthetic Channel", 1, null),
            ("zero-number", "synthetic-provider", "Synthetic Channel", 0, null),
            ("negative-number", "synthetic-provider", "Synthetic Channel", -1, null),
        ];

        foreach ((string caseId, string? providerKey, string? name, int? number, ProtectedLocatorReference? stream) in cases)
        {
            bool omitPlaybackReference = caseId == "missing-playback-reference";
            DomainResult<LiveChannel> result = CreateValidChannel(
                providerKey,
                name,
                number,
                streamReference: stream,
                omitPlaybackReference: omitPlaybackReference);
            AssertInvariantFailure(result, caseId);
        }

        DomainResult<LiveChannel> ambiguousPlayback = CreateValidChannel(
            providerPlaybackKey: CreateProviderItemKey("synthetic-stream-ambiguous"),
            streamReference: SourceDraftTestFixtures.CreateLocatorReference());
        AssertInvariantFailure(ambiguousPlayback, "ambiguous-playback-reference");
    }

    [TestMethod]
    public void FactoryRejectsUnknownEnumAndWarningBits()
    {
        DomainResult<LiveChannel> unknownContainer = LiveChannel.Create(
            ChannelId.Generate(),
            StableKey,
            Snapshot,
            Category,
            "synthetic-provider",
            CreateProviderItemKey("synthetic-stream-42"),
            "Synthetic Channel",
            1,
            logoReference: null,
            streamReference: null,
            (ChannelContainerHint)int.MaxValue,
            isAdultHint: null,
            ChannelNormalizationWarnings.None);
        DomainResult<LiveChannel> unknownWarnings = LiveChannel.Create(
            ChannelId.Generate(),
            StableKey,
            Snapshot,
            Category,
            "synthetic-provider",
            CreateProviderItemKey("synthetic-stream-42"),
            "Synthetic Channel",
            1,
            logoReference: null,
            streamReference: null,
            containerHint: null,
            isAdultHint: null,
            (ChannelNormalizationWarnings)(1 << 12));

        AssertInvariantFailure(unknownContainer, "unknown-container");
        AssertInvariantFailure(unknownWarnings, "unknown-warning-bit");
    }

    private static DomainResult<LiveChannel> CreateValidChannel(
        string? providerKey = "synthetic-provider",
        string? name = "Synthetic Channel",
        int? number = 1,
        ProviderItemKey? providerPlaybackKey = null,
        ProtectedLocatorReference? streamReference = null,
        bool omitPlaybackReference = false) =>
        LiveChannel.Create(
            ChannelId.Generate(),
            StableKey,
            Snapshot,
            Category,
            providerKey,
            omitPlaybackReference
                ? null
                : providerPlaybackKey ?? (streamReference is null
                    ? CreateProviderItemKey("synthetic-stream-42")
                    : null),
            name,
            number,
            logoReference: null,
            streamReference,
            ChannelContainerHint.MpegTs,
            isAdultHint: null,
            ChannelNormalizationWarnings.None);

    private static ProviderItemKey CreateProviderItemKey(string value)
    {
        DomainResult<ProviderItemKey> result = ProviderItemKey.Create(value);
        Assert.IsTrue(result.IsSuccess);
        return result.Value;
    }

    private static ChannelStableKey CreateStableKey()
    {
        DomainResult<ChannelStableKey> result = ChannelStableKeyBuilder.FromProviderStreamId(
            Source,
            "xtream-compatible",
            "synthetic-stream-42");
        Assert.IsTrue(result.IsSuccess);
        return result.Value;
    }

    private static void AssertInvariantFailure(DomainResult<LiveChannel> result, string caseId)
    {
        Assert.IsFalse(result.IsSuccess, caseId);
        Assert.IsNull(result.Value, caseId);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, result.Error!.Code, caseId);
        Assert.AreEqual("[DOMAIN-RESULT:DomainInvariantViolation]", result.ToString(), caseId);
    }
}
