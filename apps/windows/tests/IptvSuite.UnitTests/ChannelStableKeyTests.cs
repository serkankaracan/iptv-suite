using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class ChannelStableKeyTests
{
    private static readonly SourceId FirstSource = CreateSourceId("6870f3e5-df2f-4fd3-8a5c-03e8825aa5d4");
    private static readonly SourceId SecondSource = CreateSourceId("d36743ee-5d21-4d73-86fb-268f1a5f3398");
    private static readonly LocatorFingerprint SyntheticFingerprint = CreateFingerprint(
        "9D9F290527A6BE626A8F5985B26E19B237B4486DC48233E2ABDDAB688500D141");

    [TestMethod]
    [DataRow("null")]
    [DataRow("empty")]
    [DataRow("non-hex")]
    [DataRow("short")]
    public void LocatorFingerprintRejectsAnythingOtherThanSha256Hex(string caseId)
    {
        string? candidate = caseId switch
        {
            "null" => null,
            "empty" => string.Empty,
            "non-hex" => "NOT-A-SHA256-FINGERPRINT",
            "short" => "9D9F290527A6BE626A8F5985B26E19B237B4486DC48233E2ABDDAB688500D14",
            _ => throw new InvalidOperationException("Unknown synthetic fingerprint case."),
        };

        DomainResult<LocatorFingerprint> result = LocatorFingerprint.Create(candidate);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, result.Error!.Code);
    }

    [TestMethod]
    public void ProviderKeyIsDeterministicSourceScopedAndVersioned()
    {
        DomainResult<ChannelStableKey> first = ChannelStableKeyBuilder.FromProviderStreamId(
            FirstSource,
            "xtream-compatible",
            "synthetic-stream-42");
        DomainResult<ChannelStableKey> repeated = ChannelStableKeyBuilder.FromProviderStreamId(
            FirstSource,
            "xtream-compatible",
            "synthetic-stream-42");
        DomainResult<ChannelStableKey> otherSource = ChannelStableKeyBuilder.FromProviderStreamId(
            SecondSource,
            "xtream-compatible",
            "synthetic-stream-42");

        AssertSuccess(first);
        AssertSuccess(repeated);
        AssertSuccess(otherSource);
        Assert.AreEqual(first.Value.Value, repeated.Value.Value);
        Assert.AreNotEqual(first.Value.Value, otherSource.Value.Value);
        Assert.AreEqual(ChannelStableKeyBuilder.AlgorithmVersion, first.Value.AlgorithmVersion);
        Assert.AreEqual(FirstSource, first.Value.SourceId);
        Assert.AreEqual(64, first.Value.Value.Length);
        Assert.AreEqual(
            "BB601A0613ED996EE6C5F5ABFF7971BB7A9865111047BA77BC020AFF24A70325",
            first.Value.Value);
    }

    [TestMethod]
    public void FallbackNormalizesNfcBeforeHashingPrecomputedFingerprint()
    {
        DomainResult<ChannelStableKey> composed = ChannelStableKeyBuilder.FromFallback(
            FirstSource,
            "Caf\u00E9 News",
            "G\u00E9n\u00E9ral",
            SyntheticFingerprint);
        DomainResult<ChannelStableKey> decomposed = ChannelStableKeyBuilder.FromFallback(
            FirstSource,
            "  Cafe\u0301 News  ",
            "Ge\u0301ne\u0301ral",
            SyntheticFingerprint);

        AssertSuccess(composed);
        AssertSuccess(decomposed);
        Assert.AreEqual(composed.Value.Value, decomposed.Value.Value);
        Assert.AreEqual("[LOCATOR-FINGERPRINT]", SyntheticFingerprint.ToString());
        Assert.AreEqual("[CHANNEL-STABLE-KEY]", composed.Value.ToString());
    }

    [TestMethod]
    public void OccurrenceDiscriminatorSeparatesOtherwiseCollidingInputs()
    {
        DomainResult<ChannelStableKey> first = ChannelStableKeyBuilder.FromM3uTvgId(
            FirstSource,
            "synthetic-tvg-id",
            occurrenceDiscriminator: 0);
        DomainResult<ChannelStableKey> collision = ChannelStableKeyBuilder.FromM3uTvgId(
            FirstSource,
            "synthetic-tvg-id",
            occurrenceDiscriminator: 1);

        AssertSuccess(first);
        AssertSuccess(collision);
        Assert.AreNotEqual(first.Value.Value, collision.Value.Value);
    }

    [TestMethod]
    public void InvalidProviderIdentityReturnsTypedFailureWithoutPublishingRawCaseData()
    {
        (string CaseId, string ProviderKind, string ProviderId)[] cases =
        [
            ("missing-provider-id", "provider-kind", string.Empty),
            ("missing-provider-kind", string.Empty, "provider-id"),
            ("control-in-provider-kind", "provider\0kind", "provider-id"),
            ("locator-shaped-provider-id", "provider-kind", "https://fixtures.invalid/private/item"),
        ];

        foreach ((string caseId, string providerKind, string providerId) in cases)
        {
            DomainResult<ChannelStableKey> result = ChannelStableKeyBuilder.FromProviderStreamId(
                FirstSource,
                providerKind,
                providerId);

            Assert.IsFalse(result.IsSuccess, caseId);
            Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, result.Error!.Code, caseId);
        }
    }

    [TestMethod]
    public void FallbackCannotBeBuiltFromDefaultFingerprintOrSource()
    {
        DomainResult<ChannelStableKey> missingFingerprint = ChannelStableKeyBuilder.FromFallback(
            FirstSource,
            "Synthetic News",
            "Synthetic Group",
            default);
        DomainResult<ChannelStableKey> missingSource = ChannelStableKeyBuilder.FromFallback(
            default,
            "Synthetic News",
            "Synthetic Group",
            SyntheticFingerprint);

        Assert.IsFalse(missingFingerprint.IsSuccess);
        Assert.IsFalse(missingSource.IsSuccess);
    }

    private static void AssertSuccess(DomainResult<ChannelStableKey> result)
    {
        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Value.IsEmpty);
        Assert.IsNull(result.Error);
    }

    private static SourceId CreateSourceId(string value)
    {
        DomainResult<SourceId> result = SourceId.Create(Guid.Parse(value));
        return result.Value;
    }

    private static LocatorFingerprint CreateFingerprint(string value)
    {
        DomainResult<LocatorFingerprint> result = LocatorFingerprint.Create(value);
        return result.Value;
    }
}
