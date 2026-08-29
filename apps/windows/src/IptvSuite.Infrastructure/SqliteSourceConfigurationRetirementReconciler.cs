using System.Globalization;
using System.Runtime.Versioning;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class SqliteSourceConfigurationRetirementReconciler
    : ISourceConfigurationRetirementReconciler
{
    internal const int MaximumBatchSize = 100;
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;
    private readonly ISecretStore _secretStore;

    public SqliteSourceConfigurationRetirementReconciler(
        string databasePath,
        ISecretStore secretStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _database = new SqliteCatalogDatabase(_databasePath);
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public async ValueTask<SourceConfigurationRetirementReconciliationResult> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<RetiredConfiguration> batch;
        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            batch = await ReadBatchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return SourceConfigurationRetirementReconciliationResult.Failed(
                0,
                0,
                DomainError.Create(DomainErrorCode.StorageUnavailable));
        }

        int attempted = 0;
        int completed = 0;
        foreach (RetiredConfiguration retired in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;
            if (retired.IsCurrentConfiguration)
            {
                return SourceConfigurationRetirementReconciliationResult.Failed(
                    attempted,
                    completed,
                    DomainError.Create(DomainErrorCode.DomainInvariantViolation));
            }

            SecretStoreOperationResult deleted;
            try
            {
                deleted = await DeleteProtectedRecordAsync(retired, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return SourceConfigurationRetirementReconciliationResult.Failed(
                    attempted,
                    completed,
                    DomainError.Create(DomainErrorCode.StorageUnavailable));
            }

            if (!deleted.IsSuccess)
            {
                return SourceConfigurationRetirementReconciliationResult.Failed(
                    attempted,
                    completed,
                    DomainError.Create(DomainErrorCode.StorageUnavailable));
            }

            try
            {
                if (!await DeleteJournalEntryAsync(retired).ConfigureAwait(false))
                {
                    return SourceConfigurationRetirementReconciliationResult.Failed(
                        attempted,
                        completed,
                        DomainError.Create(DomainErrorCode.StorageUnavailable));
                }
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return SourceConfigurationRetirementReconciliationResult.Failed(
                    attempted,
                    completed,
                    DomainError.Create(DomainErrorCode.StorageUnavailable));
            }

            completed++;
        }

        bool hasRemaining;
        try
        {
            hasRemaining = await HasRemainingAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return SourceConfigurationRetirementReconciliationResult.Failed(
                attempted,
                completed,
                DomainError.Create(DomainErrorCode.StorageUnavailable));
        }

        return SourceConfigurationRetirementReconciliationResult.Completed(
            attempted,
            completed,
            hasRemaining);
    }

    private async ValueTask<IReadOnlyList<RetiredConfiguration>> ReadBatchAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(
            SqliteOpenMode.ReadOnly,
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT retired.source_id, retired.configuration_id, retired.source_kind,
                   retired.configuration_reference,
                   EXISTS (
                       SELECT 1
                       FROM sources AS active
                       WHERE active.source_id = retired.source_id
                         AND active.configuration_id = retired.configuration_id
                         AND active.source_kind = retired.source_kind
                         AND active.configuration_reference = retired.configuration_reference
                   )
            FROM source_configuration_retirements AS retired
            ORDER BY retired.retired_utc, retired.source_id, retired.configuration_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", MaximumBatchSize);
        var rows = new List<RetiredConfiguration>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ParseRetiredConfiguration(reader));
        }

        return rows;
    }

    private ValueTask<SecretStoreOperationResult> DeleteProtectedRecordAsync(
        RetiredConfiguration retired,
        CancellationToken cancellationToken)
    {
        ProtectedRecordOwner owner =
            ProtectedRecordOwner.ForSourceConfiguration(retired.ConfigurationId);
        return retired.Kind switch
        {
            SourceKind.XtreamCompatible => _secretStore.DeleteCredentialsAsync(
                retired.SourceId,
                owner,
                retired.SecretReference!,
                cancellationToken),
            SourceKind.RemotePlaylist => _secretStore.DeleteLocatorAsync(
                retired.SourceId,
                ProtectedValuePurpose.RemotePlaylistLocator,
                owner,
                retired.LocatorReference!,
                cancellationToken),
            _ => ValueTask.FromResult(SecretStoreOperationResult.Failed(
                SecretStoreFailure.StorageUnavailable)),
        };
    }

    private async ValueTask<bool> DeleteJournalEntryAsync(RetiredConfiguration retired)
    {
        await using SqliteConnection connection = await OpenAsync(
            SqliteOpenMode.ReadWrite,
            CancellationToken.None).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM source_configuration_retirements
            WHERE source_id = $source
              AND configuration_id = $configuration
              AND source_kind = $kind
              AND configuration_reference = $reference;
            """;
        command.Parameters.AddWithValue("$source", Id(retired.SourceId.Value));
        command.Parameters.AddWithValue(
            "$configuration",
            Id(retired.ConfigurationId.Value));
        command.Parameters.AddWithValue("$kind", (int)retired.Kind);
        command.Parameters.AddWithValue("$reference", retired.SerializedReference);
        return await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false) == 1;
    }

    private async ValueTask<bool> HasRemainingAsync()
    {
        await using SqliteConnection connection = await OpenAsync(
            SqliteOpenMode.ReadOnly,
            CancellationToken.None).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM source_configuration_retirements);";
        object? value = await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
    }

    private async ValueTask<SqliteConnection> OpenAsync(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (mode != SqliteOpenMode.ReadOnly)
            {
                await using SqliteCommand pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static RetiredConfiguration ParseRetiredConfiguration(SqliteDataReader reader)
    {
        SourceId sourceId = ParseSourceId(reader.GetString(0));
        SourceConfigurationId configurationId = ParseConfigurationId(reader.GetString(1));
        SourceKind kind = (SourceKind)reader.GetInt32(2);
        string serializedReference = reader.GetString(3);
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException("Retired source kind is invalid.");
        }

        DomainResult<SecretReference>? secret = kind == SourceKind.XtreamCompatible
            ? SecretReference.Parse(serializedReference)
            : null;
        DomainResult<ProtectedLocatorReference>? locator = kind == SourceKind.RemotePlaylist
            ? ProtectedLocatorReference.Parse(serializedReference)
            : null;
        if (secret is { IsSuccess: false } || locator is { IsSuccess: false })
        {
            throw new InvalidDataException("Retired source reference is invalid.");
        }

        return new RetiredConfiguration(
            sourceId,
            configurationId,
            kind,
            serializedReference,
            secret?.Value,
            locator?.Value,
            reader.GetInt32(4) == 1);
    }

    private static SourceId ParseSourceId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out Guid guid))
        {
            throw new InvalidDataException("Retired source identifier is invalid.");
        }

        DomainResult<SourceId> result = SourceId.Create(guid);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidDataException("Retired source identifier is invalid.");
    }

    private static SourceConfigurationId ParseConfigurationId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out Guid guid))
        {
            throw new InvalidDataException("Retired configuration identifier is invalid.");
        }

        DomainResult<SourceConfigurationId> result = SourceConfigurationId.Create(guid);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidDataException("Retired configuration identifier is invalid.");
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private static string Id(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    private sealed record RetiredConfiguration(
        SourceId SourceId,
        SourceConfigurationId ConfigurationId,
        SourceKind Kind,
        string SerializedReference,
        SecretReference? SecretReference,
        ProtectedLocatorReference? LocatorReference,
        bool IsCurrentConfiguration);
}
