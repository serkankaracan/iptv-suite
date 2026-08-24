using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using IptvSuite.Testing;

namespace IptvSuite.PlaybackUiAcceptanceHarness;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string Command = "serve-and-seed";
    private const string ExistingCatalogSourceName = "Synthetic 50k source";
    private const string PlaybackSourceName = "00 Synthetic protected playback source";
    private const string PlaybackChannelName = "Synthetic protected Tier A channel";
    private const string MediaRoute = "/direct-h264-aac.ts";
    private const string FixtureId = "iptvsuite-tier-a-synthetic-v1";
    private const string FixtureLicense = "CC0-1.0";
    private const string FixtureFileName = "direct-h264-aac.ts";
    private const string FixtureManifestName = "fixture-manifest.json";
    private const string ReadyTicketName = "ready.json";
    private const string ResultTicketName = "result.json";
    private const string StopSignalName = "stop.signal";
    private const string PublicCertificateName = "loopback.cer";
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
        int exitCode = 0;

        try
        {
            byte[] fixture = LoadValidatedFixture(paths.FixtureRoot);
            try
            {
                server = await LocalHttpFixtureServer.StartHttpsAsync(
                    new Dictionary<string, FixtureHttpResponse>(StringComparer.Ordinal)
                    {
                        [MediaRoute] = new FixtureHttpResponse(
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

            await SeedAndVerifyAsync(
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

            await WaitForStopSignalAsync(paths.StopSignalPath, cancellationToken)
                .ConfigureAwait(false);
            stopObserved = true;
            await server.DisposeAsync().ConfigureAwait(false);
            stoppedGracefully = true;
        }
        catch
        {
            exitCode = 1;
        }
        finally
        {
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
                        FailureCount: server?.FailureCount ?? 0),
                    paths.ResultTicketPath);
            }
            catch
            {
                exitCode = 1;
            }
        }

        return exitCode;
    }

    private static async Task SeedAndVerifyAsync(
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
        Uri mediaUri = new(baseAddress, MediaRoute);
        var secretStore = new DpapiCurrentUserSecretStore(
            protectedStorePath,
            cancellationToken);
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

        byte[] playlist = BuildPlaylist(mediaUri);
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
            if (!parsed.IsSuccess || parsed.Value?.ProcessedEntryCount != 1)
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
        if (playbackPage.TotalCount != 1 || playbackPage.Items.Count != 1 ||
            !string.Equals(playbackPage.Items[0].Name, PlaybackChannelName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The synthetic playback channel is invalid.");
        }

        var resolver = new SqlitePlaybackSourceResolver(catalogDatabasePath, secretStore);
        PlaybackSourceResolutionResult resolved = await resolver.ResolveAsync(
            new PlaybackSelection(playbackSource.SourceId, playbackPage.Items[0].ChannelId),
            cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            resolved.Lease?.Dispose();
            throw new InvalidDataException("The protected playback binding is unavailable.");
        }

        byte[] expectedLocator = Encoding.UTF8.GetBytes(mediaUri.AbsoluteUri);
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

    private static byte[] BuildPlaylist(Uri mediaUri) => Encoding.UTF8.GetBytes(
        string.Concat(
            "#EXTM3U\n",
            "#EXTINF:-1 tvg-id=\"m11-protected\" group-title=\"Synthetic\",",
            PlaybackChannelName,
            "\n",
            mediaUri.AbsoluteUri,
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

    private static async Task WaitForStopSignalAsync(
        string stopSignalPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(stopSignalPath))
            {
                throw new IOException("The acceptance stop signal is invalid.");
            }

            if (File.Exists(stopSignalPath))
            {
                var signal = new FileInfo(stopSignalPath);
                signal.Refresh();
                if ((signal.Attributes & FileAttributes.ReparsePoint) != 0 || signal.Length != 0)
                {
                    throw new IOException("The acceptance stop signal is invalid.");
                }

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
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
        string PublicCertificatePath);

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
        int FailureCount);
}
