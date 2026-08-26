using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace IptvSuite.IntegrationTests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class M16SyntheticEndToEndJourneyTests
{
    private const string SourceName = "M16 synthetic source";
    private const string CategoryName = "Synthetic";
    private const string RecoveryChannelName = "M16 Recovery";
    private const string OtherChannelName = "M16 Other";
    private static readonly string[] ExpectedRequests =
    [
        "GET /catalog.m3u",
        "GET /catalog.m3u",
        "GET /media/recovery.ts",
        "GET /media/recovery.ts",
    ];

    [TestMethod]
    [Timeout(60_000)]
    public async Task AuthorizedRemotePlaylistCompletesExactSyntheticReleaseCandidateJourney()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m16-synthetic-journey");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        FakeTimeProvider time = TestTime.Create(
            new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        byte[] recoveryMedia = CreateMediaBody(0x31);
        byte[] otherMedia = CreateMediaBody(0x63);
        byte[] playlist = Encoding.UTF8.GetBytes(
            "#EXTM3U\n" +
            $"#EXTINF:-1 tvg-id=\"m16-recovery\" group-title=\"{CategoryName}\",{RecoveryChannelName}\n" +
            "/media/recovery.ts\n" +
            $"#EXTINF:-1 tvg-id=\"m16-other\" group-title=\"{CategoryName}\",{OtherChannelName}\n" +
            "/media/other.ts\n");
        var routes = new Dictionary<string, FixtureHttpResponse>(StringComparer.Ordinal)
        {
            ["/catalog.m3u"] = new(200, "audio/x-mpegurl", playlist),
            ["/media/recovery.ts"] = new(200, "video/mp2t", recoveryMedia),
            ["/media/other.ts"] = new(200, "video/mp2t", otherMedia),
        };

        await using LocalHttpFixtureServer server =
            await LocalHttpFixtureServer.StartHttpsAsync(routes);
        using BoundedHttpTransport transport = CreatePinnedTransport(server);
        string sourceLocator = new Uri(server.BaseAddress, "/catalog.m3u").AbsoluteUri;
        string recoveryLocator = new Uri(server.BaseAddress, "/media/recovery.ts").AbsoluteUri;
        string otherLocator = new Uri(server.BaseAddress, "/media/other.ts").AbsoluteUri;
        var importer = new SqliteRemotePlaylistCatalogImporter(databasePath, store, transport);
        var onboarding = new RemotePlaylistSourceOnboardingService(
            store,
            transport,
            importer,
            time);

        DomainResult<RemotePlaylistSourceOnboardingResult> added =
            await onboarding.AddAsync(SourceName, sourceLocator);

        Assert.IsTrue(added.IsSuccess, added.Error?.ToString());
        RemotePlaylistSourceOnboardingResult onboardingResult = added.Value!;
        Assert.AreEqual(2, onboardingResult.ImportedChannelCount);
        Assert.AreEqual(0, onboardingResult.WarningCount);
        Assert.AreEqual("[REMOTE-PLAYLIST-SOURCE-ONBOARDING-RESULT]", onboardingResult.ToString());
        SourceId sourceId = onboardingResult.SourceId;
        Assert.IsFalse(sourceId.IsEmpty);
        SourceBinding binding = await ReadSourceBindingAsync(databasePath, sourceId);
        Assert.AreEqual(1, store.ActiveRecordCount);
        SecretStoreReadResult sourceRead = await store.ReadLocatorAsync(
            sourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            ProtectedRecordOwner.ForSourceConfiguration(binding.ConfigurationId),
            binding.LocatorReference);
        Assert.IsTrue(sourceRead.IsSuccess);
        using (SecretLease sourceLease = sourceRead.Lease!)
        {
            AssertRemotePlaylistLocatorMatches(sourceLease.Value, sourceLocator);
        }

        CatalogCounts importedCounts = await ReadCatalogCountsAsync(databasePath);
        Assert.AreEqual(new CatalogCounts(1, 1, 1, 1, 2, 2, 1, 0), importedCounts);
        await AssertDatabaseExcludesAsync(databasePath, sourceLocator, recoveryLocator, otherLocator);

        var query = new SqliteCatalogQuery(databasePath);
        using var browser = new CatalogBrowseCoordinator(query, time);
        IReadOnlyList<CatalogSourceItem> sources = await browser.ReadSourcesAsync();
        Assert.HasCount(1, sources);
        Assert.AreEqual(sourceId, sources[0].SourceId);
        Assert.AreEqual(SourceName, sources[0].Name);

        CatalogBrowseResult all = (await browser.BrowseAsync(
            sourceId,
            categoryId: null,
            searchText: null,
            offset: 0,
            limit: 10,
            debounce: false))!;
        Assert.HasCount(1, all.Categories);
        Assert.AreEqual(CategoryName, all.Categories[0].Name);
        Assert.AreEqual(2, all.Channels.TotalCount);
        CollectionAssert.AreEqual(
            new[] { OtherChannelName, RecoveryChannelName },
            all.Channels.Items.Select(static item => item.Name).ToArray());
        Assert.IsTrue(all.Channels.Items.All(static item => !item.ChannelId.IsEmpty));

        CatalogBrowseResult searched = (await browser.BrowseAsync(
            sourceId,
            all.Categories[0].CategoryId,
            "Recovery",
            offset: 0,
            limit: 10,
            debounce: false))!;
        Assert.AreEqual("Recovery", searched.SearchText);
        Assert.AreEqual(1, searched.Channels.TotalCount);
        Assert.HasCount(1, searched.Channels.Items);
        CatalogChannelItem selectedChannel = searched.Channels.Items[0];
        Assert.AreEqual(RecoveryChannelName, selectedChannel.Name);

        var engine = new ResolvingProbePlaybackEngine(
            databasePath,
            store,
            transport,
            time);
        await using var playback = new PlaybackSessionCoordinator(
            engine,
            new PlaybackReconnectPolicy(),
            time,
            static _ => TimeSpan.Zero);

        PlaybackSessionSnapshot started = (await playback.StartAsync(
            sourceId,
            selectedChannel.ChannelId))!;
        Assert.AreEqual(PlaybackState.Playing, started.State);
        Assert.AreEqual(sourceId, started.SourceId);
        Assert.AreEqual(selectedChannel.ChannelId, started.ChannelId);
        PlaybackSessionId logicalSession = started.SessionId;

        Assert.IsTrue((await playback.PauseAsync()).IsSuccess);
        Assert.AreEqual(PlaybackState.Paused, playback.Current.State);
        Assert.IsTrue((await playback.PlayAsync()).IsSuccess);
        Assert.AreEqual(PlaybackState.Playing, playback.Current.State);
        Assert.IsTrue((await playback.SetVolumeAsync(
            logicalSession,
            PlaybackVolume.FromPercent(42))).IsSuccess);
        Assert.IsTrue((await playback.SetMutedAsync(logicalSession, isMuted: true)).IsSuccess);
        Assert.IsTrue((await playback.SetAspectModeAsync(
            logicalSession,
            PlaybackAspectMode.Fill)).IsSuccess);
        Assert.AreEqual(42, playback.CurrentControls.Volume.Percent);
        Assert.IsTrue(playback.CurrentControls.IsMuted);
        Assert.AreEqual(PlaybackAspectMode.Fill, playback.CurrentControls.AspectMode);

        engine.EnterRebuffering();
        Assert.AreEqual(PlaybackState.Buffering, playback.Current.State);
        await AdvanceAsync(time, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() =>
            playback.Current.State == PlaybackState.Reconnecting &&
            playback.Current.Reconnect?.Phase == PlaybackReconnectPhase.Waiting);
        Assert.HasCount(1, engine.WatchdogExpirations);
        Assert.AreEqual(
            PlaybackFaultWatchdogFailureKind.RebufferTimeout,
            engine.WatchdogExpirations.Single().FailureKind);
        Assert.AreEqual(DomainErrorCode.StreamInterrupted, engine.WatchdogExpirations.Single().Error.Code);
        Assert.AreEqual(1, playback.Current.Reconnect!.AttemptNumber);

        await AdvanceAsync(time, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => playback.Current.State == PlaybackState.Playing);
        Assert.AreEqual(logicalSession, playback.Current.SessionId);
        Assert.AreEqual(42, playback.CurrentControls.Volume.Percent);
        Assert.IsTrue(playback.CurrentControls.IsMuted);
        Assert.AreEqual(PlaybackAspectMode.Fill, playback.CurrentControls.AspectMode);
        Assert.HasCount(2, engine.OpenSessions);
        Assert.AreNotEqual(engine.OpenSessions[0], engine.OpenSessions[1]);
        Assert.HasCount(1, engine.StopSessions);
        Assert.AreEqual(engine.OpenSessions[0], engine.StopSessions[0]);
        int firstStop = engine.Journal.IndexOf($"Stop:{engine.OpenSessions[0].Value}");
        int secondOpen = engine.Journal.IndexOf($"Open:{engine.OpenSessions[1].Value}");
        int restoredVolume = engine.Journal.IndexOf($"Volume:{engine.OpenSessions[1].Value}:42");
        int restoredMute = engine.Journal.IndexOf($"Muted:{engine.OpenSessions[1].Value}:True");
        int restoredAspect = engine.Journal.IndexOf($"Aspect:{engine.OpenSessions[1].Value}:Fill");
        int secondPlay = engine.Journal.IndexOf($"Play:{engine.OpenSessions[1].Value}");
        Assert.IsTrue(firstStop >= 0 && secondOpen > firstStop);
        Assert.IsTrue(
            restoredVolume > secondOpen &&
            restoredMute > restoredVolume &&
            restoredAspect > restoredMute &&
            secondPlay > restoredAspect,
            "Reconnect did not restore controls on the second physical session before play.");
        Assert.AreEqual(2, engine.ResolvedLocatorSha256.Count);
        Assert.IsTrue(engine.ResolvedLocatorSha256.All(hash =>
            string.Equals(hash, Sha256(recoveryLocator), StringComparison.Ordinal)));
        Assert.AreEqual(2, engine.MediaBodySha256.Count);
        Assert.IsTrue(engine.MediaBodySha256.All(hash =>
            string.Equals(hash, Sha256(recoveryMedia), StringComparison.Ordinal)));
        Assert.AreEqual(2, engine.ResolvedLeases.Count);
        foreach (SecretLease resolvedLease in engine.ResolvedLeases)
        {
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = resolvedLease.Value);
        }

        ISourceDeletionLifecycle lifecycle = CreateDeletionLifecycle(databasePath, store);
        await using var deletion = new SourceDeletionCoordinator(lifecycle, playback);
        SourceDeletionResult deleted = await deletion.DeleteAsync(sourceId);

        Assert.IsTrue(deleted.IsSuccess, deleted.ToString());
        Assert.AreEqual(SourceDeletionFailureStage.None, deleted.FailureStage);
        Assert.IsNull(deleted.Error);
        Assert.AreEqual(PlaybackState.Closed, playback.Current.State);
        Assert.HasCount(2, engine.StopSessions);
        Assert.AreEqual(engine.OpenSessions[1], engine.StopSessions[1]);
        int requestCountBeforeRetiredStart = server.RequestCount;
        int openCountBeforeRetiredStart = engine.OpenSessions.Count;
        Assert.IsNull(await playback.StartAsync(sourceId, selectedChannel.ChannelId));
        Assert.AreEqual(requestCountBeforeRetiredStart, server.RequestCount);
        Assert.AreEqual(openCountBeforeRetiredStart, engine.OpenSessions.Count);

        Assert.IsEmpty(await browser.ReadSourcesAsync());
        CatalogBrowseResult deletedBrowse = (await browser.BrowseAsync(
            sourceId,
            categoryId: null,
            searchText: null,
            offset: 0,
            limit: 10,
            debounce: false))!;
        Assert.IsEmpty(deletedBrowse.Categories);
        Assert.IsEmpty(deletedBrowse.Channels.Items);
        Assert.AreEqual(0, deletedBrowse.Channels.TotalCount);
        ResolvedSource afterDelete = await ResolveSourceAsync(
            databasePath,
            store,
            new PlaybackSelection(sourceId, selectedChannel.ChannelId));
        try
        {
            Assert.AreEqual("Unavailable", afterDelete.Failure);
            Assert.IsNull(afterDelete.Lease);
        }
        finally
        {
            afterDelete.Lease?.Dispose();
        }

        SecretStoreReadResult deletedSourceRead = await store.ReadLocatorAsync(
            sourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            ProtectedRecordOwner.ForSourceConfiguration(binding.ConfigurationId),
            binding.LocatorReference);
        Assert.IsFalse(deletedSourceRead.IsSuccess);
        Assert.AreEqual(SecretStoreFailure.ProtectedRecordUnavailable, deletedSourceRead.Failure);
        Assert.AreEqual(0, store.ActiveRecordCount);
        Assert.IsTrue(store.RetiredBuffersAreZeroed);
        Assert.AreEqual(new CatalogCounts(0, 0, 0, 0, 0, 0, 0, 1),
            await ReadCatalogCountsAsync(databasePath));
        Assert.AreEqual(1L, await ReadScalarAsync(
            databasePath,
            "SELECT count(*) FROM source_deletion_tombstones WHERE protected_delete_completed = 1;"));
        SourceDeletionPendingBatchReadResult pending = await lifecycle.ReadPendingBatchAsync();
        Assert.IsTrue(pending.IsSuccess);
        Assert.IsEmpty(pending.SourceIds);
        Assert.IsNull(pending.NextAfterExclusive);

        CollectionAssert.AreEqual(
            ExpectedRequests,
            server.Requests.Select(static request => $"{request.Method} {request.Path}").ToArray());
        Assert.AreEqual(4, server.RequestCount);
        Assert.AreEqual(4, server.CompletedResponseCount);
        Assert.AreEqual((playlist.Length * 2L) + (recoveryMedia.Length * 2L), server.CompletedBodyBytes);
        Assert.AreEqual(0, server.FailureCount);
        await AssertDatabaseExcludesAsync(databasePath, sourceLocator, recoveryLocator, otherLocator);
        string observable = string.Join('|',
            onboardingResult,
            deleted,
            playback.Current,
            playback.CurrentControls,
            string.Join(',', engine.Journal));
        Assert.DoesNotContain(server.BaseAddress.Host, observable, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog.m3u", observable, StringComparison.Ordinal);
        Assert.DoesNotContain("recovery.ts", observable, StringComparison.Ordinal);
    }

    private static byte[] CreateMediaBody(byte seed)
    {
        var body = new byte[188 * 4];
        for (int index = 0; index < body.Length; index++)
        {
            body[index] = unchecked((byte)(seed + index));
        }

        return body;
    }

    private static BoundedHttpTransport CreatePinnedTransport(LocalHttpFixtureServer server)
    {
        Assert.IsNotNull(server.Certificate);
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                CryptographicOperations.FixedTimeEquals(
                    certificate.RawData,
                    server.Certificate.RawData),
        };
        try
        {
            ConstructorInfo constructor = typeof(BoundedHttpTransport).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(HttpMessageHandler), typeof(TimeSpan)],
                modifiers: null) ?? throw new InvalidOperationException(
                    "The bounded transport test seam is unavailable.");
            return (BoundedHttpTransport)constructor.Invoke([handler, TimeSpan.FromSeconds(5)]);
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    private static ISourceDeletionLifecycle CreateDeletionLifecycle(
        string databasePath,
        ISecretStore store)
    {
        Type type = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly.GetType(
            "IptvSuite.Infrastructure.SqliteSourceDeletionLifecycle",
            throwOnError: true)!;
        return (ISourceDeletionLifecycle)Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [databasePath, store],
            culture: null)!;
    }

    private static async Task<ResolvedSource> ResolveSourceAsync(
        string databasePath,
        ISecretStore store,
        PlaybackSelection selection,
        CancellationToken cancellationToken = default)
    {
        Type resolverType = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly.GetType(
            "IptvSuite.Infrastructure.SqlitePlaybackSourceResolver",
            throwOnError: true)!;
        object resolver = Activator.CreateInstance(
            resolverType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [databasePath, store],
            culture: null)!;
        MethodInfo method = resolverType.GetMethod(
            "ResolveAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException(
                "The playback source resolver test seam is unavailable.");
        object valueTask = method.Invoke(resolver, [selection, cancellationToken])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task.ConfigureAwait(false);
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return new ResolvedSource(
            (SecretLease?)result.GetType().GetProperty("Lease")!.GetValue(result),
            result.GetType().GetProperty("Failure")!.GetValue(result)!.ToString()!);
    }

    private static void AssertRemotePlaylistLocatorMatches(
        ReadOnlyMemory<byte> payload,
        string expectedLocator)
    {
        Type decoderType = typeof(SourceDraftProtectionService).Assembly.GetType(
            "IptvSuite.Application.ProtectedSourcePayloadDecoder",
            throwOnError: true)!;
        MethodInfo method = decoderType.GetMethod(
            "TryDecodeRemotePlaylist",
            BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException(
                "The protected remote-playlist decoder is unavailable.");
        object?[] parameters = [payload, null];
        Assert.IsTrue(method.Invoke(null, parameters) is true);
        object layout = parameters[1] ?? throw new InvalidOperationException(
            "The protected remote-playlist layout is unavailable.");
        int offset = (int)layout.GetType().GetProperty("LocatorOffset")!.GetValue(layout)!;
        int length = (int)layout.GetType().GetProperty("LocatorLength")!.GetValue(layout)!;
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedLocator);
        try
        {
            Assert.IsTrue(
                CryptographicOperations.FixedTimeEquals(
                    payload.Span.Slice(offset, length),
                    expectedBytes),
                "The protected remote-playlist locator payload changed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    private static async Task<SourceBinding> ReadSourceBindingAsync(
        string databasePath,
        SourceId sourceId)
    {
        await using SqliteConnection connection = await OpenReadAsync(databasePath);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT configuration_id, configuration_reference
            FROM sources
            WHERE source_id = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        DomainResult<SourceConfigurationId> configurationId =
            SourceConfigurationId.Create(Guid.ParseExact(reader.GetString(0), "N"));
        DomainResult<ProtectedLocatorReference> locatorReference =
            ProtectedLocatorReference.Parse(reader.GetString(1));
        Assert.IsTrue(configurationId.IsSuccess);
        Assert.IsTrue(locatorReference.IsSuccess);
        Assert.IsFalse(await reader.ReadAsync());
        return new SourceBinding(configurationId.Value, locatorReference.Value!);
    }

    private static async Task<CatalogCounts> ReadCatalogCountsAsync(string databasePath)
    {
        await using SqliteConnection connection = await OpenReadAsync(databasePath);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM sources),
                (SELECT count(*) FROM snapshots),
                (SELECT count(*) FROM snapshot_keys),
                (SELECT count(*) FROM categories),
                (SELECT count(*) FROM channels),
                (SELECT count(*) FROM protected_locators),
                (SELECT count(*) FROM sync_runs),
                (SELECT count(*) FROM source_deletion_tombstones);
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        CatalogCounts counts = new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7));
        Assert.IsFalse(await reader.ReadAsync());
        return counts;
    }

    private static async Task<long> ReadScalarAsync(string databasePath, string sql)
    {
        await using SqliteConnection connection = await OpenReadAsync(databasePath);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<SqliteConnection> OpenReadAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task AssertDatabaseExcludesAsync(
        string databasePath,
        params string[] rawLocators)
    {
        string directory = Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException(
            "The catalog database directory is unavailable.");
        string databaseName = Path.GetFileName(databasePath);
        foreach (string path in Directory.EnumerateFiles(
            directory,
            databaseName + "*",
            SearchOption.TopDirectoryOnly))
        {
            byte[] content = await File.ReadAllBytesAsync(path);
            foreach (string rawLocator in rawLocators)
            {
                Assert.IsFalse(
                    content.AsSpan().IndexOf(Encoding.UTF8.GetBytes(rawLocator)) >= 0,
                    $"A raw locator was found in {Path.GetFileName(path)}.");
            }
        }
    }

    private static string Sha256(string value) => Sha256(Encoding.UTF8.GetBytes(value));

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static async Task AdvanceAsync(FakeTimeProvider time, TimeSpan duration)
    {
        TimeSpan remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            TimeSpan step = remaining > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : remaining;
            time.Advance(step);
            remaining -= step;
            await Task.Yield();
            await Task.Yield();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int ordinal = 0; ordinal < 200 && !condition(); ordinal++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.IsTrue(condition(), "The deterministic state transition did not complete.");
    }

    private sealed record SourceBinding(
        SourceConfigurationId ConfigurationId,
        ProtectedLocatorReference LocatorReference);

    private sealed record CatalogCounts(
        long Sources,
        long Snapshots,
        long SnapshotKeys,
        long Categories,
        long Channels,
        long ProtectedLocators,
        long SyncRuns,
        long Tombstones);

    private sealed record ResolvedSource(SecretLease? Lease, string Failure);

    private sealed class ResolvingProbePlaybackEngine : IPlaybackEngine
    {
        private readonly string _databasePath;
        private readonly ISecretStore _secretStore;
        private readonly IHttpTransport _transport;
        private readonly PlaybackFaultWatchdog _watchdog;
        private readonly object _sync = new();
        private PlaybackEngineSnapshot _current = PlaybackEngineSnapshot.Closed();
        private PlaybackControlSnapshot _controls = PlaybackControlSnapshot.Idle(
            PlaybackVolume.FromPercent(100),
            isMuted: false,
            PlaybackAspectMode.Fit);
        private bool _disposed;

        internal ResolvingProbePlaybackEngine(
            string databasePath,
            ISecretStore secretStore,
            IHttpTransport transport,
            TimeProvider timeProvider)
        {
            _databasePath = databasePath;
            _secretStore = secretStore;
            _transport = transport;
            _watchdog = new PlaybackFaultWatchdog(
                new PlaybackFaultWatchdogOptions(
                    startupTimeout: TimeSpan.FromSeconds(10),
                    rebufferTimeout: TimeSpan.FromSeconds(5)),
                timeProvider);
            _watchdog.Expired += OnWatchdogExpired;
        }

        public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

        public PlaybackEngineSnapshot Current
        {
            get
            {
                lock (_sync)
                {
                    return _current;
                }
            }
        }

        public PlaybackControlSnapshot CurrentControls
        {
            get
            {
                lock (_sync)
                {
                    return _controls;
                }
            }
        }

        internal List<string> Journal { get; } = [];

        internal List<PlaybackSessionId> OpenSessions { get; } = [];

        internal List<PlaybackSessionId> StopSessions { get; } = [];

        internal List<string> ResolvedLocatorSha256 { get; } = [];

        internal List<string> MediaBodySha256 { get; } = [];

        internal List<SecretLease> ResolvedLeases { get; } = [];

        internal List<PlaybackFaultWatchdogExpiredEventArgs> WatchdogExpirations { get; } = [];

        public async ValueTask<PlaybackEngineOperationResult> OpenAsync(
            PlaybackSessionId sessionId,
            PlaybackSelection selection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordOpen(sessionId);
            Publish(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Opening));
            ResolvedSource resolved = await ResolveSourceAsync(
                _databasePath,
                _secretStore,
                selection,
                cancellationToken).ConfigureAwait(false);
            if (resolved.Lease is null || !string.Equals(resolved.Failure, "None", StringComparison.Ordinal))
            {
                resolved.Lease?.Dispose();
                return PlaybackEngineOperationResult.Failed(DomainErrorCode.StorageUnavailable);
            }

            SecretLease lease = resolved.Lease;
            try
            {
                ResolvedLocatorSha256.Add(Sha256(lease.Value.Span));
                string locator = new UTF8Encoding(false, true).GetString(lease.Value.Span);
                DomainResult<PreparedRemotePlaylistSourceDraft> prepared =
                    SourceConfigurationValidator.PrepareRemotePlaylist("M16 playback probe", locator);
                if (!prepared.IsSuccess ||
                    !Uri.TryCreate(locator, UriKind.Absolute, out Uri? requestUri) ||
                    requestUri is null)
                {
                    return PlaybackEngineOperationResult.Failed(DomainErrorCode.PlaybackStartFailed);
                }

                using HttpTransportRequest request = HttpTransportRequest.Create(
                    requestUri,
                    prepared.Value!.SafeEndpoint,
                    HttpTransportLimits.MaximumAllowedResponseBytes);
                HttpTransportResult response = await _transport.GetAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    return PlaybackEngineOperationResult.Failed(DomainErrorCode.PlaybackStartFailed);
                }

                using HttpResponseLease responseLease = response.Response!;
                MediaBodySha256.Add(Sha256(responseLease.Content.Span));
            }
            finally
            {
                lease.Dispose();
                ResolvedLeases.Add(lease);
            }

            Publish(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Buffering));
            return PlaybackEngineOperationResult.Succeeded();
        }

        public ValueTask<PlaybackEngineOperationResult> PlayAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Journal.Add($"Play:{sessionId.Value}");
            Publish(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Playing));
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> PauseAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Journal.Add($"Pause:{sessionId.Value}");
            Publish(PlaybackEngineSnapshot.Active(sessionId, PlaybackState.Paused));
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> StopAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Journal.Add($"Stop:{sessionId.Value}");
                StopSessions.Add(sessionId);
                _current = PlaybackEngineSnapshot.Closed();
                _controls = PlaybackControlSnapshot.Idle(
                    _controls.Volume,
                    _controls.IsMuted,
                    _controls.AspectMode);
            }

            _watchdog.Observe(PlaybackEngineSnapshot.Closed(sessionId));
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> SetVolumeAsync(
            PlaybackSessionId sessionId,
            PlaybackVolume volume,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Journal.Add($"Volume:{sessionId.Value}:{volume.Percent}");
                _controls = PlaybackControlSnapshot.Active(
                    sessionId,
                    volume,
                    _controls.IsMuted,
                    _controls.AspectMode);
            }

            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> SetMutedAsync(
            PlaybackSessionId sessionId,
            bool isMuted,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Journal.Add($"Muted:{sessionId.Value}:{isMuted}");
                _controls = PlaybackControlSnapshot.Active(
                    sessionId,
                    _controls.Volume,
                    isMuted,
                    _controls.AspectMode);
            }

            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<PlaybackEngineOperationResult> SetAspectModeAsync(
            PlaybackSessionId sessionId,
            PlaybackAspectMode aspectMode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Journal.Add($"Aspect:{sessionId.Value}:{aspectMode}");
                _controls = PlaybackControlSnapshot.Active(
                    sessionId,
                    _controls.Volume,
                    _controls.IsMuted,
                    aspectMode);
            }

            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask<DomainResult<PlaybackTrackSnapshot>> GetTracksAsync(
            PlaybackSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(DomainResult.Success(
                PlaybackTrackSnapshot.Create(
                    sessionId,
                    PlaybackTrackCapabilities.None,
                    [])));
        }

        public ValueTask<PlaybackEngineOperationResult> SelectTrackAsync(
            PlaybackSessionId sessionId,
            PlaybackTrackId trackId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(PlaybackEngineOperationResult.Succeeded());
        }

        public ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return ValueTask.CompletedTask;
                }

                _disposed = true;
            }

            _watchdog.Expired -= OnWatchdogExpired;
            _watchdog.Dispose();
            StateChanged = null;
            return ValueTask.CompletedTask;
        }

        internal void EnterRebuffering()
        {
            PlaybackEngineSnapshot current = Current;
            Assert.AreEqual(PlaybackState.Playing, current.State);
            Publish(PlaybackEngineSnapshot.Active(current.SessionId, PlaybackState.Buffering));
        }

        private void RecordOpen(PlaybackSessionId sessionId)
        {
            lock (_sync)
            {
                Journal.Add($"Open:{sessionId.Value}");
                OpenSessions.Add(sessionId);
                _controls = PlaybackControlSnapshot.Active(
                    sessionId,
                    _controls.Volume,
                    _controls.IsMuted,
                    _controls.AspectMode);
            }
        }

        private void Publish(PlaybackEngineSnapshot snapshot)
        {
            EventHandler<PlaybackEngineStateChangedEventArgs>? changed;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _current = snapshot;
                changed = StateChanged;
            }

            _watchdog.Observe(snapshot);
            changed?.Invoke(this, new PlaybackEngineStateChangedEventArgs(snapshot));
        }

        private void OnWatchdogExpired(
            object? sender,
            PlaybackFaultWatchdogExpiredEventArgs eventArgs)
        {
            WatchdogExpirations.Add(eventArgs);
            Publish(PlaybackEngineSnapshot.Failed(eventArgs.SessionId, eventArgs.Error));
        }
    }
}
