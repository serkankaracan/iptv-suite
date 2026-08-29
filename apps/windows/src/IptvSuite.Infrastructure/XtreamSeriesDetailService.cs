using System.Globalization;
using System.Runtime.Versioning;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class XtreamSeriesDetailService : ISeriesDetailRefreshCoordinator
{
    private readonly string _databasePath;
    private readonly IXtreamProviderClient _providerClient;
    private readonly ISeriesDetailStore _store;
    private readonly Func<DateTimeOffset> _utcNow;

    public XtreamSeriesDetailService(
        string databasePath,
        ISecretStore secretStore,
        IHttpTransport transport)
        : this(
            databasePath,
            new XtreamProviderClient(secretStore, transport),
            new SqliteContentCatalog(databasePath),
            () => DateTimeOffset.UtcNow)
    {
    }

    internal XtreamSeriesDetailService(
        string databasePath,
        IXtreamProviderClient providerClient,
        ISeriesDetailStore store,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _providerClient = providerClient ?? throw new ArgumentNullException(nameof(providerClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public async ValueTask<DomainResult<SeriesDetailRefreshResult>> RefreshAsync(
        SourceId sourceId,
        SeriesId seriesId,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty || seriesId.IsEmpty)
        {
            return DomainResult.Failure<SeriesDetailRefreshResult>(
                DomainErrorCode.DomainInvariantViolation);
        }

        DomainResult<SeriesRequestContext> context;
        try
        {
            context = await ReadContextAsync(sourceId, seriesId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or InvalidDataException)
        {
            return DomainResult.Failure<SeriesDetailRefreshResult>(
                DomainErrorCode.StorageUnavailable);
        }

        if (!context.IsSuccess)
        {
            return DomainResult.Failure<SeriesDetailRefreshResult>(context.Error!);
        }

        DomainResult<XtreamSeriesDetails> loaded = await _providerClient.LoadSeriesDetailsAsync(
            context.Value!.Source,
            context.Value.ProviderKey,
            cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return DomainResult.Failure<SeriesDetailRefreshResult>(loaded.Error!);
        }

        DomainResult<SeriesDetailMutation> mapped = Map(
            sourceId,
            context.Value.SnapshotId,
            seriesId,
            loaded.Value!);
        if (!mapped.IsSuccess)
        {
            return DomainResult.Failure<SeriesDetailRefreshResult>(mapped.Error!);
        }

        try
        {
            await _store.ReplaceSeriesDetailsAsync(mapped.Value!, cancellationToken)
                .ConfigureAwait(false);
            return DomainResult.Success(new SeriesDetailRefreshResult(
                seriesId,
                mapped.Value!.Seasons.Count,
                mapped.Value.Episodes.Count));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return DomainResult.Failure<SeriesDetailRefreshResult>(
                DomainErrorCode.RemoteResourceNotFound);
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            return DomainResult.Failure<SeriesDetailRefreshResult>(
                DomainErrorCode.StorageUnavailable);
        }
    }

    private async ValueTask<DomainResult<SeriesRequestContext>> ReadContextAsync(
        SourceId sourceId,
        SeriesId seriesId,
        CancellationToken cancellationToken)
    {
        var database = new SqliteCatalogDatabase(_databasePath);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
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
            SELECT source.configuration_id, source.display_name, source.endpoint_scheme,
                   source.endpoint_host, source.endpoint_port, source.configuration_reference,
                   item.snapshot_id, item.provider_item_id
            FROM series AS item
            JOIN sources AS source ON source.active_snapshot_id = item.snapshot_id
            WHERE source.source_id = $source
              AND source.source_kind = $xtream
              AND source.status = $ready
              AND item.series_id = $series;
            """;
        command.Parameters.AddWithValue("$source", Id(sourceId.Value));
        command.Parameters.AddWithValue("$xtream", (int)SourceKind.XtreamCompatible);
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$series", Id(seriesId.Value));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DomainResult.Failure<SeriesRequestContext>(
                DomainErrorCode.RemoteResourceNotFound);
        }

        if (!Guid.TryParseExact(reader.GetString(0), "N", out Guid configurationGuid) ||
            !Guid.TryParseExact(reader.GetString(6), "N", out Guid snapshotGuid))
        {
            return DomainResult.Failure<SeriesRequestContext>(
                DomainErrorCode.StorageUnavailable);
        }

        DomainResult<SourceConfigurationId> configurationId =
            SourceConfigurationId.Create(configurationGuid);
        DomainResult<SnapshotId> snapshotId = SnapshotId.Create(snapshotGuid);
        DomainResult<SecretReference> reference = SecretReference.Parse(reader.GetString(5));
        DomainResult<ProviderItemKey> providerKey = ProviderItemKey.Create(reader.GetString(7));
        if (!configurationId.IsSuccess || !snapshotId.IsSuccess ||
            !reference.IsSuccess || !providerKey.IsSuccess)
        {
            return DomainResult.Failure<SeriesRequestContext>(
                DomainErrorCode.StorageUnavailable);
        }

        string scheme = reader.GetString(2);
        DomainResult<ContentSource> source = StoredXtreamSourceFactory.RestoreForRefresh(
            sourceId,
            configurationId.Value,
            reader.GetString(1),
            scheme,
            reader.GetString(3),
            reader.GetInt32(4),
            reference.Value!,
            allowsInsecureTransport: string.Equals(
                scheme,
                Uri.UriSchemeHttp,
                StringComparison.Ordinal),
            _utcNow());
        return source.IsSuccess
            ? DomainResult.Success(new SeriesRequestContext(
                source.Value!,
                snapshotId.Value,
                providerKey.Value))
            : DomainResult.Failure<SeriesRequestContext>(source.Error!);
    }

    private static DomainResult<SeriesDetailMutation> Map(
        SourceId sourceId,
        SnapshotId snapshotId,
        SeriesId seriesId,
        XtreamSeriesDetails details)
    {
        var inputByNumber = new Dictionary<int, XtreamSeasonInput>();
        foreach (XtreamSeasonInput input in details.Seasons)
        {
            if (!inputByNumber.TryAdd(input.Number, input))
            {
                return DomainResult.Failure<SeriesDetailMutation>(
                    DomainErrorCode.UnsupportedPlaylistFormat);
            }
        }

        foreach (int number in details.Episodes.Select(item => item.SeasonNumber).Distinct())
        {
            if (!inputByNumber.ContainsKey(number))
            {
                inputByNumber.Add(number, new XtreamSeasonInput(
                    null,
                    number,
                    $"Season {number.ToString(CultureInfo.InvariantCulture)}"));
            }
        }

        if (inputByNumber.Count > XtreamProviderJsonParser.MaximumSeasonCount)
        {
            return DomainResult.Failure<SeriesDetailMutation>(
                DomainErrorCode.UnsupportedPlaylistFormat);
        }

        var seasonIds = new Dictionary<int, SeasonId>();
        var seasons = new List<Season>(inputByNumber.Count);
        foreach ((int number, XtreamSeasonInput input) in inputByNumber.OrderBy(item => item.Key))
        {
            SeasonId seasonId = SeasonId.Generate();
            DomainResult<Season> season = Season.Create(
                seasonId,
                snapshotId,
                seriesId,
                number,
                input.Name);
            if (!season.IsSuccess)
            {
                return DomainResult.Failure<SeriesDetailMutation>(season.Error!);
            }

            seasonIds.Add(number, seasonId);
            seasons.Add(season.Value!);
        }

        var episodes = new List<Episode>(details.Episodes.Count);
        foreach (XtreamEpisodeInput input in details.Episodes)
        {
            if (!seasonIds.TryGetValue(input.SeasonNumber, out SeasonId seasonId))
            {
                return DomainResult.Failure<SeriesDetailMutation>(
                    DomainErrorCode.UnsupportedPlaylistFormat);
            }

            DomainResult<Episode> episode = Episode.Create(
                EpisodeId.Generate(),
                snapshotId,
                seasonId,
                input.ProviderPlaybackKey,
                input.EpisodeNumber,
                input.Name,
                input.ContainerExtension,
                input.Duration);
            if (!episode.IsSuccess)
            {
                return DomainResult.Failure<SeriesDetailMutation>(episode.Error!);
            }

            episodes.Add(episode.Value!);
        }

        return DomainResult.Success(new SeriesDetailMutation(
            sourceId,
            snapshotId,
            seriesId,
            seasons,
            episodes));
    }

    private static string Id(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    private sealed record SeriesRequestContext(
        ContentSource Source,
        SnapshotId SnapshotId,
        ProviderItemKey ProviderKey);
}
