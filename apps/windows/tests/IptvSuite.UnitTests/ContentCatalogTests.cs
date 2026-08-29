using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class ContentCatalogTests
{
    [TestMethod]
    public void ContentKindsAndIdentifiersAreExplicitAndNonEmpty()
    {
        CollectionAssert.AreEquivalent(
            new[] { ContentKind.LiveTv, ContentKind.Movie, ContentKind.Series, ContentKind.Episode },
            Enum.GetValues<ContentKind>());
        Assert.IsFalse(MovieId.Generate().IsEmpty);
        Assert.IsFalse(SeriesId.Generate().IsEmpty);
        Assert.IsFalse(SeasonId.Generate().IsEmpty);
        Assert.IsFalse(EpisodeId.Generate().IsEmpty);
        Assert.IsFalse(MovieId.Create(Guid.Empty).IsSuccess);
        Assert.IsFalse(SeriesId.Create(Guid.Empty).IsSuccess);
        Assert.IsFalse(SeasonId.Create(Guid.Empty).IsSuccess);
        Assert.IsFalse(EpisodeId.Create(Guid.Empty).IsSuccess);
    }

    [TestMethod]
    public void MovieAndEpisodeRequireTypedProviderPlaybackKeys()
    {
        SnapshotId snapshotId = SnapshotId.Generate();
        ProviderItemKey providerKey = ProviderItemKey.Create("synthetic-42").Value;
        DomainResult<Movie> movie = Movie.Create(
            MovieId.Generate(),
            snapshotId,
            null,
            providerKey,
            "Synthetic Movie",
            "mp4",
            false);
        SeriesId seriesId = SeriesId.Generate();
        DomainResult<Series> series = Series.Create(
            seriesId,
            snapshotId,
            null,
            providerKey,
            "Synthetic Series",
            false);
        DomainResult<Season> season = Season.Create(
            SeasonId.Generate(),
            snapshotId,
            seriesId,
            1,
            "Season 1");
        DomainResult<Episode> episode = Episode.Create(
            EpisodeId.Generate(),
            snapshotId,
            season.Value!.Id,
            providerKey,
            1,
            "Episode 1",
            "mkv",
            TimeSpan.FromMinutes(42));

        Assert.IsTrue(movie.IsSuccess);
        Assert.IsTrue(series.IsSuccess);
        Assert.IsTrue(season.IsSuccess);
        Assert.IsTrue(episode.IsSuccess);
        Assert.AreEqual("[PROVIDER-ITEM-KEY]", movie.Value!.ProviderPlaybackKey.ToString());
        Assert.AreEqual(TimeSpan.FromMinutes(42), episode.Value!.Duration);
    }

    [TestMethod]
    public async Task InsecureXtreamGrantIsPersistedOnlyOnTheValidatedSourceConfiguration()
    {
        var store = new ReferenceStore();
        var service = new SourceDraftProtectionService(store);
        DomainResult<ValidatedSourceDraft> result = await service.ProtectXtreamAllowingInsecureHttpAsync(
            SourceId.Generate(),
            "Synthetic HTTP account",
            "http://127.0.0.1:18080/player_api.php",
            "synthetic-user",
            "synthetic-password");

        Assert.IsTrue(result.IsSuccess);
        var configuration = (XtreamSourceConfiguration)result.Value!.Configuration;
        Assert.IsTrue(configuration.AllowsInsecureTransport);
        Assert.AreEqual(Uri.UriSchemeHttp, configuration.SafeEndpoint.Scheme);
        Assert.AreEqual("[VALIDATED-SOURCE-DRAFT]", result.Value.ToString());
    }

    [TestMethod]
    public void AuthoritativeCountsRejectNegativeOrOverflowingValues()
    {
        var counts = new ContentCatalogCounts(
            liveTvCount: 7,
            movieCount: 3,
            seriesCount: 2,
            episodeCount: 9);

        Assert.AreEqual(12, counts.TotalTopLevelCount);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new ContentCatalogCounts(-1, 0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new ContentCatalogCounts(0, -1, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new ContentCatalogCounts(0, 0, -1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new ContentCatalogCounts(0, 0, 0, -1));
        Assert.ThrowsExactly<OverflowException>(() =>
            _ = new ContentCatalogCounts(int.MaxValue, 1, 0, 0));
    }

    private sealed class ReferenceStore : ISecretStore
    {
        public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                SecretReferenceCreationResult.Succeeded(
                    SecretReference.Parse($"secret-ref-v1:{Guid.NewGuid():N}").Value!));

        public ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
            SourceId sourceId, ProtectedValuePurpose purpose, ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => throw Unused();
        public ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
            SourceId sourceId, ProtectedRecordOwner owner, SecretReference reference,
            CancellationToken cancellationToken = default) => throw Unused();
        public ValueTask<SecretStoreReadResult> ReadLocatorAsync(
            SourceId sourceId, ProtectedValuePurpose purpose, ProtectedRecordOwner owner,
            ProtectedLocatorReference reference, CancellationToken cancellationToken = default) => throw Unused();
        public ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
            SourceId sourceId, ProtectedRecordOwner owner, SecretReference reference,
            ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => throw Unused();
        public ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
            SourceId sourceId, ProtectedValuePurpose purpose, ProtectedRecordOwner owner,
            ProtectedLocatorReference reference, ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw Unused();
        public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
            SourceId sourceId, ProtectedRecordOwner owner, SecretReference reference,
            CancellationToken cancellationToken = default) => throw Unused();
        public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
            SourceId sourceId, ProtectedValuePurpose purpose, ProtectedRecordOwner owner,
            ProtectedLocatorReference reference, CancellationToken cancellationToken = default) => throw Unused();

        private static InvalidOperationException Unused() => new("Unexpected secret-store operation.");
    }
}
