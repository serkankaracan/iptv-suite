using System.Reflection;
using System.Text;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class RemoteM3uPlaylistParserTests
{
    [TestMethod]
    public async Task Utf8BomMetadataAndRelativeLocatorProduceBoundedRedactedEntry()
    {
        const string playlist = "\uFEFF#EXTM3U\r\n#EXTINF:-1 tvg-id=\"news-1\" tvg-name=\"Synthetic News\" tvg-logo=\"logo.png\" group-title=\"News\" tvg-chno=\"7\",Synthetic News HD\r\nstreams/news.ts\r\n";
        ParseSnapshot result = await ParseAsync(Encoding.UTF8.GetBytes(playlist));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Entries);
        EntrySnapshot entry = result.Entries[0];
        Assert.AreEqual("https://fixtures.invalid/catalog/streams/news.ts", entry.Locator);
        Assert.AreEqual("Synthetic News HD", entry.Name);
        Assert.AreEqual("news-1", entry.TvgId);
        Assert.AreEqual("Synthetic News", entry.TvgName);
        Assert.AreEqual("logo.png", entry.Logo);
        Assert.AreEqual("News", entry.GroupTitle);
        Assert.AreEqual(7, entry.Number);
        Assert.AreEqual(ChannelNormalizationWarnings.None, entry.Warnings);
        Assert.AreEqual("[REMOTE-M3U-ENTRY]", entry.RedactedText);
    }

    [TestMethod]
    [DataRow("#EXTM3U\n#EXT-X-TARGETDURATION:10\nsegment.ts", true)]
    [DataRow("#EXTM3U\n#EXTINF:-1,Synthetic\nhttp://fixtures.invalid/live.ts", false)]
    [DataRow("#EXTM3U\n#EXTINF:-1,Synthetic\nhttps://user:pass@fixtures.invalid/live.ts", false)]
    [DataRow("#EXTM3U\n#EXTINF:-1,Synthetic\nhttps://fixtures.invalid/live.ts#fragment", false)]
    public async Task HlsOrUnsafeLocatorFailsClosed(string playlist, bool wholePlaylistFailure)
    {
        ParseSnapshot result = await ParseAsync(Encoding.UTF8.GetBytes(playlist));
        Assert.AreEqual(!wholePlaylistFailure, result.IsSuccess);
        if (wholePlaylistFailure) Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, result.ErrorCode);
        else
        {
            Assert.IsEmpty(result.Entries);
            Assert.AreEqual(1, result.SkippedEntryCount);
        }
    }

    [TestMethod]
    public async Task InvalidUtf8AndCancellationReturnTypedFailures()
    {
        ParseSnapshot invalid = await ParseAsync([.. Encoding.UTF8.GetBytes("#EXTM3U\n"), 0xC3, 0x28]);
        Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, invalid.ErrorCode);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ParseSnapshot cancelled = await ParseAsync(Encoding.UTF8.GetBytes("#EXTM3U\n"), cancellation.Token);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, cancelled.ErrorCode);
    }

    [TestMethod]
    public async Task ExactMaximumEntryCountSucceedsAndLimitPlusOneFailsClosed()
    {
        ParseSnapshot success = await ParseAsync(Encoding.UTF8.GetBytes(CreatePlaylist(50_000)));
        Assert.IsTrue(success.IsSuccess);
        Assert.HasCount(50_000, success.Entries);

        ParseSnapshot failure = await ParseAsync(Encoding.UTF8.GetBytes(CreatePlaylist(50_001)));
        Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, failure.ErrorCode);
    }

    private static async Task<ParseSnapshot> ParseAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        Type parserType = typeof(BoundedHttpTransport).Assembly.GetType("IptvSuite.Infrastructure.RemoteM3uPlaylistParser", true)!;
        MethodInfo method = parserType.GetMethod("ParseAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        await using var stream = new MemoryStream(payload, writable: false);
        object valueTask = method.Invoke(null, [stream, new Uri("https://fixtures.invalid/catalog/list.m3u"), cancellationToken])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        bool success = (bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!;
        if (!success)
        {
            object error = result.GetType().GetProperty("Error")!.GetValue(result)!;
            var code = (DomainErrorCode)error.GetType().GetProperty("Code")!.GetValue(error)!;
            return new(false, code, [], 0);
        }

        object parsed = result.GetType().GetProperty("Value")!.GetValue(result)!;
        var rawEntries = (System.Collections.IEnumerable)GetProperty(parsed, "Entries")!;
        var entries = new List<EntrySnapshot>();
        foreach (object entry in rawEntries)
        {
            entries.Add(new(
                Get<string>(entry, "Locator"), Get<string>(entry, "Name"), Get<string?>(entry, "TvgId"),
                Get<string?>(entry, "TvgName"), Get<string?>(entry, "Logo"), Get<string?>(entry, "GroupTitle"),
                Get<int?>(entry, "Number"), Get<ChannelNormalizationWarnings>(entry, "Warnings"), entry.ToString()!));
        }

        return new(true, null, entries, (int)GetProperty(parsed, "SkippedEntryCount")!);
    }

    private static object? GetProperty(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);

    private static T Get<T>(object instance, string name) => (T)GetProperty(instance, name)!;

    private static string CreatePlaylist(int count)
    {
        var builder = new StringBuilder("#EXTM3U\n", count * 72);
        for (int index = 0; index < count; index++)
            builder.Append("#EXTINF:-1 group-title=\"Synthetic\",Channel ").Append(index)
                .Append("\nstream/").Append(index).Append(".ts\n");
        return builder.ToString();
    }

    private sealed record ParseSnapshot(bool IsSuccess, DomainErrorCode? ErrorCode, IReadOnlyList<EntrySnapshot> Entries, int SkippedEntryCount);
    private sealed record EntrySnapshot(string Locator, string Name, string? TvgId, string? TvgName, string? Logo, string? GroupTitle, int? Number, ChannelNormalizationWarnings Warnings, string RedactedText);
}
