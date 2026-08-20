using System.Reflection;
using System.Diagnostics;
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
    [DataRow("#EXTM3U\n#EXTINF:-1,Synthetic\nhttp://fixtures.invalid/live.ts")]
    [DataRow("#EXTM3U\n#EXTINF:-1,Synthetic\nhttps://user:pass@fixtures.invalid/live.ts")]
    [DataRow("#EXTM3U\n#EXTINF:-1,Synthetic\nhttps://fixtures.invalid/live.ts#fragment")]
    public async Task UnsafeLocatorIsSkipped(string playlist)
    {
        ParseSnapshot result = await ParseAsync(Encoding.UTF8.GetBytes(playlist));
        Assert.IsTrue(result.IsSuccess);
        Assert.IsEmpty(result.Entries);
        Assert.AreEqual(1, result.SkippedEntryCount);
    }

    [TestMethod]
    [DataRow("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nvariant.m3u8", PlaylistContentKind.HlsMasterManifest)]
    [DataRow("#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\nsegment.ts\n#EXT-X-ENDLIST", PlaylistContentKind.HlsMediaManifest)]
    public async Task ValidHlsIsRoutedWithoutCatalogEntries(string playlist, PlaylistContentKind expected)
    {
        ParseSnapshot result = await ParseAsync(Encoding.UTF8.GetBytes(playlist));
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(expected, result.ContentKind);
        Assert.IsEmpty(result.Entries);
        Assert.AreEqual("https://fixtures.invalid/catalog/list.m3u", result.HlsLocator);
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
    public async Task DuplicateTvgIdentifierIsFirstWinsVisibleWarning()
    {
        const string playlist = "#EXTM3U\n" +
            "#EXTINF:-1 tvg-id=\"duplicate\" group-title=\"News\",First\nfirst.ts\n" +
            "#EXTINF:-1 tvg-id=\"duplicate\" group-title=\"News\",Second\nsecond.ts\n";

        ParseSnapshot result = await ParseAsync(Encoding.UTF8.GetBytes(playlist));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, result.Entries);
        Assert.AreEqual(ChannelNormalizationWarnings.None, result.Entries[0].Warnings);
        Assert.AreEqual(
            ChannelNormalizationWarnings.DuplicateProviderIdentifier,
            result.Entries[1].Warnings);
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

    [TestMethod]
    public async Task FiftyThousandEntryParserStaysWithinReferenceStageBudget()
    {
        byte[] payload = Encoding.UTF8.GetBytes(CreatePlaylist(50_000));
        var samples = new List<long>();
        for (int iteration = 0; iteration < 10; iteration++)
        {
            long started = Stopwatch.GetTimestamp();
            ParseSnapshot result = await ParseAsync(payload);
            samples.Add((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.IsTrue(result.IsSuccess);
        }

        samples.Sort();
        long p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
        Assert.IsLessThanOrEqualTo(2_000L, p95);
    }

    [TestMethod]
    public async Task MidStreamCancellationCompletesWithinBoundaryBudget()
    {
        byte[] payload = Encoding.UTF8.GetBytes(CreatePlaylist(50_000));
        var latencies = new List<long>();
        for (int iteration = 0; iteration < 20; iteration++)
        {
            using var cancellation = new CancellationTokenSource();
            await using var stream = new CancellingChunkStream(payload, cancellation, cancelAfterReads: 3);
            long started = Stopwatch.GetTimestamp();
            ParseSnapshot result = await InvokeParserAsync(stream, cancellation.Token);
            latencies.Add((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.AreEqual(DomainErrorCode.OperationCancelled, result.ErrorCode);
        }

        latencies.Sort();
        long p95 = latencies[(int)Math.Ceiling(latencies.Count * 0.95) - 1];
        Assert.IsLessThanOrEqualTo(250L, p95);
    }

    [TestMethod]
    public async Task DeterministicMalformedByteCorpusNeverEscapesTheTypedResultBoundary()
    {
        var random = new Random(20260820);
        for (int iteration = 0; iteration < 256; iteration++)
        {
            int length = iteration % 2 == 0 ? random.Next(8, 4_097) : random.Next(0, 4_097);
            byte[] payload = new byte[length];
            random.NextBytes(payload);
            if (iteration % 2 == 0)
            {
                byte[] header = Encoding.UTF8.GetBytes("#EXTM3U\n");
                header.CopyTo(payload, 0);
            }

            ParseSnapshot result = await ParseAsync(payload);
            Assert.IsTrue(result.IsSuccess || result.ErrorCode is not null);
        }
    }

    private static async Task<ParseSnapshot> ParseAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        await using var stream = new MemoryStream(payload, writable: false);
        return await InvokeParserAsync(stream, cancellationToken);
    }

    private static async Task<ParseSnapshot> InvokeParserAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        Type parserType = typeof(BoundedHttpTransport).Assembly.GetType("IptvSuite.Infrastructure.RemoteM3uPlaylistParser", true)!;
        MethodInfo method = parserType.GetMethod("ParseAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(null, [stream, new Uri("https://fixtures.invalid/catalog/list.m3u"), cancellationToken])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        bool success = (bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!;
        if (!success)
        {
            object error = result.GetType().GetProperty("Error")!.GetValue(result)!;
            var code = (DomainErrorCode)error.GetType().GetProperty("Code")!.GetValue(error)!;
            return new(false, code, PlaylistContentKind.Unknown, [], 0, null);
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

        return new(
            true,
            null,
            (PlaylistContentKind)GetProperty(parsed, "ContentKind")!,
            entries,
            (int)GetProperty(parsed, "SkippedEntryCount")!,
            (string?)GetProperty(parsed, "HlsLocator"));
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

    private sealed record ParseSnapshot(
        bool IsSuccess,
        DomainErrorCode? ErrorCode,
        PlaylistContentKind ContentKind,
        IReadOnlyList<EntrySnapshot> Entries,
        int SkippedEntryCount,
        string? HlsLocator);
    private sealed record EntrySnapshot(string Locator, string Name, string? TvgId, string? TvgName, string? Logo, string? GroupTitle, int? Number, ChannelNormalizationWarnings Warnings, string RedactedText);

    private sealed class CancellingChunkStream(
        byte[] payload,
        CancellationTokenSource cancellation,
        int cancelAfterReads) : MemoryStream(payload, writable: false)
    {
        private int _reads;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == cancelAfterReads)
            {
                cancellation.Cancel();
            }

            int requested = Math.Min(buffer.Length, 256);
            return base.ReadAsync(buffer[..requested], cancellationToken);
        }
    }
}
