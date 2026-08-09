using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class DomainModelInvariantTests
{
    private const string SyntheticContentHash =
        "D8DDFB2C32D4EE1BE5BDEC621F026B81F9D4FBF3DFF40A20EA90A42F08582EEB";

    private static readonly SourceId Source = SourceId.Generate();
    private static readonly SnapshotId Snapshot = SnapshotId.Generate();
    private static readonly CategoryId Category = CategoryId.Generate();

    [TestMethod]
    public void SnapshotFactoryNormalizesSafeMetadataAndUtcInstants()
    {
        DateTimeOffset retrievedAt = new(2026, 8, 9, 20, 15, 0, TimeSpan.FromHours(3));
        DateTimeOffset lastModified = retrievedAt.AddMinutes(-5);

        DomainResult<PlaylistSnapshot> result = PlaylistSnapshot.Create(
            Snapshot,
            Source,
            retrievedAt,
            SyntheticContentHash.ToLowerInvariant(),
            parserVersion: 1,
            normalizationVersion: 1,
            schemaVersion: 1,
            itemCount: 50,
            warningCount: 2,
            PlaylistSnapshotState.Complete,
            entityTag: "W/\"synthetic-etag\"",
            lastModified: lastModified);

        Assert.IsTrue(result.IsSuccess);
        PlaylistSnapshot snapshot = result.Value!;
        Assert.AreEqual(SyntheticContentHash, snapshot.ContentHash);
        Assert.AreEqual("W/\"synthetic-etag\"", snapshot.EntityTag);
        Assert.AreEqual(TimeSpan.Zero, snapshot.RetrievedAt.Offset);
        Assert.AreEqual(TimeSpan.Zero, snapshot.LastModified!.Value.Offset);
    }

    [TestMethod]
    [DataRow(0, 1, 1, 0, 0)]
    [DataRow(1, 0, 1, 0, 0)]
    [DataRow(1, 1, 0, 0, 0)]
    [DataRow(1, 1, 1, -1, 0)]
    [DataRow(1, 1, 1, 0, -1)]
    public void SnapshotFactoryRejectsInvalidVersionsAndCounts(
        int parserVersion,
        int normalizationVersion,
        int schemaVersion,
        int itemCount,
        int warningCount)
    {
        DomainResult<PlaylistSnapshot> result = PlaylistSnapshot.Create(
            Snapshot,
            Source,
            DateTimeOffset.UtcNow,
            SyntheticContentHash,
            parserVersion,
            normalizationVersion,
            schemaVersion,
            itemCount,
            warningCount,
            PlaylistSnapshotState.Importing);

        AssertInvariantFailure(result);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("non-digest")]
    [DataRow("invalid-hex")]
    public void SnapshotFactoryRejectsMalformedContentHash(string caseId)
    {
        string contentHash = caseId switch
        {
            "empty" => string.Empty,
            "non-digest" => "not-a-digest",
            "invalid-hex" => "D8DDFB2C32D4EE1BE5BDEC621F026B81F9D4FBF3DFF40A20EA90A42F08582EEZ",
            _ => throw new InvalidOperationException("Unknown synthetic hash case."),
        };

        DomainResult<PlaylistSnapshot> result = PlaylistSnapshot.Create(
            Snapshot,
            Source,
            DateTimeOffset.UtcNow,
            contentHash,
            1,
            1,
            1,
            0,
            0,
            PlaylistSnapshotState.Rejected);

        AssertInvariantFailure(result);
    }

    [TestMethod]
    public void SnapshotFactoryRejectsMalformedOrUnboundedEntityTag()
    {
        (string CaseId, string EntityTag)[] cases =
        [
            ("oversized", new string('E', PlaylistSnapshot.MaximumEntityTagLength + 1)),
            ("control", "\"synthetic\tentity-tag\""),
            ("missing-quotes", "synthetic-entity-tag"),
            ("surrounding-whitespace", "  \"synthetic-entity-tag\"  "),
        ];

        foreach ((string caseId, string entityTag) in cases)
        {
            DomainResult<PlaylistSnapshot> result = PlaylistSnapshot.Create(
                Snapshot,
                Source,
                DateTimeOffset.UtcNow,
                SyntheticContentHash,
                1,
                1,
                1,
                0,
                0,
                PlaylistSnapshotState.Complete,
                entityTag);

            AssertInvariantFailure(result, caseId);
        }
    }

    [TestMethod]
    public void CategoryFactoryTrimsAndNormalizesNfc()
    {
        DomainResult<ChannelCategory> result = ChannelCategory.Create(
            Category,
            Snapshot,
            "  synthetic-provider-category  ",
            "  Cafe\u0301 News  ",
            sortOrder: 4,
            isSynthetic: false);

        Assert.IsTrue(result.IsSuccess);
        ChannelCategory category = result.Value!;
        Assert.AreEqual("synthetic-provider-category", category.ProviderKey);
        Assert.AreEqual("Caf\u00E9 News", category.NormalizedName);
        Assert.AreEqual(4, category.SortOrder);
    }

    [TestMethod]
    public void CategoryFactoryRejectsInvalidNameWithoutPublishingRawCaseData()
    {
        (string CaseId, string Name)[] cases =
        [
            ("empty", string.Empty),
            ("whitespace", "   "),
            ("control", "Synthetic\0Category"),
            ("oversized", new string('C', ChannelCategory.MaximumNameLength + 1)),
        ];

        foreach ((string caseId, string name) in cases)
        {
            DomainResult<ChannelCategory> result = ChannelCategory.Create(
                Category,
                Snapshot,
                providerKey: null,
                name,
                sortOrder: 0,
                isSynthetic: false);

            AssertInvariantFailure(result, caseId);
        }
    }

    [TestMethod]
    public void SyntheticCategoryCannotRetainProviderIdentity()
    {
        DomainResult<ChannelCategory> result = ChannelCategory.Create(
            Category,
            Snapshot,
            "synthetic-provider-category",
            "Uncategorized",
            sortOrder: 0,
            isSynthetic: true);

        AssertInvariantFailure(result);
    }

    [TestMethod]
    public void CategoryFactoryRejectsUnboundedOrControlBearingProviderKey()
    {
        (string CaseId, string ProviderKey)[] cases =
        [
            ("oversized", new string('P', ChannelCategory.MaximumProviderKeyLength + 1)),
            ("control", "synthetic\tprovider-key"),
        ];

        foreach ((string caseId, string providerKey) in cases)
        {
            DomainResult<ChannelCategory> result = ChannelCategory.Create(
                Category,
                Snapshot,
                providerKey,
                "Synthetic Category",
                sortOrder: 0,
                isSynthetic: false);

            AssertInvariantFailure(result, caseId);
        }
    }

    private static void AssertInvariantFailure<T>(DomainResult<T> result, string? message = null)
    {
        Assert.IsFalse(result.IsSuccess, message);
        Assert.IsNull(result.Value, message);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, result.Error!.Code, message);
    }
}
