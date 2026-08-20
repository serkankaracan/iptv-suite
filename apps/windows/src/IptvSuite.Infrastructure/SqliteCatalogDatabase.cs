using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

internal sealed class SqliteCatalogDatabase
{
    internal const int SchemaVersion = 2;

    private static readonly string[] RequiredTables =
    [
        "catalog_metadata",
        "sources",
        "snapshots",
        "snapshot_keys",
        "categories",
        "channels",
        "protected_locators",
        "favorites",
        "sync_runs",
    ];

    private readonly string _databasePath;

    internal SqliteCatalogDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = ValidateDatabasePath(databasePath);
    }

    internal async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        EnsurePathRemainsSafe();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode = DELETE;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA synchronous = EXTRA;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);

        long version = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version == 0)
        {
            await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (version == 1)
        {
            await MigrateVersionOneAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (version != SchemaVersion)
        {
            throw new InvalidDataException("Catalog schema version is unsupported.");
        }

        await ValidateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateVersionOneAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ValidateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "ALTER TABLE snapshots ADD COLUMN cache_key BLOB NULL CHECK (cache_key IS NULL OR length(cache_key) = 32);",
            cancellationToken,
            transaction).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "CREATE INDEX ix_snapshots_source_cache ON snapshots(source_id, cache_key);",
            cancellationToken,
            transaction).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA user_version = {SchemaVersion};", cancellationToken, transaction)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await ValidateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, SchemaSql, cancellationToken, transaction).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA user_version = {SchemaVersion};", cancellationToken, transaction)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await ValidateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        var actual = new HashSet<string>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actual.Add(reader.GetString(0));
        }

        if (RequiredTables.Any(table => !actual.Contains(table)))
        {
            throw new InvalidDataException("Catalog schema is incomplete.");
        }
    }

    private static async Task<long> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateDatabasePath(string databasePath)
    {
        string fullPath = Path.GetFullPath(databasePath);
        if (!Path.IsPathFullyQualified(databasePath) ||
            !string.Equals(Path.GetExtension(fullPath), ".db", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Catalog database path must be an absolute .db file.", nameof(databasePath));
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("Catalog database parent directory does not exist.");
        }

        EnsureNoReparsePoints(parent);
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Catalog database path cannot be a reparse point.");
        }

        return fullPath;
    }

    private void EnsurePathRemainsSafe()
    {
        string parent = Path.GetDirectoryName(_databasePath)!;
        EnsureNoReparsePoints(parent);
        if (File.Exists(_databasePath) && (File.GetAttributes(_databasePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Catalog database path cannot be a reparse point.");
        }
    }

    private static void EnsureNoReparsePoints(string directoryPath)
    {
        var current = new DirectoryInfo(directoryPath);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Catalog database directory cannot contain a reparse point.");
            }

            current = current.Parent;
        }
    }

    private const string SchemaSql = """
        CREATE TABLE catalog_metadata (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
            created_utc TEXT NOT NULL,
            schema_name TEXT NOT NULL CHECK (schema_name = 'catalog-v1')
        ) STRICT;
        INSERT INTO catalog_metadata(singleton, created_utc, schema_name)
        VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'catalog-v1');

        CREATE TABLE sources (
            source_id TEXT NOT NULL PRIMARY KEY CHECK (length(source_id) = 32),
            configuration_id TEXT NOT NULL CHECK (length(configuration_id) = 32),
            source_kind INTEGER NOT NULL,
            display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 256),
            endpoint_scheme TEXT NULL,
            endpoint_host TEXT NULL,
            endpoint_port INTEGER NULL CHECK (endpoint_port IS NULL OR endpoint_port BETWEEN 1 AND 65535),
            configuration_reference TEXT NOT NULL CHECK (length(configuration_reference) IN (46, 47)),
            status INTEGER NOT NULL,
            active_snapshot_id TEXT NULL CHECK (active_snapshot_id IS NULL OR length(active_snapshot_id) = 32),
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            last_error_code INTEGER NULL
        ) STRICT;

        CREATE TABLE snapshots (
            snapshot_id TEXT NOT NULL PRIMARY KEY CHECK (length(snapshot_id) = 32),
            source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE CASCADE,
            retrieved_utc TEXT NOT NULL,
            content_hash BLOB NOT NULL CHECK (length(content_hash) = 32),
            http_etag TEXT NULL CHECK (http_etag IS NULL OR length(http_etag) <= 512),
            http_last_modified_utc TEXT NULL,
            parser_version INTEGER NOT NULL,
            normalization_version INTEGER NOT NULL,
            schema_version INTEGER NOT NULL,
            item_count INTEGER NOT NULL CHECK (item_count >= 0),
            warning_count INTEGER NOT NULL CHECK (warning_count >= 0),
            state INTEGER NOT NULL,
            cache_key BLOB NULL CHECK (cache_key IS NULL OR length(cache_key) = 32)
        ) STRICT;

        CREATE TABLE snapshot_keys (
            snapshot_id TEXT NOT NULL PRIMARY KEY REFERENCES snapshots(snapshot_id) ON DELETE CASCADE,
            key_generation_id TEXT NOT NULL UNIQUE CHECK (length(key_generation_id) = 32),
            wrapped_dek BLOB NULL,
            key_state INTEGER NOT NULL,
            CHECK ((key_state = 2 AND wrapped_dek IS NULL) OR (key_state <> 2 AND wrapped_dek IS NOT NULL))
        ) STRICT;

        CREATE TABLE categories (
            category_id TEXT NOT NULL PRIMARY KEY CHECK (length(category_id) = 32),
            snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id) ON DELETE CASCADE,
            stable_key TEXT NOT NULL CHECK (length(stable_key) BETWEEN 1 AND 256),
            display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 256),
            sort_order INTEGER NOT NULL,
            UNIQUE(snapshot_id, stable_key)
        ) STRICT;

        CREATE TABLE channels (
            channel_id TEXT NOT NULL PRIMARY KEY CHECK (length(channel_id) = 32),
            snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id) ON DELETE CASCADE,
            category_id TEXT NULL REFERENCES categories(category_id) ON DELETE SET NULL,
            stable_key_version INTEGER NOT NULL,
            stable_key TEXT NOT NULL CHECK (length(stable_key) BETWEEN 1 AND 512),
            display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 512),
            channel_number INTEGER NULL CHECK (channel_number IS NULL OR channel_number >= 0),
            stream_reference TEXT NULL CHECK (stream_reference IS NULL OR length(stream_reference) = 47),
            logo_reference TEXT NULL CHECK (logo_reference IS NULL OR length(logo_reference) = 47),
            provider_item_kind INTEGER NULL,
            provider_item_id TEXT NULL CHECK (provider_item_id IS NULL OR length(provider_item_id) BETWEEN 1 AND 512),
            container_hint TEXT NULL CHECK (container_hint IS NULL OR length(container_hint) <= 32),
            is_adult INTEGER NOT NULL CHECK (is_adult IN (0, 1)),
            warning_flags INTEGER NOT NULL,
            UNIQUE(snapshot_id, stable_key_version, stable_key),
            CHECK ((stream_reference IS NOT NULL) <> (provider_item_kind IS NOT NULL AND provider_item_id IS NOT NULL))
        ) STRICT;

        CREATE TABLE protected_locators (
            locator_reference TEXT NOT NULL PRIMARY KEY CHECK (length(locator_reference) = 47),
            snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id) ON DELETE CASCADE,
            key_generation_id TEXT NOT NULL REFERENCES snapshot_keys(key_generation_id),
            owner_kind INTEGER NOT NULL,
            owner_id TEXT NOT NULL CHECK (length(owner_id) = 32),
            purpose INTEGER NOT NULL,
            nonce BLOB NOT NULL CHECK (length(nonce) = 12),
            authentication_tag BLOB NOT NULL CHECK (length(authentication_tag) = 16),
            ciphertext BLOB NOT NULL CHECK (length(ciphertext) BETWEEN 1 AND 65536),
            UNIQUE(snapshot_id, owner_kind, owner_id, purpose)
        ) STRICT;

        CREATE TABLE favorites (
            source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE CASCADE,
            stable_key_version INTEGER NOT NULL,
            stable_key TEXT NOT NULL CHECK (length(stable_key) BETWEEN 1 AND 512),
            created_utc TEXT NOT NULL,
            PRIMARY KEY(source_id, stable_key_version, stable_key)
        ) WITHOUT ROWID, STRICT;

        CREATE TABLE sync_runs (
            sync_run_id TEXT NOT NULL PRIMARY KEY CHECK (length(sync_run_id) = 32),
            source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE CASCADE,
            started_utc TEXT NOT NULL,
            completed_utc TEXT NULL,
            result_code INTEGER NULL,
            parsed_count INTEGER NOT NULL DEFAULT 0 CHECK (parsed_count >= 0),
            persisted_count INTEGER NOT NULL DEFAULT 0 CHECK (persisted_count >= 0),
            warning_count INTEGER NOT NULL DEFAULT 0 CHECK (warning_count >= 0),
            failure_code INTEGER NULL
        ) STRICT;

        CREATE INDEX ix_sources_status ON sources(status);
        CREATE INDEX ix_snapshots_source_state ON snapshots(source_id, state, retrieved_utc DESC);
        CREATE INDEX ix_snapshots_source_cache ON snapshots(source_id, cache_key);
        CREATE INDEX ix_categories_snapshot_sort ON categories(snapshot_id, sort_order, category_id);
        CREATE INDEX ix_channels_snapshot_category ON channels(snapshot_id, category_id, display_name, channel_id);
        CREATE INDEX ix_channels_snapshot_number ON channels(snapshot_id, channel_number, channel_id);
        CREATE INDEX ix_locators_snapshot_owner ON protected_locators(snapshot_id, owner_kind, owner_id);
        CREATE INDEX ix_sync_runs_source_started ON sync_runs(source_id, started_utc DESC);
        """;
}
