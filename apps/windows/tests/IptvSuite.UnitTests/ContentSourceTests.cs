using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class ContentSourceTests
{
    private static readonly ValidatedSourceDraft RemoteDraft =
        SourceDraftTestFixtures.CreateRemoteDraft(SourceId.Generate());

    [TestMethod]
    public void DraftFactoryNormalizesDisplayNameAndUtcInstants()
    {
        DateTimeOffset createdAt = new(2026, 8, 9, 16, 0, 0, TimeSpan.FromHours(3));
        DateTimeOffset updatedAt = createdAt.AddMinutes(5);

        SourceId sourceId = SourceId.Generate();
        ValidatedSourceDraft sourceDraft = SourceDraftTestFixtures.CreateRemoteDraft(
            sourceId,
            "  Cafe\u0301 Source  ");
        DomainResult<ContentSource> result = ContentSource.Create(
            sourceDraft,
            ContentSourceStatus.Draft,
            createdAt,
            updatedAt);

        Assert.IsTrue(result.IsSuccess);
        ContentSource source = result.Value!;
        Assert.AreEqual(sourceId, source.Id);
        Assert.AreEqual("Caf\u00E9 Source", source.DisplayName);
        Assert.AreEqual(SourceKind.RemotePlaylist, source.Kind);
        Assert.AreSame(sourceDraft.Configuration, source.Configuration);
        Assert.IsFalse(source.Configuration.ConfigurationId.IsEmpty);
        Assert.AreEqual(
            sourceDraft.Configuration.ConfigurationId,
            source.Configuration.ConfigurationId);
        Assert.AreSame(sourceDraft.Configuration.SafeEndpoint, source.SafeEndpoint);
        Assert.AreEqual(TimeSpan.Zero, source.CreatedAt.Offset);
        Assert.AreEqual(TimeSpan.Zero, source.UpdatedAt.Offset);
        Assert.IsNull(source.ActiveSnapshotId);
        Assert.IsNull(source.LastSuccessfulSyncAt);
        Assert.IsNull(source.LastErrorCode);
        StringAssert.StartsWith(source.ToString(), "[CONTENT-SOURCE:");
        Assert.AreEqual("[SAFE-ENDPOINT]", source.SafeEndpoint.ToString());
    }

    [TestMethod]
    public void PublicFactoryCannotReplaceTheProtectedDraftSourceIdentifier()
    {
        MethodInfo? create = typeof(ContentSource).GetMethod(
            nameof(ContentSource.Create),
            BindingFlags.Public | BindingFlags.Static);
        Assert.IsNotNull(create);
        Type[] parameterTypes = create.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.AreEqual(typeof(ValidatedSourceDraft), parameterTypes[0]);
        Assert.IsFalse(parameterTypes.Contains(typeof(SourceId)));
        Assert.IsFalse(parameterTypes.Contains(typeof(SourceConfiguration)));
    }

    [TestMethod]
    public void ReadyFactoryRetainsOnlyAtomicSnapshotLifecycleMetadata()
    {
        DateTimeOffset createdAt = new(2026, 8, 9, 13, 0, 0, TimeSpan.Zero);
        DateTimeOffset synchronizedAt = createdAt.AddMinutes(10);
        SnapshotId activeSnapshotId = SnapshotId.Generate();

        DomainResult<ContentSource> result = ContentSource.Create(
            RemoteDraft,
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

        ValidatedSourceDraft sourceDraft = SourceDraftTestFixtures.CreateRemoteDraft(
            SourceId.Generate(),
            oneHundredAstralScalars);
        DomainResult<ContentSource> result = ContentSource.Create(
            sourceDraft,
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

        ValidatedSourceDraft sourceDraft = SourceDraftTestFixtures.CreateRemoteDraft(
            SourceId.Generate(),
            $"  {maximumNormalizedName}  ");
        DomainResult<ContentSource> accepted = ContentSource.Create(
            sourceDraft,
            ContentSourceStatus.Draft,
            new DateTimeOffset(2026, 8, 9, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 9, 13, 1, 0, TimeSpan.Zero));

        Assert.IsTrue(accepted.IsSuccess);
        Assert.AreEqual(ContentSource.MaximumDisplayNameLength, accepted.Value!.DisplayName.Length);
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
                RemoteDraft,
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
    public void FactoryRejectsMissingDraftAndInvalidTimeBounds()
    {
        DateTimeOffset createdAt = new(2026, 8, 9, 13, 0, 0, TimeSpan.Zero);
        DateTimeOffset updatedAt = createdAt.AddMinutes(1);
        (string CaseId, ValidatedSourceDraft? Draft, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)[] cases =
        [
            ("missing-draft", null, createdAt, updatedAt),
            ("missing-created-time", RemoteDraft, default, updatedAt),
            ("missing-updated-time", RemoteDraft, createdAt, default),
            ("reverse-times", RemoteDraft, updatedAt, createdAt),
        ];

        foreach ((string caseId, ValidatedSourceDraft? draft, DateTimeOffset start, DateTimeOffset end) in cases)
        {
            DomainResult<ContentSource> result = ContentSource.Create(
                draft,
                ContentSourceStatus.Draft,
                start,
                end);

            AssertInvariantFailure(result, caseId);
        }
    }

    private static void AssertInvariantFailure(DomainResult<ContentSource> result, string caseId)
    {
        Assert.IsFalse(result.IsSuccess, caseId);
        Assert.IsNull(result.Value, caseId);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, result.Error!.Code, caseId);
        Assert.AreEqual("[DOMAIN-RESULT:DomainInvariantViolation]", result.ToString(), caseId);
    }
}
