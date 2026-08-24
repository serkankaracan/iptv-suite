using System.Security.Cryptography;
using System.Text;
using IptvSuite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IptvSuite.CatalogUiAcceptanceHarness;

internal static class Program
{
    private const int RequiredChannelCount = 50_000;

    private static async Task<int> Main(string[] args)
    {
        if (args is not ["seed", string databasePath, "50000"])
        {
            return 2;
        }

        int stage = 10;
        try
        {
            stage = 11;
            string path = ValidateDatabasePath(databasePath);
            stage = 12;
            var query = new SqliteCatalogQuery(path);
            if ((await query.ReadSourcesAsync().ConfigureAwait(false)).Count != 0)
            {
                return 3;
            }

            stage = 13;
            await SeedAsync(path).ConfigureAwait(false);
            stage = 14;
            IReadOnlyList<IptvSuite.Application.CatalogSourceItem> sources =
                await query.ReadSourcesAsync().ConfigureAwait(false);
            if (sources.Count != 1)
            {
                return 4;
            }

            IptvSuite.Application.CatalogChannelPage page = await query.ReadChannelsAsync(
                sources[0].SourceId,
                null,
                null,
                0,
                200).ConfigureAwait(false);
            return page.TotalCount == RequiredChannelCount && page.Items.Count == 200 ? 0 : 5;
        }
        catch (SqliteException exception)
        {
            return 20 + exception.SqliteErrorCode;
        }
        catch
        {
            return stage;
        }
    }

    private static string ValidateDatabasePath(string value)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("An absolute database path is required.", nameof(value));
        }

        string path = Path.GetFullPath(value);
        DirectoryInfo? v2 = Directory.GetParent(path);
        DirectoryInfo? catalog = v2?.Parent;
        if (!string.Equals(Path.GetFileName(path), "catalog.db", StringComparison.Ordinal) ||
            !string.Equals(v2?.Name, "v2", StringComparison.Ordinal) ||
            !string.Equals(catalog?.Name, "Catalog", StringComparison.Ordinal) ||
            File.Exists(path))
        {
            throw new IOException("The disposable catalog path is invalid.");
        }

        for (DirectoryInfo? current = catalog; current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The disposable catalog path cannot contain a reparse point.");
            }
        }

        Directory.CreateDirectory(v2!.FullName);
        if ((new DirectoryInfo(v2.FullName).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The disposable catalog directory cannot be a reparse point.");
        }

        return path;
    }

    private static async Task SeedAsync(string path)
    {
        string source = StableId("source", 0);
        string snapshot = StableId("snapshot", 0);
        string category = StableId("category", 0);
        string now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync().ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, """
            INSERT INTO sources(source_id, configuration_id, source_kind, display_name, endpoint_scheme,
                endpoint_host, endpoint_port, configuration_reference, status, active_snapshot_id,
                created_utc, updated_utc, last_error_code)
            VALUES ($source, $configuration, 1, 'Synthetic 50k source', 'https', 'synthetic.invalid', 443,
                $reference, $ready, $snapshot, $now, $now, NULL);
            """, ("$source", source), ("$configuration", StableId("configuration", 0)),
            ("$reference", $"secret-ref-v1:{StableId("reference", 0)}"),
            ("$ready", (int)IptvSuite.Domain.ContentSourceStatus.Ready),
            ("$snapshot", snapshot), ("$now", now));
        await ExecuteAsync(connection, transaction, """
            INSERT INTO snapshots(snapshot_id, source_id, retrieved_utc, content_hash, http_etag,
                http_last_modified_utc, parser_version, normalization_version, schema_version,
                item_count, warning_count, state, cache_key)
            VALUES ($snapshot, $source, $now, $hash, NULL, NULL, 1, 1, 1, 50000, 0, 1, NULL);
            """, ("$snapshot", snapshot), ("$source", source), ("$now", now), ("$hash", new byte[32]));
        await ExecuteAsync(connection, transaction, """
            INSERT INTO categories(category_id, snapshot_id, stable_key, display_name, sort_order)
            VALUES ($category, $snapshot, 'category:all', 'All synthetic channels', 0);
            """, ("$category", category), ("$snapshot", snapshot));

        await using SqliteCommand channel = connection.CreateCommand();
        channel.Transaction = transaction;
        channel.CommandText = """
            INSERT INTO channels(channel_id, snapshot_id, category_id, stable_key_version, stable_key,
                display_name, channel_number, stream_reference, logo_reference, provider_item_kind,
                provider_item_id, container_hint, is_adult, warning_flags)
            VALUES ($id, $snapshot, $category, 1, $stable, $name, $number, NULL, NULL, 1,
                $provider, NULL, 0, 0);
            """;
        SqliteParameter id = channel.Parameters.Add("$id", SqliteType.Text);
        channel.Parameters.AddWithValue("$snapshot", snapshot);
        channel.Parameters.AddWithValue("$category", category);
        SqliteParameter stable = channel.Parameters.Add("$stable", SqliteType.Text);
        SqliteParameter name = channel.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter number = channel.Parameters.Add("$number", SqliteType.Integer);
        SqliteParameter provider = channel.Parameters.Add("$provider", SqliteType.Text);
        channel.Prepare();
        for (int ordinal = 0; ordinal < RequiredChannelCount; ordinal++)
        {
            string token = ordinal.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
            id.Value = StableId("channel", ordinal);
            stable.Value = $"synthetic:{token}";
            name.Value = $"Synthetic channel {token}";
            number.Value = ordinal + 1;
            provider.Value = $"item-{token}";
            await channel.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string StableId(string domain, int ordinal)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"iptvsuite-m9-ui:{domain}:{ordinal}"));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
