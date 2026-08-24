using System.Reflection;
using Microsoft.Data.Sqlite;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class SqliteCatalogDatabaseTests
{
    private static readonly string[] ExpectedTables =
    [
        "catalog_metadata",
        "categories",
        "channels",
        "favorites",
        "protected_locators",
        "snapshot_keys",
        "snapshots",
        "source_deletion_reconciliation_state",
        "source_deletion_tombstones",
        "sources",
        "sync_runs",
    ];

    private static readonly string[] ExpectedTriggers =
    [
        "tr_source_deletion_tombstones_reject_delete",
        "tr_source_deletion_tombstones_reject_invalid_insert",
        "tr_source_deletion_tombstones_reject_invalid_update",
        "tr_source_deletion_tombstones_require_authorized_phase",
        "tr_sources_reject_tombstoned_insert",
        "tr_sources_reject_tombstoned_update",
        "tr_sources_require_completed_delete",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "ix_categories_snapshot_sort",
        "ix_channels_snapshot_category",
        "ix_channels_snapshot_category_name",
        "ix_channels_snapshot_name",
        "ix_channels_snapshot_number",
        "ix_locators_snapshot_owner",
        "ix_snapshots_source_cache",
        "ix_snapshots_source_state",
        "ix_sources_status",
        "ix_sync_runs_source_started",
    ];

    [TestMethod]
    [Timeout(30_000)]
    public async Task FreshDatabaseCreatesExactVersionedCatalogSchema()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-sqlite-schema");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");

        await InitializeAsync(databasePath);

        await using SqliteConnection connection = await OpenAsync(databasePath);
        Assert.AreEqual(5L, await ExecuteScalarInt64Async(connection, "PRAGMA user_version;"));
        CollectionAssert.AreEqual(ExpectedTables, await ReadObjectNamesAsync(connection, "table"));
        CollectionAssert.AreEqual(ExpectedIndexes, await ReadObjectNamesAsync(connection, "index", "ix_%"));
        CollectionAssert.AreEqual(ExpectedTriggers, await ReadObjectNamesAsync(connection, "trigger"));
        Assert.AreEqual(1L, await ExecuteScalarInt64Async(connection, "PRAGMA foreign_keys;"));
        Assert.AreEqual(2L, await ExecuteScalarInt64Async(connection, "PRAGMA synchronous;"));
        Assert.AreEqual("delete", await ExecuteScalarStringAsync(connection, "PRAGMA journal_mode;"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ReopenValidatesExistingSchemaWithoutChangingIt()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-sqlite-reopen");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeAsync(databasePath);
        byte[] before = await File.ReadAllBytesAsync(databasePath);

        await InitializeAsync(databasePath);

        byte[] after = await File.ReadAllBytesAsync(databasePath);
        CollectionAssert.AreEqual(before, after);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task VersionOneSchemaMigratesAtomicallyToCurrentVersion()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-sqlite-migration");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeAsync(databasePath);
        await using (SqliteConnection connection = await OpenAsync(databasePath))
        {
            await ExecuteAsync(connection, """
                DROP TRIGGER tr_sources_reject_tombstoned_insert;
                DROP TRIGGER tr_sources_reject_tombstoned_update;
                DROP TRIGGER tr_sources_require_completed_delete;
                DROP TABLE source_deletion_reconciliation_state;
                DROP TABLE source_deletion_tombstones;
                DROP INDEX ix_snapshots_source_cache;
                DROP INDEX ix_channels_snapshot_name;
                DROP INDEX ix_channels_snapshot_category_name;
                ALTER TABLE snapshots DROP COLUMN cache_key;
                PRAGMA user_version = 1;
                """);
        }

        await InitializeAsync(databasePath);

        await using SqliteConnection migrated = await OpenAsync(databasePath);
        Assert.AreEqual(5L, await ExecuteScalarInt64Async(migrated, "PRAGMA user_version;"));
        Assert.AreEqual(1L, await ExecuteScalarInt64Async(
            migrated,
            "SELECT count(*) FROM pragma_table_info('snapshots') WHERE name = 'cache_key';"));
        Assert.AreEqual(1L, await ExecuteScalarInt64Async(
            migrated,
            "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_snapshots_source_cache';"));
        Assert.AreEqual(1L, await ExecuteScalarInt64Async(
            migrated,
            "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_channels_snapshot_name';"));
        CollectionAssert.AreEqual(ExpectedTriggers, await ReadObjectNamesAsync(migrated, "trigger"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task VersionTwoSchemaAddsCatalogBrowseIndexesAtomically()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m9-sqlite-query-index-migration");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeAsync(databasePath);
        await using (SqliteConnection connection = await OpenAsync(databasePath))
        {
            await ExecuteAsync(connection, """
                DROP TRIGGER tr_sources_reject_tombstoned_insert;
                DROP TRIGGER tr_sources_reject_tombstoned_update;
                DROP TRIGGER tr_sources_require_completed_delete;
                DROP TABLE source_deletion_reconciliation_state;
                DROP TABLE source_deletion_tombstones;
                DROP INDEX ix_channels_snapshot_name;
                DROP INDEX ix_channels_snapshot_category_name;
                PRAGMA user_version = 2;
                """);
        }

        await InitializeAsync(databasePath);

        await using SqliteConnection migrated = await OpenAsync(databasePath);
        Assert.AreEqual(5L, await ExecuteScalarInt64Async(migrated, "PRAGMA user_version;"));
        CollectionAssert.Contains(await ReadObjectNamesAsync(migrated, "index", "ix_%"), "ix_channels_snapshot_name");
        CollectionAssert.Contains(await ReadObjectNamesAsync(migrated, "index", "ix_%"), "ix_channels_snapshot_category_name");
        CollectionAssert.AreEqual(ExpectedTriggers, await ReadObjectNamesAsync(migrated, "trigger"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task VersionThreeSchemaAddsDurableSourceDeletionBoundaryAtomically()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-sqlite-deletion-migration");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeAsync(databasePath);
        await using (SqliteConnection connection = await OpenAsync(databasePath))
        {
            await ExecuteAsync(connection, """
                DROP TRIGGER tr_sources_reject_tombstoned_insert;
                DROP TRIGGER tr_sources_reject_tombstoned_update;
                DROP TRIGGER tr_sources_require_completed_delete;
                DROP TABLE source_deletion_reconciliation_state;
                DROP TABLE source_deletion_tombstones;
                PRAGMA user_version = 3;
                """);
        }

        await InitializeAsync(databasePath);

        await using SqliteConnection migrated = await OpenAsync(databasePath);
        Assert.AreEqual(5L, await ExecuteScalarInt64Async(migrated, "PRAGMA user_version;"));
        CollectionAssert.Contains(
            await ReadObjectNamesAsync(migrated, "table"),
            "source_deletion_tombstones");
        CollectionAssert.AreEqual(ExpectedTriggers, await ReadObjectNamesAsync(migrated, "trigger"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task VersionFourSchemaAddsDurableDeletionReconciliationCursorAtomically()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create(
            "m12-sqlite-deletion-cursor-migration");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeAsync(databasePath);
        await using (SqliteConnection connection = await OpenAsync(databasePath))
        {
            await ExecuteAsync(connection, """
                DROP TABLE source_deletion_reconciliation_state;
                PRAGMA user_version = 4;
                """);
        }

        await InitializeAsync(databasePath);

        await using SqliteConnection migrated = await OpenAsync(databasePath);
        Assert.AreEqual(5L, await ExecuteScalarInt64Async(migrated, "PRAGMA user_version;"));
        Assert.AreEqual(1L, await ExecuteScalarInt64Async(
            migrated,
            "SELECT count(*) FROM source_deletion_reconciliation_state WHERE singleton = 1 AND after_source_id IS NULL;"));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [Timeout(30_000)]
    public async Task LegacyPendingSourceIsBackfilledIntoExactPhaseZeroJournal(int legacyVersion)
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-sqlite-deletion-backfill");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeAsync(databasePath);
        await using (SqliteConnection connection = await OpenAsync(databasePath))
        {
            await ExecuteAsync(connection, """
                DROP TRIGGER tr_sources_reject_tombstoned_insert;
                DROP TRIGGER tr_sources_reject_tombstoned_update;
                DROP TRIGGER tr_sources_require_completed_delete;
                DROP TABLE source_deletion_reconciliation_state;
                DROP TABLE source_deletion_tombstones;
                INSERT INTO sources(
                    source_id, configuration_id, source_kind, display_name, endpoint_scheme,
                    endpoint_host, endpoint_port, configuration_reference, status,
                    active_snapshot_id, created_utc, updated_utc, last_error_code)
                VALUES (
                    '11111111111111111111111111111111',
                    '22222222222222222222222222222222',
                    1, 'Legacy pending source', 'https', 'fixtures.invalid', 443,
                    'locator-ref-v1:33333333333333333333333333333333', 6, NULL,
                    '2026-08-24T00:00:00.0000000+00:00',
                    '2026-08-24T00:00:00.0000000+00:00', NULL);
                """);
            await DowngradeToLegacyVersionAsync(connection, legacyVersion);
        }

        await InitializeAsync(databasePath);

        await using SqliteConnection migrated = await OpenAsync(databasePath);
        Assert.AreEqual(5L, await ExecuteScalarInt64Async(migrated, "PRAGMA user_version;"));
        Assert.AreEqual(1L, await ExecuteScalarInt64Async(
            migrated,
            """
            SELECT count(*)
            FROM source_deletion_tombstones
            WHERE source_id = '11111111111111111111111111111111'
              AND configuration_id = '22222222222222222222222222222222'
              AND source_kind = 1
              AND configuration_reference = 'locator-ref-v1:33333333333333333333333333333333'
              AND protected_delete_completed = 0;
            """));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MigrationFailureRollsBackSchemaAndVersionAtomically()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-sqlite-migration-rollback");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeAsync(databasePath);
        await using (SqliteConnection connection = await OpenAsync(databasePath))
        {
            await ExecuteAsync(connection, """
                DROP TRIGGER tr_sources_reject_tombstoned_insert;
                DROP TRIGGER tr_sources_reject_tombstoned_update;
                DROP TRIGGER tr_sources_require_completed_delete;
                DROP TABLE source_deletion_reconciliation_state;
                DROP TABLE source_deletion_tombstones;
                DROP INDEX ix_snapshots_source_cache;
                DROP INDEX ix_channels_snapshot_name;
                DROP INDEX ix_channels_snapshot_category_name;
                ALTER TABLE snapshots DROP COLUMN cache_key;
                CREATE TABLE ix_snapshots_source_cache(value INTEGER) STRICT;
                PRAGMA user_version = 1;
                """);
        }

        await Assert.ThrowsExactlyAsync<SqliteException>(async () => await InitializeAsync(databasePath));

        await using SqliteConnection unchanged = await OpenAsync(databasePath);
        Assert.AreEqual(1L, await ExecuteScalarInt64Async(unchanged, "PRAGMA user_version;"));
        Assert.AreEqual(0L, await ExecuteScalarInt64Async(
            unchanged,
            "SELECT count(*) FROM pragma_table_info('snapshots') WHERE name = 'cache_key';"));
        Assert.AreEqual(1L, await ExecuteScalarInt64Async(
            unchanged,
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'ix_snapshots_source_cache';"));
        Assert.AreEqual(0L, await ExecuteScalarInt64Async(
            unchanged,
            "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_snapshots_source_cache';"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UnsupportedOrIncompleteSchemaFailsClosed()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-sqlite-invalid");
        string unsupportedPath = Path.Combine(temporary.FullPath, "unsupported.db");
        await using (SqliteConnection connection = await OpenAsync(unsupportedPath))
        {
            await ExecuteAsync(connection, "PRAGMA user_version = 2;");
        }

        await AssertInitializationFailureAsync(unsupportedPath);

        string incompletePath = Path.Combine(temporary.FullPath, "incomplete.db");
        await using (SqliteConnection connection = await OpenAsync(incompletePath))
        {
            await ExecuteAsync(connection, "CREATE TABLE placeholder(value INTEGER) STRICT; PRAGMA user_version = 1;");
        }

        await AssertInitializationFailureAsync(incompletePath);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CorruptedDatabaseFailsClosedWithoutReplacement()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-sqlite-corrupt");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        byte[] corruption = "not-a-sqlite-catalog"u8.ToArray();
        await File.WriteAllBytesAsync(databasePath, corruption);

        await Assert.ThrowsExactlyAsync<SqliteException>(async () => await InitializeAsync(databasePath));

        CollectionAssert.AreEqual(corruption, await File.ReadAllBytesAsync(databasePath));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task SchemaContainsOnlyOpaqueOrEncryptedLocatorStorage()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-sqlite-confidentiality");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await InitializeAsync(databasePath);
        await using SqliteConnection connection = await OpenAsync(databasePath);

        string schema = await ExecuteScalarStringAsync(
            connection,
            "SELECT lower(group_concat(sql, ' ')) FROM sqlite_master WHERE sql IS NOT NULL;");

        Assert.DoesNotContain("password", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist_url", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("stream_url", schema, StringComparison.Ordinal);
        StringAssert.Contains(schema, "configuration_reference");
        StringAssert.Contains(schema, "locator_reference");
        StringAssert.Contains(schema, "ciphertext");
        StringAssert.Contains(schema, "authentication_tag");
        StringAssert.Contains(schema, "wrapped_dek");
    }

    private static async Task InitializeAsync(string databasePath)
    {
        Type type = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly.GetType(
            "IptvSuite.Infrastructure.SqliteCatalogDatabase",
            throwOnError: true)!;
        object database = Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [databasePath],
            culture: null)!;
        MethodInfo method = type.GetMethod("InitializeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(database, [CancellationToken.None])!;
        await (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
    }

    private static Task DowngradeToLegacyVersionAsync(
        SqliteConnection connection,
        int legacyVersion) => legacyVersion switch
        {
            1 => ExecuteAsync(connection, """
                DROP INDEX ix_snapshots_source_cache;
                DROP INDEX ix_channels_snapshot_name;
                DROP INDEX ix_channels_snapshot_category_name;
                ALTER TABLE snapshots DROP COLUMN cache_key;
                PRAGMA user_version = 1;
                """),
            2 => ExecuteAsync(connection, """
                DROP INDEX ix_channels_snapshot_name;
                DROP INDEX ix_channels_snapshot_category_name;
                PRAGMA user_version = 2;
                """),
            3 => ExecuteAsync(connection, "PRAGMA user_version = 3;"),
            _ => throw new ArgumentOutOfRangeException(nameof(legacyVersion)),
        };

    private static async Task AssertInitializationFailureAsync(string databasePath)
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await InitializeAsync(databasePath));
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<string[]> ReadObjectNamesAsync(
        SqliteConnection connection,
        string type,
        string? like = null)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = like is null
            ? "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%' ORDER BY name;"
            : "SELECT name FROM sqlite_master WHERE type = $type AND name LIKE $like ORDER BY name;";
        command.Parameters.AddWithValue("$type", type);
        if (like is not null)
        {
            command.Parameters.AddWithValue("$like", like);
        }

        var names = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    private static async Task<long> ExecuteScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ExecuteScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
