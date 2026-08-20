using System.Text;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class RemoteM3uPlaylistParserTests
{
    [TestMethod]
    public async Task Utf8BomMetadataAndRelativeLocatorProduceBoundedEntry()
    {
        const string playlist = "\uFEFF#EXTM3U\r\n" +
            "#EXTINF:-1 tvg-id=\"news-1\" tvg-name=\"Synthetic News\" tvg-logo=\"logo.png\" group-title=\"News\" tvg-chno=\"7\",Synthetic News HD\r\n" +
            "streams/news.ts\r\n";

        DomainResult<RemoteM3uParseResult> result = await ParseAsync(playlist);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Value!.Entries);
        RemoteM3uEntry entry = result.Value.Entries[0];
        Assert.AreEqual("https://fixtures.invalid/catalog/streams/news.ts", entry.Locator);
        Assert.AreEqual("Synthetic News HD", entry.Name);
        Assert.AreEqual("news-1", entry.TvgId);
        Assert.AreEqual("Synthetic News", entry.TvgName);
        Assert.AreEqual("logo.png", entry.Logo);
        Assert.AreEqual("News", entry.GroupTitle);
        Assert.AreEqual(7, entry.Number);
        Assert.AreEqual(ChannelNormalizationWarnings.None, entry.Warnings);
    }

    [TestMethod]
    public async Task UnknownDirectivesAndAttributesAreIgnoredWithoutRetainingThem()
    {
        const string playlist = "#EXTM3U\n" +
            "# synthetic comment\n" +
            "#EXTINF:-1 unknown=\"ignored\" group-title=\"Synthetic\",Safe\n" +
            "https://media.invalid/live/1.ts\n";

        DomainResult<RemoteM3uParseResult> result = await ParseAsync(playlist);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Value!.Entries);
        Assert.AreEqual("Safe", result.Value.Entries[0].Name);
    }

    [TestMethod]
    [DataRow("#EXTM3U\n#EXT-X-TARGETDURATION:10\nsegment.ts")]
    [DataRow("#EXTM3U\n#EXTINF:-1,Synthetic\nhttp://fixtures.invalid/live.ts")]
    [DataRow("#EXTM3U\n#EXTINF:-1,Synthetic\nhttps://user:pass@fixtures.invalid/live.ts")]
    [DataRow("#EXTM3U\n#EXTINF:-1,Synthetic\nhttps://fixtures.invalid/live.ts#fragment")]
    public async Task HlsOrUnsafeLocatorIsRejectedOrSkipped(string playlist)
    {
        DomainResult<RemoteM3uParseResult> result = await ParseAsync(playlist);

        if (playlist.Contains("#EXT-X-", StringComparison.Ordinal))
        {
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, result.Error!.Code);
        }
        else
        {
            Assert.IsTrue(result.IsSuccess);
            Assert.IsEmpty(result.Value!.Entries);
            Assert.AreEqual(1, result.Value.SkippedEntryCount);
        }
    }

    [TestMethod]
    public async Task InvalidUtf8FailsClosed()
    {
        byte[] payload = [.. Encoding.UTF8.GetBytes("#EXTM3U\n"), 0xC3, 0x28];
        await using var stream = new MemoryStream(payload, writable: false);

        DomainResult<RemoteM3uParseResult> result = await RemoteM3uPlaylistParser.ParseAsync(
            stream,
            new Uri("https://fixtures.invalid/catalog/list.m3u"));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, result.Error!.Code);
    }

    [TestMethod]
    public async Task CancellationReturnsTypedFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("#EXTM3U\n"), writable: false);

        DomainResult<RemoteM3uParseResult> result = await RemoteM3uPlaylistParser.ParseAsync(
            stream,
            new Uri("https://fixtures.invalid/catalog/list.m3u"),
            cancellation.Token);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, result.Error!.Code);
    }

    [TestMethod]
    public async Task ExactMaximumEntryCountSucceedsAndLimitPlusOneFailsClosed()
    {
        string accepted = CreatePlaylist(RemoteM3uPlaylistParser.MaximumEntries);
        DomainResult<RemoteM3uParseResult> success = await ParseAsync(accepted);

        Assert.IsTrue(success.IsSuccess);
        Assert.HasCount(RemoteM3uPlaylistParser.MaximumEntries, success.Value!.Entries);

        string rejected = CreatePlaylist(RemoteM3uPlaylistParser.MaximumEntries + 1);
        DomainResult<RemoteM3uParseResult> failure = await ParseAsync(rejected);

        Assert.IsFalse(failure.IsSuccess);
        Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, failure.Error!.Code);
    }

    private static async Task<DomainResult<RemoteM3uParseResult>> ParseAsync(string playlist)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(playlist), writable: false);
        return await RemoteM3uPlaylistParser.ParseAsync(
            stream,
            new Uri("https://fixtures.invalid/catalog/list.m3u"));
    }

    private static string CreatePlaylist(int count)
    {
        var builder = new StringBuilder("#EXTM3U\n", count * 48);
        for (int index = 0; index < count; index++)
        {
            builder.Append("#EXTINF:-1 group-title=\"Synthetic\",Channel ")
                .Append(index)
                .Append("\nstream/")
                .Append(index)
                .Append(".ts\n");
        }

        return builder.ToString();
    }
}
