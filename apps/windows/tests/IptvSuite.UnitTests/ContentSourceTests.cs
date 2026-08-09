using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class ContentSourceTests
{
    private static readonly SourceConfiguration RemoteConfiguration = CreateRemoteConfiguration();

    [TestMethod]
    public void DraftFactoryNormalizesDisplayNameAndUtcInstants()
    {
        DateTimeOffset createdAt = new(2026, 8, 9, 16, 0, 0, TimeSpan.FromHours(3));
        DateTimeOffset updatedAt = createdAt.AddMinutes(5);

        DomainResult<ContentSource> result = ContentSource.Create(
            SourceId.Generate(),
            "  Cafe\u0301 Source  ",
            RemoteConfiguration,
            ContentSourceStatus.Draft,
            createdAt,
            updatedAt);

        Assert.IsTrue(result.IsSuccess);
        ContentSource source = result.Value!;
        Assert.AreEqual("Caf\u00E9 Source", source.DisplayName);
        Assert.AreEqual(SourceKind.RemotePlaylist, source.Kind);
        Assert.AreSame(RemoteConfiguration.SafeEndpoint, source.SafeEndpoint);
        Assert.AreEqual(TimeSpan.Zero, source.CreatedAt.Offset);
        Assert.AreEqual(TimeSpan.Zero, source.UpdatedAt.Offset);
        Assert.IsNull(source.ActiveSnapshotId);
        Assert.IsNull(source.LastSuccessfulSyncAt);
        Assert.IsNull(source.LastErrorCode);
        StringAssert.StartsWith(source.ToString(), "[CONTENT-SOURCE:");
        Assert.AreEqual("[SAFE-ENDPOINT]", source.SafeEndpoint.ToString());
    }

    [TestMethod]
    public void ReadyFactoryRetainsOnlyAtomicSnapshotLifecycleMetadata()
    {
        DateTimeOffset createdAt = new(2026, 8, 9, 13, 0, 0, TimeSpan.Zero);
        DateTimeOffset synchronizedAt = createdAt.AddMinutes(10);
        SnapshotId activeSnapshotId = SnapshotId.Generate();

        DomainResult<ContentSource> result = ContentSource.Create(
            SourceId.Generate(),
            "Synthetic Ready Source",
            RemoteConfiguration,
            ContentSourceStatus.Ready,
            createdAt,
            synchronizedAt,
            activeSnapshotId,
            synchronizedAt);

        Assert.IsTrue(result.IsSuccess);
        ContentSource source = result.Value!;
        Assert.AreEqual(activeSnapshotId, source.ActiveSnapshotId);
        Assert.AreEqual(synchronizedAt, source.LastSuccessfulSyncAt);
        Assert.AreEqual(ContentSourceStatus.Ready, source.Status);
    }

    [TestMethod]
    public void DisplayNameLimitCountsUnicodeScalarsInsteadOfUtf16CodeUnits()
    {
        string oneHundredAstralScalars = string.Concat(
            Enumerable.Repeat("\U0001F4FA", ContentSource.MaximumDisplayNameLength));

        DomainResult<ContentSource> result = ContentSource.Create(
            SourceId.Generate(),
            oneHundredAstralScalars,
            RemoteConfiguration,
            ContentSourceStatus.Draft,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(oneHundredAstralScalars, result.Value!.DisplayName);
    }

    [TestMethod]
    public void DisplayNameLimitAppliesAfterTrimAndNfcNormalization()
    {
        string decomposedScalar = "e\u0301";
        string maximumNormalizedName = string.Concat(
            Enumerable.Repeat(decomposedScalar, ContentSource.MaximumDisplayNameLength));

        DomainResult<ContentSource> accepted = ContentSource.Create(
            SourceId.Generate(),
            $"  {maximumNormalizedName}  ",
            RemoteConfiguration,
            ContentSourceStatus.Draft,
            new DateTimeOffset(2026, 8, 9, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 9, 13, 1, 0, TimeSpan.Zero));
        DomainResult<ContentSource> rejected = ContentSource.Create(
            SourceId.Generate(),
            maximumNormalizedName + decomposedScalar,
            RemoteConfiguration,
            ContentSourceStatus.Draft,
            new DateTimeOffset(2026, 8, 9, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 9, 13, 1, 0, TimeSpan.Zero));

        Assert.IsTrue(accepted.IsSuccess);
        Assert.AreEqual(ContentSource.MaximumDisplayNameLength, accepted.Value!.DisplayName.Length);
        AssertInvariantFailure(rejected, "normalized-scalar-overflow");
    }

    [TestMethod]
    public void FactoryRejectsInconsistentLifecycleCombinations()
    {
        (string CaseId, ContentSourceStatus Status, bool HasSnapshot, bool HasSync, bool HasError)[] cases =
        [
            ("ready-without-snapshot", ContentSourceStatus.Ready, false, false, false),
            ("snapshot-without-success", ContentSourceStatus.Draft, true, false, false),
            ("success-without-snapshot", ContentSourceStatus.Draft, false, true, false),
            ("failed-without-error", ContentSourceStatus.Failed, false, false, false),
            ("error-outside-failed", ContentSourceStatus.Draft, false, false, true),
        ];

        DateTimeOffset createdAt = new(2026, 8, 9, 13, 0, 0, TimeSpan.Zero);
        DateTimeOffset updatedAt = createdAt.AddMinutes(1);

        foreach ((string caseId, ContentSourceStatus status, bool hasSnapshot, bool hasSync, bool hasError) in cases)
        {
            DomainResult<ContentSource> result = ContentSource.Create(
                SourceId.Generate(),
                "Synthetic Lifecycle Source",
                RemoteConfiguration,
                status,
                createdAt,
                updatedAt,
                hasSnapshot ? SnapshotId.Generate() : null,
                hasSync ? updatedAt : null,
                hasError ? DomainErrorCode.NetworkUnreachable : null);

            AssertInvariantFailure(result, caseId);
        }
    }

    [TestMethod]
    public void FactoryRejectsUntrustedDisplayAndInvalidTimeBoundsWithoutEchoingThem()
    {
        (string CaseId, string DisplayName, bool ReverseTimes)[] cases =
        [
            ("empty-display", string.Empty, false),
            ("control-display", "Synthetic\0Source", false),
            ("oversized-display", new string('S', ContentSource.MaximumDisplayNameLength + 1), false),
            ("oversized-astral-display", string.Concat(
                Enumerable.Repeat("\U0001F4FA", ContentSource.MaximumDisplayNameLength + 1)), false),
            ("invalid-utf16-display", "\uD800", false),
            ("reverse-times", "Synthetic Source", true),
        ];

        DateTimeOffset createdAt = new(2026, 8, 9, 13, 0, 0, TimeSpan.Zero);
        DateTimeOffset updatedAt = createdAt.AddMinutes(1);

        foreach ((string caseId, string displayName, bool reverseTimes) in cases)
        {
            DomainResult<ContentSource> result = ContentSource.Create(
                SourceId.Generate(),
                displayName,
                RemoteConfiguration,
                ContentSourceStatus.Draft,
                reverseTimes ? updatedAt : createdAt,
                reverseTimes ? createdAt : updatedAt);

            AssertInvariantFailure(result, caseId);
        }
    }

    private static SourceConfiguration CreateRemoteConfiguration()
    {
        DomainResult<ValidatedSourceDraft> result = SourceConfigurationValidator.ValidateRemotePlaylist(
            "Synthetic Playlist",
            "https://fixtures.invalid/catalog.m3u",
            ProtectedLocatorReference.Create());
        Assert.IsTrue(result.IsSuccess);
        return result.Value!.Configuration;
    }

    private static void AssertInvariantFailure(DomainResult<ContentSource> result, string caseId)
    {
        Assert.IsFalse(result.IsSuccess, caseId);
        Assert.IsNull(result.Value, caseId);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, result.Error!.Code, caseId);
        Assert.AreEqual("[DOMAIN-RESULT:DomainInvariantViolation]", result.ToString(), caseId);
    }
}
