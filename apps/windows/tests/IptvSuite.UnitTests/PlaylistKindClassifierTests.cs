using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class PlaylistKindClassifierTests
{
    private const string CatalogContent = """
        #EXTM3U
        #EXTINF:-1 tvg-id="synthetic-news",Synthetic News
        https://fixtures.invalid/live/synthetic-news.ts
        """;

    private const string MasterManifestContent = """
        #EXTM3U
        #EXT-X-VERSION:7
        #EXT-X-STREAM-INF:BANDWIDTH=1280000
        synthetic-720p.m3u8
        """;

    private const string MediaManifestContent = """
        #EXTM3U
        #EXT-X-TARGETDURATION:10
        #EXTINF:10.0,
        synthetic-segment-1.ts
        #EXT-X-ENDLIST
        """;

    [TestMethod]
    public void CatalogClassificationDependsOnContentNotExternalLocatorExtension()
    {
        string[] externalLocators =
        [
            "https://fixtures.invalid/source.m3u",
            "https://fixtures.invalid/source.m3u8",
        ];

        foreach (string externalLocator in externalLocators)
        {
            DomainResult<PlaylistContentKind> result = PlaylistKindClassifier.Classify(CatalogContent);

            AssertSuccess(result, PlaylistContentKind.ExtendedM3uCatalog, externalLocator);
        }
    }

    [TestMethod]
    public void MasterManifestClassificationDependsOnContentNotExternalLocatorExtension()
    {
        string[] externalLocators =
        [
            "https://fixtures.invalid/master.m3u",
            "https://fixtures.invalid/master.m3u8",
        ];

        foreach (string externalLocator in externalLocators)
        {
            DomainResult<PlaylistContentKind> result = PlaylistKindClassifier.Classify(MasterManifestContent);

            AssertSuccess(result, PlaylistContentKind.HlsMasterManifest, externalLocator);
        }
    }

    [TestMethod]
    public void MediaManifestIsDistinguishedFromCatalogExtInf()
    {
        DomainResult<PlaylistContentKind> result = PlaylistKindClassifier.Classify(MediaManifestContent);

        AssertSuccess(result, PlaylistContentKind.HlsMediaManifest, "media manifest");
    }

    [TestMethod]
    public void BomCrLfAndDecomposedUnicodeAreAcceptedWithoutChangingTheDecision()
    {
        const string content =
            "\uFEFF#EXTM3U\r\n#EXTINF:-1,Synthetic Cafe\u0301\r\nhttps://fixtures.invalid/live/cafe.ts\r\n";

        DomainResult<PlaylistContentKind> result = PlaylistKindClassifier.Classify(content);

        AssertSuccess(result, PlaylistContentKind.ExtendedM3uCatalog, "BOM/CRLF/NFC input");
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("empty")]
    [DataRow("whitespace")]
    [DataRow("missing-header")]
    [DataRow("malformed-header")]
    [DataRow("generic-hls")]
    [DataRow("conflicting-hls")]
    public void UnknownOrMalformedPrefixReturnsTypedUnsupportedError(string caseId)
    {
        string? content = caseId switch
        {
            "null" => null,
            "empty" => string.Empty,
            "whitespace" => "   \r\n\t",
            "missing-header" => "#EXTINF:-1,Synthetic\nhttps://fixtures.invalid/live.ts",
            "malformed-header" => "#EXTM3U:malformed\n#EXTINF:-1,Synthetic\nhttps://fixtures.invalid/live.ts",
            "generic-hls" => "#EXTM3U\n#EXT-X-VERSION:7\n#EXTINF:10,Synthetic\nsegment.ts",
            "conflicting-hls" => "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n#EXT-X-TARGETDURATION:10",
            _ => throw new InvalidOperationException("Unknown synthetic playlist case."),
        };

        DomainResult<PlaylistContentKind> result = PlaylistKindClassifier.Classify(content);

        AssertUnsupported(result);
    }

    [TestMethod]
    public void PrefixAndLineCapsFailClosed()
    {
        string exactLine = string.Concat(
            "#EXTM3U\n#EXTINF:",
            new string(' ', PlaylistKindClassifier.MaxLineCharacters - "#EXTINF:".Length));
        string oversizedPrefix = new('x', PlaylistKindClassifier.MaxPrefixCharacters + 1);
        string oversizedLine = string.Concat(
            "#EXTM3U\n",
            new string('x', PlaylistKindClassifier.MaxLineCharacters + 1));

        AssertSuccess(
            PlaylistKindClassifier.Classify(exactLine),
            PlaylistContentKind.ExtendedM3uCatalog,
            "inclusive line length cap");
        AssertUnsupported(PlaylistKindClassifier.Classify(oversizedPrefix));
        AssertUnsupported(PlaylistKindClassifier.Classify(oversizedLine));
    }

    [TestMethod]
    public void LineCountCapIsInclusiveAndOverflowFailsClosed()
    {
        string exactLineCountWithTerminalNewline = string.Concat(
            "#EXTM3U\n#EXTINF:-1,Synthetic\n",
            string.Concat(Enumerable.Repeat("\n", PlaylistKindClassifier.MaxInspectedLines - 2)));
        string tooManyLines = string.Concat(
            "#EXTM3U\n",
            string.Concat(Enumerable.Repeat("\n", PlaylistKindClassifier.MaxInspectedLines)));

        AssertSuccess(
            PlaylistKindClassifier.Classify(exactLineCountWithTerminalNewline),
            PlaylistContentKind.ExtendedM3uCatalog,
            "inclusive line cap with terminal newline");
        AssertUnsupported(PlaylistKindClassifier.Classify(tooManyLines));
    }

    [TestMethod]
    public void ControlCharactersFailWithoutEnteringErrorOrDiagnosticText()
    {
        string canary = SecurityTestAssertions.CreateSensitiveValue("PLAYLIST-RAW");
        string content = $"#EXTM3U\n#EXTINF:-1,Synthetic\0{canary}\nhttps://fixtures.invalid/live.ts";

        DomainResult<PlaylistContentKind> result = PlaylistKindClassifier.Classify(content);

        AssertUnsupported(result);
        Assert.IsFalse(result.ToString().Contains(canary, StringComparison.Ordinal));
        Assert.IsFalse(result.Error!.ToString().Contains(canary, StringComparison.Ordinal));
        Assert.IsFalse(result.Error.ResourceKey.Contains(canary, StringComparison.Ordinal));
    }

    private static void AssertSuccess(
        DomainResult<PlaylistContentKind> result,
        PlaylistContentKind expected,
        string scenario)
    {
        Assert.IsTrue(result.IsSuccess, scenario);
        Assert.AreEqual(expected, result.Value, scenario);
        Assert.IsNull(result.Error, scenario);
    }

    private static void AssertUnsupported(DomainResult<PlaylistContentKind> result)
    {
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PlaylistContentKind.Unknown, result.Value);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, result.Error.Code);
        Assert.AreEqual(DomainRetryability.Never, result.Error.Retryability);
        Assert.AreEqual("Errors.Playlist.UnsupportedFormat", result.Error.ResourceKey);
    }
}
