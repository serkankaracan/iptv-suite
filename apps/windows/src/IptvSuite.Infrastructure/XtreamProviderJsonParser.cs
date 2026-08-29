using System.Globalization;
using System.Text.Json;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.Infrastructure;

public static class XtreamProviderJsonParser
{
    public const int MaximumCategoryCount = 10_000;
    public const int MaximumStreamCount = 50_000;
    public const int MaximumSeasonCount = 100;
    public const int MaximumEpisodeCount = 5_000;

    public static DomainResult<XtreamAccountStatus> ParseAccountStatus(ReadOnlyMemory<byte> content)
    {
        try
        {
            using JsonDocument document = ParseDocument(
                content,
                XtreamTransportLimits.MaximumAccountResponseBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("user_info", out JsonElement userInfo) ||
                userInfo.ValueKind != JsonValueKind.Object ||
                !TryGetBoolean(userInfo, "auth", out bool authenticated))
            {
                return DomainResult.Failure<XtreamAccountStatus>(
                    DomainErrorCode.UnsupportedPlaylistFormat);
            }

            return authenticated
                ? DomainResult.Success(new XtreamAccountStatus(true))
                : DomainResult.Failure<XtreamAccountStatus>(DomainErrorCode.AuthenticationRejected);
        }
        catch (JsonException)
        {
            return DomainResult.Failure<XtreamAccountStatus>(
                DomainErrorCode.UnsupportedPlaylistFormat);
        }
    }

    public static DomainResult<XtreamProviderPage<XtreamCategoryInput>> ParseCategories(
        ReadOnlyMemory<byte> content) => ParseCategories(content, ContentKind.LiveTv);

    public static DomainResult<XtreamProviderPage<XtreamCategoryInput>> ParseCategories(
        ReadOnlyMemory<byte> content,
        ContentKind contentKind)
    {
        if (contentKind is not (ContentKind.LiveTv or ContentKind.Movie or ContentKind.Series))
        {
            return DomainResult.Failure<XtreamProviderPage<XtreamCategoryInput>>(
                DomainErrorCode.DomainInvariantViolation);
        }

        try
        {
            using JsonDocument document = ParseDocument(
                content,
                XtreamTransportLimits.MaximumCategoryResponseBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > MaximumCategoryCount)
            {
                return DomainResult.Failure<XtreamProviderPage<XtreamCategoryInput>>(
                    DomainErrorCode.UnsupportedPlaylistFormat);
            }

            List<XtreamCategoryInput> items = [];
            HashSet<string> identifiers = new(StringComparer.Ordinal);
            int skipped = 0;
            int duplicates = 0;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !TryGetBoundedScalar(element, "category_id", 512, out string? identifier) ||
                    !TryGetBoundedScalar(element, "category_name", 256, out string? name))
                {
                    skipped++;
                    continue;
                }

                if (!identifiers.Add(identifier!))
                {
                    duplicates++;
                    continue;
                }

                items.Add(new XtreamCategoryInput(identifier!, name!, contentKind));
            }

