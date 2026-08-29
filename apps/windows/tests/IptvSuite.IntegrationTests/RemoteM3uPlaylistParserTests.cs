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
    [DataRow("#EXTM3U url-tvg=\"https://guide.invalid/epg.xml\"")]
    [DataRow("#EXTM3U\tx-tvg-url=\"https://guide.invalid/epg.xml\"")]
    public async Task CommonExtendedHeaderAttributesAreAcceptedAfterWhitespace(string header)
    {
        string playlist = $"{header}\n#EXTINF:-1,Synthetic\nhttps://fixtures.invalid/live.ts\n";

        ParseSnapshot result = await ParseAsync(Encoding.UTF8.GetBytes(playlist));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Entries);
        Assert.AreEqual("Synthetic", result.Entries[0].Name);
    }

    [TestMethod]
    [DataRow("#EXTM3UX")]
    [DataRow("#EXTM3U-PLUS")]
    [DataRow("#EXTM3Uurl-tvg=\"https://guide.invalid/epg.xml\"")]
    public async Task ExtendedHeaderLookalikesWithoutWhitespaceAreRejected(string header)
    {
        string playlist = $"{header}\n#EXTINF:-1,Synthetic\nhttps://fixtures.invalid/live.ts\n";

        ParseSnapshot result = await ParseAsync(Encoding.UTF8.GetBytes(playlist));

        Assert.AreEqual(DomainErrorCode.PlaylistHeaderInvalid, result.ErrorCode);
    }

    [TestMethod]
    public async Task EmptyResponseReturnsSafeHeaderReason()
    {
        ParseSnapshot result = await ParseAsync([]);

        Assert.AreEqual(DomainErrorCode.PlaylistHeaderInvalid, result.ErrorCode);
    }

    [TestMethod]
    public async Task MetadataBoundsMatchDownstreamDomainAndOversizedEntriesAreSkippedIndividually()
    {
        int maximumTvgIdCharacters = Math.Min(
            LiveChannel.MaximumProviderKeyLength,
            ChannelStableKeyBuilder.MaximumProviderIdentifierLength);
        int maximumGroupCharacters = Math.Min(
            ChannelCategory.MaximumProviderKeyLength,
            ChannelCategory.MaximumNameLength);
        string maximumTvgId = new('i', maximumTvgIdCharacters);
        string maximumGroup = new('g', maximumGroupCharacters);
        string oversizedTvgId = new('i', maximumTvgIdCharacters + 1);
        string oversizedGroup = new('g', maximumGroupCharacters + 1);
        string playlist = "#EXTM3U\n" +
            $"#EXTINF:-1 tvg-id=\"{oversizedTvgId}\" group-title=\"News\",Oversized identifier\n" +
            "https://fixtures.invalid/oversized-id.ts\n" +
            $"#EXTINF:-1 tvg-id=\"group-overflow\" group-title=\"{oversizedGroup}\",Oversized group\n" +
            "https://fixtures.invalid/oversized-group.ts\n" +
            $"#EXTINF:-1 tvg-id=\"{maximumTvgId}\" group-title=\"{maximumGroup}\",Boundary channel\n" +
            "https://fixtures.invalid/boundary.ts\n";

        ParseSnapshot result = await ParseAsync(Encoding.UTF8.GetBytes(playlist));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Entries);
        Assert.AreEqual(2, result.SkippedEntryCount);
        Assert.AreEqual("Boundary channel", result.Entries[0].Name);
        Assert.AreEqual(maximumTvgId, result.Entries[0].TvgId);
        Assert.AreEqual(maximumGroup, result.Entries[0].GroupTitle);
    }

    [TestMethod]
    [DataRow("#EXTM3U\n#EXTINF:-1,Synthetic\nhttp://fixtures.invalid/live.ts")]
    [DataRow("#EXTM3U\n#EXTINF:-1,Synthetic\nhttps://user:pass@fixtures.invalid/live.ts")]
    [DataRow("#EXTM3U\n#EXTINF:-1,Synthetic\nhttps://fixtures.invalid/live.ts#fragment")]
    public async Task UnsafeLocatorIsSkipped(string playlist)
    {
        string catalog = playlist +
            "\n#EXTINF:-1,Accepted\nhttps://fixtures.invalid/accepted.ts\n";

        ParseSnapshot result = await ParseAsync(Encoding.UTF8.GetBytes(catalog));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Entries);
        Assert.AreEqual(1, result.SkippedEntryCount);
        Assert.IsFalse(result.EntryLimitReached);
    }

    [TestMethod]
    [DataRow("#EXTM3U\n", DomainErrorCode.PlaylistNoUsableEntries)]
    [DataRow(
        "#EXTM3U\n#EXTINF:-1,Synthetic\nhttp://fixtures.invalid/live.ts\n",
        DomainErrorCode.PlaylistEntriesRejectedByAddressPolicy)]
    [DataRow(
        "#EXTM3U\n#EXTINF:-1 malformed\nhttps://fixtures.invalid/live.ts\n",
        DomainErrorCode.PlaylistNoUsableEntries)]
    public async Task CatalogWithoutAcceptedEntriesReturnsSafeReason(
        string playlist,
        DomainErrorCode expectedError)
    {
        ParseSnapshot result = await ParseAsync(Encoding.UTF8.GetBytes(playlist));

        Assert.AreEqual(expectedError, result.ErrorCode);
    }

    [TestMethod]
    public async Task HttpSourceAcceptsOnlyExactOriginHttpEntriesWhileHttpsRemainsAllowed()
    {
        const string playlist = "#EXTM3U\n" +
            "#EXTINF:-1 tvg-logo=\"http://fixtures.invalid:8080/logo.png\",Synthetic relative\nlive/relative.ts\n" +
            "#EXTINF:-1 tvg-logo=\"https://fixtures.invalid:8080/logo.png\",Synthetic exact\nhttp://fixtures.invalid:8080/live/exact.ts\n" +
            "#EXTINF:-1,Synthetic secure\nhttps://media.invalid/live/secure.ts\n" +
            "#EXTINF:-1,Synthetic other host\nhttp://other.invalid:8080/live/rejected.ts\n" +
            "#EXTINF:-1,Synthetic other port\nhttp://fixtures.invalid:8081/live/rejected.ts\n" +
            "#EXTINF:-1,Synthetic user info\nhttp://user:pass@fixtures.invalid:8080/live/rejected.ts\n" +
            "#EXTINF:-1,Synthetic fragment\nhttp://fixtures.invalid:8080/live/rejected.ts#fragment\n";

        ParseSnapshot result = await ParseForSourceAsync(
            Encoding.UTF8.GetBytes(playlist),
            "http://fixtures.invalid:8080/catalog/list.m3u",
            "http://fixtures.invalid:8080/catalog/final/list.m3u");

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(3, result.Entries);
        Assert.AreEqual(
            "http://fixtures.invalid:8080/catalog/final/live/relative.ts",
            result.Entries[0].Locator);
        Assert.IsNull(result.Entries[0].Logo);
        Assert.AreEqual("http://fixtures.invalid:8080/live/exact.ts", result.Entries[1].Locator);
        Assert.IsNull(result.Entries[1].Logo);
        Assert.AreEqual("https://media.invalid/live/secure.ts", result.Entries[2].Locator);
        Assert.AreEqual(4, result.SkippedEntryCount);
    }

    [TestMethod]
    public async Task HttpSourceFinalCatalogRequiresExactHttpOriginButAllowsHttpsUpgrade()
    {
        byte[] playlist = Encoding.UTF8.GetBytes(
            "#EXTM3U\n#EXTINF:-1,Synthetic\nhttps://media.invalid/live/secure.ts\n");

        ParseSnapshot sameOrigin = await ParseForSourceAsync(
            playlist,
            "http://fixtures.invalid:8080/catalog/list.m3u",
            "http://fixtures.invalid:8080/catalog/final/list.m3u");
        ParseSnapshot otherHttpOrigin = await ParseForSourceAsync(
            playlist,
            "http://fixtures.invalid:8080/catalog/list.m3u",
            "http://redirect.invalid:8080/catalog/final/list.m3u");
        ParseSnapshot httpsUpgrade = await ParseForSourceAsync(
            playlist,
            "http://fixtures.invalid:8080/catalog/list.m3u",
            "https://redirect.invalid/catalog/final/list.m3u");
        ParseSnapshot httpsSourceDowngrade = await ParseForSourceAsync(
            playlist,
            "https://fixtures.invalid/catalog/list.m3u",
            "http://fixtures.invalid/catalog/final/list.m3u");
        ParseSnapshot httpFinalUserInfo = await ParseForSourceAsync(
            playlist,
            "http://fixtures.invalid:8080/catalog/list.m3u",
            "http://user:pass@fixtures.invalid:8080/catalog/final/list.m3u");
        ParseSnapshot httpsFinalFragment = await ParseForSourceAsync(
            playlist,
            "http://fixtures.invalid:8080/catalog/list.m3u",
            "https://redirect.invalid/catalog/final/list.m3u#fragment");

        Assert.IsTrue(sameOrigin.IsSuccess);
        Assert.AreEqual(DomainErrorCode.PlaylistResponseAddressRejected, otherHttpOrigin.ErrorCode);
        Assert.IsTrue(httpsUpgrade.IsSuccess);
        Assert.AreEqual(DomainErrorCode.PlaylistResponseAddressRejected, httpsSourceDowngrade.ErrorCode);
        Assert.AreEqual(DomainErrorCode.PlaylistResponseAddressRejected, httpFinalUserInfo.ErrorCode);
        Assert.AreEqual(DomainErrorCode.PlaylistResponseAddressRejected, httpsFinalFragment.ErrorCode);
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
        Assert.AreEqual(DomainErrorCode.PlaylistTextEncodingInvalid, invalid.ErrorCode);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ParseSnapshot cancelled = await ParseAsync(Encoding.UTF8.GetBytes("#EXTM3U\n"), cancellation.Token);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, cancelled.ErrorCode);
    }

    [TestMethod]
    public async Task PhysicalLinesAboveLegacyBoundRemainCompatibleWithinCurrentBound()
    {
        var metadata = new StringBuilder("#EXTINF:-1 tvg-id=\"long-line\" group-title=\"Synthetic\"");
        for (int index = 0; index < 700; index++)
        {
            metadata.Append(" x-").Append(index).Append("=\"synthetic-value\"");
        }

        metadata.Append(",Long metadata channel");
        Assert.IsGreaterThan(8_192, metadata.Length);
        Assert.IsLessThanOrEqualTo(64 * 1024, metadata.Length);
        string playlist = $"#EXTM3U\r\n{metadata}\r\nstream/long.ts\r\n";

        ParseSnapshot result = await ParseAsync(Encoding.UTF8.GetBytes(playlist));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Entries);
        Assert.AreEqual("Long metadata channel", result.Entries[0].Name);
        Assert.AreEqual("long-line", result.Entries[0].TvgId);
        Assert.AreEqual("Synthetic", result.Entries[0].GroupTitle);
        Assert.AreEqual("https://fixtures.invalid/catalog/stream/long.ts", result.Entries[0].Locator);
    }

    [TestMethod]
    public async Task PhysicalLineBoundAndMixedStructureReturnDistinctSafeReasons()
    {
        byte[] oversizedLine = Encoding.UTF8.GetBytes(
            $"#EXTM3U\n#{new string('x', 64 * 1024)}");
        const string mixedStructure = "#EXTM3U\n" +
            "#EXTINF:-1,Synthetic\nhttps://fixtures.invalid/live.ts\n" +
            "#EXT-X-ENDLIST\n";

        ParseSnapshot limit = await ParseAsync(oversizedLine);
        ParseSnapshot structure = await ParseAsync(Encoding.UTF8.GetBytes(mixedStructure));

        Assert.AreEqual(DomainErrorCode.PlaylistLineLimitExceeded, limit.ErrorCode);
        Assert.AreEqual(DomainErrorCode.PlaylistStructureInvalid, structure.ErrorCode);
    }

    [TestMethod]
    public async Task ExactPhysicalLineBoundAndCrLfAcrossReaderBufferRemainSupported()
    {
        string exactBoundComment = $"#{new string('x', (64 * 1024) - 1)}";
        string crLfBoundaryComment = $"#{new string('x', 4_085)}";
        string playlist = "#EXTM3U\r\n" + crLfBoundaryComment + "\r\n" +
            exactBoundComment + "\r\n#EXTINF:-1,Synthetic\r\nstream/live.ts\r\n";

        ParseSnapshot result = await ParseAsync(Encoding.UTF8.GetBytes(playlist));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Entries);
        Assert.AreEqual("Synthetic", result.Entries[0].Name);
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
    public async Task ExactMaximumEntryCountSucceedsAndHttpsPathsRemainFailClosedAtLimitPlusOne()
    {
        ParseSnapshot success = await ParseAsync(Encoding.UTF8.GetBytes(CreatePlaylist(50_000)));
        Assert.IsTrue(success.IsSuccess);
        Assert.HasCount(50_000, success.Entries);
        Assert.IsFalse(success.EntryLimitReached);

        byte[] overflow = Encoding.UTF8.GetBytes(CreatePlaylist(50_001));
        ParseSnapshot directFailure = await ParseAsync(overflow);
        ParseSnapshot configuredHttpsFailure = await ParseForSourceAsync(
            overflow,
            "https://fixtures.invalid/catalog/list.m3u",
            "https://fixtures.invalid/catalog/final/list.m3u");

        Assert.AreEqual(DomainErrorCode.PlaylistEntryLimitExceeded, directFailure.ErrorCode);
        Assert.AreEqual(DomainErrorCode.PlaylistEntryLimitExceeded, configuredHttpsFailure.ErrorCode);
    }

    [TestMethod]
    public async Task ExplicitHttpSourceKeepsFirstMaximumEntriesAndMarksTruncatedTail()
    {
        ParseSnapshot result = await ParseForSourceAsync(
            Encoding.UTF8.GetBytes(CreatePlaylist(50_001)),
            "http://fixtures.invalid:8080/catalog/list.m3u",
            "http://fixtures.invalid:8080/catalog/final/list.m3u");

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(50_000, result.Entries);
        Assert.AreEqual(1, result.SkippedEntryCount);
        Assert.IsTrue(result.EntryLimitReached);
        Assert.AreEqual("Channel 49999", result.Entries[^1].Name);
    }

    [TestMethod]
    public async Task ExplicitHttpSourceStillRejectsInvalidUtf8AfterTruncatedTail()
    {
        byte[] validPrefix = Encoding.UTF8.GetBytes(CreatePlaylist(50_001));
        byte[] payload = [.. validPrefix, 0xC3, 0x28];

        ParseSnapshot result = await ParseForSourceAsync(
            payload,
            "http://fixtures.invalid:8080/catalog/list.m3u",
            "http://fixtures.invalid:8080/catalog/final/list.m3u");

        Assert.AreEqual(DomainErrorCode.PlaylistTextEncodingInvalid, result.ErrorCode);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task FiftyThousandEntryParserStaysWithinReferenceStageBudget()
    {
        byte[] payload = Encoding.UTF8.GetBytes(CreatePlaylist(50_000));
        ParserOutcome warmup = await ParseOutcomeAsync(payload);
        AssertSuccessfulParserOutcome(warmup);

        const int iterations = 20;
        var samples = new List<long>(iterations);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            long started = Stopwatch.GetTimestamp();
            ParserOutcome result = await ParseOutcomeAsync(payload);
            samples.Add((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            AssertSuccessfulParserOutcome(result);
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

    private static async Task<ParseSnapshot> ParseForSourceAsync(
        byte[] payload,
        string sourceLocator,
        string finalLocator,
        CancellationToken cancellationToken = default)
    {
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared =
            SourceConfigurationValidator.PrepareRemotePlaylistAllowingInsecureHttp(
                "Synthetic source",
                sourceLocator);
        Assert.IsTrue(prepared.IsSuccess);
        await using var stream = new MemoryStream(payload, writable: false);
        return await InvokeParserForSourceAsync(
            stream,
            new Uri(finalLocator),
            prepared.Value!.SafeEndpoint,
            cancellationToken);
    }

    private static async Task<ParserOutcome> ParseOutcomeAsync(
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new MemoryStream(payload, writable: false);
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
            return new(
                false,
                (DomainErrorCode)error.GetType().GetProperty("Code")!.GetValue(error)!,
                0,
                0);
        }

        object parsed = result.GetType().GetProperty("Value")!.GetValue(result)!;
        return new(
            true,
            null,
            (int)GetProperty(parsed, "ProcessedEntryCount")!,
            (int)GetProperty(parsed, "SkippedEntryCount")!);
    }

    private static async Task<ParseSnapshot> InvokeParserAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        Type parserType = typeof(BoundedHttpTransport).Assembly.GetType("IptvSuite.Infrastructure.RemoteM3uPlaylistParser", true)!;
        MethodInfo method = parserType.GetMethod("ParseAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(null, [stream, new Uri("https://fixtures.invalid/catalog/list.m3u"), cancellationToken])!;
        return await ReadParseSnapshotAsync(valueTask);
    }

    private static async Task<ParseSnapshot> InvokeParserForSourceAsync(
        Stream stream,
        Uri finalPlaylistUri,
        SafeEndpoint configuredSourceEndpoint,
        CancellationToken cancellationToken)
    {
        Type parserType = typeof(BoundedHttpTransport).Assembly.GetType(
            "IptvSuite.Infrastructure.RemoteM3uPlaylistParser",
            true)!;
        MethodInfo method = parserType.GetMethod(
            "ParseForSourceAsync",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(
            null,
            [stream, finalPlaylistUri, configuredSourceEndpoint, cancellationToken])!;
        return await ReadParseSnapshotAsync(valueTask);
    }

    private static async Task<ParseSnapshot> ReadParseSnapshotAsync(object valueTask)
    {
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        bool success = (bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!;
        if (!success)
        {
            object error = result.GetType().GetProperty("Error")!.GetValue(result)!;
            var code = (DomainErrorCode)error.GetType().GetProperty("Code")!.GetValue(error)!;
            return new(false, code, PlaylistContentKind.Unknown, [], 0, false, null);
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
            (bool)GetProperty(parsed, "EntryLimitReached")!,
            (string?)GetProperty(parsed, "HlsLocator"));
    }

    private static void AssertSuccessfulParserOutcome(ParserOutcome outcome)
    {
        Assert.IsTrue(outcome.IsSuccess);
        Assert.AreEqual(50_000, outcome.ProcessedEntryCount);
        Assert.AreEqual(0, outcome.SkippedEntryCount);
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
        bool EntryLimitReached,
        string? HlsLocator);
    private sealed record ParserOutcome(
        bool IsSuccess,
        DomainErrorCode? ErrorCode,
        int ProcessedEntryCount,
        int SkippedEntryCount);
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
