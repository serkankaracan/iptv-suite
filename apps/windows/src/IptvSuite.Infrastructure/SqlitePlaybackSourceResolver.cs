using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

internal enum PlaybackSourceResolutionFailure
{
    None,
    Unavailable,
    UnsupportedSource,
    InvalidLocator,
    StorageUnavailable,
}

internal sealed record PlaybackSourceResolutionResult(
    SecretLease? Lease,
    PlaybackSourceResolutionFailure Failure)
{
    internal bool IsSuccess =>
        Lease is not null && Failure == PlaybackSourceResolutionFailure.None;

    public override string ToString() =>
        IsSuccess
            ? "[PLAYBACK-SOURCE-RESOLUTION:SUCCESS]"
            : $"[PLAYBACK-SOURCE-RESOLUTION:{Failure}]";
}

[SupportedOSPlatform("windows")]
internal sealed class SqlitePlaybackSourceResolver
{
    private const long XtreamLiveProviderItemKind = 1;
    private const long XtreamMovieProviderItemKind = 2;
    private const long XtreamEpisodeProviderItemKind = 3;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;
    private readonly ISecretStore _secretStore;

    internal SqlitePlaybackSourceResolver(
        string databasePath,
        ISecretStore secretStore)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _secretStore = secretStore;
    }

    internal async ValueTask<PlaybackSourceResolutionResult> ResolveAsync(
        PlaybackSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            PlaybackSourceBinding? binding = await ReadActiveBindingAsync(
                selection,
                cancellationToken).ConfigureAwait(false);
            if (binding is null)
            {
                return Failed(PlaybackSourceResolutionFailure.Unavailable);
            }

            return binding.SourceKind switch
            {
                SourceKind.RemotePlaylist => await ResolveRemotePlaylistAsync(
                    selection,
                    binding,
                    cancellationToken).ConfigureAwait(false),
                SourceKind.XtreamCompatible => await ResolveXtreamAsync(
                    selection,
                    binding,
                    cancellationToken).ConfigureAwait(false),
                _ => Failed(PlaybackSourceResolutionFailure.UnsupportedSource),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException)
        {
            return Failed(PlaybackSourceResolutionFailure.StorageUnavailable);
        }
        catch (IOException)
        {
            return Failed(PlaybackSourceResolutionFailure.StorageUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(PlaybackSourceResolutionFailure.StorageUnavailable);
        }
        catch (InvalidDataException)
        {
            return Failed(PlaybackSourceResolutionFailure.StorageUnavailable);
        }
    }

    private async ValueTask<PlaybackSourceResolutionResult> ResolveRemotePlaylistAsync(
        PlaybackSelection selection,
        PlaybackSourceBinding binding,
        CancellationToken cancellationToken)
    {
        if (selection.Target.Kind != PlaybackTargetKind.Live ||
            binding.StreamReference is null ||
            binding.ProviderItemKind.HasValue ||
            binding.ProviderItemId is not null ||
            !ProtectedLocatorReference.Parse(binding.ConfigurationReference).IsSuccess)
        {
            return Failed(PlaybackSourceResolutionFailure.UnsupportedSource);
        }

        DomainResult<ProtectedLocatorReference> parsedReference =
            ProtectedLocatorReference.Parse(binding.StreamReference);
        if (!parsedReference.IsSuccess)
        {
            return Failed(PlaybackSourceResolutionFailure.InvalidLocator);
        }

        var locatorReader = new SqliteCatalogLocatorReader(_databasePath);
        CatalogLocatorReadResult locator = await locatorReader.ReadAsync(
            selection.SourceId,
            selection.ChannelId,
            ProtectedValuePurpose.ChannelStreamLocator,
            parsedReference.Value!,
            cancellationToken).ConfigureAwait(false);
        if (!locator.IsSuccess)
        {
            return Failed(locator.Failure == CatalogLocatorReadFailure.AuthenticationFailed
                ? PlaybackSourceResolutionFailure.InvalidLocator
                : PlaybackSourceResolutionFailure.Unavailable);
        }

        SecretLease? lease = locator.Lease!;
        try
        {
            if (!IsValidRemotePlaylistLocator(lease.Value.Span, binding))
            {
                return Failed(PlaybackSourceResolutionFailure.InvalidLocator);
            }

            var result = new PlaybackSourceResolutionResult(
                lease,
                PlaybackSourceResolutionFailure.None);
            lease = null;
            return result;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async ValueTask<PlaybackSourceResolutionResult> ResolveXtreamAsync(
        PlaybackSelection selection,
        PlaybackSourceBinding binding,
        CancellationToken cancellationToken)
    {
        if (binding.StreamReference is not null ||
            binding.ProviderItemKind != ExpectedProviderItemKind(selection.Target.Kind) ||
            binding.ProviderItemId is null ||
            !TryGetXtreamContainerExtension(
                selection.Target.Kind,
                binding.ContainerHint,
                out string? extension))
        {
            return Failed(PlaybackSourceResolutionFailure.UnsupportedSource);
        }

        DomainResult<SecretReference> parsedReference =
            SecretReference.Parse(binding.ConfigurationReference);
        DomainResult<ProviderItemKey> parsedProviderItem =
            ProviderItemKey.Create(binding.ProviderItemId);
        if (!parsedReference.IsSuccess ||
            !parsedProviderItem.IsSuccess ||
            !string.Equals(
                parsedProviderItem.Value.Value,
                binding.ProviderItemId,
                StringComparison.Ordinal))
        {
            return Failed(PlaybackSourceResolutionFailure.InvalidLocator);
        }

        SecretStoreReadResult read = await _secretStore.ReadCredentialsAsync(
            selection.SourceId,
            ProtectedRecordOwner.ForSourceConfiguration(binding.ConfigurationId),
            parsedReference.Value!,
            cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return Failed(read.Failure == SecretStoreFailure.StorageUnavailable
                ? PlaybackSourceResolutionFailure.StorageUnavailable
                : PlaybackSourceResolutionFailure.Unavailable);
        }

        using SecretLease credentials = read.Lease!;
        if (!ProtectedSourcePayloadDecoder.TryDecodeXtream(
                credentials.Value,
                out XtreamSourcePayloadLayout layout) ||
            !TryBuildXtreamLocator(
                credentials.Value.Span,
                layout,
                parsedProviderItem.Value,
                selection.Target.Kind,
                extension!,
                binding,
                out SecretLease? locatorLease))
        {
            return Failed(PlaybackSourceResolutionFailure.InvalidLocator);
        }

        return new PlaybackSourceResolutionResult(
            locatorLease,
            PlaybackSourceResolutionFailure.None);
    }

    private async ValueTask<PlaybackSourceBinding?> ReadActiveBindingAsync(
        PlaybackSelection selection,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        (string sql, Guid targetId) = selection.Target.Kind switch
        {
            PlaybackTargetKind.Live => (
                """
                SELECT s.configuration_id, s.source_kind, s.endpoint_scheme, s.endpoint_host,
                    s.endpoint_port, s.configuration_reference, c.stream_reference,
                    c.provider_item_kind, c.provider_item_id, c.container_hint
                FROM channels AS c
                JOIN sources AS s ON s.active_snapshot_id = c.snapshot_id
                JOIN snapshots AS p
                    ON p.snapshot_id = c.snapshot_id AND p.source_id = s.source_id
                WHERE s.source_id = $source AND c.channel_id = $target AND s.status = $ready;
                """,
                selection.Target.ChannelId!.Value.Value),
            PlaybackTargetKind.Movie => (
                """
                SELECT s.configuration_id, s.source_kind, s.endpoint_scheme, s.endpoint_host,
                    s.endpoint_port, s.configuration_reference, NULL,
                    2, item.provider_item_id, item.container_extension
                FROM movies AS item
                JOIN sources AS s ON s.active_snapshot_id = item.snapshot_id
                JOIN snapshots AS p
                    ON p.snapshot_id = item.snapshot_id AND p.source_id = s.source_id
                WHERE s.source_id = $source AND item.movie_id = $target AND s.status = $ready;
                """,
                selection.Target.MovieId!.Value.Value),
            PlaybackTargetKind.Episode => (
                """
                SELECT s.configuration_id, s.source_kind, s.endpoint_scheme, s.endpoint_host,
                    s.endpoint_port, s.configuration_reference, NULL,
                    3, item.provider_item_id, item.container_extension
                FROM episodes AS item
                JOIN sources AS s ON s.active_snapshot_id = item.snapshot_id
                JOIN snapshots AS p
                    ON p.snapshot_id = item.snapshot_id AND p.source_id = s.source_id
                WHERE s.source_id = $source AND item.episode_id = $target AND s.status = $ready;
                """,
                selection.Target.EpisodeId!.Value.Value),
            _ => throw new InvalidDataException("The playback target kind is invalid."),
        };
        command.CommandText = sql;
        command.Parameters.AddWithValue(
            "$source",
            selection.SourceId.Value.ToString("N"));
        command.Parameters.AddWithValue(
            "$target",
            targetId.ToString("N"));
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) ||
            reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5) ||
            !Guid.TryParseExact(reader.GetString(0), "N", out Guid configurationValue))
        {
            throw new InvalidDataException("The playback source configuration is incomplete.");
        }

        DomainResult<SourceConfigurationId> configurationId =
            SourceConfigurationId.Create(configurationValue);
        long sourceKindValue = reader.GetInt64(1);
        if (!configurationId.IsSuccess ||
            sourceKindValue < int.MinValue || sourceKindValue > int.MaxValue ||
            !Enum.IsDefined((SourceKind)(int)sourceKindValue))
        {
            throw new InvalidDataException("The playback source configuration is invalid.");
        }

        string? streamReference = reader.IsDBNull(6) ? null : reader.GetString(6);
        bool hasProviderKind = !reader.IsDBNull(7);
        bool hasProviderId = !reader.IsDBNull(8);
        bool hasProviderItem = hasProviderKind && hasProviderId;
        if (hasProviderKind != hasProviderId ||
            (streamReference is not null) == hasProviderItem)
        {
            throw new InvalidDataException("The playback source binding is contradictory.");
        }

        return new PlaybackSourceBinding(
            configurationId.Value,
            (SourceKind)(int)sourceKindValue,
            reader.GetString(2),
            reader.GetString(3),
            checked((int)reader.GetInt64(4)),
            reader.GetString(5),
            streamReference,
            hasProviderKind ? reader.GetInt64(7) : null,
            hasProviderId ? reader.GetString(8) : null,
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    private static bool TryBuildXtreamLocator(
        ReadOnlySpan<byte> payload,
        XtreamSourcePayloadLayout layout,
        ProviderItemKey providerItem,
        PlaybackTargetKind targetKind,
        string extension,
        PlaybackSourceBinding binding,
        out SecretLease? locatorLease)
    {
        locatorLease = null;
        byte[]? locatorBytes = null;
        try
        {
            // Uri construction requires transient managed strings. They remain operation-local,
            // are never returned or logged, and mirror the existing M6 provider-client boundary.
            string locator = StrictUtf8.GetString(
                payload.Slice(layout.LocatorOffset, layout.LocatorLength));
            string username = StrictUtf8.GetString(
                payload.Slice(layout.UsernameOffset, layout.UsernameLength));
            string password = StrictUtf8.GetString(
                payload.Slice(layout.PasswordOffset, layout.PasswordLength));
            bool sourceUsesHttp = string.Equals(
                binding.EndpointScheme,
                Uri.UriSchemeHttp,
                StringComparison.Ordinal);
            DomainResult<PreparedXtreamSourceDraft> prepared = sourceUsesHttp
                ? SourceConfigurationValidator.PrepareXtreamAllowingInsecureHttp(
                    "Playback source",
                    locator,
                    username,
                    password)
                : SourceConfigurationValidator.PrepareXtream(
                    "Playback source",
                    locator,
                    username,
                    password);
            if (!prepared.IsSuccess ||
                !MatchesEndpoint(prepared.Value!.SafeEndpoint, binding) ||
                !Uri.TryCreate(locator, UriKind.Absolute, out Uri? baseUri))
            {
                return false;
            }

            string basePath = NormalizeXtreamBasePath(baseUri.AbsolutePath);
            string segment = targetKind switch
            {
                PlaybackTargetKind.Live => "live/",
                PlaybackTargetKind.Movie => "movie/",
                PlaybackTargetKind.Episode => "series/",
                _ => throw new ArgumentOutOfRangeException(nameof(targetKind)),
            };
            var builder = new UriBuilder(baseUri)
            {
                Path = string.Concat(
                    basePath,
                    segment,
                    Uri.EscapeDataString(username),
                    "/",
                    Uri.EscapeDataString(password),
                    "/",
                    Uri.EscapeDataString(providerItem.Value),
                    ".",
                    extension),
                Query = string.Empty,
                Fragment = string.Empty,
            };
            locatorBytes = StrictUtf8.GetBytes(builder.Uri.AbsoluteUri);
            if (!IsValidXtreamLocator(locatorBytes, binding))
            {
                return false;
            }

            locatorLease = SecretLease.TakeOwnership(locatorBytes);
            locatorBytes = null;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (UriFormatException)
        {
            return false;
        }
        finally
        {
            if (locatorBytes is not null)
            {
                CryptographicOperations.ZeroMemory(locatorBytes);
            }
        }
    }

    private static bool MatchesEndpoint(
        SafeEndpoint endpoint,
        PlaybackSourceBinding binding) =>
        string.Equals(endpoint.Scheme, binding.EndpointScheme, StringComparison.Ordinal) &&
        string.Equals(endpoint.Host, binding.EndpointHost, StringComparison.Ordinal) &&
        endpoint.Port == binding.EndpointPort;

    private static long ExpectedProviderItemKind(PlaybackTargetKind targetKind) => targetKind switch
    {
        PlaybackTargetKind.Live => XtreamLiveProviderItemKind,
        PlaybackTargetKind.Movie => XtreamMovieProviderItemKind,
        PlaybackTargetKind.Episode => XtreamEpisodeProviderItemKind,
        _ => long.MinValue,
    };

    private static bool TryGetXtreamContainerExtension(
        PlaybackTargetKind targetKind,
        string? containerHint,
        out string? extension)
    {
        extension = targetKind == PlaybackTargetKind.Live
            ? containerHint switch
            {
                nameof(ChannelContainerHint.Hls) => "m3u8",
                nameof(ChannelContainerHint.MpegTs) => "ts",
                _ => null,
            }
            : IsSafeContainerExtension(containerHint)
                ? containerHint!.ToLowerInvariant()
                : null;
        return extension is not null;
    }

    private static bool IsSafeContainerExtension(string? value) =>
        value is { Length: >= 1 and <= 16 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character));

    private static string NormalizeXtreamBasePath(string absolutePath)
    {
        string path = absolutePath;
        string fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "get.php", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "player_api.php", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^fileName.Length];
        }

        return path.EndsWith('/') ? path : string.Concat(path, "/");
    }

    private static bool IsValidXtreamLocator(
        ReadOnlySpan<byte> locatorBytes,
        PlaybackSourceBinding binding)
    {
        if (!TryDecodeLocator(locatorBytes, out string? locator) ||
            !Uri.TryCreate(locator, UriKind.Absolute, out Uri? locatorUri) ||
            !string.IsNullOrEmpty(locatorUri.UserInfo))
        {
            return false;
        }

        bool sourceUsesHttp = string.Equals(
            binding.EndpointScheme,
            Uri.UriSchemeHttp,
            StringComparison.Ordinal);
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared = sourceUsesHttp
            ? SourceConfigurationValidator.PrepareRemotePlaylistAllowingInsecureHttp(
                "Playback source",
                locator)
            : SourceConfigurationValidator.PrepareRemotePlaylist(
                "Playback source",
                locator);
        return prepared.IsSuccess && MatchesEndpoint(prepared.Value!.SafeEndpoint, binding);
    }

    private static bool IsValidRemotePlaylistLocator(
        ReadOnlySpan<byte> locatorBytes,
        PlaybackSourceBinding binding)
    {
        bool sourceUsesHttp = string.Equals(
            binding.EndpointScheme,
            Uri.UriSchemeHttp,
            StringComparison.Ordinal);
        if (!TryDecodeLocator(locatorBytes, out string? locator) ||
            !Uri.TryCreate(locator, UriKind.Absolute, out Uri? locatorUri) ||
            !string.IsNullOrEmpty(locatorUri.UserInfo))
        {
            return false;
        }

        DomainResult<PreparedRemotePlaylistSourceDraft> prepared = sourceUsesHttp
            ? SourceConfigurationValidator.PrepareRemotePlaylistAllowingInsecureHttp(
                "Playback source",
                locator)
            : SourceConfigurationValidator.PrepareRemotePlaylist(
                "Playback source",
                locator);
        if (!prepared.IsSuccess)
        {
            return false;
        }

        SafeEndpoint locatorEndpoint = prepared.Value!.SafeEndpoint;
        return string.Equals(
                locatorEndpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.Ordinal) ||
            (sourceUsesHttp && MatchesEndpoint(locatorEndpoint, binding));
    }

    private static bool TryDecodeLocator(
        ReadOnlySpan<byte> locatorBytes,
        out string? locator)
    {
        try
        {
            locator = StrictUtf8.GetString(locatorBytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            locator = null;
            return false;
        }
    }

    private static PlaybackSourceResolutionResult Failed(
        PlaybackSourceResolutionFailure failure) => new(null, failure);

    private sealed record PlaybackSourceBinding(
        SourceConfigurationId ConfigurationId,
        SourceKind SourceKind,
        string EndpointScheme,
        string EndpointHost,
        int EndpointPort,
        string ConfigurationReference,
        string? StreamReference,
        long? ProviderItemKind,
        string? ProviderItemId,
        string? ContainerHint);
}
