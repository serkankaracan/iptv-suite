using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class XtreamProviderJsonParserTests
{
    [TestMethod]
    public void AccountStatusAcceptsTolerantBooleanScalarsAndRejectsBodyLevelAuthenticationFailure()
    {
        DomainResult<XtreamAccountStatus> accepted =
            XtreamProviderJsonParser.ParseAccountStatus(Utf8("""{"user_info":{"auth":"1"}}"""));
        DomainResult<XtreamAccountStatus> rejected =
            XtreamProviderJsonParser.ParseAccountStatus(Utf8("""{"user_info":{"auth":0}}"""));

        Assert.IsTrue(accepted.IsSuccess);
        Assert.IsTrue(accepted.Value!.IsAuthenticated);
        Assert.AreEqual("[XTREAM-ACCOUNT-STATUS]", accepted.Value.ToString());
        Assert.IsFalse(rejected.IsSuccess);
        Assert.AreEqual(DomainErrorCode.AuthenticationRejected, rejected.Error!.Code);
    }

    [TestMethod]
    public void AccountStatusRequiresTheBoundedUserInfoAuthenticationShape()
    {
        foreach (ReadOnlyMemory<byte> item in new[]
                 {
                     Utf8("{}"),
                     Utf8("""{"user_info":null}"""),
                     Utf8("""{"user_info":{"auth":"unknown"}}"""),
                 })
        {
            DomainResult<XtreamAccountStatus> result = XtreamProviderJsonParser.ParseAccountStatus(item);
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, result.Error!.Code);
        }
    }

    [TestMethod]
    public void CategoriesAcceptStringNumberAndBooleanIdentifiersAndIgnoreUnknownFields()
    {
        DomainResult<XtreamProviderPage<XtreamCategoryInput>> result =
            XtreamProviderJsonParser.ParseCategories(Utf8("""
                [
                  {"category_id":"10","category_name":" News ","unknown":{"nested":true}},
                  {"category_id":20,"category_name":"Sports"},
                  {"category_id":true,"category_name":"Synthetic"}
                ]
                """));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(3, result.Value!.Items);
        Assert.AreEqual("News", result.Value.Items[0].Name);
        Assert.AreEqual("20", result.Value.Items[1].ProviderIdentifier);
        Assert.AreEqual("true", result.Value.Items[2].ProviderIdentifier);
    }

    [TestMethod]
    public void DuplicateAndMissingCategoryIdentifiersHaveVisibleDeterministicPolicy()
    {
        DomainResult<XtreamProviderPage<XtreamCategoryInput>> result =
            XtreamProviderJsonParser.ParseCategories(Utf8("""
                [
                  {"category_id":"10","category_name":"First"},
                  {"category_id":"10","category_name":"Duplicate"},
                  {"category_name":"Missing"},
                  null
                ]
                """));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Value!.Items);
        Assert.AreEqual(1, result.Value.DuplicateIdentifierCount);
        Assert.AreEqual(2, result.Value.SkippedItemCount);
    }

    [TestMethod]
    public void LiveStreamsNormalizeScalarVariantsWithoutRetainingUnknownFields()
    {
        DomainResult<XtreamProviderPage<XtreamStreamInput>> result =
            XtreamProviderJsonParser.ParseLiveStreams(Utf8("""
                [
                  {
                    "stream_id":42,
                    "name":" Synthetic Live ",
                    "category_id":7,
                    "num":"12",
                    "container_extension":"m3u8",
                    "is_adult":"1",
                    "direct_source":"https://must-not-be-retained.invalid/secret"
                  }
                ]
                """));

        Assert.IsTrue(result.IsSuccess);
        XtreamStreamInput stream = result.Value!.Items.Single();
        Assert.AreEqual("42", stream.ProviderPlaybackKey.Value);
        Assert.AreEqual("Synthetic Live", stream.Name);
        Assert.AreEqual("7", stream.CategoryIdentifier);
        Assert.AreEqual(12, stream.Number);
        Assert.AreEqual("m3u8", stream.ContainerExtension);
        Assert.AreEqual(true, stream.IsAdultHint);
        Assert.AreEqual("[XTREAM-STREAM-INPUT]", stream.ToString());
    }

    [TestMethod]
    public void VodAndSeriesDocumentsProduceDistinctTypedItemsWithoutDirectSourceRetention()
    {
        DomainResult<XtreamProviderPage<XtreamMovieInput>> movies =
            XtreamProviderJsonParser.ParseMovies(Utf8("""
                [{
                  "stream_id": "movie-7",
                  "name": " Synthetic Movie ",
                  "category_id": "20",
                  "container_extension": "mp4",
                  "is_adult": 0,
                  "direct_source": "https://must-not-be-retained.invalid/private"
                }]
                """));
        DomainResult<XtreamProviderPage<XtreamSeriesInput>> series =
            XtreamProviderJsonParser.ParseSeries(Utf8("""
                [{
                  "series_id": 9,
                  "name": " Synthetic Series ",
                  "category_id": 30,
                  "is_adult": "1",
                  "cover": "https://must-not-be-retained.invalid/private"
                }]
                """));

        Assert.IsTrue(movies.IsSuccess);
        Assert.IsTrue(series.IsSuccess);
        Assert.AreEqual("movie-7", movies.Value!.Items.Single().ProviderPlaybackKey.Value);
        Assert.AreEqual("Synthetic Movie", movies.Value.Items.Single().Name);
        Assert.AreEqual("mp4", movies.Value.Items.Single().ContainerExtension);
        Assert.AreEqual("9", series.Value!.Items.Single().ProviderKey.Value);
        Assert.AreEqual("Synthetic Series", series.Value.Items.Single().Name);
        Assert.AreEqual(true, series.Value.Items.Single().IsAdultHint);
        Assert.AreEqual("[XTREAM-MOVIE-INPUT]", movies.Value.Items.Single().ToString());
        Assert.AreEqual("[XTREAM-SERIES-INPUT]", series.Value.Items.Single().ToString());
    }

    [TestMethod]
    public void TypedCatalogNamesUseBoundedStringTitleFallbackWithoutChangingEndpointKind()
    {
        DomainResult<XtreamProviderPage<XtreamStreamInput>> live =
            XtreamProviderJsonParser.ParseLiveStreams(Utf8("""
                [{"stream_id":1,"title":"Live title","stream_type":"movie"}]
                """));
        DomainResult<XtreamProviderPage<XtreamMovieInput>> movies =
            XtreamProviderJsonParser.ParseMovies(Utf8("""
                [{"stream_id":2,"title":"Movie title","stream_type":"live"}]
                """));
        DomainResult<XtreamProviderPage<XtreamSeriesInput>> series =
            XtreamProviderJsonParser.ParseSeries(Utf8("""
                [{"series_id":3,"name":"Fallback name","title":"Preferred title","stream_type":"live"}]
                """));
        DomainResult<XtreamProviderPage<XtreamMovieInput>> invalidDisplay =
            XtreamProviderJsonParser.ParseMovies(Utf8("""
                [{"stream_id":4,"name":42,"title":false}]
                """));

        Assert.AreEqual("Live title", live.Value!.Items.Single().Name);
        Assert.AreEqual("Movie title", movies.Value!.Items.Single().Name);
        Assert.AreEqual("Preferred title", series.Value!.Items.Single().Name);
        Assert.IsTrue(invalidDisplay.IsSuccess);
        Assert.IsEmpty(invalidDisplay.Value!.Items);
        Assert.AreEqual(1, invalidDisplay.Value.SkippedItemCount);
    }

    [TestMethod]
    public void CategoryContentKindComesFromTheExplicitEndpointContract()
    {
        byte[] json = Utf8("""[{"category_id":"7","category_name":"Synthetic"}]""").ToArray();
        DomainResult<XtreamProviderPage<XtreamCategoryInput>> live =
            XtreamProviderJsonParser.ParseCategories(json, ContentKind.LiveTv);
        DomainResult<XtreamProviderPage<XtreamCategoryInput>> movies =
            XtreamProviderJsonParser.ParseCategories(json, ContentKind.Movie);
        DomainResult<XtreamProviderPage<XtreamCategoryInput>> series =
            XtreamProviderJsonParser.ParseCategories(json, ContentKind.Series);

        Assert.AreEqual(ContentKind.LiveTv, live.Value!.Items.Single().ContentKind);
        Assert.AreEqual(ContentKind.Movie, movies.Value!.Items.Single().ContentKind);
        Assert.AreEqual(ContentKind.Series, series.Value!.Items.Single().ContentKind);
    }

    [TestMethod]
    public void CatalogCollectionsAcceptUtf8PreambleAndNumericKeyObjectShape()
    {
        byte[] document = Encoding.UTF8.GetBytes("""
            {
              "10":{"stream_id":"movie-10","name":"First","container_extension":"mp4"},
              "42":{"stream_id":42,"name":"Second","container_extension":"mkv"}
            }
            """);
        byte[] content = new byte[document.Length + 3];
        content[0] = 0xef;
        content[1] = 0xbb;
        content[2] = 0xbf;
        document.CopyTo(content, 3);

        DomainResult<XtreamProviderPage<XtreamMovieInput>> result =
            XtreamProviderJsonParser.ParseMovies(content);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, result.Value!.Items);
        Assert.AreEqual("movie-10", result.Value.Items[0].ProviderPlaybackKey.Value);
        Assert.AreEqual("42", result.Value.Items[1].ProviderPlaybackKey.Value);
    }

    [TestMethod]
    public void ExactEmptyProviderSentinelsBecomeEmptyPages()
    {
        ReadOnlyMemory<byte>[] sentinels =
        [
            Utf8("{}"),
            Utf8("null"),
            Utf8("false"),
        ];

        foreach (ReadOnlyMemory<byte> sentinel in sentinels)
        {
            DomainResult<XtreamProviderPage<XtreamCategoryInput>> categories =
                XtreamProviderJsonParser.ParseCategories(sentinel, ContentKind.Movie);
            DomainResult<XtreamProviderPage<XtreamMovieInput>> movies =
                XtreamProviderJsonParser.ParseMovies(sentinel);
            DomainResult<XtreamProviderPage<XtreamSeriesInput>> series =
                XtreamProviderJsonParser.ParseSeries(sentinel);

            Assert.IsTrue(categories.IsSuccess);
            Assert.IsTrue(movies.IsSuccess);
            Assert.IsTrue(series.IsSuccess);
            Assert.IsEmpty(categories.Value!.Items);
            Assert.IsEmpty(movies.Value!.Items);
            Assert.IsEmpty(series.Value!.Items);
            Assert.IsTrue(categories.Value.IsCompatibilityEmptySentinel);
            Assert.IsTrue(movies.Value.IsCompatibilityEmptySentinel);
            Assert.IsTrue(series.Value.IsCompatibilityEmptySentinel);
        }

        DomainResult<XtreamProviderPage<XtreamMovieInput>> canonicalEmpty =
            XtreamProviderJsonParser.ParseMovies(Utf8("[]"));
        Assert.IsTrue(canonicalEmpty.IsSuccess);
        Assert.IsFalse(canonicalEmpty.Value!.IsCompatibilityEmptySentinel);
    }

    [TestMethod]
    public void SeriesDetailsAreBoundedAndParsedOnlyFromTheExplicitHierarchyDocument()
    {
        DomainResult<XtreamSeriesDetails> result = XtreamProviderJsonParser.ParseSeriesDetails(Utf8("""
            {
              "seasons": [{"id":"season-1","season_number":1,"name":"Season 1"}],
              "episodes": {
                "1": [{
                  "id":"episode-7",
                  "episode_num":7,
                  "title":"Episode 7",
                  "container_extension":"mkv",
                  "info":{"duration_secs":2520},
                  "direct_source":"https://must-not-be-retained.invalid/private"
                }]
              }
            }
            """));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Value!.Seasons);
        Assert.HasCount(1, result.Value.Episodes);
        Assert.AreEqual(1, result.Value.Seasons[0].Number);
        Assert.AreEqual("episode-7", result.Value.Episodes[0].ProviderPlaybackKey.Value);
        Assert.AreEqual(TimeSpan.FromMinutes(42), result.Value.Episodes[0].Duration);
        Assert.AreEqual("[XTREAM-SERIES-DETAILS]", result.Value.ToString());
    }

    [TestMethod]
    public void SeriesDetailsAcceptMissingSeasonMetadataAndArrayShapedEpisodeInfo()
    {
        DomainResult<XtreamSeriesDetails> result = XtreamProviderJsonParser.ParseSeriesDetails(Utf8("""
            {
              "episodes": {
                "2": [{
                  "id":"episode-2",
                  "episode_num":1,
                  "title":"Synthetic episode",
                  "info":[]
                }]
              }
            }
            """));

        Assert.IsTrue(result.IsSuccess);
        Assert.IsEmpty(result.Value!.Seasons);
        Assert.HasCount(1, result.Value.Episodes);
        Assert.AreEqual(2, result.Value.Episodes[0].SeasonNumber);
        Assert.IsNull(result.Value.Episodes[0].Duration);
    }

    [TestMethod]
    public void SeriesDetailRawEpisodeAndSeasonGroupBudgetsAreFailClosed()
    {
        string maximumEpisodes = string.Join(
            ',',
            Enumerable.Repeat("{}", XtreamProviderJsonParser.MaximumEpisodeCount));
        string excessiveEpisodes = string.Concat(maximumEpisodes, ",{}");
        DomainResult<XtreamSeriesDetails> atEpisodeLimit =
            XtreamProviderJsonParser.ParseSeriesDetails(Utf8(string.Concat(
                "{\"episodes\":{\"1\":[",
                maximumEpisodes,
                "]}}")));
        DomainResult<XtreamSeriesDetails> overEpisodeLimit =
            XtreamProviderJsonParser.ParseSeriesDetails(Utf8(string.Concat(
                "{\"episodes\":{\"1\":[",
                excessiveEpisodes,
                "]}}")));

        string maximumGroups = string.Join(
            ',',
            Enumerable.Range(1, XtreamProviderJsonParser.MaximumSeasonCount)
                .Select(number => $"\"{number}\":[]"));
        DomainResult<XtreamSeriesDetails> atGroupLimit =
            XtreamProviderJsonParser.ParseSeriesDetails(Utf8(string.Concat(
                "{\"episodes\":{",
                maximumGroups,
                "}}")));
        DomainResult<XtreamSeriesDetails> overGroupLimit =
            XtreamProviderJsonParser.ParseSeriesDetails(Utf8(string.Concat(
                "{\"episodes\":{",
                maximumGroups,
                ",\"101\":[]}}")));
        string maximumSeasonMetadata = string.Join(
            ',',
            Enumerable.Range(1, XtreamProviderJsonParser.MaximumSeasonCount)
                .Select(number => $"{{\"season_number\":{number}}}"));
        DomainResult<XtreamSeriesDetails> overCombinedSeasonLimit =
            XtreamProviderJsonParser.ParseSeriesDetails(Utf8(string.Concat(
                "{\"seasons\":[",
                maximumSeasonMetadata,
                "],\"episodes\":{\"101\":[]}}")));

        Assert.IsTrue(atEpisodeLimit.IsSuccess);
        Assert.IsFalse(overEpisodeLimit.IsSuccess);
        Assert.AreEqual(
            DomainErrorCode.PlaylistEntryLimitExceeded,
            overEpisodeLimit.Error!.Code);
        Assert.IsTrue(atGroupLimit.IsSuccess);
        Assert.IsFalse(overGroupLimit.IsSuccess);
        Assert.AreEqual(
            DomainErrorCode.PlaylistEntryLimitExceeded,
            overGroupLimit.Error!.Code);
        Assert.IsFalse(overCombinedSeasonLimit.IsSuccess);
        Assert.AreEqual(
            DomainErrorCode.PlaylistEntryLimitExceeded,
            overCombinedSeasonLimit.Error!.Code);
    }

    [TestMethod]
    public void CatalogParserAcceptsDocumentAboveSeriesDetailBudgetWhileDetailsRemainBounded()
    {
        const int seriesDetailsBudget = 16 * 1024 * 1024;
        byte[] catalogDocument = CreatePaddedJson(
            "[{\"stream_id\":\"1\",\"name\":\"Synthetic\",\"ignored\":\""u8,
            "\"}]"u8,
            seriesDetailsBudget + 1);
        byte[] seriesDetailsDocument = CreatePaddedJson(
            "{\"seasons\":[],\"episodes\":{},\"ignored\":\""u8,
            "\"}"u8,
            seriesDetailsBudget + 1);

        DomainResult<XtreamProviderPage<XtreamStreamInput>> catalog =
            XtreamProviderJsonParser.ParseLiveStreams(catalogDocument);
        DomainResult<XtreamSeriesDetails> details =
            XtreamProviderJsonParser.ParseSeriesDetails(seriesDetailsDocument);

        Assert.IsTrue(catalog.IsSuccess);
        Assert.HasCount(1, catalog.Value!.Items);
        Assert.IsFalse(details.IsSuccess);
        Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, details.Error!.Code);
    }

    [TestMethod]
    public void MalformedAndAmbiguousCollectionDocumentsFailWithStableTypedError()
    {
        ReadOnlyMemory<byte>[] cases =
        [
            Utf8("{"),
            Utf8("true"),
            Utf8("0"),
            Utf8("""{"error":"synthetic"}"""),
            Utf8("""{"1":{},"1":{}}"""),
            Utf8("""{"first":{}}"""),
        ];

        foreach (ReadOnlyMemory<byte> item in cases)
        {
            DomainResult<XtreamProviderPage<XtreamStreamInput>> result =
                XtreamProviderJsonParser.ParseLiveStreams(item);
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, result.Error!.Code);
        }
    }

    [TestMethod]
    public void DeclaredItemBudgetsRejectExcessiveLiveArrays()
    {
        string categories = string.Concat(
            "[",
            string.Join(',', Enumerable.Repeat("{}", XtreamProviderJsonParser.MaximumCategoryCount + 1)),
            "]");
        string streams = string.Concat(
            "[",
            string.Join(',', Enumerable.Repeat("{}", XtreamProviderJsonParser.MaximumStreamCount + 1)),
            "]");

        DomainResult<XtreamProviderPage<XtreamCategoryInput>> categoryResult =
            XtreamProviderJsonParser.ParseCategories(Utf8(categories));
        DomainResult<XtreamProviderPage<XtreamStreamInput>> streamResult =
            XtreamProviderJsonParser.ParseLiveStreams(Utf8(streams));

        Assert.IsFalse(categoryResult.IsSuccess);
        Assert.AreEqual(DomainErrorCode.PlaylistEntryLimitExceeded, categoryResult.Error!.Code);
        Assert.IsFalse(streamResult.IsSuccess);
        Assert.AreEqual(DomainErrorCode.PlaylistEntryLimitExceeded, streamResult.Error!.Code);
    }

    [TestMethod]
    public void MaximumLiveStreamBudgetProducesAllTypedPlaybackKeys()
    {
        var document = new StringBuilder(XtreamProviderJsonParser.MaximumStreamCount * 48);
        document.Append('[');
        for (int index = 0; index < XtreamProviderJsonParser.MaximumStreamCount; index++)
        {
            if (index > 0)
            {
                document.Append(',');
            }

            document.Append("{\"stream_id\":")
                .Append(index + 1)
                .Append(",\"name\":\"Synthetic\"}");
        }

        document.Append(']');
        byte[] input = Encoding.UTF8.GetBytes(document.ToString());
        Assert.IsLessThan(HttpTransportLimits.MaximumAllowedResponseBytes, input.Length);

        DomainResult<XtreamProviderPage<XtreamStreamInput>> result =
            XtreamProviderJsonParser.ParseLiveStreams(input);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(XtreamProviderJsonParser.MaximumStreamCount, result.Value!.Items);
        Assert.AreEqual("1", result.Value.Items[0].ProviderPlaybackKey.Value);
        Assert.AreEqual(
            XtreamProviderJsonParser.MaximumStreamCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.Value.Items[^1].ProviderPlaybackKey.Value);
    }

    private static byte[] CreatePaddedJson(
        ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> suffix,
        int totalLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            totalLength,
            prefix.Length + suffix.Length);

        byte[] document = new byte[totalLength];
        prefix.CopyTo(document);
        document.AsSpan(prefix.Length, totalLength - prefix.Length - suffix.Length).Fill((byte)'x');
        suffix.CopyTo(document.AsSpan(totalLength - suffix.Length));
        return document;
    }

    private static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
