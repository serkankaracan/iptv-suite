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
        if (binding.StreamReference is null ||
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
            if (!IsValidHttpsLocator(lease.Value.Span))
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
            binding.ProviderItemKind != XtreamLiveProviderItemKind ||
            binding.ProviderItemId is null ||
            !TryGetXtreamContainerExtension(binding.ContainerHint, out string? extension))
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
            !TryBuildXtreamLiveLocator(
                credentials.Value.Span,
                layout,
                parsedProviderItem.Value,
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
        command.CommandText = """
            SELECT s.configuration_id, s.source_kind, s.endpoint_scheme, s.endpoint_host,
                s.endpoint_port, s.configuration_reference, c.stream_reference,
                c.provider_item_kind, c.provider_item_id, c.container_hint
            FROM channels AS c
            JOIN sources AS s ON s.active_snapshot_id = c.snapshot_id
            JOIN snapshots AS p
                ON p.snapshot_id = c.snapshot_id AND p.source_id = s.source_id
            WHERE s.source_id = $source AND c.channel_id = $channel AND s.status = $ready;
            """;
        command.Parameters.AddWithValue(
            "$source",
            selection.SourceId.Value.ToString("N"));
        command.Parameters.AddWithValue(
            "$channel",
            selection.ChannelId.Value.ToString("N"));
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

    private static bool TryBuildXtreamLiveLocator(
        ReadOnlySpan<byte> payload,
        XtreamSourcePayloadLayout layout,
        ProviderItemKey providerItem,
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
            DomainResult<PreparedXtreamSourceDraft> prepared =
                SourceConfigurationValidator.PrepareXtream(
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

            string basePath = baseUri.AbsolutePath.EndsWith('/')
                ? baseUri.AbsolutePath
                : string.Concat(baseUri.AbsolutePath, "/");
            var builder = new UriBuilder(baseUri)
            {
                Path = string.Concat(
                    basePath,
                    "live/",
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
            if (!IsValidHttpsLocator(locatorBytes))
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

    private static bool TryGetXtreamContainerExtension(
        string? containerHint,
        out string? extension)
    {
        extension = containerHint switch
        {
            nameof(ChannelContainerHint.Hls) => "m3u8",
            nameof(ChannelContainerHint.MpegTs) => "ts",
            _ => null,
        };
        return extension is not null;
    }

    private static bool IsValidHttpsLocator(ReadOnlySpan<byte> locatorBytes)
    {
        string locator;
        try
        {
            locator = StrictUtf8.GetString(locatorBytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        DomainResult<PreparedRemotePlaylistSourceDraft> prepared =
            SourceConfigurationValidator.PrepareRemotePlaylist(
                "Playback source",
                locator);
        return prepared.IsSuccess;
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
