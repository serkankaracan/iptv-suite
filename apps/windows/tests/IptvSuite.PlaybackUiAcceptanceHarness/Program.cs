using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using IptvSuite.Testing;
using Microsoft.Data.Sqlite;

namespace IptvSuite.PlaybackUiAcceptanceHarness;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string Command = "serve-and-seed";
    private const string ExistingCatalogSourceName = "Synthetic 50k source";
    private const string PlaybackSourceName = "00 Synthetic protected playback source";
    private const string PlaybackChannelAName = "Synthetic protected Tier A channel A";
    private const string PlaybackChannelBName = "Synthetic protected Tier A channel B";
    private const string MediaRouteA = "/direct-h264-aac-a.ts";
    private const string MediaRouteB = "/direct-h264-aac-b.ts";
    private const string FixtureId = "iptvsuite-tier-a-synthetic-v1";
    private const string FixtureLicense = "CC0-1.0";
    private const string FixtureFileName = "direct-h264-aac.ts";
    private const string FixtureManifestName = "fixture-manifest.json";
    private const string ReadyTicketName = "ready.json";
    private const string ResultTicketName = "result.json";
    private const string StopSignalName = "stop.signal";
    private const string CancelVerificationSignalName = "verify-cancel.signal";
    private const string CancelVerificationTicketName = "cancel-result.json";
    private const string DialogCloseVerificationSignalName = "verify-dialog-close.signal";
    private const string DialogCloseVerificationTicketName = "dialog-close-result.json";
    private const string DeletionFaultReadyTicketName = "delete-failure-ready.json";
    private const string PendingVerificationSignalName = "verify-pending.signal";
    private const string PendingVerificationTicketName = "pending-result.json";
    private const string PublicCertificateName = "loopback.cer";
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions TicketJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    private static async Task<int> Main(string[] args)
    {
        if (args is not
            [Command, string catalogDatabasePath, string protectedStorePath,
                string fixtureRoot, string controlDirectory])
        {
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            return 3;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await RunAsync(
                catalogDatabasePath,
                protectedStorePath,
                fixtureRoot,
                controlDirectory,
                cancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> RunAsync(
        string catalogDatabasePath,
        string protectedStorePath,
        string fixtureRoot,
        string controlDirectory,
        CancellationToken cancellationToken)
    {
        HarnessPaths paths = ValidatePaths(
            catalogDatabasePath,
            protectedStorePath,
            fixtureRoot,
            controlDirectory);
        LocalHttpFixtureServer? server = null;
        string? certificateThumbprint = null;
        bool seedCompleted = false;
        bool readyPublished = false;
        bool stopObserved = false;
        bool stoppedGracefully = false;
        bool cancelNoMutationVerified = false;
        bool dialogCloseNoMutationVerified = false;
        bool pendingDeletionVerified = false;
        bool pendingTargetCatalogPreserved = false;
        bool pendingConfigurationRecordPreserved = false;
        bool pendingTombstoneBindingVerified = false;
        bool pendingSiblingCatalogRetained = false;
        bool deletionFaultReleased = false;
        bool targetCatalogDeleted = false;
        bool targetProtectedRecordsDeleted = false;
        bool tombstoneBindingCompleted = false;
        bool siblingCatalogRetained = false;
        SeedContext? seedContext = null;
        FileStream? deletionFaultLease = null;
        int exitCode = 0;

        try
        {
            byte[] fixture = LoadValidatedFixture(paths.FixtureRoot);
            try
            {
                server = await LocalHttpFixtureServer.StartHttpsAsync(
                    new Dictionary<string, FixtureHttpResponse>(StringComparer.Ordinal)
                    {
                        [MediaRouteA] = new FixtureHttpResponse(
                            200,
                            "video/mp2t",
                            fixture,
                            SupportsByteRanges: true),
                        [MediaRouteB] = new FixtureHttpResponse(
                            200,
                            "video/mp2t",
                            fixture,
                            SupportsByteRanges: true),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fixture);
            }

            X509Certificate2 certificate = server.Certificate ??
                throw new InvalidOperationException("The loopback certificate is unavailable.");
            certificateThumbprint = NormalizeThumbprint(certificate.Thumbprint);
            WritePublicCertificate(certificate, paths.PublicCertificatePath);

            seedContext = await SeedAndVerifyAsync(
                paths.CatalogDatabasePath,
                paths.ProtectedStorePath,
                server.BaseAddress,
                cancellationToken).ConfigureAwait(false);
            seedCompleted = true;

            WriteJsonAtomically(
                new ReadyTicket(
                    IsReady: true,
                    SeedCompleted: true,
                    CertificateThumbprint: certificateThumbprint),
                paths.ReadyTicketPath);
            readyPublished = true;

            bool cancelSignalObserved = await WaitForPhaseSignalAsync(
                paths,
                paths.CancelVerificationSignalPath,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    CancelVerificationSignalName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            if (!cancelSignalObserved)
            {
                stopObserved = true;
                throw new InvalidDataException("The acceptance protocol stopped before cancel verification.");
            }

            PreservationOracleResult cancelOracle = await VerifyPreservedStateAsync(
                paths,
                seedContext,
                cancellationToken).ConfigureAwait(false);
            WriteJsonAtomically(cancelOracle, paths.CancelVerificationTicketPath);
            cancelNoMutationVerified = cancelOracle.IsVerified;
            if (!cancelOracle.IsVerified)
            {
                throw new InvalidDataException("The cancel preservation oracle failed.");
            }

            bool closeSignalObserved = await WaitForPhaseSignalAsync(
                paths,
                paths.DialogCloseVerificationSignalPath,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    CancelVerificationSignalName,
                    CancelVerificationTicketName,
                    DialogCloseVerificationSignalName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            if (!closeSignalObserved)
            {
                stopObserved = true;
                throw new InvalidDataException("The acceptance protocol stopped before dialog-close verification.");
            }

            PreservationOracleResult closeOracle = await VerifyPreservedStateAsync(
                paths,
                seedContext,
                cancellationToken).ConfigureAwait(false);
            WriteJsonAtomically(closeOracle, paths.DialogCloseVerificationTicketPath);
            dialogCloseNoMutationVerified = closeOracle.IsVerified;
            if (!closeOracle.IsVerified)
            {
                throw new InvalidDataException("The dialog-close preservation oracle failed.");
            }

            deletionFaultLease = OpenDeletionFaultLease(paths, seedContext);
            WriteJsonAtomically(
                new DeletionFaultReadyTicket(IsReady: true),
                paths.DeletionFaultReadyTicketPath);

            bool pendingSignalObserved = await WaitForPhaseSignalAsync(
                paths,
                paths.PendingVerificationSignalPath,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    CancelVerificationSignalName,
                    CancelVerificationTicketName,
                    DialogCloseVerificationSignalName,
                    DialogCloseVerificationTicketName,
                    DeletionFaultReadyTicketName,
                    PendingVerificationSignalName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            if (!pendingSignalObserved)
            {
                stopObserved = true;
                throw new InvalidDataException("The acceptance protocol stopped before pending verification.");
            }

            PendingOracleResult pendingOracle = await VerifyPendingStateAsync(
                paths,
                seedContext,
                cancellationToken).ConfigureAwait(false);
            deletionFaultLease.Dispose();
            deletionFaultLease = null;
            deletionFaultReleased = true;
            WriteJsonAtomically(
                new PendingVerificationTicket(
                    IsVerified: pendingOracle.IsVerified,
                    TargetCatalogPreserved: pendingOracle.TargetCatalogPreserved,
                    ConfigurationRecordPreserved: pendingOracle.ConfigurationRecordPreserved,
                    TombstoneBindingPending: pendingOracle.TombstoneBindingPending,
                    SiblingCatalogRetained: pendingOracle.SiblingCatalogRetained,
                    DeletionFaultReleased: deletionFaultReleased),
                paths.PendingVerificationTicketPath);
            pendingDeletionVerified = pendingOracle.IsVerified;
            pendingTargetCatalogPreserved = pendingOracle.TargetCatalogPreserved;
            pendingConfigurationRecordPreserved = pendingOracle.ConfigurationRecordPreserved;
            pendingTombstoneBindingVerified = pendingOracle.TombstoneBindingPending;
            pendingSiblingCatalogRetained = pendingOracle.SiblingCatalogRetained;
            if (!pendingOracle.IsVerified)
            {
                throw new InvalidDataException("The pending source-deletion oracle failed.");
            }

            await WaitForFinalStopSignalAsync(
                paths,
                [
                    ReadyTicketName,
                    PublicCertificateName,
                    CancelVerificationSignalName,
                    CancelVerificationTicketName,
                    DialogCloseVerificationSignalName,
                    DialogCloseVerificationTicketName,
                    DeletionFaultReadyTicketName,
                    PendingVerificationSignalName,
                    PendingVerificationTicketName,
                    StopSignalName,
                ],
                cancellationToken).ConfigureAwait(false);
            stopObserved = true;

            DeletionOracleResult deletionOracle = await VerifyDeletedStateAsync(
                paths,
                seedContext,
                cancellationToken).ConfigureAwait(false);
            targetCatalogDeleted = deletionOracle.TargetCatalogDeleted;
            targetProtectedRecordsDeleted = deletionOracle.TargetProtectedRecordsDeleted;
            tombstoneBindingCompleted = deletionOracle.TombstoneBindingCompleted;
            siblingCatalogRetained = deletionOracle.SiblingCatalogRetained;
            if (!deletionOracle.IsVerified)
            {
                throw new InvalidDataException("The source-deletion oracle failed.");
            }

            await server.DisposeAsync().ConfigureAwait(false);
            stoppedGracefully = true;
        }
        catch
        {
            exitCode = 1;
        }
        finally
        {
            if (deletionFaultLease is not null)
            {
                try
                {
                    deletionFaultLease.Dispose();
                    deletionFaultReleased = true;
                }
                catch
                {
                    exitCode = 1;
                }
                finally
                {
                    deletionFaultLease = null;
                }
            }

            if (server is not null && !stoppedGracefully)
            {
                try
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    stoppedGracefully = stopObserved;
                }
                catch
                {
                    exitCode = 1;
                }
            }

            try
            {
                IReadOnlyList<FixtureHttpRequest> requests = server?.Requests ?? [];
                WriteJsonAtomically(
                    new ResultTicket(
                        ReadyPublished: readyPublished,
                        SeedCompleted: seedCompleted,
                        StopObserved: stopObserved,
                        StoppedGracefully: stoppedGracefully,
                        CertificateThumbprint: certificateThumbprint,
                        RequestCount: server?.RequestCount ?? 0,
                        CompletedResponseCount: server?.CompletedResponseCount ?? 0,
                        CompletedBodyBytes: server?.CompletedBodyBytes ?? 0,
                        FailureCount: server?.FailureCount ?? 0,
                        ChannelARequestCount: requests.Count(request =>
                            string.Equals(request.Path, MediaRouteA, StringComparison.Ordinal)),
                        ChannelBRequestCount: requests.Count(request =>
                            string.Equals(request.Path, MediaRouteB, StringComparison.Ordinal)),
                        CancelNoMutationVerified: cancelNoMutationVerified,
                        DialogCloseNoMutationVerified: dialogCloseNoMutationVerified,
                        PendingDeletionVerified: pendingDeletionVerified,
                        PendingTargetCatalogPreserved: pendingTargetCatalogPreserved,
                        PendingConfigurationRecordPreserved: pendingConfigurationRecordPreserved,
                        PendingTombstoneBindingVerified: pendingTombstoneBindingVerified,
                        PendingSiblingCatalogRetained: pendingSiblingCatalogRetained,
                        DeletionFaultReleased: deletionFaultReleased,
                        TargetCatalogDeleted: targetCatalogDeleted,
                        TargetProtectedRecordsDeleted: targetProtectedRecordsDeleted,
                        TombstoneBindingCompleted: tombstoneBindingCompleted,
                        SiblingCatalogRetained: siblingCatalogRetained),
                    paths.ResultTicketPath);
            }
            catch
            {
                exitCode = 1;
            }

            seedContext?.Dispose();
        }

        return exitCode;
    }

    private static async Task<SeedContext> SeedAndVerifyAsync(
        string catalogDatabasePath,
        string protectedStorePath,
        Uri baseAddress,
        CancellationToken cancellationToken)
    {
        var browser = new SqliteCatalogQuery(catalogDatabasePath);
        IReadOnlyList<CatalogSourceItem> existingSources = await browser
            .ReadSourcesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existingSources.Count != 1 ||
            !string.Equals(existingSources[0].Name, ExistingCatalogSourceName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The disposable catalog prerequisite is invalid.");
        }

        CatalogChannelPage existingPage = await browser.ReadChannelsAsync(
            existingSources[0].SourceId,
            categoryId: null,
            searchText: null,
            offset: 0,
            limit: 1,
            cancellationToken).ConfigureAwait(false);
        if (existingPage.TotalCount != 50_000 || existingPage.Items.Count != 1)
        {
            throw new InvalidDataException("The disposable catalog prerequisite is incomplete.");
        }

        Uri playlistUri = new(baseAddress, "/synthetic-playlist.m3u");
        Uri mediaUriA = new(baseAddress, MediaRouteA);
        Uri mediaUriB = new(baseAddress, MediaRouteB);
        var secretStore = new DpapiCurrentUserSecretStore(
            protectedStorePath,
            cancellationToken);
        string[] protectedRecordsBefore = ReadProtectedRecordInventory(protectedStorePath);
        SourceId sourceId = SourceId.Generate();
        DomainResult<ValidatedSourceDraft> draft = await new SourceDraftProtectionService(secretStore)
            .ProtectRemotePlaylistAsync(
                sourceId,
                PlaybackSourceName,
                playlistUri.AbsoluteUri,
                cancellationToken)
            .ConfigureAwait(false);
        if (!draft.IsSuccess)
        {
            throw new InvalidDataException("The protected source draft could not be created.");
        }

        string configurationRecordFileName = GetAddedProtectedRecordFileName(
            protectedRecordsBefore,
            ReadProtectedRecordInventory(protectedStorePath));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DomainResult<ContentSource> source = ContentSource.Create(
            draft.Value,
            ContentSourceStatus.Testing,
            now,
            now);
        if (!source.IsSuccess)
        {
            throw new InvalidDataException("The synthetic content source is invalid.");
        }

        byte[] playlist = BuildPlaylist(mediaUriA, mediaUriB);
        await using var sink = new SqliteRemoteM3uImportSink(catalogDatabasePath);
        try
        {
            DomainResult<bool> began = await sink.BeginAsync(
                source.Value!,
                entityTag: null,
                lastModified: null,
                cancellationToken).ConfigureAwait(false);
            if (!began.IsSuccess)
            {
                throw new InvalidDataException("The protected catalog seed could not begin.");
            }

            await using var content = new MemoryStream(playlist, writable: false);
            DomainResult<RemoteM3uParseResult> parsed = await RemoteM3uPlaylistParser
                .ParseToSinkAsync(content, playlistUri, sink, cancellationToken)
                .ConfigureAwait(false);
            if (!parsed.IsSuccess || parsed.Value?.ProcessedEntryCount != 2)
            {
                throw new InvalidDataException("The synthetic playlist could not be parsed.");
            }

            DomainResult<bool> completed = await sink.CompleteAsync(
                parsed.Value,
                cancellationToken).ConfigureAwait(false);
            if (!completed.IsSuccess)
            {
                throw new InvalidDataException("The protected catalog seed could not complete.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(playlist);
        }

        IReadOnlyList<CatalogSourceItem> sources = await browser
            .ReadSourcesAsync(cancellationToken)
            .ConfigureAwait(false);
        CatalogSourceItem? playbackSource = sources.SingleOrDefault(item =>
            string.Equals(item.Name, PlaybackSourceName, StringComparison.Ordinal));
        if (sources.Count != 2 || playbackSource is null ||
            !ReferenceEquals(sources[0], playbackSource))
        {
            throw new InvalidDataException("The synthetic playback source ordering is invalid.");
        }

        CatalogChannelPage playbackPage = await browser.ReadChannelsAsync(
            playbackSource.SourceId,
            categoryId: null,
            searchText: null,
            offset: 0,
            limit: 2,
            cancellationToken).ConfigureAwait(false);
        if (playbackPage.TotalCount != 2 || playbackPage.Items.Count != 2)
        {
            throw new InvalidDataException("The synthetic playback channel is invalid.");
        }

        var resolver = new SqlitePlaybackSourceResolver(catalogDatabasePath, secretStore);
        (string Name, Uri Locator)[] expectedChannels =
        [
            (PlaybackChannelAName, mediaUriA),
            (PlaybackChannelBName, mediaUriB),
        ];
        foreach ((string expectedName, Uri expectedUri) in expectedChannels)
        {
            CatalogChannelItem? channel = playbackPage.Items.SingleOrDefault(item =>
                string.Equals(item.Name, expectedName, StringComparison.Ordinal));
            if (channel is null)
            {
                throw new InvalidDataException("The synthetic playback channel is invalid.");
            }

            PlaybackSourceResolutionResult resolved = await resolver.ResolveAsync(
                new PlaybackSelection(playbackSource.SourceId, channel.ChannelId),
                cancellationToken).ConfigureAwait(false);
            if (!resolved.IsSuccess)
            {
                resolved.Lease?.Dispose();
                throw new InvalidDataException("The protected playback binding is unavailable.");
            }

            byte[] expectedLocator = Encoding.UTF8.GetBytes(expectedUri.AbsoluteUri);
            try
            {
                using SecretLease lease = resolved.Lease!;
                if (!CryptographicOperations.FixedTimeEquals(expectedLocator, lease.Value.Span))
                {
                    throw new InvalidDataException("The protected playback binding is inconsistent.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedLocator);
            }
        }

        if (draft.Value!.Configuration is not RemotePlaylistSourceConfiguration configuration)
        {
            throw new InvalidDataException("The protected source configuration is invalid.");
        }

        SeedBaseline baseline = await ReadSeedBaselineAsync(
            catalogDatabasePath,
            playbackSource.SourceId,
            existingSources[0].SourceId,
            configuration.ConfigurationId,
            playbackPage.Items.Select(item => item.ChannelId).ToArray(),
            cancellationToken).ConfigureAwait(false);
        SecretStoreReadResult configurationRead = await secretStore.ReadLocatorAsync(
            playbackSource.SourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            ProtectedRecordOwner.ForSourceConfiguration(configuration.ConfigurationId),
            configuration.LocatorReference,
            cancellationToken).ConfigureAwait(false);
        if (!configurationRead.IsSuccess)
        {
            configurationRead.Lease?.Dispose();
            throw new InvalidDataException("The protected source configuration is unavailable.");
        }

        byte[] expectedConfigurationDigest;
        using (SecretLease lease = configurationRead.Lease!)
        {
            expectedConfigurationDigest = SHA256.HashData(lease.Value.Span);
        }

        return new SeedContext(
            playbackSource.SourceId,
            existingSources[0].SourceId,
            configuration.ConfigurationId,
            configuration.LocatorReference,
            baseline.ConfigurationReference,
            baseline.SnapshotId,
            playbackPage.Items.Select(item => item.ChannelId).ToArray(),
            baseline.TargetGraph,
            secretStore,
            configurationRecordFileName,
            expectedConfigurationDigest);
    }

    private static string[] ReadProtectedRecordInventory(string protectedStorePath)
    {
        EnsureNoReparsePoints(protectedStorePath);
        string[] entries = Directory.EnumerateFileSystemEntries(
                protectedStorePath,
                "*",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        var fileNames = new string[entries.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            string entryPath = entries[index];
            FileAttributes attributes = File.GetAttributes(entryPath);
            string fileName = Path.GetFileName(entryPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !IsProtectedRecordFileName(fileName))
            {
                throw new IOException("The protected-store inventory is invalid.");
            }

            fileNames[index] = fileName;
        }

        Array.Sort(fileNames, StringComparer.Ordinal);
        return fileNames;
    }

    private static string GetAddedProtectedRecordFileName(
        string[] before,
        string[] after)
    {
        string[] added = after.Except(before, StringComparer.Ordinal).ToArray();
        if (added.Length != 1 ||
            after.Length != before.Length + 1 ||
            before.Except(after, StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException("The protected source record inventory is invalid.");
        }

        return added[0];
    }

    private static bool IsProtectedRecordFileName(string value)
    {
        const string prefix = "record-v2-";
        const string suffix = ".dpapi";
        if (value.Length != prefix.Length + 64 + suffix.Length ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char character in value.AsSpan(prefix.Length, 64))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<SeedBaseline> ReadSeedBaselineAsync(
        string catalogDatabasePath,
        SourceId targetSourceId,
        SourceId siblingSourceId,
        SourceConfigurationId configurationId,
        ChannelId[] channelIds,
        CancellationToken cancellationToken)
    {
        if (channelIds.Length != 2 || channelIds.Any(channelId => channelId.IsEmpty))
        {
            throw new InvalidDataException("The protected channel baseline is invalid.");
        }

        await using SqliteConnection connection = await OpenReadOnlyConnectionAsync(
            catalogDatabasePath,
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        SourceBindingRow? target = await ReadSourceBindingAsync(
            connection,
            transaction,
            targetSourceId,
            cancellationToken).ConfigureAwait(false);
        if (target is null ||
            target.Status != ContentSourceStatus.Ready ||
            target.ConfigurationId != configurationId.Value.ToString("N") ||
            target.SourceKind != (long)SourceKind.RemotePlaylist ||
            target.SnapshotId is null)
        {
            throw new InvalidDataException("The protected source baseline is invalid.");
        }

        TargetGraphCounts graph = await ReadTargetGraphAsync(
            connection,
            transaction,
            targetSourceId,
            target.SnapshotId.Value,
            cancellationToken).ConfigureAwait(false);
        long exactChannels = await CountAsync(
            connection,
            transaction,
            "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot AND channel_id IN ($channelA, $channelB);",
            cancellationToken,
            ("$snapshot", target.SnapshotId.Value.Value.ToString("N")),
            ("$channelA", channelIds[0].Value.ToString("N")),
            ("$channelB", channelIds[1].Value.ToString("N"))).ConfigureAwait(false);
        bool siblingRetained = await VerifySiblingCatalogAsync(
            connection,
            transaction,
            siblingSourceId,
            cancellationToken).ConfigureAwait(false);
        long tombstones = await CountAsync(
            connection,
            transaction,
            "SELECT count(*) FROM source_deletion_tombstones WHERE source_id = $source;",
            cancellationToken,
            ("$source", targetSourceId.Value.ToString("N"))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (graph is not
            {
                Snapshots: 1,
                SnapshotKeys: 1,
                Categories: 1,
                Channels: 2,
                ProtectedLocators: 2,
                Favorites: 0,
                SyncRuns: 1,
            } || exactChannels != 2 || !siblingRetained || tombstones != 0)
        {
            throw new InvalidDataException("The protected catalog baseline is invalid.");
        }

        return new SeedBaseline(target.ConfigurationReference, target.SnapshotId.Value, graph);
    }

    private static async Task<PreservationOracleResult> VerifyPreservedStateAsync(
        HarnessPaths paths,
        SeedContext context,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenReadOnlyConnectionAsync(
            paths.CatalogDatabasePath,
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        SourceBindingRow? target = await ReadSourceBindingAsync(
            connection,
            transaction,
            context.TargetSourceId,
            cancellationToken).ConfigureAwait(false);
        TargetGraphCounts graph = await ReadTargetGraphAsync(
            connection,
            transaction,
            context.TargetSourceId,
            context.TargetSnapshotId,
            cancellationToken).ConfigureAwait(false);
        long exactChannels = await CountAsync(
            connection,
            transaction,
            "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot AND channel_id IN ($channelA, $channelB);",
            cancellationToken,
            ("$snapshot", context.TargetSnapshotId.Value.ToString("N")),
            ("$channelA", context.TargetChannelIds[0].Value.ToString("N")),
            ("$channelB", context.TargetChannelIds[1].Value.ToString("N"))).ConfigureAwait(false);
        long tombstones = await CountAsync(
            connection,
            transaction,
            "SELECT count(*) FROM source_deletion_tombstones WHERE source_id = $source;",
            cancellationToken,
            ("$source", context.TargetSourceId.Value.ToString("N"))).ConfigureAwait(false);
        bool siblingRetained = await VerifySiblingCatalogAsync(
            connection,
            transaction,
            context.SiblingSourceId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        bool targetPreserved = target is not null &&
            target.Status == ContentSourceStatus.Ready &&
            target.ConfigurationId == context.ConfigurationId.Value.ToString("N") &&
            target.SourceKind == (long)SourceKind.RemotePlaylist &&
            target.ConfigurationReference == context.ConfigurationReference &&
            target.SnapshotId == context.TargetSnapshotId &&
            graph == context.TargetGraph &&
            exactChannels == 2;
        bool configurationPreserved = await VerifyConfigurationRecordAsync(
            context,
            expectPresent: true,
            cancellationToken).ConfigureAwait(false);
        return new PreservationOracleResult(
            IsVerified: targetPreserved && configurationPreserved && tombstones == 0 && siblingRetained,
            TargetCatalogPreserved: targetPreserved,
            ConfigurationRecordPreserved: configurationPreserved,
            NoDeletionTombstone: tombstones == 0,
            SiblingCatalogRetained: siblingRetained);
    }

    private static FileStream OpenDeletionFaultLease(
        HarnessPaths paths,
        SeedContext context)
    {
        if (!IsProtectedRecordFileName(context.ConfigurationRecordFileName))
        {
            throw new InvalidDataException("The protected source record binding is invalid.");
        }

        string recordPath = Path.GetFullPath(Path.Combine(
            paths.ProtectedStorePath,
            context.ConfigurationRecordFileName));
        if (!string.Equals(
                Path.GetDirectoryName(recordPath),
                paths.ProtectedStorePath,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(recordPath))
        {
            throw new IOException("The protected source record is unavailable.");
        }

        EnsureNoReparsePoints(recordPath);
        FileAttributes attributes = File.GetAttributes(recordPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("The protected source record is invalid.");
        }

        var options = new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan,
            BufferSize = 4096,
        };
        var lease = new FileStream(recordPath, options);
        if (lease.Length <= 0)
        {
            lease.Dispose();
            throw new InvalidDataException("The protected source record is empty.");
        }

        return lease;
    }

    private static async Task<PendingOracleResult> VerifyPendingStateAsync(
        HarnessPaths paths,
        SeedContext context,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenReadOnlyConnectionAsync(
            paths.CatalogDatabasePath,
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        SourceBindingRow? target = await ReadSourceBindingAsync(
            connection,
            transaction,
            context.TargetSourceId,
            cancellationToken).ConfigureAwait(false);
        TargetGraphCounts graph = await ReadTargetGraphAsync(
            connection,
            transaction,
            context.TargetSourceId,
            context.TargetSnapshotId,
            cancellationToken).ConfigureAwait(false);
        long exactChannels = await CountAsync(
            connection,
            transaction,
            "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot AND channel_id IN ($channelA, $channelB);",
            cancellationToken,
            ("$snapshot", context.TargetSnapshotId.Value.ToString("N")),
            ("$channelA", context.TargetChannelIds[0].Value.ToString("N")),
            ("$channelB", context.TargetChannelIds[1].Value.ToString("N"))).ConfigureAwait(false);
        bool tombstoneBindingPending = await VerifyPendingTombstoneAsync(
            connection,
            transaction,
            context,
            cancellationToken).ConfigureAwait(false);
        bool siblingRetained = await VerifySiblingCatalogAsync(
            connection,
            transaction,
            context.SiblingSourceId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        bool targetPreserved = target is not null &&
            target.Status == ContentSourceStatus.DeletionPending &&
            target.ConfigurationId == context.ConfigurationId.Value.ToString("N") &&
            target.SourceKind == (long)SourceKind.RemotePlaylist &&
            target.ConfigurationReference == context.ConfigurationReference &&
            target.SnapshotId == context.TargetSnapshotId &&
            graph == context.TargetGraph &&
            exactChannels == 2;
        bool configurationPreserved = await VerifyConfigurationRecordAsync(
            context,
            expectPresent: true,
            cancellationToken).ConfigureAwait(false);
        return new PendingOracleResult(
            IsVerified: targetPreserved && configurationPreserved &&
                tombstoneBindingPending && siblingRetained,
            TargetCatalogPreserved: targetPreserved,
            ConfigurationRecordPreserved: configurationPreserved,
            TombstoneBindingPending: tombstoneBindingPending,
            SiblingCatalogRetained: siblingRetained);
    }

    private static async Task<DeletionOracleResult> VerifyDeletedStateAsync(
        HarnessPaths paths,
        SeedContext context,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenReadOnlyConnectionAsync(
            paths.CatalogDatabasePath,
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        string source = context.TargetSourceId.Value.ToString("N");
        string snapshot = context.TargetSnapshotId.Value.ToString("N");
        bool targetCatalogDeleted =
            await CountAsync(connection, transaction, "SELECT count(*) FROM sources WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM snapshots WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM snapshots WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM snapshot_keys WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM categories WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM channels WHERE channel_id IN ($channelA, $channelB);", cancellationToken,
                ("$channelA", context.TargetChannelIds[0].Value.ToString("N")),
                ("$channelB", context.TargetChannelIds[1].Value.ToString("N"))).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM protected_locators WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM favorites WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false) == 0 &&
            await CountAsync(connection, transaction, "SELECT count(*) FROM sync_runs WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false) == 0;

        bool tombstoneBindingCompleted = await VerifyCompletedTombstoneAsync(
            connection,
            transaction,
            context,
            cancellationToken).ConfigureAwait(false);
        bool siblingRetained = await VerifySiblingCatalogAsync(
            connection,
            transaction,
            context.SiblingSourceId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        bool configurationDeleted = await VerifyConfigurationRecordAsync(
            context,
            expectPresent: false,
            cancellationToken).ConfigureAwait(false);
        return new DeletionOracleResult(
            IsVerified: targetCatalogDeleted && configurationDeleted &&
                tombstoneBindingCompleted && siblingRetained,
            TargetCatalogDeleted: targetCatalogDeleted,
            TargetProtectedRecordsDeleted: configurationDeleted,
            TombstoneBindingCompleted: tombstoneBindingCompleted,
            SiblingCatalogRetained: siblingRetained);
    }

    private static async Task<SqliteConnection> OpenReadOnlyConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<SourceBindingRow?> ReadSourceBindingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT configuration_id, source_kind, configuration_reference, status, active_snapshot_id
            FROM sources
            WHERE source_id = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string? snapshotValue = reader.IsDBNull(4) ? null : reader.GetString(4);
        SnapshotId? snapshotId = snapshotValue is null
            ? null
            : SnapshotId.Create(Guid.ParseExact(snapshotValue, "N")).Value;
        var row = new SourceBindingRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            (ContentSourceStatus)reader.GetInt64(3),
            snapshotId);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The source binding is not unique.");
        }

        return row;
    }

    private static async Task<TargetGraphCounts> ReadTargetGraphAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceId sourceId,
        SnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        string source = sourceId.Value.ToString("N");
        string snapshot = snapshotId.Value.ToString("N");
        return new TargetGraphCounts(
            Snapshots: await CountAsync(connection, transaction, "SELECT count(*) FROM snapshots WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false),
            SnapshotKeys: await CountAsync(connection, transaction, "SELECT count(*) FROM snapshot_keys WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false),
            Categories: await CountAsync(connection, transaction, "SELECT count(*) FROM categories WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false),
            Channels: await CountAsync(connection, transaction, "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false),
            ProtectedLocators: await CountAsync(connection, transaction, "SELECT count(*) FROM protected_locators WHERE snapshot_id = $snapshot;", cancellationToken, ("$snapshot", snapshot)).ConfigureAwait(false),
            Favorites: await CountAsync(connection, transaction, "SELECT count(*) FROM favorites WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false),
            SyncRuns: await CountAsync(connection, transaction, "SELECT count(*) FROM sync_runs WHERE source_id = $source;", cancellationToken, ("$source", source)).ConfigureAwait(false));
    }

    private static async Task<bool> VerifySiblingCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceId siblingSourceId,
        CancellationToken cancellationToken)
    {
        SourceBindingRow? sibling = await ReadSourceBindingAsync(
            connection,
            transaction,
            siblingSourceId,
            cancellationToken).ConfigureAwait(false);
        if (sibling is null || sibling.Status != ContentSourceStatus.Ready || sibling.SnapshotId is null)
        {
            return false;
        }

        return await CountAsync(
            connection,
            transaction,
            "SELECT count(*) FROM channels WHERE snapshot_id = $snapshot;",
            cancellationToken,
            ("$snapshot", sibling.SnapshotId.Value.Value.ToString("N"))).ConfigureAwait(false) == 50_000;
    }

    private static async Task<bool> VerifyCompletedTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SeedContext context,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT configuration_id, source_kind, configuration_reference, protected_delete_completed
            FROM source_deletion_tombstones
            WHERE source_id = $source;
            """;
        command.Parameters.AddWithValue("$source", context.TargetSourceId.Value.ToString("N"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        bool matches = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            reader.GetString(0) == context.ConfigurationId.Value.ToString("N") &&
            reader.GetInt64(1) == (long)SourceKind.RemotePlaylist &&
            reader.GetString(2) == context.ConfigurationReference &&
            reader.GetInt64(3) == 1;
        return matches && !await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> VerifyPendingTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SeedContext context,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT configuration_id, source_kind, configuration_reference, protected_delete_completed
            FROM source_deletion_tombstones
            WHERE source_id = $source;
            """;
        command.Parameters.AddWithValue("$source", context.TargetSourceId.Value.ToString("N"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        bool matches = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            reader.GetString(0) == context.ConfigurationId.Value.ToString("N") &&
            reader.GetInt64(1) == (long)SourceKind.RemotePlaylist &&
            reader.GetString(2) == context.ConfigurationReference &&
            reader.GetInt64(3) == 0;
        return matches && !await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> VerifyConfigurationRecordAsync(
        SeedContext context,
        bool expectPresent,
        CancellationToken cancellationToken)
    {
        SecretStoreReadResult read = await context.SecretStore.ReadLocatorAsync(
            context.TargetSourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            ProtectedRecordOwner.ForSourceConfiguration(context.ConfigurationId),
            context.ConfigurationLocatorReference,
            cancellationToken).ConfigureAwait(false);
        if (!expectPresent)
        {
            read.Lease?.Dispose();
            return !read.IsSuccess &&
                read.Lease is null &&
                read.Failure == SecretStoreFailure.ProtectedRecordUnavailable;
        }

        if (!read.IsSuccess)
        {
            read.Lease?.Dispose();
            return false;
        }

        using SecretLease lease = read.Lease!;
        byte[] actualDigest = SHA256.HashData(lease.Value.Span);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                context.ExpectedConfigurationDigest,
                actualDigest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualDigest);
        }
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        object? count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] BuildPlaylist(Uri mediaUriA, Uri mediaUriB) => Encoding.UTF8.GetBytes(
        string.Concat(
            "#EXTM3U\n",
            "#EXTINF:-1 tvg-id=\"m12-protected-a\" group-title=\"Synthetic\",",
            PlaybackChannelAName,
            "\n",
            mediaUriA.AbsoluteUri,
            "\n",
            "#EXTINF:-1 tvg-id=\"m12-protected-b\" group-title=\"Synthetic\",",
            PlaybackChannelBName,
            "\n",
            mediaUriB.AbsoluteUri,
            "\n"));

    private static byte[] LoadValidatedFixture(string fixtureRoot)
    {
        string manifestPath = Path.Combine(fixtureRoot, FixtureManifestName);
        string fixturePath = Path.Combine(fixtureRoot, FixtureFileName);
        EnsureNoReparsePoints(manifestPath);
        EnsureNoReparsePoints(fixturePath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement root = document.RootElement;
        if (!string.Equals(root.GetProperty("FixtureId").GetString(), FixtureId, StringComparison.Ordinal) ||
            !string.Equals(
                root.GetProperty("Rights").GetProperty("License").GetString(),
                FixtureLicense,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The playback fixture manifest is invalid.");
        }

        JsonElement[] matches = root.GetProperty("Files")
            .EnumerateArray()
            .Where(file => string.Equals(
                file.GetProperty("Path").GetString(),
                FixtureFileName,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("The playback fixture entry is invalid.");
        }

        byte[] fixture = File.ReadAllBytes(fixturePath);
        string expectedHash = matches[0].GetProperty("Sha256").GetString() ?? string.Empty;
        long expectedSize = matches[0].GetProperty("SizeBytes").GetInt64();
        byte[] actualHash = SHA256.HashData(fixture);
        try
        {
            byte[] expectedHashBytes = Convert.FromHexString(expectedHash);
            try
            {
                if (fixture.LongLength != expectedSize ||
                    !CryptographicOperations.FixedTimeEquals(actualHash, expectedHashBytes))
                {
                    throw new InvalidDataException("The playback fixture integrity check failed.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedHashBytes);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(fixture);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualHash);
        }

        return fixture;
    }

    private static HarnessPaths ValidatePaths(
        string catalogDatabasePath,
        string protectedStorePath,
        string fixtureRoot,
        string controlDirectory)
    {
        string catalogPath = NormalizeAbsolutePath(catalogDatabasePath);
        string storePath = Path.TrimEndingDirectorySeparator(
            NormalizeAbsolutePath(protectedStorePath));
        string fixturePath = Path.TrimEndingDirectorySeparator(
            NormalizeAbsolutePath(fixtureRoot));
        string controlPath = Path.TrimEndingDirectorySeparator(
            NormalizeAbsolutePath(controlDirectory));

        DirectoryInfo? catalogV2 = Directory.GetParent(catalogPath);
        DirectoryInfo? catalog = catalogV2?.Parent;
        DirectoryInfo? catalogLocalCache = catalog?.Parent;
        DirectoryInfo storeV2 = new(storePath);
        DirectoryInfo? protectedStore = storeV2.Parent;
        DirectoryInfo? storeLocalCache = protectedStore?.Parent;
        if (!File.Exists(catalogPath) ||
            !string.Equals(Path.GetFileName(catalogPath), "catalog.db", StringComparison.Ordinal) ||
            !string.Equals(catalogV2?.Name, "v2", StringComparison.Ordinal) ||
            !string.Equals(catalog?.Name, "Catalog", StringComparison.Ordinal) ||
            !Directory.Exists(storePath) ||
            !string.Equals(storeV2.Name, "v2", StringComparison.Ordinal) ||
            !string.Equals(protectedStore?.Name, "ProtectedStore", StringComparison.Ordinal) ||
            catalogLocalCache is null || storeLocalCache is null ||
            !string.Equals(catalogLocalCache.Name, "LocalCache", StringComparison.Ordinal) ||
            !string.Equals(
                catalogLocalCache.FullName,
                storeLocalCache.FullName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The disposable package paths are invalid.");
        }

        if (!Directory.Exists(fixturePath) ||
            !string.Equals(Path.GetFileName(fixturePath), "tier-a", StringComparison.Ordinal) ||
            !Directory.Exists(controlPath) ||
            Path.GetFileName(controlPath).Length != 32 ||
            Path.GetFileName(controlPath).Any(character => !Uri.IsHexDigit(character)) ||
            Directory.EnumerateFileSystemEntries(controlPath).Any())
        {
            throw new IOException("The acceptance input paths are invalid.");
        }

        EnsureNoReparsePoints(catalogPath);
        EnsureNoReparsePoints(storePath);
        EnsureNoReparsePoints(fixturePath);
        EnsureNoReparsePoints(controlPath);
        return new HarnessPaths(
            catalogPath,
            storePath,
            fixturePath,
            controlPath,
            Path.Combine(controlPath, ReadyTicketName),
            Path.Combine(controlPath, ResultTicketName),
            Path.Combine(controlPath, StopSignalName),
            Path.Combine(controlPath, CancelVerificationSignalName),
            Path.Combine(controlPath, CancelVerificationTicketName),
            Path.Combine(controlPath, DialogCloseVerificationSignalName),
            Path.Combine(controlPath, DialogCloseVerificationTicketName),
            Path.Combine(controlPath, DeletionFaultReadyTicketName),
            Path.Combine(controlPath, PendingVerificationSignalName),
            Path.Combine(controlPath, PendingVerificationTicketName),
            Path.Combine(controlPath, PublicCertificateName));
    }

    private static string NormalizeAbsolutePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new IOException("An absolute acceptance path is required.");
        }

        return Path.GetFullPath(value);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(path)
            : new DirectoryInfo(path);
        for (int depth = 0; current is not null && depth < 128; depth++)
        {
            current.Refresh();
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Acceptance paths cannot contain a reparse point.");
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }

        if (current is not null)
        {
            throw new IOException("The acceptance path depth is invalid.");
        }
    }

    private static void WritePublicCertificate(
        X509Certificate2 certificate,
        string destinationPath)
    {
        byte[] publicCertificate = certificate.Export(X509ContentType.Cert);
        try
        {
            using var stream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            stream.Write(publicCertificate);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicCertificate);
        }
    }

    private static async Task<bool> WaitForPhaseSignalAsync(
        HarnessPaths paths,
        string phaseSignalPath,
        IReadOnlyCollection<string> allowedNames,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + PhaseTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertAllowedControlEntries(paths.ControlDirectory, allowedNames);
            if (TryValidateSignal(paths.StopSignalPath))
            {
                return false;
            }

            if (TryValidateSignal(phaseSignalPath))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("The bounded acceptance phase timed out.");
    }

    private static async Task WaitForFinalStopSignalAsync(
        HarnessPaths paths,
        IReadOnlyCollection<string> allowedNames,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + PhaseTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertAllowedControlEntries(paths.ControlDirectory, allowedNames);
            if (TryValidateSignal(paths.StopSignalPath))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("The bounded acceptance stop phase timed out.");
    }

    private static bool TryValidateSignal(string signalPath)
    {
        if (Directory.Exists(signalPath))
        {
            throw new IOException("The acceptance signal is invalid.");
        }

        if (!File.Exists(signalPath))
        {
            return false;
        }

        var signal = new FileInfo(signalPath);
        signal.Refresh();
        if ((signal.Attributes & FileAttributes.ReparsePoint) != 0 || signal.Length != 0)
        {
            throw new IOException("The acceptance signal is invalid.");
        }

        return true;
    }

    private static void AssertAllowedControlEntries(
        string controlDirectory,
        IReadOnlyCollection<string> allowedNames)
    {
        var allowed = new HashSet<string>(allowedNames, StringComparer.Ordinal);
        foreach (string entryPath in Directory.EnumerateFileSystemEntries(controlDirectory))
        {
            var entry = new FileInfo(entryPath);
            entry.Refresh();
            if (!entry.Exists ||
                (entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !allowed.Contains(entry.Name))
            {
                throw new IOException("The acceptance control directory is invalid.");
            }
        }
    }

    private static void WriteJsonAtomically<T>(T value, string destinationPath)
    {
        string temporaryPath = destinationPath + ".tmp";
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, TicketJsonOptions);
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private static string NormalizeThumbprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The loopback certificate thumbprint is invalid.");
        }

        return value.ToUpperInvariant();
    }

    private sealed record HarnessPaths(
        string CatalogDatabasePath,
        string ProtectedStorePath,
        string FixtureRoot,
        string ControlDirectory,
        string ReadyTicketPath,
        string ResultTicketPath,
        string StopSignalPath,
        string CancelVerificationSignalPath,
        string CancelVerificationTicketPath,
        string DialogCloseVerificationSignalPath,
        string DialogCloseVerificationTicketPath,
        string DeletionFaultReadyTicketPath,
        string PendingVerificationSignalPath,
        string PendingVerificationTicketPath,
        string PublicCertificatePath);

    private sealed record SeedBaseline(
        string ConfigurationReference,
        SnapshotId SnapshotId,
        TargetGraphCounts TargetGraph);

    private sealed record SourceBindingRow(
        string ConfigurationId,
        long SourceKind,
        string ConfigurationReference,
        ContentSourceStatus Status,
        SnapshotId? SnapshotId);

    private sealed record TargetGraphCounts(
        long Snapshots,
        long SnapshotKeys,
        long Categories,
        long Channels,
        long ProtectedLocators,
        long Favorites,
        long SyncRuns);

    private sealed class SeedContext : IDisposable
    {
        public SeedContext(
            SourceId targetSourceId,
            SourceId siblingSourceId,
            SourceConfigurationId configurationId,
            ProtectedLocatorReference configurationLocatorReference,
            string configurationReference,
            SnapshotId targetSnapshotId,
            ChannelId[] targetChannelIds,
            TargetGraphCounts targetGraph,
            ISecretStore secretStore,
            string configurationRecordFileName,
            byte[] expectedConfigurationDigest)
        {
            TargetSourceId = targetSourceId;
            SiblingSourceId = siblingSourceId;
            ConfigurationId = configurationId;
            ConfigurationLocatorReference = configurationLocatorReference;
            ConfigurationReference = configurationReference;
            TargetSnapshotId = targetSnapshotId;
            TargetChannelIds = targetChannelIds;
            TargetGraph = targetGraph;
            SecretStore = secretStore;
            ConfigurationRecordFileName = configurationRecordFileName;
            ExpectedConfigurationDigest = expectedConfigurationDigest;
        }

        public SourceId TargetSourceId { get; }

        public SourceId SiblingSourceId { get; }

        public SourceConfigurationId ConfigurationId { get; }

        public ProtectedLocatorReference ConfigurationLocatorReference { get; }

        public string ConfigurationReference { get; }

        public SnapshotId TargetSnapshotId { get; }

        public ChannelId[] TargetChannelIds { get; }

        public TargetGraphCounts TargetGraph { get; }

        public ISecretStore SecretStore { get; }

        public string ConfigurationRecordFileName { get; }

        public byte[] ExpectedConfigurationDigest { get; }

        public void Dispose() => CryptographicOperations.ZeroMemory(ExpectedConfigurationDigest);
    }

    private sealed record ReadyTicket(
        bool IsReady,
        bool SeedCompleted,
        string CertificateThumbprint);

    private sealed record ResultTicket(
        bool ReadyPublished,
        bool SeedCompleted,
        bool StopObserved,
        bool StoppedGracefully,
        string? CertificateThumbprint,
        int RequestCount,
        int CompletedResponseCount,
        long CompletedBodyBytes,
        int FailureCount,
        int ChannelARequestCount,
        int ChannelBRequestCount,
        bool CancelNoMutationVerified,
        bool DialogCloseNoMutationVerified,
        bool PendingDeletionVerified,
        bool PendingTargetCatalogPreserved,
        bool PendingConfigurationRecordPreserved,
        bool PendingTombstoneBindingVerified,
        bool PendingSiblingCatalogRetained,
        bool DeletionFaultReleased,
        bool TargetCatalogDeleted,
        bool TargetProtectedRecordsDeleted,
        bool TombstoneBindingCompleted,
        bool SiblingCatalogRetained);

    private sealed record PreservationOracleResult(
        bool IsVerified,
        bool TargetCatalogPreserved,
        bool ConfigurationRecordPreserved,
        bool NoDeletionTombstone,
        bool SiblingCatalogRetained);

    private sealed record DeletionFaultReadyTicket(bool IsReady);

    private sealed record PendingVerificationTicket(
        bool IsVerified,
        bool TargetCatalogPreserved,
        bool ConfigurationRecordPreserved,
        bool TombstoneBindingPending,
        bool SiblingCatalogRetained,
        bool DeletionFaultReleased);

    private sealed record PendingOracleResult(
        bool IsVerified,
        bool TargetCatalogPreserved,
        bool ConfigurationRecordPreserved,
        bool TombstoneBindingPending,
        bool SiblingCatalogRetained);

    private sealed record DeletionOracleResult(
        bool IsVerified,
        bool TargetCatalogDeleted,
        bool TargetProtectedRecordsDeleted,
        bool TombstoneBindingCompleted,
        bool SiblingCatalogRetained);
}
