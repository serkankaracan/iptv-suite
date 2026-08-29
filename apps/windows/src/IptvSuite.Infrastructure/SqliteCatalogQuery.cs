using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

public sealed class SqliteCatalogQuery : ICatalogBrowser
{
    public const int MaximumPageSize = 200;
    public const int MaximumCategoryCount = 2_000;
    public const int MaximumSourceCount = 100;
    public const int MaximumSearchLength = 100;
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;

    public SqliteCatalogQuery(string databasePath)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public async ValueTask<IReadOnlyList<CatalogSourceItem>> ReadSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, display_name, endpoint_scheme
            FROM sources
            WHERE status = $ready
              AND active_snapshot_id IS NOT NULL
            ORDER BY display_name COLLATE NOCASE, source_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$limit", MaximumSourceCount + 1);
        var rows = new List<CatalogSourceItem>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == MaximumSourceCount)
            {
                throw new InvalidDataException("Catalog source count exceeds the supported limit.");
            }

            if (!Guid.TryParseExact(reader.GetString(0), "N", out Guid sourceGuid))
            {
                throw new InvalidDataException("Catalog identifier is invalid.");
            }

            DomainResult<SourceId> sourceId = SourceId.Create(sourceGuid);
            if (!sourceId.IsSuccess)
            {
                throw new InvalidDataException("Catalog identifier is invalid.");
            }

            if (reader.IsDBNull(2))
            {
                throw new InvalidDataException("Catalog endpoint scheme is invalid.");
            }

            string endpointScheme = reader.GetString(2);
            if (!string.Equals(endpointScheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
                !string.Equals(endpointScheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Catalog endpoint scheme is invalid.");
            }

            rows.Add(new(
                sourceId.Value,
                reader.GetString(1),
                UsesInsecureHttp: string.Equals(
                    endpointScheme,
                    Uri.UriSchemeHttp,
                    StringComparison.Ordinal)));
        }

        return rows;
    }

    public async ValueTask<IReadOnlyList<CatalogCategoryItem>> ReadCategoriesAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A source identifier is required.", nameof(sourceId));
        }

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.category_id, c.display_name, c.sort_order
            FROM categories AS c
            JOIN sources AS s ON s.active_snapshot_id = c.snapshot_id
            WHERE s.source_id = $source
              AND s.status = $ready
            ORDER BY c.sort_order, c.display_name COLLATE NOCASE, c.category_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N"));
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$limit", MaximumCategoryCount + 1);
        var rows = new List<CatalogCategoryItem>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == MaximumCategoryCount)
            {
                throw new InvalidDataException("Catalog category count exceeds the supported limit.");
            }

            rows.Add(new(ParseCategoryId(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2)));
        }

        return rows;
    }

    public async ValueTask<CatalogChannelPage> ReadChannelsAsync(
        SourceId sourceId,
        CategoryId? categoryId,
        string? searchText,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty || offset < 0 || limit is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A bounded catalog page is required.");
        }

        if (categoryId?.IsEmpty == true)
        {
            throw new ArgumentException("A non-empty category identifier is required.", nameof(categoryId));
        }

        string? search = NormalizeSearch(searchText);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand countCommand = connection.CreateCommand();
        countCommand.Transaction = transaction;
        countCommand.CommandText = """
            SELECT count(*)
            FROM channels AS c
            JOIN sources AS s ON s.active_snapshot_id = c.snapshot_id
            WHERE s.source_id = $source
              AND s.status = $ready
              AND ($category IS NULL OR c.category_id = $category)
              AND ($search IS NULL OR instr(lower(c.display_name), lower($search)) > 0);
            """;
        AddChannelFilterParameters(countCommand, sourceId, categoryId, search);
        int totalCount = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.channel_id, c.category_id, c.stable_key, c.display_name,
                   c.channel_number, c.is_adult, c.logo_reference IS NOT NULL
            FROM channels AS c
            JOIN sources AS s ON s.active_snapshot_id = c.snapshot_id
            WHERE s.source_id = $source
              AND s.status = $ready
              AND ($category IS NULL OR c.category_id = $category)
              AND ($search IS NULL OR instr(lower(c.display_name), lower($search)) > 0)
            ORDER BY c.display_name COLLATE NOCASE, c.channel_id
            LIMIT $limit OFFSET $offset;
            """;
        AddChannelFilterParameters(command, sourceId, categoryId, search);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var rows = new List<CatalogChannelItem>(limit);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParseExact(reader.GetString(0), "N", out Guid channelGuid) ||
                !Guid.TryParseExact(reader.GetString(1), "N", out Guid categoryGuid))
            {
                throw new InvalidDataException("Catalog identifier is invalid.");
            }

            DomainResult<ChannelId> channelId = ChannelId.Create(channelGuid);
            DomainResult<CategoryId> rowCategoryId = CategoryId.Create(categoryGuid);
            if (!channelId.IsSuccess || !rowCategoryId.IsSuccess)
            {
                throw new InvalidDataException("Catalog identifier is invalid.");
            }

            rows.Add(new(
                channelId.Value,
                rowCategoryId.Value,
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetInt32(5) == 1,
                reader.GetInt32(6) == 1));
        }

        return new CatalogChannelPage(rows, offset, totalCount);
    }

    internal ValueTask<IReadOnlyList<CatalogChannelItem>> ReadChannelPageAsync(
        SourceId sourceId,
        int offset,
        int limit,
        CancellationToken cancellationToken = default) =>
        ReadChannelItemsAsync(sourceId, offset, limit, cancellationToken);

    private async ValueTask<IReadOnlyList<CatalogChannelItem>> ReadChannelItemsAsync(
        SourceId sourceId,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        CatalogChannelPage page = await ReadChannelsAsync(
            sourceId,
            null,
            null,
            offset,
            limit,
            cancellationToken).ConfigureAwait(false);
        return page.Items;
    }

    private async ValueTask<SqliteConnection> OpenReadConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static CategoryId ParseCategoryId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out Guid guid))
        {
            throw new InvalidDataException("Catalog identifier is invalid.");
        }

        DomainResult<CategoryId> result = CategoryId.Create(guid);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidDataException("Catalog identifier is invalid.");
    }

    private static string? NormalizeSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.EnumerateRunes().Count() > MaximumSearchLength ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Search text is invalid.");
        }

        return normalized;
    }

    private static void AddChannelFilterParameters(
        SqliteCommand command,
        SourceId sourceId,
        CategoryId? categoryId,
        string? search)
    {
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N"));
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$category", categoryId.HasValue
            ? categoryId.Value.Value.ToString("N")
            : DBNull.Value);
        command.Parameters.AddWithValue("$search", search ?? (object)DBNull.Value);
    }
}
