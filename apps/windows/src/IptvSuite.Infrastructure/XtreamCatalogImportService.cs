using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class XtreamCatalogImportService : IXtreamCatalogImportService
{
    private const int ParserVersion = 1;
    private const int NormalizationVersion = 1;
    private const int SnapshotSchemaVersion = 2;
    private readonly string _databasePath;
    private readonly IXtreamProviderClient _providerClient;
    private readonly Func<DateTimeOffset> _utcNow;

    public XtreamCatalogImportService(
        string databasePath,
        ISecretStore secretStore,
        IHttpTransport transport)
        : this(databasePath, new XtreamProviderClient(secretStore, transport), () => DateTimeOffset.UtcNow)
    {
    }

    internal XtreamCatalogImportService(
        string databasePath,
        IXtreamProviderClient providerClient,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _providerClient = providerClient ?? throw new ArgumentNullException(nameof(providerClient));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public async ValueTask<DomainResult<ContentCatalogCounts>> ImportAsync(
        ContentSource source,
        CancellationToken cancellationToken = default)
    {
        XtreamCatalogImportResult result = await ImportWithDispositionAsync(
            source,
            cancellationToken).ConfigureAwait(false);
        return result.Disposition == CatalogImportCommitDisposition.Committed
            ? DomainResult.Success(result.Counts!)
            : DomainResult.Failure<ContentCatalogCounts>(
                result.Error ?? DomainError.Create(DomainErrorCode.StorageUnavailable));
    }

    public async ValueTask<XtreamCatalogImportResult> ImportWithDispositionAsync(
        ContentSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Configuration is not XtreamSourceConfiguration ||
            source.Status == ContentSourceStatus.DeletionPending)
        {
            return XtreamCatalogImportResult.NotCommitted(
                DomainError.Create(DomainErrorCode.DomainInvariantViolation));
        }

        DomainResult<XtreamContentCatalog> loaded;
        try
        {
            loaded = await _providerClient.LoadContentCatalogAsync(
                source,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return XtreamCatalogImportResult.NotCommitted(
                DomainError.Create(DomainErrorCode.OperationCancelled));
        }
        if (!loaded.IsSuccess)
        {
            return XtreamCatalogImportResult.NotCommitted(loaded.Error!);
        }

        DomainResult<ContentCatalogCounts> existingCounts = await ReadExistingCountsAsync(
            source.Id,
            cancellationToken).ConfigureAwait(false);
        if (!existingCounts.IsSuccess)
        {
            return XtreamCatalogImportResult.NotCommitted(existingCounts.Error!);
        }

        DomainErrorCode? suspiciousEmptyStage = FindSuspiciousEmptyReplacement(
            loaded.Value!,
            existingCounts.Value!);
        if (suspiciousEmptyStage.HasValue)
        {
            return XtreamCatalogImportResult.NotCommitted(
                DomainError.Create(suspiciousEmptyStage.Value));
        }

        DateTimeOffset retrievedAt = _utcNow().ToUniversalTime();
        if (retrievedAt == default)
        {
            return XtreamCatalogImportResult.NotCommitted(
                DomainError.Create(DomainErrorCode.DomainInvariantViolation));
        }

        SnapshotId snapshotId = SnapshotId.Generate();
        DomainResult<LiveCatalogMapping> live = MapLiveCatalog(
            source.Id,
            snapshotId,
            loaded.Value!);
        DomainResult<ContentCatalogMutation> content = XtreamContentCatalogMapper.Map(
            source.Id,
            snapshotId,
            loaded.Value);
        if (!live.IsSuccess || !content.IsSuccess)
        {
            return XtreamCatalogImportResult.NotCommitted(
                live.Error ?? content.Error!);
        }

        int warningCount = checked(
            loaded.Value!.LiveCategories.SkippedItemCount +
            loaded.Value.LiveCategories.DuplicateIdentifierCount +
            loaded.Value.LiveStreams.SkippedItemCount +
            loaded.Value.LiveStreams.DuplicateIdentifierCount +
            loaded.Value.MovieCategories.SkippedItemCount +
            loaded.Value.MovieCategories.DuplicateIdentifierCount +
            loaded.Value.Movies.SkippedItemCount +
            loaded.Value.Movies.DuplicateIdentifierCount +
            loaded.Value.SeriesCategories.SkippedItemCount +
            loaded.Value.SeriesCategories.DuplicateIdentifierCount +
            loaded.Value.Series.SkippedItemCount +
            loaded.Value.Series.DuplicateIdentifierCount);
        DomainResult<PlaylistSnapshot> snapshot = PlaylistSnapshot.Create(
            snapshotId,
            source.Id,
            retrievedAt,
            ComputeContentHash(loaded.Value),
            ParserVersion,
            NormalizationVersion,
            SnapshotSchemaVersion,
            live.Value!.Channels.Count,
            warningCount,
            PlaylistSnapshotState.Complete);
        if (!snapshot.IsSuccess)
        {
            return XtreamCatalogImportResult.NotCommitted(snapshot.Error!);
        }

        try
        {
            var writer = new SqliteCatalogSnapshotWriter(_databasePath);
            await writer.ActivateAsync(
                new CatalogSnapshotBatch(
                    source,
                    snapshot.Value!,
                    live.Value.Categories,
                    live.Value.Channels,
                    [],
                    content.Value),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return XtreamCatalogImportResult.Committed(new ContentCatalogCounts(
                live.Value.Channels.Count,
                content.Value!.Movies.Count,
                content.Value.Series.Count,
                content.Value.Episodes.Count));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return XtreamCatalogImportResult.Indeterminate(
                DomainError.Create(DomainErrorCode.OperationCancelled));
        }
        catch (Exception exception) when (exception is SqliteException or IOException or
                                          CryptographicException or InvalidOperationException)
        {
            return XtreamCatalogImportResult.Indeterminate(
                DomainError.Create(DomainErrorCode.StorageUnavailable));
        }
    }

    public async ValueTask<DomainResult<ContentCatalogCounts>> RefreshFromStoredConfigurationAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty)
        {
            return DomainResult.Failure<ContentCatalogCounts>(
                DomainErrorCode.DomainInvariantViolation);
        }

        DomainResult<ContentSource> source = await ReadStoredSourceAsync(sourceId, cancellationToken)
            .ConfigureAwait(false);
        return source.IsSuccess
            ? await ImportAsync(source.Value!, cancellationToken).ConfigureAwait(false)
            : DomainResult.Failure<ContentCatalogCounts>(source.Error!);
    }

    private async ValueTask<DomainResult<ContentSource>> ReadStoredSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        var database = new SqliteCatalogDatabase(_databasePath);
        try
        {
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
                SELECT configuration_id, display_name, endpoint_scheme, endpoint_host,
                       endpoint_port, configuration_reference, source_kind
                FROM sources
                WHERE source_id = $source
                  AND status <> $deletionPending;
                """;
            command.Parameters.AddWithValue("$source", Id(sourceId.Value));
            command.Parameters.AddWithValue("$deletionPending", (int)ContentSourceStatus.DeletionPending);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return DomainResult.Failure<ContentSource>(DomainErrorCode.RemoteResourceNotFound);
            }

            if ((SourceKind)reader.GetInt32(6) != SourceKind.XtreamCompatible ||
                !Guid.TryParseExact(reader.GetString(0), "N", out Guid configurationGuid))
            {
                return DomainResult.Failure<ContentSource>(DomainErrorCode.DomainInvariantViolation);
            }

            DomainResult<SourceConfigurationId> configurationId =
                SourceConfigurationId.Create(configurationGuid);
            DomainResult<SecretReference> reference =
                SecretReference.Parse(reader.GetString(5));
            if (!configurationId.IsSuccess || !reference.IsSuccess)
            {
                return DomainResult.Failure<ContentSource>(DomainErrorCode.StorageUnavailable);
            }

            string scheme = reader.GetString(2);
            return StoredXtreamSourceFactory.RestoreForRefresh(
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
        }
        catch (SqliteException)
        {
            return DomainResult.Failure<ContentSource>(DomainErrorCode.StorageUnavailable);
        }
    }

    private async ValueTask<DomainResult<ContentCatalogCounts>> ReadExistingCountsAsync(
        SourceId sourceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var catalog = new SqliteContentCatalog(_databasePath);
            return DomainResult.Success(await catalog.ReadCountsAsync(
                sourceId,
                cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DomainResult.Failure<ContentCatalogCounts>(
                DomainErrorCode.OperationCancelled);
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            return DomainResult.Failure<ContentCatalogCounts>(
                DomainErrorCode.StorageUnavailable);
        }
    }

    private static DomainErrorCode? FindSuspiciousEmptyReplacement(
        XtreamContentCatalog catalog,
        ContentCatalogCounts existingCounts)
    {
        if (existingCounts.LiveTvCount > 0 && IsSuspiciousEmpty(catalog.LiveStreams))
        {
            return DomainErrorCode.XtreamLiveCatalogResponseUnsupported;
        }

        if (existingCounts.MovieCount > 0 && IsSuspiciousEmpty(catalog.Movies))
        {
            return DomainErrorCode.XtreamMovieCatalogResponseUnsupported;
        }

        if (existingCounts.SeriesCount > 0 && IsSuspiciousEmpty(catalog.Series))
        {
            return DomainErrorCode.XtreamSeriesCatalogResponseUnsupported;
        }

        return null;
    }

    private static bool IsSuspiciousEmpty<T>(XtreamProviderPage<T> page) =>
        page.Items.Count == 0 &&
        (page.IsCompatibilityEmptySentinel || page.SkippedItemCount > 0);

    private static DomainResult<LiveCatalogMapping> MapLiveCatalog(
        SourceId sourceId,
        SnapshotId snapshotId,
        XtreamContentCatalog catalog)
    {
        var categories = new List<ChannelCategory>();
        var categoryMap = new Dictionary<string, CategoryId>(StringComparer.Ordinal);
        int sortOrder = 0;
        foreach (XtreamCategoryInput input in catalog.LiveCategories.Items)
        {
            if (input.ContentKind != ContentKind.LiveTv)
            {
                return DomainResult.Failure<LiveCatalogMapping>(
                    DomainErrorCode.DomainInvariantViolation);
            }

            CategoryId categoryId = CategoryId.Generate();
            DomainResult<ChannelCategory> category = ChannelCategory.Create(
                categoryId,
                snapshotId,
                BuildCategoryStableKey(ContentKind.LiveTv, input.ProviderIdentifier),
                input.Name,
                sortOrder++,
                false);
            if (!category.IsSuccess || !categoryMap.TryAdd(input.ProviderIdentifier, categoryId))
            {
                return DomainResult.Failure<LiveCatalogMapping>(
                    DomainErrorCode.DomainInvariantViolation);
            }

            categories.Add(category.Value!);
        }

        ChannelCategory? fallbackCategory = null;
        var channels = new List<LiveChannel>(catalog.LiveStreams.Items.Count);
        foreach (XtreamStreamInput input in catalog.LiveStreams.Items)
        {
            CategoryId categoryId = default;
            bool hasCategory = input.CategoryIdentifier is not null &&
                categoryMap.TryGetValue(input.CategoryIdentifier, out categoryId);
            if (!hasCategory)
            {
                if (fallbackCategory is null)
                {
                    DomainResult<ChannelCategory> created = ChannelCategory.Create(
                        CategoryId.Generate(),
                        snapshotId,
                        null,
                        "Uncategorized",
                        sortOrder,
                        true);
                    if (!created.IsSuccess)
                    {
                        return DomainResult.Failure<LiveCatalogMapping>(created.Error!);
                    }

                    fallbackCategory = created.Value!;
                    categories.Add(fallbackCategory);
                }

                categoryId = fallbackCategory.Id;
            }

            DomainResult<ChannelStableKey> stableKey = ChannelStableKeyBuilder.FromProviderStreamId(
                sourceId,
                "xtream-live",
                input.ProviderPlaybackKey.Value);
            DomainResult<LiveChannel> channel = stableKey.IsSuccess
                ? LiveChannel.Create(
                    ChannelId.Generate(),
                    stableKey.Value,
                    snapshotId,
                    categoryId,
                    input.ProviderPlaybackKey.Value,
                    input.ProviderPlaybackKey,
                    input.Name,
                    input.Number,
                    null,
                    null,
                    ParseContainer(input.ContainerExtension),
                    input.IsAdultHint,
                    hasCategory
                        ? ChannelNormalizationWarnings.None
                        : ChannelNormalizationWarnings.MissingGroup)
                : DomainResult.Failure<LiveChannel>(stableKey.Error!);
            if (!channel.IsSuccess)
            {
                return DomainResult.Failure<LiveCatalogMapping>(channel.Error!);
            }

            channels.Add(channel.Value!);
        }

        return DomainResult.Success(new LiveCatalogMapping(categories, channels));
    }

    private static ChannelContainerHint? ParseContainer(string? value) => value?.ToLowerInvariant() switch
    {
        "m3u8" => ChannelContainerHint.Hls,
        "ts" => ChannelContainerHint.MpegTs,
        _ => null,
    };

    private static string ComputeContentHash(XtreamContentCatalog catalog)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "XTREAM-CONTENT-SNAPSHOT-V1");
        foreach (XtreamCategoryInput item in catalog.LiveCategories.Items
                     .Concat(catalog.MovieCategories.Items)
                     .Concat(catalog.SeriesCategories.Items))
        {
            Append(hash, item.ContentKind.ToString());
            Append(hash, item.ProviderIdentifier);
            Append(hash, item.Name);
        }

        foreach (XtreamStreamInput item in catalog.LiveStreams.Items)
        {
            Append(hash, "live");
            Append(hash, item.ProviderPlaybackKey.Value);
            Append(hash, item.Name);
        }

        foreach (XtreamMovieInput item in catalog.Movies.Items)
        {
            Append(hash, "movie");
            Append(hash, item.ProviderPlaybackKey.Value);
            Append(hash, item.Name);
        }

        foreach (XtreamSeriesInput item in catalog.Series.Items)
        {
            Append(hash, "series");
            Append(hash, item.ProviderKey.Value);
            Append(hash, item.Name);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string BuildCategoryStableKey(ContentKind kind, string providerIdentifier)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(providerIdentifier);
        try
        {
            return $"xtream:{kind.ToString().ToLowerInvariant()}:{Convert.ToHexString(SHA256.HashData(bytes))}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Id(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    private sealed record LiveCatalogMapping(
        IReadOnlyList<ChannelCategory> Categories,
        IReadOnlyList<LiveChannel> Channels);
}
