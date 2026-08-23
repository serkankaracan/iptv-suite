using System.Runtime.Versioning;
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
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;

    internal SqlitePlaybackSourceResolver(string databasePath)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
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

            if (binding.StreamReference is null)
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
            SELECT c.stream_reference, c.provider_item_kind, c.provider_item_id
            FROM channels AS c
            JOIN sources AS s ON s.active_snapshot_id = c.snapshot_id
            WHERE s.source_id = $source AND c.channel_id = $channel;
            """;
        command.Parameters.AddWithValue(
            "$source",
            selection.SourceId.Value.ToString("N"));
        command.Parameters.AddWithValue(
            "$channel",
            selection.ChannelId.Value.ToString("N"));
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string? streamReference = reader.IsDBNull(0) ? null : reader.GetString(0);
        bool hasProviderKind = !reader.IsDBNull(1);
        bool hasProviderId = !reader.IsDBNull(2);
        bool hasProviderItem = hasProviderKind && hasProviderId;
        if (hasProviderKind != hasProviderId ||
            (streamReference is not null) == hasProviderItem)
        {
            throw new InvalidDataException("The playback source binding is contradictory.");
        }

        return new PlaybackSourceBinding(streamReference);
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

    private sealed record PlaybackSourceBinding(string? StreamReference);
}
