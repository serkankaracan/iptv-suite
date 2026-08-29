using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

internal sealed class SqliteCatalogDatabase
{
    internal const int SchemaVersion = 7;

    private static readonly string[] LegacyRequiredTables =
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

    private static readonly string[] VersionFourRequiredTables =
    [
        .. LegacyRequiredTables,
        "source_deletion_tombstones",
    ];

    private static readonly string[] VersionFiveRequiredTables =
    [
        .. VersionFourRequiredTables,
        "source_deletion_reconciliation_state",
    ];

    private static readonly string[] VersionSixRequiredTables =
    [
        .. VersionFiveRequiredTables,
        "movies",
        "series",
        "seasons",
        "episodes",
    ];

    private static readonly string[] RequiredTables =
    [
        .. VersionSixRequiredTables,
        "source_configuration_retirements",
    ];

    private static readonly string[] RequiredTriggers =
    [
        "tr_source_deletion_tombstones_reject_delete",
        "tr_source_deletion_tombstones_reject_invalid_insert",
        "tr_source_deletion_tombstones_reject_invalid_update",
        "tr_source_deletion_tombstones_require_authorized_phase",
        "tr_sources_reject_tombstoned_insert",
        "tr_sources_reject_tombstoned_update",
        "tr_sources_require_completed_delete",
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

        if (version == 2)
        {
            await MigrateVersionTwoAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (version == 3)
        {
            await MigrateVersionThreeAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (version == 4)
        {
            await MigrateVersionFourAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (version == 5)
        {
            await MigrateVersionFiveAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (version == 6)
        {
            await MigrateVersionSixAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (version != SchemaVersion)
        {
            throw new InvalidDataException("Catalog schema version is unsupported.");
        }

        await ValidateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateVersionOneAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ValidateLegacySchemaAsync(connection, cancellationToken).ConfigureAwait(false);
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
        await CreateCatalogBrowseIndexesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CreateSourceDeletionBoundaryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CreateContentCatalogTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CreateSourceConfigurationRetirementsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA user_version = {SchemaVersion};", cancellationToken, transaction)
            .ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        await ValidateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateVersionTwoAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ValidateLegacySchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await CreateCatalogBrowseIndexesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CreateSourceDeletionBoundaryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CreateContentCatalogTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CreateSourceConfigurationRetirementsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA user_version = {SchemaVersion};", cancellationToken, transaction)
            .ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        await ValidateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateVersionThreeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ValidateLegacySchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await CreateSourceDeletionBoundaryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CreateContentCatalogTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CreateSourceConfigurationRetirementsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA user_version = {SchemaVersion};", cancellationToken, transaction)
            .ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        await ValidateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateVersionFourAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ValidateSchemaAsync(
            connection,
            VersionFourRequiredTables,
            RequiredTriggers,
            cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await CreateSourceDeletionReconciliationStateAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await CreateContentCatalogTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CreateSourceConfigurationRetirementsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA user_version = {SchemaVersion};", cancellationToken, transaction)
            .ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        await ValidateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateVersionFiveAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ValidateSchemaAsync(
            connection,
            VersionFiveRequiredTables,
            RequiredTriggers,
            cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await CreateContentCatalogTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CreateSourceConfigurationRetirementsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA user_version = {SchemaVersion};", cancellationToken, transaction)
            .ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        await ValidateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateVersionSixAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ValidateSchemaAsync(
            connection,
            VersionSixRequiredTables,
            RequiredTriggers,
            cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await CreateSourceConfigurationRetirementsAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            $"PRAGMA user_version = {SchemaVersion};",
            cancellationToken,
            transaction).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        await ValidateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateCatalogBrowseIndexesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            "CREATE INDEX ix_channels_snapshot_name ON channels(snapshot_id, display_name COLLATE NOCASE, channel_id);",
            cancellationToken,
            transaction).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "CREATE INDEX ix_channels_snapshot_category_name ON channels(snapshot_id, category_id, display_name COLLATE NOCASE, channel_id);",
            cancellationToken,
            transaction).ConfigureAwait(false);
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, SchemaSql, cancellationToken, transaction).ConfigureAwait(false);
        await CreateSourceDeletionBoundaryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CreateContentCatalogTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CreateSourceConfigurationRetirementsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"PRAGMA user_version = {SchemaVersion};", cancellationToken, transaction)
            .ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        await ValidateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static Task ValidateLegacySchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken) =>
        ValidateSchemaAsync(connection, LegacyRequiredTables, [], cancellationToken);

    private static Task ValidateCurrentSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken) =>
        ValidateSchemaAsync(connection, RequiredTables, RequiredTriggers, cancellationToken);

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        IReadOnlyCollection<string> requiredTables,
        IReadOnlyCollection<string> requiredTriggers,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type, name FROM sqlite_master WHERE type IN ('table', 'trigger') ORDER BY type, name;";
        var actualTables = new HashSet<string>(StringComparer.Ordinal);
        var actualTriggers = new HashSet<string>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            HashSet<string> target = string.Equals(reader.GetString(0), "table", StringComparison.Ordinal)
                ? actualTables
                : actualTriggers;
            target.Add(reader.GetString(1));
        }

        if (requiredTables.Any(table => !actualTables.Contains(table)) ||
            requiredTriggers.Any(trigger => !actualTriggers.Contains(trigger)))
        {
            throw new InvalidDataException("Catalog schema is incomplete.");
        }
    }

    private static Task CreateSourceDeletionBoundaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, SourceDeletionBoundarySql, cancellationToken, transaction);

    private static Task CreateSourceDeletionReconciliationStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, SourceDeletionReconciliationStateSql, cancellationToken, transaction);

    private static Task CreateContentCatalogTablesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, ContentCatalogSql, cancellationToken, transaction);

    private static Task CreateSourceConfigurationRetirementsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            SourceConfigurationRetirementSql,
            cancellationToken,
            transaction);

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

    private static readonly string SourceDeletionBoundarySql = $$"""
        CREATE TABLE source_deletion_tombstones (
            source_id TEXT NOT NULL PRIMARY KEY CHECK (length(source_id) = 32),
            configuration_id TEXT NOT NULL CHECK (length(configuration_id) = 32),
            source_kind INTEGER NOT NULL,
            configuration_reference TEXT NOT NULL CHECK (length(configuration_reference) IN (46, 47)),
            protected_delete_completed INTEGER NOT NULL CHECK (protected_delete_completed IN (0, 1)),
            marked_utc TEXT NOT NULL
        ) WITHOUT ROWID, STRICT;

        {{SourceDeletionReconciliationStateSql}}

        INSERT INTO source_deletion_tombstones(
            source_id, configuration_id, source_kind, configuration_reference,
            protected_delete_completed, marked_utc)
        SELECT source_id, configuration_id, source_kind, configuration_reference,
            0, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        FROM sources
        WHERE status = {{(int)ContentSourceStatus.DeletionPending}};

        CREATE TRIGGER tr_source_deletion_tombstones_reject_invalid_insert
        BEFORE INSERT ON source_deletion_tombstones
        WHEN NEW.protected_delete_completed <> 0
            OR EXISTS (
                SELECT 1
                FROM source_deletion_tombstones
                WHERE source_id = NEW.source_id
            )
            OR NOT EXISTS (
                SELECT 1
                FROM sources AS source
                WHERE source.source_id = NEW.source_id
                  AND source.configuration_id = NEW.configuration_id
                  AND source.source_kind = NEW.source_kind
                  AND source.configuration_reference = NEW.configuration_reference
            )
        BEGIN
            SELECT RAISE(IGNORE);
        END;

        CREATE TRIGGER tr_source_deletion_tombstones_reject_invalid_update
        BEFORE UPDATE ON source_deletion_tombstones
        WHEN NEW.source_id IS NOT OLD.source_id
            OR NEW.configuration_id IS NOT OLD.configuration_id
            OR NEW.source_kind IS NOT OLD.source_kind
            OR NEW.configuration_reference IS NOT OLD.configuration_reference
            OR NEW.marked_utc IS NOT OLD.marked_utc
            OR NOT (
                NEW.protected_delete_completed = OLD.protected_delete_completed
                OR (
                    OLD.protected_delete_completed = 0
                    AND NEW.protected_delete_completed = 1
                )
            )
        BEGIN
            SELECT RAISE(IGNORE);
        END;

        CREATE TRIGGER tr_source_deletion_tombstones_reject_delete
        BEFORE DELETE ON source_deletion_tombstones
        BEGIN
            SELECT RAISE(IGNORE);
        END;

        CREATE TRIGGER tr_source_deletion_tombstones_require_authorized_phase
        BEFORE UPDATE OF protected_delete_completed ON source_deletion_tombstones
        WHEN OLD.protected_delete_completed = 0
            AND NEW.protected_delete_completed = 1
            AND iptv_source_delete_authorized(
                NEW.source_id,
                NEW.configuration_id,
                NEW.source_kind,
                NEW.configuration_reference) <> 1
        BEGIN
            SELECT RAISE(IGNORE);
        END;

        CREATE TRIGGER tr_sources_reject_tombstoned_insert
        BEFORE INSERT ON sources
        WHEN NEW.status = {{(int)ContentSourceStatus.DeletionPending}}
            OR EXISTS (
                SELECT 1 FROM source_deletion_tombstones WHERE source_id = NEW.source_id
            )
        BEGIN
            SELECT RAISE(IGNORE);
        END;

        CREATE TRIGGER tr_sources_reject_tombstoned_update
        BEFORE UPDATE ON sources
        WHEN (
                NEW.status = {{(int)ContentSourceStatus.DeletionPending}}
                OR
                EXISTS (
                    SELECT 1 FROM source_deletion_tombstones WHERE source_id = OLD.source_id
                )
                OR EXISTS (
                    SELECT 1 FROM source_deletion_tombstones WHERE source_id = NEW.source_id
                )
            )
            AND NOT EXISTS (
                SELECT 1
                FROM source_deletion_tombstones AS tombstone
                WHERE tombstone.source_id = OLD.source_id
                  AND NEW.source_id = OLD.source_id
                  AND OLD.configuration_id = tombstone.configuration_id
                  AND NEW.configuration_id = tombstone.configuration_id
                  AND OLD.source_kind = tombstone.source_kind
                  AND NEW.source_kind = tombstone.source_kind
                  AND OLD.configuration_reference = tombstone.configuration_reference
                  AND NEW.configuration_reference = tombstone.configuration_reference
                  AND tombstone.protected_delete_completed = 0
                  AND NEW.display_name IS OLD.display_name
                  AND NEW.endpoint_scheme IS OLD.endpoint_scheme
                  AND NEW.endpoint_host IS OLD.endpoint_host
                  AND NEW.endpoint_port IS OLD.endpoint_port
                  AND NEW.active_snapshot_id IS OLD.active_snapshot_id
                  AND NEW.created_utc IS OLD.created_utc
                  AND NEW.last_error_code IS OLD.last_error_code
                  AND (
                      (
                          OLD.status <> {{(int)ContentSourceStatus.DeletionPending}}
                          AND NEW.status = {{(int)ContentSourceStatus.DeletionPending}}
                      )
                      OR (
                          OLD.status = {{(int)ContentSourceStatus.DeletionPending}}
                          AND NEW.status = {{(int)ContentSourceStatus.DeletionPending}}
                          AND NEW.updated_utc IS OLD.updated_utc
                      )
                  )
            )
        BEGIN
            SELECT RAISE(IGNORE);
        END;

        CREATE TRIGGER tr_sources_require_completed_delete
        BEFORE DELETE ON sources
        WHEN OLD.status <> {{(int)ContentSourceStatus.DeletionPending}}
            OR NOT EXISTS (
                SELECT 1
                FROM source_deletion_tombstones AS tombstone
                WHERE tombstone.source_id = OLD.source_id
                  AND tombstone.configuration_id = OLD.configuration_id
                  AND tombstone.source_kind = OLD.source_kind
                  AND tombstone.configuration_reference = OLD.configuration_reference
                  AND tombstone.protected_delete_completed = 1
            )
            OR iptv_source_delete_authorized(
                OLD.source_id,
                OLD.configuration_id,
                OLD.source_kind,
                OLD.configuration_reference) <> 1
        BEGIN
            SELECT RAISE(IGNORE);
        END;
        """;

    private const string SourceDeletionReconciliationStateSql = """
        CREATE TABLE source_deletion_reconciliation_state (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
            after_source_id TEXT NULL
                REFERENCES source_deletion_tombstones(source_id)
                CHECK (
                    after_source_id IS NULL
                    OR (
                        length(after_source_id) = 32
                        AND after_source_id = lower(after_source_id)
                        AND after_source_id NOT GLOB '*[^0-9a-f]*'
                    )
                )
        ) STRICT;
        INSERT INTO source_deletion_reconciliation_state(singleton, after_source_id)
        VALUES (1, NULL);
        """;

    private const string ContentCatalogSql = """
        CREATE TABLE IF NOT EXISTS movies (
            movie_id TEXT NOT NULL PRIMARY KEY CHECK (length(movie_id) = 32),
            snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id) ON DELETE CASCADE,
            category_id TEXT NULL REFERENCES categories(category_id) ON DELETE SET NULL,
            provider_item_id TEXT NOT NULL CHECK (length(provider_item_id) BETWEEN 1 AND 512),
            display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 256),
            container_extension TEXT NULL CHECK (container_extension IS NULL OR length(container_extension) <= 32),
            is_adult INTEGER NOT NULL CHECK (is_adult IN (0, 1)),
            UNIQUE(snapshot_id, provider_item_id)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS series (
            series_id TEXT NOT NULL PRIMARY KEY CHECK (length(series_id) = 32),
            snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id) ON DELETE CASCADE,
            category_id TEXT NULL REFERENCES categories(category_id) ON DELETE SET NULL,
            provider_item_id TEXT NOT NULL CHECK (length(provider_item_id) BETWEEN 1 AND 512),
            display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 256),
            is_adult INTEGER NOT NULL CHECK (is_adult IN (0, 1)),
            UNIQUE(snapshot_id, provider_item_id)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS seasons (
            season_id TEXT NOT NULL PRIMARY KEY CHECK (length(season_id) = 32),
            snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id) ON DELETE CASCADE,
            series_id TEXT NOT NULL REFERENCES series(series_id) ON DELETE CASCADE,
            provider_item_id TEXT NULL CHECK (provider_item_id IS NULL OR length(provider_item_id) BETWEEN 1 AND 512),
            season_number INTEGER NOT NULL CHECK (season_number >= 0),
            display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 256),
            UNIQUE(series_id, season_number)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS episodes (
            episode_id TEXT NOT NULL PRIMARY KEY CHECK (length(episode_id) = 32),
            snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id) ON DELETE CASCADE,
            season_id TEXT NOT NULL REFERENCES seasons(season_id) ON DELETE CASCADE,
            provider_item_id TEXT NOT NULL CHECK (length(provider_item_id) BETWEEN 1 AND 512),
            episode_number INTEGER NOT NULL CHECK (episode_number > 0),
            display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 256),
            container_extension TEXT NULL CHECK (container_extension IS NULL OR length(container_extension) <= 32),
            duration_ms INTEGER NULL CHECK (duration_ms IS NULL OR duration_ms BETWEEN 1 AND 172800000),
            UNIQUE(season_id, provider_item_id)
        ) STRICT;

        CREATE INDEX IF NOT EXISTS ix_movies_snapshot_name
            ON movies(snapshot_id, display_name COLLATE NOCASE, movie_id);
        CREATE INDEX IF NOT EXISTS ix_movies_snapshot_category_name
            ON movies(snapshot_id, category_id, display_name COLLATE NOCASE, movie_id);
        CREATE INDEX IF NOT EXISTS ix_series_snapshot_name
            ON series(snapshot_id, display_name COLLATE NOCASE, series_id);
        CREATE INDEX IF NOT EXISTS ix_series_snapshot_category_name
            ON series(snapshot_id, category_id, display_name COLLATE NOCASE, series_id);
        CREATE INDEX IF NOT EXISTS ix_seasons_series_number
            ON seasons(series_id, season_number, season_id);
        CREATE INDEX IF NOT EXISTS ix_episodes_season_number
            ON episodes(season_id, episode_number, episode_id);
        """;

    private const string SourceConfigurationRetirementSql = """
        CREATE TABLE IF NOT EXISTS source_configuration_retirements (
            source_id TEXT NOT NULL CHECK (length(source_id) = 32),
            configuration_id TEXT NOT NULL CHECK (length(configuration_id) = 32),
            source_kind INTEGER NOT NULL CHECK (source_kind IN (0, 1)),
            configuration_reference TEXT NOT NULL CHECK (length(configuration_reference) IN (46, 47)),
            retired_utc TEXT NOT NULL,
            PRIMARY KEY(source_id, configuration_id)
        ) WITHOUT ROWID, STRICT;
        """;

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
            stable_key TEXT NOT NULL CHECK (length(stable_key) BETWEEN 1 AND 512),
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
        CREATE INDEX ix_channels_snapshot_name ON channels(snapshot_id, display_name COLLATE NOCASE, channel_id);
        CREATE INDEX ix_channels_snapshot_category_name ON channels(snapshot_id, category_id, display_name COLLATE NOCASE, channel_id);
        CREATE INDEX ix_channels_snapshot_number ON channels(snapshot_id, channel_number, channel_id);
        CREATE INDEX ix_locators_snapshot_owner ON protected_locators(snapshot_id, owner_kind, owner_id);
        CREATE INDEX ix_sync_runs_source_started ON sync_runs(source_id, started_utc DESC);
        """;
}