            return DomainResult.Success<XtreamProviderPage<XtreamCategoryInput>>(
                new XtreamProviderPage<XtreamCategoryInput>(items, skipped, duplicates));
        }
        catch (JsonException)
        {
            return DomainResult.Failure<XtreamProviderPage<XtreamCategoryInput>>(
                DomainErrorCode.UnsupportedPlaylistFormat);
        }
    }

    public static DomainResult<XtreamProviderPage<XtreamStreamInput>> ParseLiveStreams(
        ReadOnlyMemory<byte> content)
    {
        try
        {
            using JsonDocument document = ParseDocument(
                content,
                XtreamTransportLimits.MaximumCatalogResponseBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > MaximumStreamCount)
            {
                return DomainResult.Failure<XtreamProviderPage<XtreamStreamInput>>(
                    DomainErrorCode.UnsupportedPlaylistFormat);
            }

            List<XtreamStreamInput> items = [];
            HashSet<string> identifiers = new(StringComparer.Ordinal);
            int skipped = 0;
            int duplicates = 0;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !TryGetBoundedScalar(element, "stream_id", 512, out string? identifier) ||
                    !TryGetBoundedScalar(element, "name", 256, out string? name))
                {
                    skipped++;
                    continue;
                }

                if (!identifiers.Add(identifier!))
                {
                    duplicates++;
                    continue;
                }

                _ = TryGetOptionalBoundedScalar(element, "category_id", 512, out string? categoryIdentifier);
                _ = TryGetOptionalBoundedScalar(element, "container_extension", 32, out string? container);
                int? number = TryGetInt32(element, "num", out int parsedNumber) && parsedNumber > 0
                    ? parsedNumber
                    : null;
                bool? isAdult = TryGetBoolean(element, "is_adult", out bool parsedAdult)
                    ? parsedAdult
                    : null;
                DomainResult<ProviderItemKey> playbackKey = ProviderItemKey.Create(identifier);
                if (!playbackKey.IsSuccess)
                {
                    skipped++;
                    continue;
                }

                items.Add(new XtreamStreamInput(
                    playbackKey.Value!,
                    name!,
                    categoryIdentifier,
                    number,
                    container,
                    isAdult));
            }

            return DomainResult.Success<XtreamProviderPage<XtreamStreamInput>>(
                new XtreamProviderPage<XtreamStreamInput>(items, skipped, duplicates));
        }
        catch (JsonException)
        {
            return DomainResult.Failure<XtreamProviderPage<XtreamStreamInput>>(
                DomainErrorCode.UnsupportedPlaylistFormat);
        }
    }

    public static DomainResult<XtreamProviderPage<XtreamMovieInput>> ParseMovies(
        ReadOnlyMemory<byte> content)
    {
        try
        {
            using JsonDocument document = ParseDocument(
                content,
                XtreamTransportLimits.MaximumCatalogResponseBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > MaximumStreamCount)
            {
                return DomainResult.Failure<XtreamProviderPage<XtreamMovieInput>>(
                    DomainErrorCode.UnsupportedPlaylistFormat);
            }

            List<XtreamMovieInput> items = [];
            HashSet<string> identifiers = new(StringComparer.Ordinal);
            int skipped = 0;
            int duplicates = 0;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !TryGetBoundedScalar(element, "stream_id", 512, out string? identifier) ||
                    !TryGetBoundedScalar(element, "name", 256, out string? name))
                {
                    skipped++;
                    continue;
                }

                if (!identifiers.Add(identifier!))
                {
                    duplicates++;
                    continue;
                }

                _ = TryGetOptionalBoundedScalar(element, "category_id", 512, out string? categoryIdentifier);
                _ = TryGetOptionalBoundedScalar(element, "container_extension", 32, out string? container);
                bool? isAdult = TryGetBoolean(element, "is_adult", out bool parsedAdult)
                    ? parsedAdult
                    : null;
                DomainResult<ProviderItemKey> playbackKey = ProviderItemKey.Create(identifier);
                if (!playbackKey.IsSuccess)
                {
                    skipped++;
                    continue;
                }

                items.Add(new XtreamMovieInput(
                    playbackKey.Value!,
                    name!,
                    categoryIdentifier,
                    container,
                    isAdult));
            }

            return DomainResult.Success<XtreamProviderPage<XtreamMovieInput>>(
                new XtreamProviderPage<XtreamMovieInput>(items, skipped, duplicates));
        }
        catch (JsonException)
        {
            return DomainResult.Failure<XtreamProviderPage<XtreamMovieInput>>(
                DomainErrorCode.UnsupportedPlaylistFormat);
        }
    }

    public static DomainResult<XtreamProviderPage<XtreamSeriesInput>> ParseSeries(
        ReadOnlyMemory<byte> content)
    {
        try
        {
            using JsonDocument document = ParseDocument(
                content,
                XtreamTransportLimits.MaximumCatalogResponseBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > MaximumStreamCount)
            {
                return DomainResult.Failure<XtreamProviderPage<XtreamSeriesInput>>(
                    DomainErrorCode.UnsupportedPlaylistFormat);
            }

            List<XtreamSeriesInput> items = [];
            HashSet<string> identifiers = new(StringComparer.Ordinal);
            int skipped = 0;
            int duplicates = 0;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !TryGetBoundedScalar(element, "series_id", 512, out string? identifier) ||
                    !TryGetBoundedScalar(element, "name", 256, out string? name))
                {
                    skipped++;
                    continue;
                }

                if (!identifiers.Add(identifier!))
                {
                    duplicates++;
                    continue;
                }

                _ = TryGetOptionalBoundedScalar(element, "category_id", 512, out string? categoryIdentifier);
                bool? isAdult = TryGetBoolean(element, "is_adult", out bool parsedAdult)
                    ? parsedAdult
                    : null;
                DomainResult<ProviderItemKey> providerKey = ProviderItemKey.Create(identifier);
                if (!providerKey.IsSuccess)
                {
                    skipped++;
                    continue;
                }

                items.Add(new XtreamSeriesInput(
                    providerKey.Value!,
                    name!,
                    categoryIdentifier,
                    isAdult));
            }

            return DomainResult.Success<XtreamProviderPage<XtreamSeriesInput>>(
                new XtreamProviderPage<XtreamSeriesInput>(items, skipped, duplicates));
        }
        catch (JsonException)
        {
            return DomainResult.Failure<XtreamProviderPage<XtreamSeriesInput>>(
                DomainErrorCode.UnsupportedPlaylistFormat);
        }
    }

    public static DomainResult<XtreamSeriesDetails> ParseSeriesDetails(ReadOnlyMemory<byte> content)
    {
        try
        {
            using JsonDocument document = ParseDocument(
                content,
                XtreamTransportLimits.MaximumSeriesDetailsResponseBytes);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("seasons", out JsonElement seasonsElement) ||
                seasonsElement.ValueKind != JsonValueKind.Array ||
                seasonsElement.GetArrayLength() > MaximumSeasonCount ||
                !root.TryGetProperty("episodes", out JsonElement episodesElement) ||
                episodesElement.ValueKind != JsonValueKind.Object)
            {
                return DomainResult.Failure<XtreamSeriesDetails>(
                    DomainErrorCode.UnsupportedPlaylistFormat);
            }

            var seasons = new List<XtreamSeasonInput>();
            var seasonNumbers = new HashSet<int>();
            foreach (JsonElement element in seasonsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !TryGetInt32(element, "season_number", out int number) ||
                    number < 0 || !seasonNumbers.Add(number))
                {
                    continue;
                }

                _ = TryGetOptionalBoundedScalar(element, "id", 512, out string? identifier);
                _ = TryGetOptionalBoundedScalar(element, "name", 256, out string? name);
                ProviderItemKey? providerKey = identifier is null
                    ? null
                    : ProviderItemKey.Create(identifier).Value;
                seasons.Add(new XtreamSeasonInput(
                    providerKey,
                    number,
                    name ?? $"Season {number.ToString(CultureInfo.InvariantCulture)}"));
            }

            var episodes = new List<XtreamEpisodeInput>();
            var episodeKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty seasonProperty in episodesElement.EnumerateObject())
            {
                if (!int.TryParse(
                        seasonProperty.Name,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int seasonNumber) ||
                    seasonNumber < 0 || seasonProperty.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement element in seasonProperty.Value.EnumerateArray())
                {
                    if (episodes.Count == MaximumEpisodeCount)
                    {
                        return DomainResult.Failure<XtreamSeriesDetails>(
                            DomainErrorCode.UnsupportedPlaylistFormat);
                    }

                    if (element.ValueKind != JsonValueKind.Object ||
                        !TryGetBoundedScalar(element, "id", 512, out string? identifier) ||
                        !TryGetInt32(element, "episode_num", out int episodeNumber) ||
                        episodeNumber <= 0 || !episodeKeys.Add(identifier!))
                    {
                        continue;
                    }

                    _ = TryGetOptionalBoundedScalar(element, "title", 256, out string? title);
                    _ = TryGetOptionalBoundedScalar(
                        element,
                        "container_extension",
                        32,
                        out string? container);
                    DomainResult<ProviderItemKey> playbackKey = ProviderItemKey.Create(identifier);
                    if (!playbackKey.IsSuccess)
                    {
                        continue;
                    }

                    episodes.Add(new XtreamEpisodeInput(
                        playbackKey.Value!,
                        seasonNumber,
                        episodeNumber,
                        title ?? $"Episode {episodeNumber.ToString(CultureInfo.InvariantCulture)}",
                        container,
                        TryGetDuration(element)));
                }
            }

            return DomainResult.Success(new XtreamSeriesDetails(seasons, episodes));
        }
        catch (JsonException)
        {
            return DomainResult.Failure<XtreamSeriesDetails>(
                DomainErrorCode.UnsupportedPlaylistFormat);
        }
    }

    private static TimeSpan? TryGetDuration(JsonElement episode)
    {
        if (!episode.TryGetProperty("info", out JsonElement info) ||
            info.ValueKind != JsonValueKind.Object ||
            !TryGetInt32(info, "duration_secs", out int seconds) ||
            seconds <= 0 || seconds > (int)TimeSpan.FromDays(2).TotalSeconds)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static JsonDocument ParseDocument(
        ReadOnlyMemory<byte> content,
        int maximumResponseBytes)
    {
        if (content.IsEmpty || content.Length > maximumResponseBytes)
        {
            throw new JsonException("The provider document is outside the bounded input contract.");
        }

        return JsonDocument.Parse(content, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
    }

    private static bool TryGetBoundedScalar(
        JsonElement element,
        string propertyName,
        int maximumLength,
        out string? value)
    {
        value = null;
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            TryNormalizeScalar(property, maximumLength, out value);
    }

    private static bool TryGetOptionalBoundedScalar(
        JsonElement element,
        string propertyName,
        int maximumLength,
        out string? value)
    {
        value = null;
        return !element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            TryNormalizeScalar(property, maximumLength, out value);
    }

    private static bool TryNormalizeScalar(JsonElement element, int maximumLength, out string? value)
    {
        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => null,
        };
        value = value?.Normalize().Trim();
        return !string.IsNullOrEmpty(value) && value.Length <= maximumLength &&
            !value.Any(char.IsControl);
    }

    private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                property.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value),
            _ => false,
        };
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName, out bool value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        string? scalar = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
        if (string.Equals(scalar, "1", StringComparison.Ordinal) ||
            string.Equals(scalar, "true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        return string.Equals(scalar, "0", StringComparison.Ordinal) ||
            string.Equals(scalar, "false", StringComparison.OrdinalIgnoreCase);
    }
}
