using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

internal sealed class SqliteSourceDeletionLifecycle : ISourceDeletionLifecycle
{
    private const string DeleteAuthorizationFunctionName = "iptv_source_delete_authorized";

    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;
    private readonly ISecretStore _secretStore;

    internal SqliteSourceDeletionLifecycle(string databasePath, ISecretStore secretStore)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _secretStore = secretStore;
    }

    public async ValueTask<SourceDeletionLifecycleOperationResult> MarkPendingAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceId(sourceId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenWriteConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            string sourceText = Id(sourceId);
            SourceRow? source = await ReadSourceAsync(
                connection,
                transaction,
                sourceText,
                cancellationToken).ConfigureAwait(false);
            DeletionJournal? journal = await ReadJournalAsync(
                connection,
                transaction,
                sourceText,
                cancellationToken).ConfigureAwait(false);

            if (source is null)
            {
                return journal is not null && TryParseBinding(journal.Binding, out _)
                    ? SourceDeletionLifecycleOperationResult.Succeeded()
                    : InvariantFailure();
            }

            if (!source.HasValidStatus || !TryParseBinding(source.Binding, out _))
            {
                return InvariantFailure();
            }

            if (journal is null)
            {
                int inserted = await InsertJournalAsync(
                    connection,
                    transaction,
                    sourceText,
                    source.Binding,
                    cancellationToken).ConfigureAwait(false);
                if (inserted != 1)
                {
                    return InvariantFailure();
                }
            }
            else if (!BindingEquals(journal.Binding, source.Binding) ||
                     !TryParseBinding(journal.Binding, out _))
            {
                return InvariantFailure();
            }

            if (source.Status != ContentSourceStatus.DeletionPending)
            {
                int updated = await ExecuteAffectedAsync(
                    connection,
                    transaction,
                    """
                    UPDATE sources
                    SET status = $pending,
                        updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                    WHERE source_id = $source;
                    """,
                    sourceText,
                    cancellationToken,
                    ("$pending", (int)ContentSourceStatus.DeletionPending)).ConfigureAwait(false);
                if (updated != 1)
                {
                    return InvariantFailure();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return SourceDeletionLifecycleOperationResult.Succeeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return StorageFailure();
        }
    }

    public async ValueTask<SourceDeletionLifecycleOperationResult> CompletePendingAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceId(sourceId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            SourceRow? source;
            DeletionJournal? journal;
            await using (SqliteConnection connection = await OpenWriteConnectionAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                string sourceText = Id(sourceId);
                source = await ReadSourceAsync(
                    connection,
                    transaction: null,
                    sourceText,
                    cancellationToken).ConfigureAwait(false);
                journal = await ReadJournalAsync(
                    connection,
                    transaction: null,
                    sourceText,
                    cancellationToken).ConfigureAwait(false);
            }

            if (journal is null ||
                !TryParseBinding(journal.Binding, out ConfigurationBinding? binding) ||
                (source is not null && !SourceMatchesPendingJournal(source, journal)))
            {
                return InvariantFailure();
            }

            if (journal.ProtectedDeleteCompleted)
            {
                return source is null
                    ? SourceDeletionLifecycleOperationResult.Succeeded()
                    : await CompleteCatalogAsync(sourceId, journal).ConfigureAwait(false);
            }

            SecretStoreOperationResult protectedDelete = await DeleteConfigurationAsync(
                sourceId,
                binding!,
                cancellationToken).ConfigureAwait(false);
            if (!protectedDelete.IsSuccess)
            {
                return StorageFailure();
            }

            // Protected-record absence is now durable. Do not observe newly requested
            // cancellation while advancing the journal and deleting the catalog graph.
            return await CompleteCatalogAsync(sourceId, journal).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return StorageFailure();
        }
    }

    private async ValueTask<SourceDeletionLifecycleOperationResult> CompleteCatalogAsync(
        SourceId sourceId,
        DeletionJournal expectedJournal)
    {
        await _database.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenWriteConnectionAsync(CancellationToken.None)
            .ConfigureAwait(false);
        string sourceText = Id(sourceId);
        RegisterDeleteAuthorization(connection, sourceText, expectedJournal);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(CancellationToken.None)
            .ConfigureAwait(false);
        SourceRow? source = await ReadSourceAsync(
            connection,
            transaction,
            sourceText,
            CancellationToken.None).ConfigureAwait(false);
        DeletionJournal? journal = await ReadJournalAsync(
            connection,
            transaction,
            sourceText,
            CancellationToken.None).ConfigureAwait(false);
        if (journal is null ||
            !JournalCanContinue(journal, expectedJournal) ||
            !TryParseBinding(journal.Binding, out _) ||
            (source is not null && !SourceMatchesPendingJournal(source, journal)))
        {
            return InvariantFailure();
        }

        if (!journal.ProtectedDeleteCompleted)
        {
            int advanced = await ExecuteAffectedAsync(
                connection,
                transaction,
                """
                UPDATE source_deletion_tombstones
                SET protected_delete_completed = 1
                WHERE source_id = $source
                  AND configuration_id = $configuration
                  AND source_kind = $kind
                  AND configuration_reference = $reference
                  AND protected_delete_completed = 0;
                """,
                sourceText,
                CancellationToken.None,
                ("$configuration", journal.Binding.ConfigurationId),
                ("$kind", journal.Binding.SourceKind),
                ("$reference", journal.Binding.ConfigurationReference)).ConfigureAwait(false);
            if (advanced != 1)
            {
                return InvariantFailure();
            }
        }

        if (source is not null)
        {
            await ExecuteAffectedAsync(
                connection,
                transaction,
                """
                UPDATE snapshot_keys
                SET wrapped_dek = NULL, key_state = 2
                WHERE snapshot_id IN (
                    SELECT snapshot_id FROM snapshots WHERE source_id = $source
                );
                """,
                sourceText,
                CancellationToken.None).ConfigureAwait(false);
            int deleted = await ExecuteAffectedAsync(
                connection,
                transaction,
                "DELETE FROM sources WHERE source_id = $source;",
                sourceText,
                CancellationToken.None).ConfigureAwait(false);
            if (deleted != 1)
            {
                return InvariantFailure();
            }
        }

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return SourceDeletionLifecycleOperationResult.Succeeded();
    }

    private async ValueTask<SecretStoreOperationResult> DeleteConfigurationAsync(
        SourceId sourceId,
        ConfigurationBinding binding,
        CancellationToken cancellationToken)
    {
        ProtectedRecordOwner owner =
            ProtectedRecordOwner.ForSourceConfiguration(binding.ConfigurationId);
        if (binding.Kind == SourceKind.XtreamCompatible)
        {
            return await _secretStore.DeleteCredentialsAsync(
                sourceId,
                owner,
                binding.SecretReference!,
                cancellationToken).ConfigureAwait(false);
        }

        return await _secretStore.DeleteLocatorAsync(
            sourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            owner,
            binding.LocatorReference!,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseBinding(
        PersistedBinding persisted,
        out ConfigurationBinding? binding)
    {
        binding = null;
        if (!Guid.TryParseExact(persisted.ConfigurationId, "N", out Guid configurationValue))
        {
            return false;
        }

        DomainResult<SourceConfigurationId> configurationId =
            SourceConfigurationId.Create(configurationValue);
        if (!configurationId.IsSuccess ||
            persisted.SourceKind < int.MinValue ||
            persisted.SourceKind > int.MaxValue ||
            !Enum.IsDefined((SourceKind)(int)persisted.SourceKind))
        {
            return false;
        }

        SourceKind kind = (SourceKind)(int)persisted.SourceKind;
        if (kind == SourceKind.XtreamCompatible)
        {
            DomainResult<SecretReference> reference =
                SecretReference.Parse(persisted.ConfigurationReference);
            if (!reference.IsSuccess)
            {
                return false;
            }

            binding = new ConfigurationBinding(
                configurationId.Value,
                kind,
                reference.Value!,
                LocatorReference: null);
            return true;
        }

        DomainResult<ProtectedLocatorReference> locator =
            ProtectedLocatorReference.Parse(persisted.ConfigurationReference);
        if (!locator.IsSuccess)
        {
            return false;
        }

        binding = new ConfigurationBinding(
            configurationId.Value,
            kind,
            SecretReference: null,
            locator.Value!);
        return true;
    }

    private static async Task<int> InsertJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceId,
        PersistedBinding binding,
        CancellationToken cancellationToken) =>
        await ExecuteAffectedAsync(
            connection,
            transaction,
            """
            INSERT INTO source_deletion_tombstones(
                source_id, configuration_id, source_kind, configuration_reference,
                protected_delete_completed, marked_utc)
            VALUES ($source, $configuration, $kind, $reference, 0,
                strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """,
            sourceId,
            cancellationToken,
            ("$configuration", binding.ConfigurationId),
            ("$kind", binding.SourceKind),
            ("$reference", binding.ConfigurationReference)).ConfigureAwait(false);

    private static async ValueTask<SourceRow?> ReadSourceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status, configuration_id, source_kind, configuration_reference
            FROM sources
            WHERE source_id = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        long statusValue = reader.GetInt64(0);
        bool validStatus = statusValue >= int.MinValue &&
            statusValue <= int.MaxValue &&
            Enum.IsDefined((ContentSourceStatus)(int)statusValue);
        return new SourceRow(
            validStatus,
            validStatus ? (ContentSourceStatus)(int)statusValue : default,
            new PersistedBinding(
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3)));
    }

    private static async ValueTask<DeletionJournal?> ReadJournalAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT configuration_id, source_kind, configuration_reference,
                protected_delete_completed
            FROM source_deletion_tombstones
            WHERE source_id = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DeletionJournal(
            new PersistedBinding(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2)),
            reader.GetInt64(3) == 1);
    }

    private async ValueTask<SqliteConnection> OpenWriteConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = """
                PRAGMA foreign_keys = ON;
                PRAGMA synchronous = EXTRA;
                PRAGMA busy_timeout = 5000;
                """;
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void RegisterDeleteAuthorization(
        SqliteConnection connection,
        string expectedSourceId,
        DeletionJournal expectedJournal)
    {
        connection.CreateFunction<string?, string?, long, string?, long>(
            DeleteAuthorizationFunctionName,
            (sourceId, configurationId, sourceKind, configurationReference) =>
                string.Equals(sourceId, expectedSourceId, StringComparison.Ordinal) &&
                string.Equals(
                    configurationId,
                    expectedJournal.Binding.ConfigurationId,
                    StringComparison.Ordinal) &&
                sourceKind == expectedJournal.Binding.SourceKind &&
                string.Equals(
                    configurationReference,
                    expectedJournal.Binding.ConfigurationReference,
                    StringComparison.Ordinal)
                    ? 1L
                    : 0L,
            isDeterministic: false);
    }

    private static async Task<int> ExecuteAffectedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string sourceId,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] additionalParameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$source", sourceId);
        foreach ((string name, object value) in additionalParameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool SourceMatchesPendingJournal(SourceRow source, DeletionJournal journal) =>
        source.HasValidStatus &&
        source.Status == ContentSourceStatus.DeletionPending &&
        BindingEquals(source.Binding, journal.Binding);

    private static bool BindingEquals(PersistedBinding left, PersistedBinding right) =>
        string.Equals(left.ConfigurationId, right.ConfigurationId, StringComparison.Ordinal) &&
        left.SourceKind == right.SourceKind &&
        string.Equals(
            left.ConfigurationReference,
            right.ConfigurationReference,
            StringComparison.Ordinal);

    private static bool JournalCanContinue(DeletionJournal current, DeletionJournal expected) =>
        BindingEquals(current.Binding, expected.Binding) &&
        (!expected.ProtectedDeleteCompleted || current.ProtectedDeleteCompleted);

    private static SourceDeletionLifecycleOperationResult InvariantFailure() =>
        SourceDeletionLifecycleOperationResult.Failed(
            DomainErrorCode.DomainInvariantViolation);

    private static SourceDeletionLifecycleOperationResult StorageFailure() =>
        SourceDeletionLifecycleOperationResult.Failed(
            DomainErrorCode.StorageUnavailable);

    private static void ValidateSourceId(SourceId sourceId)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException(
                "A non-empty source identifier is required.",
                nameof(sourceId));
        }
    }

    private static string Id(SourceId sourceId) => sourceId.Value.ToString("N");

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private sealed record PersistedBinding(
        string ConfigurationId,
        long SourceKind,
        string ConfigurationReference);

    private sealed record SourceRow(
        bool HasValidStatus,
        ContentSourceStatus Status,
        PersistedBinding Binding);

    private sealed record DeletionJournal(
        PersistedBinding Binding,
        bool ProtectedDeleteCompleted);

    private sealed record ConfigurationBinding(
        SourceConfigurationId ConfigurationId,
        SourceKind Kind,
        SecretReference? SecretReference,
        ProtectedLocatorReference? LocatorReference);
}
