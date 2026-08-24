using System.Reflection;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class SqliteSourceDeletionLifecycleTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task MarkPendingJournalsExactBindingAtomicallyAndIsIdempotent()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-source-delete-mark");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        PersistedSource source = await CreatePersistedRemoteSourceAsync(databasePath, store);
        ISourceDeletionLifecycle lifecycle = CreateLifecycle(databasePath, store);

        SourceDeletionLifecycleOperationResult first =
            await lifecycle.MarkPendingAsync(source.SourceId);
        SourceDeletionLifecycleOperationResult repeated =
            await lifecycle.MarkPendingAsync(source.SourceId);
        SourceDeletionLifecycleOperationResult unknown =
            await lifecycle.MarkPendingAsync(SourceId.Generate());

        Assert.IsTrue(first.IsSuccess);
        Assert.IsTrue(repeated.IsSuccess);
        Assert.IsFalse(unknown.IsSuccess);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, unknown.Error!.Code);
        await using SqliteConnection connection = await OpenAsync(databasePath);
        Assert.AreEqual((long)ContentSourceStatus.DeletionPending, await ScalarAsync(
            connection,
            "SELECT status FROM sources WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(1L, await ScalarAsync(
            connection,
            """
            SELECT count(*)
            FROM source_deletion_tombstones
            WHERE source_id = $source
              AND configuration_id = $configuration
              AND source_kind = $kind
              AND configuration_reference = $reference
              AND protected_delete_completed = 0;
            """,
            ("$source", Id(source.SourceId.Value)),
            ("$configuration", Id(source.ConfigurationId.Value)),
            ("$kind", (int)source.Kind),
            ("$reference", source.ConfigurationReference)));
        Assert.AreEqual(1, store.ActiveRecordCount);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PreCancelledMarkDoesNotCreateJournalOrChangeSource()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-source-delete-mark-cancel");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        PersistedSource source = await CreatePersistedRemoteSourceAsync(databasePath, store);
        ISourceDeletionLifecycle lifecycle = CreateLifecycle(databasePath, store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await lifecycle.MarkPendingAsync(source.SourceId, cancellation.Token));

        await using SqliteConnection connection = await OpenAsync(databasePath);
        Assert.AreEqual((long)ContentSourceStatus.Ready, await ScalarAsync(
            connection,
            "SELECT status FROM sources WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0L, await ScalarAsync(
            connection,
            "SELECT count(*) FROM source_deletion_tombstones;"));
        Assert.AreEqual(1, store.ActiveRecordCount);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task JournalAndSourceTriggersRejectForgeryMutationAndResurrection()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-source-delete-guards");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        PersistedSource source = await CreatePersistedRemoteSourceAsync(databasePath, store);
        PersistedSource sibling = await CreatePersistedRemoteSourceAsync(databasePath, store);
        ISourceDeletionLifecycle lifecycle = CreateLifecycle(databasePath, store);
        Assert.IsTrue((await lifecycle.MarkPendingAsync(source.SourceId)).IsSuccess);
        await using SqliteConnection connection = await OpenAsync(databasePath);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");

        Assert.AreEqual(0, await InsertSourceOnlyAsync(
            connection,
            source,
            ContentSourceStatus.Ready,
            replace: true));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            """
            INSERT INTO source_deletion_tombstones(
                source_id, configuration_id, source_kind, configuration_reference,
                protected_delete_completed, marked_utc)
            VALUES ($source, $configuration, $kind, $reference, 0, $marked);
            """,
            ("$source", Id(source.SourceId.Value)),
            ("$configuration", Id(source.ConfigurationId.Value)),
            ("$kind", (int)source.Kind),
            ("$reference", source.ConfigurationReference),
            ("$marked", "2099-01-01T00:00:00.0000000+00:00")));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            """
            INSERT OR REPLACE INTO source_deletion_tombstones(
                source_id, configuration_id, source_kind, configuration_reference,
                protected_delete_completed, marked_utc)
            VALUES ($source, $configuration, $kind, $reference, 0, $marked);
            """,
            ("$source", Id(source.SourceId.Value)),
            ("$configuration", Id(source.ConfigurationId.Value)),
            ("$kind", (int)source.Kind),
            ("$reference", source.ConfigurationReference),
            ("$marked", "2099-01-01T00:00:00.0000000+00:00")));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            """
            INSERT INTO source_deletion_tombstones(
                source_id, configuration_id, source_kind, configuration_reference,
                protected_delete_completed, marked_utc)
            VALUES ($source, $configuration, $kind, $reference, 1, $marked);
            """,
            ("$source", Id(sibling.SourceId.Value)),
            ("$configuration", Id(sibling.ConfigurationId.Value)),
            ("$kind", (int)sibling.Kind),
            ("$reference", sibling.ConfigurationReference),
            ("$marked", "2099-01-01T00:00:00.0000000+00:00")));
        await AssertSqlMutationRejectedAsync(() => ExecuteAffectedAsync(
            connection,
            "DELETE FROM sources WHERE source_id = $source;",
            ("$source", Id(sibling.SourceId.Value))));
        PersistedSource orphan = CreateSyntheticSource(SourceKind.RemotePlaylist);
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            """
            INSERT INTO source_deletion_tombstones(
                source_id, configuration_id, source_kind, configuration_reference,
                protected_delete_completed, marked_utc)
            VALUES ($source, $configuration, $kind, $reference, 0, $marked);
            """,
            ("$source", Id(orphan.SourceId.Value)),
            ("$configuration", Id(orphan.ConfigurationId.Value)),
            ("$kind", (int)orphan.Kind),
            ("$reference", orphan.ConfigurationReference),
            ("$marked", "2099-01-01T00:00:00.0000000+00:00")));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE sources SET configuration_id = $configuration WHERE source_id = $source;",
            ("$configuration", Id(SourceConfigurationId.Generate().Value)),
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE sources SET source_id = $replacement WHERE source_id = $source;",
            ("$replacement", Id(SourceId.Generate().Value)),
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE sources SET display_name = 'Mutated' WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            """
            UPDATE sources
            SET endpoint_scheme = 'http', endpoint_host = 'mutated.invalid', endpoint_port = 80
            WHERE source_id = $source;
            """,
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE sources SET active_snapshot_id = NULL WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE sources SET last_error_code = 999 WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE sources SET created_utc = '2099-01-01T00:00:00Z' WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE sources SET updated_utc = '2099-01-01T00:00:00Z' WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE sources SET status = $ready WHERE source_id = $source;",
            ("$ready", (int)ContentSourceStatus.Ready),
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(1, await ExecuteAffectedAsync(
            connection,
            "UPDATE sources SET status = $pending WHERE source_id = $source;",
            ("$pending", (int)ContentSourceStatus.DeletionPending),
            ("$source", Id(source.SourceId.Value))));
        await AssertSqlMutationRejectedAsync(() => ExecuteAffectedAsync(
            connection,
            "DELETE FROM sources WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE sources SET status = $pending WHERE source_id = $source;",
            ("$pending", (int)ContentSourceStatus.DeletionPending),
            ("$source", Id(sibling.SourceId.Value))));

        PersistedSource unjournaled = CreateSyntheticSource(SourceKind.RemotePlaylist);
        Assert.AreEqual(0, await InsertSourceOnlyAsync(
            connection,
            unjournaled,
            ContentSourceStatus.DeletionPending,
            replace: false));
        Assert.AreEqual((long)ContentSourceStatus.DeletionPending, await ScalarAsync(
            connection,
            "SELECT status FROM sources WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual((long)ContentSourceStatus.Ready, await ScalarAsync(
            connection,
            "SELECT status FROM sources WHERE source_id = $source;",
            ("$source", Id(sibling.SourceId.Value))));
        Assert.AreEqual(0L, await ScalarAsync(
            connection,
            "SELECT count(*) FROM sources WHERE source_id = $source;",
            ("$source", Id(unjournaled.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE source_deletion_tombstones SET source_id = $replacement WHERE source_id = $source;",
            ("$replacement", Id(SourceId.Generate().Value)),
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE source_deletion_tombstones SET configuration_id = $replacement WHERE source_id = $source;",
            ("$replacement", Id(SourceConfigurationId.Generate().Value)),
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE source_deletion_tombstones SET source_kind = $replacement WHERE source_id = $source;",
            ("$replacement", (int)SourceKind.XtreamCompatible),
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE source_deletion_tombstones SET configuration_reference = $replacement WHERE source_id = $source;",
            ("$replacement", $"locator-ref-v1:{Guid.NewGuid():N}"),
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "UPDATE source_deletion_tombstones SET marked_utc = $replacement WHERE source_id = $source;",
            ("$replacement", "2099-01-01T00:00:00.0000000+00:00"),
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await ExecuteAffectedAsync(
            connection,
            "DELETE FROM source_deletion_tombstones WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        await AssertSqlMutationRejectedAsync(() => ExecuteAffectedAsync(
            connection,
            "UPDATE source_deletion_tombstones SET protected_delete_completed = 1 WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        await AssertSqlMutationRejectedAsync(() => ExecuteAffectedAsync(
            connection,
            "DELETE FROM sources WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0L, await ScalarAsync(
            connection,
            "SELECT protected_delete_completed FROM source_deletion_tombstones WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(2L, await ScalarAsync(connection, "SELECT count(*) FROM sources;"));
        Assert.AreEqual(2L, await ScalarAsync(connection, "SELECT count(*) FROM snapshots;"));
        Assert.AreEqual(2, store.ActiveRecordCount);
        Assert.AreEqual(1L, await ScalarAsync(
            connection,
            "SELECT count(*) FROM sources WHERE source_id = $source AND status = $pending;",
            ("$source", Id(source.SourceId.Value)),
            ("$pending", (int)ContentSourceStatus.DeletionPending)));
        Assert.AreEqual(1L, await ScalarAsync(
            connection,
            "SELECT count(*) FROM snapshots WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(1L, await ScalarAsync(
            connection,
            """
            SELECT count(*) FROM snapshot_keys AS key
            JOIN snapshots AS snapshot ON snapshot.snapshot_id = key.snapshot_id
            WHERE snapshot.source_id = $source
              AND key.key_state = 1 AND key.wrapped_dek IS NOT NULL;
            """,
            ("$source", Id(source.SourceId.Value))));
        await AssertRemoteProtectedRecordAvailableAsync(store, source);
        await AssertRemoteProtectedRecordAvailableAsync(store, sibling);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RemoteProtectedDeletePrecedesCatalogAndFaultRetryPreservesExactSibling()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-source-delete-remote-retry");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        PersistedSource source = await CreatePersistedRemoteSourceAsync(databasePath, store);
        PersistedSource sibling = await CreatePersistedRemoteSourceAsync(databasePath, store);
        ISourceDeletionLifecycle lifecycle = CreateLifecycle(databasePath, store);
        Assert.IsTrue((await lifecycle.MarkPendingAsync(source.SourceId)).IsSuccess);
        await using (SqliteConnection connection = await OpenAsync(databasePath))
        {
            await ExecuteAsync(
                connection,
                """
                CREATE TRIGGER fixture_fail_source_delete
                BEFORE DELETE ON sources
                BEGIN
                    SELECT RAISE(ABORT, 'fixture delete failure');
                END;
                """);
        }

        SourceDeletionLifecycleOperationResult interrupted =
            await lifecycle.CompletePendingAsync(source.SourceId);

        Assert.IsFalse(interrupted.IsSuccess);
        Assert.AreEqual(DomainErrorCode.StorageUnavailable, interrupted.Error!.Code);
        Assert.AreEqual(1, store.ActiveRecordCount);
        await using (SqliteConnection retained = await OpenAsync(databasePath))
        {
            Assert.AreEqual(0L, await ScalarAsync(
                retained,
                """
                SELECT protected_delete_completed
                FROM source_deletion_tombstones WHERE source_id = $source;
                """,
                ("$source", Id(source.SourceId.Value))));
            Assert.AreEqual(1L, await ScalarAsync(
                retained,
                """
                SELECT count(*) FROM snapshot_keys AS key
                JOIN snapshots AS snapshot ON snapshot.snapshot_id = key.snapshot_id
                WHERE snapshot.source_id = $source
                  AND key.key_state = 1 AND key.wrapped_dek IS NOT NULL;
                """,
                ("$source", Id(source.SourceId.Value))));
            await ExecuteAsync(retained, "DROP TRIGGER fixture_fail_source_delete;");
        }

        SourceDeletionLifecycleOperationResult completed =
            await lifecycle.CompletePendingAsync(source.SourceId);
        SourceDeletionLifecycleOperationResult repeated =
            await lifecycle.CompletePendingAsync(source.SourceId);

        Assert.IsTrue(completed.IsSuccess);
        Assert.IsTrue(repeated.IsSuccess);
        Assert.AreEqual(1, store.ActiveRecordCount);
        await AssertRemoteProtectedRecordsAsync(store, source, sibling);
        await using SqliteConnection deleted = await OpenAsync(databasePath);
        Assert.AreEqual(0L, await ScalarAsync(
            deleted,
            "SELECT count(*) FROM sources WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(1L, await ScalarAsync(
            deleted,
            "SELECT count(*) FROM sources WHERE source_id = $source;",
            ("$source", Id(sibling.SourceId.Value))));
        foreach (string table in new[]
        {
            "snapshots",
            "snapshot_keys",
            "categories",
            "channels",
            "protected_locators",
            "favorites",
            "sync_runs",
        })
        {
            Assert.AreEqual(1L, await ScalarAsync(deleted, $"SELECT count(*) FROM {table};"), table);
        }

        Assert.AreEqual(1L, await ScalarAsync(
            deleted,
            """
            SELECT count(*) FROM source_deletion_tombstones
            WHERE source_id = $source AND protected_delete_completed = 1;
            """,
            ("$source", Id(source.SourceId.Value))));
        await AssertSqlMutationRejectedAsync(() => ExecuteAffectedAsync(
            deleted,
            "UPDATE source_deletion_tombstones SET protected_delete_completed = 0 WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0, await InsertSourceOnlyAsync(
            deleted,
            source,
            ContentSourceStatus.Ready,
            replace: false));
        Assert.AreEqual(0, await InsertSourceOnlyAsync(
            deleted,
            source,
            ContentSourceStatus.Ready,
            replace: true));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ProtectedStoreFailureRetainsPhaseZeroPendingGraph()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-source-delete-store-failure");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var innerStore = new M4InMemorySecretStore();
        var store = new DeleteTrackingSecretStore(
            innerStore,
            SecretStoreFailure.StorageUnavailable);
        PersistedSource source = await CreatePersistedRemoteSourceAsync(databasePath, store);
        ISourceDeletionLifecycle lifecycle = CreateLifecycle(databasePath, store);
        Assert.IsTrue((await lifecycle.MarkPendingAsync(source.SourceId)).IsSuccess);

        SourceDeletionLifecycleOperationResult result =
            await lifecycle.CompletePendingAsync(source.SourceId);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.StorageUnavailable, result.Error!.Code);
        Assert.AreEqual(1, store.DeleteAttempts);
        Assert.AreEqual(1, innerStore.ActiveRecordCount);
        await using SqliteConnection connection = await OpenAsync(databasePath);
        Assert.AreEqual((long)ContentSourceStatus.DeletionPending, await ScalarAsync(
            connection,
            "SELECT status FROM sources WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(0L, await ScalarAsync(
            connection,
            "SELECT protected_delete_completed FROM source_deletion_tombstones WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(1L, await ScalarAsync(
            connection,
            "SELECT count(*) FROM snapshot_keys WHERE key_state = 1 AND wrapped_dek IS NOT NULL;"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task XtreamCompletionDeletesExactTargetAndPreservesSibling()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-source-delete-xtream");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        PersistedSource source = await CreatePersistedXtreamSourceAsync(databasePath, store);
        PersistedSource sibling = await CreatePersistedXtreamSourceAsync(databasePath, store);
        ISourceDeletionLifecycle lifecycle = CreateLifecycle(databasePath, store);

        Assert.IsTrue((await lifecycle.MarkPendingAsync(source.SourceId)).IsSuccess);
        SourceDeletionLifecycleOperationResult completed =
            await lifecycle.CompletePendingAsync(source.SourceId);

        Assert.IsTrue(completed.IsSuccess);
        Assert.AreEqual(1, store.ActiveRecordCount);
        await AssertXtreamProtectedRecordsAsync(store, source, sibling);
        await using SqliteConnection connection = await OpenAsync(databasePath);
        Assert.AreEqual(0L, await ScalarAsync(
            connection,
            "SELECT count(*) FROM sources WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
        Assert.AreEqual(1L, await ScalarAsync(
            connection,
            "SELECT count(*) FROM sources WHERE source_id = $source;",
            ("$source", Id(sibling.SourceId.Value))));
        Assert.AreEqual(1L, await ScalarAsync(
            connection,
            """
            SELECT count(*) FROM source_deletion_tombstones
            WHERE source_id = $source
              AND configuration_id = $configuration
              AND source_kind = $kind
              AND configuration_reference = $reference
              AND protected_delete_completed = 1;
            """,
            ("$source", Id(source.SourceId.Value)),
            ("$configuration", Id(source.ConfigurationId.Value)),
            ("$kind", (int)SourceKind.XtreamCompatible),
            ("$reference", source.ConfigurationReference)));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task LegacyMalformedBindingBackfillMakesMarkAndCompleteFailClosed()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-source-delete-binding-mismatch");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var innerStore = new M4InMemorySecretStore();
        var store = new DeleteTrackingSecretStore(innerStore);
        PersistedSource source = await CreatePersistedRemoteSourceAsync(databasePath, store);
        await using (SqliteConnection connection = await OpenAsync(databasePath))
        {
            await ExecuteAsync(
                connection,
                """
                DROP TRIGGER tr_sources_reject_tombstoned_insert;
                DROP TRIGGER tr_sources_reject_tombstoned_update;
                DROP TRIGGER tr_sources_require_completed_delete;
                DROP TABLE source_deletion_tombstones;
                UPDATE sources
                SET status = $pending, configuration_reference = $malformed
                WHERE source_id = $source;
                PRAGMA user_version = 3;
                """,
                ("$malformed", "locator-ref-v1:00000000000000000000000000000000"),
                ("$pending", (int)ContentSourceStatus.DeletionPending),
                ("$source", Id(source.SourceId.Value)));
        }

        ISourceDeletionLifecycle lifecycle = CreateLifecycle(databasePath, store);
        SourceDeletionLifecycleOperationResult marked =
            await lifecycle.MarkPendingAsync(source.SourceId);
        SourceDeletionLifecycleOperationResult completed =
            await lifecycle.CompletePendingAsync(source.SourceId);

        Assert.IsFalse(marked.IsSuccess);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, marked.Error!.Code);
        Assert.IsFalse(completed.IsSuccess);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, completed.Error!.Code);
        Assert.AreEqual(0, store.DeleteAttempts);
        Assert.AreEqual(1, innerStore.ActiveRecordCount);
        await using SqliteConnection retained = await OpenAsync(databasePath);
        Assert.AreEqual(1L, await ScalarAsync(
            retained,
            "SELECT count(*) FROM sources WHERE source_id = $source AND status = $pending;",
            ("$source", Id(source.SourceId.Value)),
            ("$pending", (int)ContentSourceStatus.DeletionPending)));
        Assert.AreEqual(0L, await ScalarAsync(
            retained,
            "SELECT protected_delete_completed FROM source_deletion_tombstones WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task AbsentPhaseZeroUsesJournalBindingThenPhaseOneRetryDoesNotDeleteAgain()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-source-delete-absent-phase-zero");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var innerStore = new M4InMemorySecretStore();
        var store = new DeleteTrackingSecretStore(innerStore);
        PersistedSource source = await CreatePersistedRemoteSourceAsync(databasePath, store);
        ISourceDeletionLifecycle lifecycle = CreateLifecycle(databasePath, store);
        Assert.IsTrue((await lifecycle.MarkPendingAsync(source.SourceId)).IsSuccess);
        await using (SqliteConnection connection = await OpenAsync(databasePath))
        {
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");
            await ExecuteAsync(
                connection,
                """
                DROP TRIGGER tr_sources_require_completed_delete;
                DELETE FROM sources WHERE source_id = $source;
                CREATE TRIGGER tr_sources_require_completed_delete
                BEFORE DELETE ON sources
                WHEN OLD.status <> 6
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
                """,
                ("$source", Id(source.SourceId.Value)));
        }

        SourceDeletionLifecycleOperationResult completed =
            await lifecycle.CompletePendingAsync(source.SourceId);
        SourceDeletionLifecycleOperationResult repeated =
            await lifecycle.CompletePendingAsync(source.SourceId);

        Assert.IsTrue(completed.IsSuccess);
        Assert.IsTrue(repeated.IsSuccess);
        Assert.AreEqual(1, store.DeleteAttempts);
        Assert.AreEqual(0, innerStore.ActiveRecordCount);
        await using SqliteConnection verified = await OpenAsync(databasePath);
        Assert.AreEqual(0L, await ScalarAsync(verified, "SELECT count(*) FROM sources;"));
        Assert.AreEqual(1L, await ScalarAsync(
            verified,
            "SELECT protected_delete_completed FROM source_deletion_tombstones WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CancellationAfterProtectedDeleteCannotInterruptPhaseOneCommit()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-source-delete-post-commit-cancel");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var innerStore = new M4InMemorySecretStore();
        using var cancellation = new CancellationTokenSource();
        var store = new DeleteTrackingSecretStore(
            innerStore,
            failure: null,
            afterSuccessfulDelete: cancellation.Cancel);
        PersistedSource source = await CreatePersistedRemoteSourceAsync(databasePath, store);
        ISourceDeletionLifecycle lifecycle = CreateLifecycle(databasePath, store);
        Assert.IsTrue((await lifecycle.MarkPendingAsync(source.SourceId)).IsSuccess);

        SourceDeletionLifecycleOperationResult result =
            await lifecycle.CompletePendingAsync(source.SourceId, cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, store.DeleteAttempts);
        await using SqliteConnection connection = await OpenAsync(databasePath);
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT count(*) FROM sources;"));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT count(*) FROM snapshots;"));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT count(*) FROM snapshot_keys;"));
        Assert.AreEqual(1L, await ScalarAsync(
            connection,
            "SELECT protected_delete_completed FROM source_deletion_tombstones WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ConcurrentPhaseOneAdvanceWithSourcePresentIsRecovered()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-source-delete-concurrent-complete");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var innerStore = new M4InMemorySecretStore();
        PersistedSource source = await CreatePersistedRemoteSourceAsync(databasePath, innerStore);
        var store = new DeleteTrackingSecretStore(
            innerStore,
            afterSuccessfulDelete: () => AdvancePhaseSynchronously(databasePath, source));
        ISourceDeletionLifecycle lifecycle = CreateLifecycle(databasePath, store);
        Assert.IsTrue((await lifecycle.MarkPendingAsync(source.SourceId)).IsSuccess);

        SourceDeletionLifecycleOperationResult result =
            await lifecycle.CompletePendingAsync(source.SourceId);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, store.DeleteAttempts);
        await using SqliteConnection connection = await OpenAsync(databasePath);
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT count(*) FROM sources;"));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT count(*) FROM snapshots;"));
        Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT count(*) FROM snapshot_keys;"));
        Assert.AreEqual(1L, await ScalarAsync(
            connection,
            "SELECT protected_delete_completed FROM source_deletion_tombstones WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PreCancelledCompleteRetainsPhaseZeroAndProtectedRecord()
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m12-source-delete-complete-cancel");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var store = new M4InMemorySecretStore();
        PersistedSource source = await CreatePersistedRemoteSourceAsync(databasePath, store);
        ISourceDeletionLifecycle lifecycle = CreateLifecycle(databasePath, store);
        Assert.IsTrue((await lifecycle.MarkPendingAsync(source.SourceId)).IsSuccess);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await lifecycle.CompletePendingAsync(source.SourceId, cancellation.Token));

        Assert.AreEqual(1, store.ActiveRecordCount);
        await using SqliteConnection connection = await OpenAsync(databasePath);
        Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT count(*) FROM sources;"));
        Assert.AreEqual(0L, await ScalarAsync(
            connection,
            "SELECT protected_delete_completed FROM source_deletion_tombstones WHERE source_id = $source;",
            ("$source", Id(source.SourceId.Value))));
    }

    private static async Task<PersistedSource> CreatePersistedRemoteSourceAsync(
        string databasePath,
        ISecretStore store)
    {
        var protection = new SourceDraftProtectionService(store);
        SourceId sourceId = SourceId.Generate();
        DomainResult<ValidatedSourceDraft> draft = await protection.ProtectRemotePlaylistAsync(
            sourceId,
            "Synthetic deletion source",
            "https://fixtures.invalid/deletion-source.m3u");
        Assert.IsTrue(draft.IsSuccess);
        var configuration = (RemotePlaylistSourceConfiguration)draft.Value!.Configuration;
        var source = new PersistedSource(
            sourceId,
            configuration.ConfigurationId,
            SourceKind.RemotePlaylist,
            ToOpaqueIdentifier(configuration.LocatorReference),
            SnapshotId.Generate(),
            CategoryId.Generate(),
            ChannelId.Generate(),
            Guid.NewGuid(),
            Guid.NewGuid());
        await PersistGraphAsync(databasePath, source);
        return source;
    }

    private static async Task<PersistedSource> CreatePersistedXtreamSourceAsync(
        string databasePath,
        ISecretStore store)
    {
        var protection = new SourceDraftProtectionService(store);
        SourceId sourceId = SourceId.Generate();
        DomainResult<ValidatedSourceDraft> draft = await protection.ProtectXtreamAsync(
            sourceId,
            "Synthetic Xtream deletion source",
            "https://fixtures.invalid/xtream",
            "synthetic-user",
            "synthetic-password");
        Assert.IsTrue(draft.IsSuccess);
        var configuration = (XtreamSourceConfiguration)draft.Value!.Configuration;
        var source = new PersistedSource(
            sourceId,
            configuration.ConfigurationId,
            SourceKind.XtreamCompatible,
            ToOpaqueIdentifier(configuration.CredentialsReference),
            SnapshotId.Generate(),
            CategoryId.Generate(),
            ChannelId.Generate(),
            Guid.NewGuid(),
            Guid.NewGuid());
        await PersistGraphAsync(databasePath, source);
        return source;
    }

    private static async Task PersistGraphAsync(string databasePath, PersistedSource source)
    {
        _ = await new SqliteCatalogQuery(databasePath).ReadSourcesAsync();
        await using SqliteConnection connection = await OpenAsync(databasePath);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            INSERT INTO sources(
                source_id, configuration_id, source_kind, display_name, endpoint_scheme,
                endpoint_host, endpoint_port, configuration_reference, status, active_snapshot_id,
                created_utc, updated_utc, last_error_code)
            VALUES ($source, $configuration, $kind, 'Synthetic deletion source', 'https',
                'fixtures.invalid', 443, $configurationReference, $ready, $snapshot,
                '2026-08-24T00:00:00.0000000+00:00', '2026-08-24T00:00:00.0000000+00:00', NULL);
            INSERT INTO snapshots(
                snapshot_id, source_id, retrieved_utc, content_hash, parser_version,
                normalization_version, schema_version, item_count, warning_count, state)
            VALUES ($snapshot, $source, '2026-08-24T00:00:00.0000000+00:00', randomblob(32),
                1, 1, 2, 1, 0, 1);
            INSERT INTO snapshot_keys(snapshot_id, key_generation_id, wrapped_dek, key_state)
            VALUES ($snapshot, $generation, randomblob(64), 1);
            INSERT INTO categories(category_id, snapshot_id, stable_key, display_name, sort_order)
            VALUES ($category, $snapshot, 'synthetic-category', 'Synthetic category', 0);
            INSERT INTO channels(
                channel_id, snapshot_id, category_id, stable_key_version, stable_key,
                display_name, channel_number, stream_reference, logo_reference,
                provider_item_kind, provider_item_id, container_hint, is_adult, warning_flags)
            VALUES ($channel, $snapshot, $category, 1, 'synthetic-channel', 'Synthetic channel',
                1, $streamReference, NULL, NULL, NULL, NULL, 0, 0);
            INSERT INTO protected_locators(
                locator_reference, snapshot_id, key_generation_id, owner_kind, owner_id,
                purpose, nonce, authentication_tag, ciphertext)
            VALUES ($streamReference, $snapshot, $generation, $channelOwner, $channel,
                $streamPurpose, randomblob(12), randomblob(16), randomblob(32));
            INSERT INTO favorites(source_id, stable_key_version, stable_key, created_utc)
            VALUES ($source, 1, 'synthetic-channel', '2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO sync_runs(
                sync_run_id, source_id, started_utc, completed_utc, result_code,
                parsed_count, persisted_count, warning_count, failure_code)
            VALUES ($syncRun, $source, '2026-08-24T00:00:00.0000000+00:00',
                '2026-08-24T00:00:01.0000000+00:00', 0, 1, 1, 0, NULL);
            """;
        AddSourceParameters(command, source, ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$snapshot", Id(source.SnapshotId.Value));
        command.Parameters.AddWithValue("$category", Id(source.CategoryId.Value));
        command.Parameters.AddWithValue("$channel", Id(source.ChannelId.Value));
        command.Parameters.AddWithValue("$generation", Id(source.KeyGenerationId));
        command.Parameters.AddWithValue("$syncRun", Id(source.SyncRunId));
        command.Parameters.AddWithValue("$streamReference", $"locator-ref-v1:{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("$channelOwner", (int)ProtectedRecordOwnerKind.Channel);
        command.Parameters.AddWithValue("$streamPurpose", (int)ProtectedValuePurpose.ChannelStreamLocator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> InsertSourceOnlyAsync(
        SqliteConnection connection,
        PersistedSource source,
        ContentSourceStatus status,
        bool replace)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT {(replace ? "OR REPLACE " : string.Empty)}INTO sources(
                source_id, configuration_id, source_kind, display_name, endpoint_scheme,
                endpoint_host, endpoint_port, configuration_reference, status, active_snapshot_id,
                created_utc, updated_utc, last_error_code)
            VALUES ($source, $configuration, $kind, 'Synthetic source', 'https',
                'fixtures.invalid', 443, $configurationReference, $status, NULL,
                '2026-08-24T00:00:00.0000000+00:00', '2026-08-24T00:00:00.0000000+00:00', NULL);
            """;
        AddSourceParameters(command, source, status);
        return await command.ExecuteNonQueryAsync();
    }

    private static void AdvancePhaseSynchronously(
        string databasePath,
        PersistedSource source)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        connection.CreateFunction<string?, string?, long, string?, long>(
            "iptv_source_delete_authorized",
            (sourceId, configurationId, sourceKind, configurationReference) =>
                string.Equals(sourceId, Id(source.SourceId.Value), StringComparison.Ordinal) &&
                string.Equals(configurationId, Id(source.ConfigurationId.Value), StringComparison.Ordinal) &&
                sourceKind == (int)source.Kind &&
                string.Equals(
                    configurationReference,
                    source.ConfigurationReference,
                    StringComparison.Ordinal)
                    ? 1L
                    : 0L,
            isDeterministic: false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE source_deletion_tombstones
            SET protected_delete_completed = 1
            WHERE source_id = $source AND protected_delete_completed = 0;
            """;
        command.Parameters.AddWithValue("$source", Id(source.SourceId.Value));
        _ = command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static async Task AssertSqlMutationRejectedAsync(Func<Task<int>> mutation)
    {
        try
        {
            Assert.AreEqual(0, await mutation().ConfigureAwait(false));
        }
        catch (SqliteException)
        {
            // A connection without the ephemeral delete capability fails closed.
        }
    }

    private static void AddSourceParameters(
        SqliteCommand command,
        PersistedSource source,
        ContentSourceStatus status)
    {
        command.Parameters.AddWithValue("$source", Id(source.SourceId.Value));
        command.Parameters.AddWithValue("$configuration", Id(source.ConfigurationId.Value));
        command.Parameters.AddWithValue("$kind", (int)source.Kind);
        command.Parameters.AddWithValue("$configurationReference", source.ConfigurationReference);
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$status", (int)status);
    }

    private static PersistedSource CreateSyntheticSource(SourceKind kind) => new(
        SourceId.Generate(),
        SourceConfigurationId.Generate(),
        kind,
        kind == SourceKind.XtreamCompatible
            ? $"secret-ref-v1:{Guid.NewGuid():N}"
            : $"locator-ref-v1:{Guid.NewGuid():N}",
        SnapshotId.Generate(),
        CategoryId.Generate(),
        ChannelId.Generate(),
        Guid.NewGuid(),
        Guid.NewGuid());

    private static async Task AssertRemoteProtectedRecordsAsync(
        M4InMemorySecretStore store,
        PersistedSource deleted,
        PersistedSource retained)
    {
        DomainResult<ProtectedLocatorReference> deletedReference =
            ProtectedLocatorReference.Parse(deleted.ConfigurationReference);
        Assert.IsTrue(deletedReference.IsSuccess);
        SecretStoreReadResult deletedRead = await store.ReadLocatorAsync(
            deleted.SourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            ProtectedRecordOwner.ForSourceConfiguration(deleted.ConfigurationId),
            deletedReference.Value!);
        Assert.IsFalse(deletedRead.IsSuccess);
        Assert.AreEqual(SecretStoreFailure.ProtectedRecordUnavailable, deletedRead.Failure);
        await AssertRemoteProtectedRecordAvailableAsync(store, retained);
    }

    private static async Task AssertRemoteProtectedRecordAvailableAsync(
        M4InMemorySecretStore store,
        PersistedSource source)
    {
        DomainResult<ProtectedLocatorReference> reference =
            ProtectedLocatorReference.Parse(source.ConfigurationReference);
        Assert.IsTrue(reference.IsSuccess);
        SecretStoreReadResult read = await store.ReadLocatorAsync(
            source.SourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            ProtectedRecordOwner.ForSourceConfiguration(source.ConfigurationId),
            reference.Value!);
        Assert.IsTrue(read.IsSuccess);
        read.Lease!.Dispose();
    }

    private static async Task AssertXtreamProtectedRecordsAsync(
        M4InMemorySecretStore store,
        PersistedSource deleted,
        PersistedSource retained)
    {
        DomainResult<SecretReference> deletedReference =
            SecretReference.Parse(deleted.ConfigurationReference);
        DomainResult<SecretReference> retainedReference =
            SecretReference.Parse(retained.ConfigurationReference);
        Assert.IsTrue(deletedReference.IsSuccess);
        Assert.IsTrue(retainedReference.IsSuccess);
        SecretStoreReadResult deletedRead = await store.ReadCredentialsAsync(
            deleted.SourceId,
            ProtectedRecordOwner.ForSourceConfiguration(deleted.ConfigurationId),
            deletedReference.Value!);
        Assert.IsFalse(deletedRead.IsSuccess);
        Assert.AreEqual(SecretStoreFailure.ProtectedRecordUnavailable, deletedRead.Failure);
        SecretStoreReadResult retainedRead = await store.ReadCredentialsAsync(
            retained.SourceId,
            ProtectedRecordOwner.ForSourceConfiguration(retained.ConfigurationId),
            retainedReference.Value!);
        Assert.IsTrue(retainedRead.IsSuccess);
        retainedRead.Lease!.Dispose();
    }

    private static ISourceDeletionLifecycle CreateLifecycle(string databasePath, ISecretStore store)
    {
        Type type = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly.GetType(
            "IptvSuite.Infrastructure.SqliteSourceDeletionLifecycle",
            throwOnError: true)!;
        return (ISourceDeletionLifecycle)Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [databasePath, store],
            culture: null)!;
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

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteAffectedAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        _ = await ExecuteAffectedAsync(connection, sql, parameters);
    }

    private static void AddParameters(
        SqliteCommand command,
        IEnumerable<(string Name, object Value)> parameters)
    {
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    private static string ToOpaqueIdentifier(object reference)
    {
        MethodInfo method = reference.GetType().GetMethod(
            "ToOpaqueIdentifier",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (string)method.Invoke(reference, null)!;
    }

    private static string Id(Guid value) => value.ToString("N");

    private sealed record PersistedSource(
        SourceId SourceId,
        SourceConfigurationId ConfigurationId,
        SourceKind Kind,
        string ConfigurationReference,
        SnapshotId SnapshotId,
        CategoryId CategoryId,
        ChannelId ChannelId,
        Guid KeyGenerationId,
        Guid SyncRunId);

    private sealed class DeleteTrackingSecretStore(
        ISecretStore inner,
        SecretStoreFailure? failure = null,
        Action? afterSuccessfulDelete = null) : ISecretStore
    {
        internal int DeleteAttempts { get; private set; }

        public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) =>
            inner.CreateCredentialsAsync(sourceId, owner, value, cancellationToken);

        public ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) =>
            inner.CreateLocatorAsync(sourceId, purpose, owner, value, cancellationToken);

        public ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default) =>
            inner.ReadCredentialsAsync(sourceId, owner, reference, cancellationToken);

        public ValueTask<SecretStoreReadResult> ReadLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) =>
            inner.ReadLocatorAsync(sourceId, purpose, owner, reference, cancellationToken);

        public ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) =>
            inner.UpdateCredentialsAsync(sourceId, owner, reference, value, cancellationToken);

        public ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) =>
            inner.UpdateLocatorAsync(sourceId, purpose, owner, reference, value, cancellationToken);

        public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default) =>
            DeleteAsync(
                () => inner.DeleteCredentialsAsync(
                    sourceId,
                    owner,
                    reference,
                    cancellationToken),
                cancellationToken);

        public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) =>
            DeleteAsync(
                () => inner.DeleteLocatorAsync(
                    sourceId,
                    purpose,
                    owner,
                    reference,
                    cancellationToken),
                cancellationToken);

        private async ValueTask<SecretStoreOperationResult> DeleteAsync(
            Func<ValueTask<SecretStoreOperationResult>> delete,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteAttempts++;
            if (failure.HasValue)
            {
                return SecretStoreOperationResult.Failed(failure.Value);
            }

            SecretStoreOperationResult result = await delete().ConfigureAwait(false);
            if (result.IsSuccess)
            {
                afterSuccessfulDelete?.Invoke();
            }

            return result;
        }
    }
}
