using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

[SupportedOSPlatform("windows")]
internal sealed class SqliteRemoteM3uImportSink : IRemoteM3uImportSink, IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;
    private readonly bool _measureWriteAllocations;
    private readonly Dictionary<string, CategoryBinding> _categories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _stableKeyOccurrences = new(StringComparer.Ordinal);
    private readonly HashSet<Nonce96> _nonces = [];
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    private SQLitePCL.sqlite3_stmt? _categoryInsert;
    private SQLitePCL.sqlite3_stmt? _locatorInsert;
    private SQLitePCL.sqlite3_stmt? _channelInsert;
    private IncrementalHash? _contentHash;
    private AesGcm? _aes;
    private ContentSource? _source;
    private SnapshotId _snapshotId;
    private Guid _syncRunId;
    private DateTimeOffset _startedAt;
    private string? _entityTag;
    private DateTimeOffset? _lastModified;
    private string? _sourceText;
    private string? _snapshotText;
    private string? _keyGenerationText;
    private Guid _keyGenerationId;
    private byte[]? _dek;
    private byte[]? _wrappedDek;
    private int _written;
    private int _warnings;
    private long _measuredWriteAllocatedBytes;
    private long _measuredPreparationAllocatedBytes;
    private long _measuredLocatorAllocatedBytes;
    private long _measuredChannelAllocatedBytes;
    private long _measuredHashAllocatedBytes;

    internal SqliteRemoteM3uImportSink(string databasePath) : this(databasePath, false)
    {
    }

    internal SqliteRemoteM3uImportSink(string databasePath, bool measureWriteAllocations)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _measureWriteAllocations = measureWriteAllocations;
    }

    internal long MeasuredWriteAllocatedBytes => _measuredWriteAllocatedBytes;
    internal long MeasuredPreparationAllocatedBytes => _measuredPreparationAllocatedBytes;
    internal long MeasuredLocatorAllocatedBytes => _measuredLocatorAllocatedBytes;
    internal long MeasuredChannelAllocatedBytes => _measuredChannelAllocatedBytes;
    internal long MeasuredHashAllocatedBytes => _measuredHashAllocatedBytes;

    public async ValueTask<DomainResult<bool>> BeginAsync(
        ContentSource source,
        string? entityTag,
        DateTimeOffset? lastModified,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_transaction is not null || source.Kind != SourceKind.RemotePlaylist ||
            source.Status == ContentSourceStatus.DeletionPending)
        {
            return DomainResult.Failure<bool>(DomainErrorCode.DomainInvariantViolation);
        }

        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _source = source;
            _snapshotId = SnapshotId.Generate();
            _keyGenerationId = Guid.NewGuid();
            _syncRunId = Guid.NewGuid();
            _startedAt = DateTimeOffset.UtcNow;
            _entityTag = entityTag;
            _lastModified = lastModified?.ToUniversalTime();
            _sourceText = Id(source.Id.Value);
            _snapshotText = Id(_snapshotId.Value);
            _keyGenerationText = Id(_keyGenerationId);
            _dek = RandomNumberGenerator.GetBytes(32);
            byte[] entropy = BuildEntropy(source.Id.Value, _snapshotId.Value);
            try
            {
                _wrappedDek = ProtectedData.Protect(_dek, entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(entropy);
            }

            _contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync("PRAGMA foreign_keys = ON; PRAGMA synchronous = EXTRA; PRAGMA busy_timeout = 5000;", cancellationToken)
                .ConfigureAwait(false);
            _transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            _aes = new AesGcm(_dek, 16);
            InitializePreparedCommands();
            await UpsertSourceAsync(source, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync("""
                INSERT INTO snapshots(snapshot_id, source_id, retrieved_utc, content_hash, http_etag,
                    http_last_modified_utc, parser_version, normalization_version, schema_version,
                    item_count, warning_count, state, cache_key)
                VALUES ($snapshot, $source, $retrieved, zeroblob(32), $etag, $modified, 1, 1, 2, 0, 0, 0, NULL);
                """, cancellationToken,
                ("$snapshot", _snapshotText), ("$source", _sourceText),
                ("$retrieved", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                ("$etag", _entityTag ?? (object)DBNull.Value),
                ("$modified", _lastModified.HasValue
                    ? _lastModified.Value.ToString("O", CultureInfo.InvariantCulture)
                    : DBNull.Value)).ConfigureAwait(false);
            await ExecuteAsync(
                "INSERT INTO snapshot_keys(snapshot_id, key_generation_id, wrapped_dek, key_state) VALUES ($snapshot, $generation, $wrapped, 0);",
                cancellationToken,
                ("$snapshot", _snapshotText), ("$generation", _keyGenerationText),
                ("$wrapped", _wrappedDek)).ConfigureAwait(false);
            await ExecuteAsync("""
                INSERT INTO sync_runs(sync_run_id, source_id, started_utc, completed_utc, result_code,
                    parsed_count, persisted_count, warning_count, failure_code)
                VALUES ($run, $source, $started, NULL, NULL, 0, 0, 0, NULL);
                """, cancellationToken,
                ("$run", Id(_syncRunId)), ("$source", _sourceText),
                ("$started", _startedAt.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            return DomainResult.Success(true);
        }
        catch (OperationCanceledException)
        {
            await AbortAsync(CancellationToken.None).ConfigureAwait(false);
            return DomainResult.Failure<bool>(DomainErrorCode.OperationCancelled);
        }
        catch (Exception exception) when (exception is SqliteException or CryptographicException or IOException)
        {
            await AbortAsync(CancellationToken.None).ConfigureAwait(false);
            return DomainResult.Failure<bool>(DomainErrorCode.StorageUnavailable);
        }
    }

    public ValueTask<DomainResult<bool>> WriteAsync(
        RemoteM3uEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_transaction is null || _source is null || _dek is null || _contentHash is null)
        {
            return ValueTask.FromResult(DomainResult.Failure<bool>(DomainErrorCode.DomainInvariantViolation));
        }

        long allocatedBefore = _measureWriteAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long allocationCursor = allocatedBefore;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string groupName = string.IsNullOrWhiteSpace(entry.GroupTitle) ? "Uncategorized" : entry.GroupTitle;
            if (!_categories.TryGetValue(groupName, out CategoryBinding category))
            {
                CategoryId categoryId = CategoryId.Generate();
                DomainResult<ChannelCategory> categoryResult = ChannelCategory.Create(
                    categoryId,
                    _snapshotId,
                    entry.GroupTitle,
                    groupName,
                    _categories.Count,
                    string.IsNullOrWhiteSpace(entry.GroupTitle));
                if (!categoryResult.IsSuccess)
                {
                    return ValueTask.FromResult(DomainResult.Failure<bool>(categoryResult.Error!.Code));
                }

                string categoryText = Id(categoryId.Value);
                InsertCategory(categoryResult.Value!, categoryText, cancellationToken);
                category = new CategoryBinding(categoryId, categoryText);
                _categories.Add(groupName, category);
            }

            ChannelId channelId = ChannelId.Generate();
            string channelText = Id(channelId.Value);
            DomainResult<ChannelStableKey> stableKey = BuildStableKey(entry, cancellationToken);
            if (!stableKey.IsSuccess)
            {
                return ValueTask.FromResult(DomainResult.Failure<bool>(stableKey.Error!.Code));
            }

            (ProtectedLocatorReference streamReference, SecretStoreKey streamKey) = SecretStoreKey.IssueLocator(
                _source.Id,
                ProtectedValuePurpose.ChannelStreamLocator,
                ProtectedRecordOwner.ForChannel(channelId));
            string streamReferenceText = $"locator-ref-v1:{streamKey.RecordIdentifier:N}";
            ProtectedLocatorReference? logoReference = null;
            SecretStoreKey? logoKey = null;
            string? logoReferenceText = null;
            if (!string.IsNullOrWhiteSpace(entry.Logo))
            {
                (ProtectedLocatorReference issued, SecretStoreKey key) = SecretStoreKey.IssueLocator(
                    _source.Id,
                    ProtectedValuePurpose.ChannelLogoLocator,
                    ProtectedRecordOwner.ForChannel(channelId));
                logoReference = issued;
                logoKey = key;
                logoReferenceText = $"locator-ref-v1:{key.RecordIdentifier:N}";
            }

            DomainResult<LiveChannel> channel = LiveChannel.Create(
                channelId,
                stableKey.Value,
                _snapshotId,
                category.Id,
                entry.TvgId,
                providerPlaybackKey: null,
                entry.Name,
                entry.Number,
                logoReference,
                streamReference,
                InferContainer(entry.Locator),
                isAdultHint: null,
                entry.Warnings);
            if (!channel.IsSuccess)
            {
                return ValueTask.FromResult(DomainResult.Failure<bool>(channel.Error!.Code));
            }

            MeasureAllocation(ref allocationCursor, ref _measuredPreparationAllocatedBytes);

            EncryptAndInsert(
                channelId,
                ProtectedValuePurpose.ChannelStreamLocator,
                streamKey,
                channelText,
                streamReferenceText,
                entry.Locator,
                cancellationToken);
            if (logoKey.HasValue)
            {
                EncryptAndInsert(
                    channelId,
                    ProtectedValuePurpose.ChannelLogoLocator,
                    logoKey.Value,
                    channelText,
                    logoReferenceText!,
                    entry.Logo!,
                    cancellationToken);
            }

            MeasureAllocation(ref allocationCursor, ref _measuredLocatorAllocatedBytes);

            InsertChannel(
                channel.Value!,
                category.Text,
                channelText,
                streamReferenceText,
                logoReferenceText,
                cancellationToken);
            MeasureAllocation(ref allocationCursor, ref _measuredChannelAllocatedBytes);
            AppendHash(entry);
            _written++;
            if (entry.Warnings != ChannelNormalizationWarnings.None)
            {
                _warnings++;
            }
            MeasureAllocation(ref allocationCursor, ref _measuredHashAllocatedBytes);

            return ValueTask.FromResult(DomainResult.Success(true));
        }
        catch (OperationCanceledException)
        {
            return ValueTask.FromResult(DomainResult.Failure<bool>(DomainErrorCode.OperationCancelled));
        }
        catch (Exception exception) when (exception is SqliteException or CryptographicException or IOException)
        {
            return ValueTask.FromResult(DomainResult.Failure<bool>(DomainErrorCode.StorageUnavailable));
        }
        finally
        {
            if (_measureWriteAllocations)
            {
                _measuredWriteAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            }
        }
    }

    public async ValueTask<DomainResult<bool>> CompleteAsync(
        RemoteM3uParseResult parseResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        if (_transaction is null || _source is null || _contentHash is null ||
            parseResult.ContentKind != PlaylistContentKind.ExtendedM3uCatalog ||
            parseResult.ProcessedEntryCount != _written)
        {
            return DomainResult.Failure<bool>(DomainErrorCode.DomainInvariantViolation);
        }

        try
        {
            byte[] hash = _contentHash.GetHashAndReset();
            byte[] cache = BuildCacheKey(hash, _entityTag, _lastModified);
            try
            {
                await ExecuteAsync("""
                    UPDATE snapshots
                    SET content_hash = $hash, cache_key = $cache, item_count = $items,
                        warning_count = $warnings, state = 1
                    WHERE snapshot_id = $snapshot;
                    UPDATE snapshot_keys SET key_state = 1 WHERE snapshot_id = $snapshot;
                    UPDATE snapshot_keys SET wrapped_dek = NULL, key_state = 2
                    WHERE snapshot_id = (SELECT active_snapshot_id FROM sources WHERE source_id = $source);
                    UPDATE sources SET active_snapshot_id = $snapshot, status = $ready WHERE source_id = $source;
                    UPDATE sync_runs
                    SET completed_utc = $completed, result_code = 0, parsed_count = $items,
                        persisted_count = $items, warning_count = $warnings, failure_code = NULL
                    WHERE sync_run_id = $run;
                    """, cancellationToken,
                    ("$hash", hash), ("$cache", cache), ("$items", _written), ("$warnings", _warnings),
                    ("$snapshot", _snapshotText!), ("$source", _sourceText!),
                    ("$ready", (int)ContentSourceStatus.Ready), ("$run", Id(_syncRunId)),
                    ("$completed", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
                await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await DisposeSessionAsync().ConfigureAwait(false);
                return DomainResult.Success(true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hash);
                CryptographicOperations.ZeroMemory(cache);
            }
        }
        catch (OperationCanceledException)
        {
            return DomainResult.Failure<bool>(DomainErrorCode.OperationCancelled);
        }
        catch (Exception exception) when (exception is SqliteException or CryptographicException or IOException)
        {
            return DomainResult.Failure<bool>(DomainErrorCode.StorageUnavailable);
        }
    }

    public async ValueTask AbortAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            try
            {
                await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // Dispose still closes the transaction and connection fail-closed.
            }
        }

        await DisposeSessionAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await AbortAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task UpsertSourceAsync(ContentSource source, CancellationToken cancellationToken)
    {
        SecretStoreKey configurationKey = source.Configuration switch
        {
            RemotePlaylistSourceConfiguration remote => SecretStoreKey.ForLocator(
                source.Id,
                ProtectedValuePurpose.RemotePlaylistLocator,
                ProtectedRecordOwner.ForSourceConfiguration(remote.ConfigurationId),
                remote.LocatorReference),
            _ => throw new ArgumentException("Remote source configuration is required.", nameof(source)),
        };
        await ExecuteAsync("""
            INSERT INTO sources(source_id, configuration_id, source_kind, display_name, endpoint_scheme,
                endpoint_host, endpoint_port, configuration_reference, status, active_snapshot_id,
                created_utc, updated_utc, last_error_code)
            VALUES ($source, $configuration, $kind, $name, $scheme, $host, $port, $reference,
                $status, NULL, $created, $updated, $error)
            ON CONFLICT(source_id) DO UPDATE SET display_name=excluded.display_name,
                endpoint_scheme=excluded.endpoint_scheme, endpoint_host=excluded.endpoint_host,
                endpoint_port=excluded.endpoint_port, status=excluded.status, updated_utc=excluded.updated_utc;
            """, cancellationToken,
            ("$source", Id(source.Id.Value)), ("$configuration", Id(source.Configuration.ConfigurationId.Value)),
            ("$kind", (int)source.Kind), ("$name", source.DisplayName), ("$scheme", source.SafeEndpoint.Scheme),
            ("$host", source.SafeEndpoint.Host), ("$port", source.SafeEndpoint.Port),
            ("$reference", $"locator-ref-v1:{configurationKey.RecordIdentifier:N}"),
            ("$status", (int)source.Status),
            ("$created", source.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("$updated", source.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("$error", source.LastErrorCode.HasValue ? (int)source.LastErrorCode.Value : DBNull.Value)).ConfigureAwait(false);
    }

    private DomainResult<ChannelStableKey> BuildStableKey(
        RemoteM3uEntry entry,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(entry.TvgId))
        {
            string identity = $"tvg:{entry.TvgId}";
            int occurrence = NextOccurrence(identity);
            return ChannelStableKeyBuilder.FromM3uTvgId(
                _source!.Id,
                entry.TvgId,
                occurrence);
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] locator = Encoding.UTF8.GetBytes(entry.Locator);
        try
        {
            string fingerprintHex = Convert.ToHexString(SHA256.HashData(locator));
            DomainResult<LocatorFingerprint> fingerprint = LocatorFingerprint.Create(fingerprintHex);
            string identity = $"fallback:{entry.Name}\0{entry.GroupTitle}\0{fingerprintHex}";
            int occurrence = NextOccurrence(identity);
            return fingerprint.IsSuccess
                ? ChannelStableKeyBuilder.FromFallback(
                    _source!.Id,
                    entry.Name,
                    string.IsNullOrWhiteSpace(entry.GroupTitle) ? "Uncategorized" : entry.GroupTitle,
                    fingerprint.Value,
                    occurrence)
                : DomainResult.Failure<ChannelStableKey>(fingerprint.Error!.Code);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(locator);
        }
    }

    private void EncryptAndInsert(
        ChannelId channelId,
        ProtectedValuePurpose purpose,
        SecretStoreKey key,
        string channelText,
        string referenceText,
        string plaintextText,
        CancellationToken cancellationToken)
    {
        byte[] plaintext = Encoding.UTF8.GetBytes(plaintextText);
        byte[] nonce = NewUniqueNonce();
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] aad = BuildAad(_source!.Id.Value, _snapshotId.Value, _keyGenerationId, channelId.Value, purpose, key.RecordIdentifier);
        try
        {
            _aes!.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            cancellationToken.ThrowIfCancellationRequested();
            BindText(_locatorInsert!, 1, referenceText);
            BindText(_locatorInsert!, 2, _snapshotText!);
            BindText(_locatorInsert!, 3, _keyGenerationText!);
            BindInt(_locatorInsert!, 4, (int)ProtectedRecordOwnerKind.Channel);
            BindText(_locatorInsert!, 5, channelText);
            BindInt(_locatorInsert!, 6, (int)purpose);
            BindBlob(_locatorInsert!, 7, nonce);
            BindBlob(_locatorInsert!, 8, tag);
            BindBlob(_locatorInsert!, 9, ciphertext);
            StepAndReset(_locatorInsert!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private void InsertChannel(
        LiveChannel channel,
        string categoryText,
        string channelText,
        string streamReferenceText,
        string? logoReferenceText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BindText(_channelInsert!, 1, channelText);
        BindText(_channelInsert!, 2, _snapshotText!);
        BindText(_channelInsert!, 3, categoryText);
        BindInt(_channelInsert!, 4, channel.StableKey.AlgorithmVersion);
        BindText(_channelInsert!, 5, channel.StableKey.Value);
        BindText(_channelInsert!, 6, channel.Name);
        BindNullableInt(_channelInsert!, 7, channel.Number);
        BindText(_channelInsert!, 8, streamReferenceText);
        BindNullableText(_channelInsert!, 9, logoReferenceText);
        BindNullableText(_channelInsert!, 10, channel.ContainerHint?.ToString());
        BindInt(_channelInsert!, 11, channel.IsAdultHint == true ? 1 : 0);
        BindInt(_channelInsert!, 12, (int)channel.NormalizationWarnings);
        StepAndReset(_channelInsert!);
    }

    private void InsertCategory(
        ChannelCategory category,
        string categoryText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BindText(_categoryInsert!, 1, categoryText);
        BindText(_categoryInsert!, 2, _snapshotText!);
        BindText(_categoryInsert!, 3, category.ProviderKey ?? $"synthetic:{category.Id.Value:N}");
        BindText(_categoryInsert!, 4, category.NormalizedName);
        BindInt(_categoryInsert!, 5, category.SortOrder);
        StepAndReset(_categoryInsert!);
    }

    private void AppendHash(RemoteM3uEntry entry)
    {
        Append(entry.Name);
        Append(entry.TvgId ?? string.Empty);
        Append(entry.GroupTitle ?? string.Empty);
        Append(entry.Locator);
        Append(entry.Logo ?? string.Empty);
    }

    private void Append(string value)
    {
        int maximumByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[] bytes = ArrayPool<byte>.Shared.Rent(Math.Max(1, maximumByteCount));
        int length = 0;
        try
        {
            length = Encoding.UTF8.GetBytes(value, bytes);
            Span<byte> lengthPrefix = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, length);
            _contentHash!.AppendData(lengthPrefix);
            _contentHash.AppendData(bytes.AsSpan(0, length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes.AsSpan(0, length));
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private async Task ExecuteAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = _connection!.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void InitializePreparedCommands()
    {
        _categoryInsert = Prepare("""
            INSERT INTO categories(category_id, snapshot_id, stable_key, display_name, sort_order)
            VALUES (?, ?, ?, ?, ?);
            """);
        _locatorInsert = Prepare("""
            INSERT INTO protected_locators(locator_reference, snapshot_id, key_generation_id,
                owner_kind, owner_id, purpose, nonce, authentication_tag, ciphertext)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);
            """);
        _channelInsert = Prepare("""
            INSERT INTO channels(channel_id, snapshot_id, category_id, stable_key_version, stable_key,
                display_name, channel_number, stream_reference, logo_reference, provider_item_kind,
                provider_item_id, container_hint, is_adult, warning_flags)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, NULL, NULL, ?, ?, ?);
            """);
    }

    private SQLitePCL.sqlite3_stmt Prepare(string sql)
    {
        int result = SQLitePCL.raw.sqlite3_prepare_v2(_connection!.Handle, sql, out SQLitePCL.sqlite3_stmt statement);
        if (result != SQLitePCL.raw.SQLITE_OK)
        {
            throw new IOException("Catalog statement preparation failed.");
        }

        return statement;
    }

    private async ValueTask DisposeSessionAsync()
    {
        if (_categoryInsert is not null)
        {
            SQLitePCL.raw.sqlite3_finalize(_categoryInsert);
        }

        if (_locatorInsert is not null)
        {
            SQLitePCL.raw.sqlite3_finalize(_locatorInsert);
        }

        if (_channelInsert is not null)
        {
            SQLitePCL.raw.sqlite3_finalize(_channelInsert);
        }

        _categoryInsert = null;
        _locatorInsert = null;
        _channelInsert = null;
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _transaction = null;
        _connection = null;
        _contentHash?.Dispose();
        _contentHash = null;
        _aes?.Dispose();
        _aes = null;
        _categories.Clear();
        _stableKeyOccurrences.Clear();
        _nonces.Clear();
        _source = null;
        _snapshotId = default;
        _syncRunId = default;
        _startedAt = default;
        _entityTag = null;
        _lastModified = null;
        _sourceText = null;
        _snapshotText = null;
        _keyGenerationText = null;
        _keyGenerationId = default;
        _written = 0;
        _warnings = 0;
        Zero(_dek);
        Zero(_wrappedDek);
        _dek = null;
        _wrappedDek = null;
    }

    private static byte[] BuildEntropy(Guid sourceId, Guid snapshotId)
    {
        byte[] context = Encoding.UTF8.GetBytes($"PROTECTED-CATALOG-DEK-V1\0{sourceId:N}\0{snapshotId:N}");
        try { return SHA256.HashData(context); }
        finally { Zero(context); }
    }

    private static byte[] BuildCacheKey(
        byte[] contentHash,
        string? entityTag,
        DateTimeOffset? lastModified)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(contentHash);
        hash.AppendData([1, 1, 2]);
        Append(entityTag ?? string.Empty);
        Append(lastModified?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        return hash.GetHashAndReset();

        void Append(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                Span<byte> length = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                hash.AppendData(length);
                hash.AppendData(bytes);
            }
            finally
            {
                Zero(bytes);
            }
        }
    }

    private static byte[] BuildAad(Guid source, Guid snapshot, Guid generation, Guid channel, ProtectedValuePurpose purpose, Guid reference)
    {
        byte[] aad = new byte[81];
        int offset = 0;
        WriteGuid(source); WriteGuid(snapshot); WriteGuid(generation); WriteGuid(channel);
        aad[offset++] = (byte)purpose;
        WriteGuid(reference);
        return aad;

        void WriteGuid(Guid value)
        {
            value.TryWriteBytes(aad.AsSpan(offset, 16), bigEndian: true, out int written);
            offset += written;
        }
    }

    private static ChannelContainerHint? InferContainer(string locator) =>
        locator.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? ChannelContainerHint.Hls : null;

    private static string Id(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    private int NextOccurrence(string identity)
    {
        _stableKeyOccurrences.TryGetValue(identity, out int occurrence);
        _stableKeyOccurrences[identity] = checked(occurrence + 1);
        return occurrence;
    }

    private byte[] NewUniqueNonce()
    {
        while (true)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            if (_nonces.Add(Nonce96.From(nonce)))
            {
                return nonce;
            }

            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null) CryptographicOperations.ZeroMemory(value);
    }

    private static void BindText(SQLitePCL.sqlite3_stmt statement, int index, string value) =>
        EnsureBind(SQLitePCL.raw.sqlite3_bind_text(statement, index, value));

    private static void BindNullableText(SQLitePCL.sqlite3_stmt statement, int index, string? value) =>
        EnsureBind(value is null
            ? SQLitePCL.raw.sqlite3_bind_null(statement, index)
            : SQLitePCL.raw.sqlite3_bind_text(statement, index, value));

    private static void BindBlob(SQLitePCL.sqlite3_stmt statement, int index, byte[] value) =>
        EnsureBind(SQLitePCL.raw.sqlite3_bind_blob(statement, index, value));

    private static void BindInt(SQLitePCL.sqlite3_stmt statement, int index, int value) =>
        EnsureBind(SQLitePCL.raw.sqlite3_bind_int(statement, index, value));

    private static void BindNullableInt(SQLitePCL.sqlite3_stmt statement, int index, int? value) =>
        EnsureBind(value.HasValue
            ? SQLitePCL.raw.sqlite3_bind_int(statement, index, value.Value)
            : SQLitePCL.raw.sqlite3_bind_null(statement, index));

    private static void EnsureBind(int result)
    {
        if (result != SQLitePCL.raw.SQLITE_OK)
        {
            throw new IOException("Catalog value binding failed.");
        }
    }

    private static void StepAndReset(SQLitePCL.sqlite3_stmt statement)
    {
        int step = SQLitePCL.raw.sqlite3_step(statement);
        int reset = SQLitePCL.raw.sqlite3_reset(statement);
        int clear = SQLitePCL.raw.sqlite3_clear_bindings(statement);
        if (step != SQLitePCL.raw.SQLITE_DONE || reset != SQLitePCL.raw.SQLITE_OK || clear != SQLitePCL.raw.SQLITE_OK)
        {
            throw new IOException("Catalog statement execution failed.");
        }
    }

    private void MeasureAllocation(ref long cursor, ref long total)
    {
        if (!_measureWriteAllocations)
        {
            return;
        }

        long current = GC.GetAllocatedBytesForCurrentThread();
        total += current - cursor;
        cursor = current;
    }

    private readonly record struct Nonce96(uint First, uint Second, uint Third)
    {
        internal static Nonce96 From(ReadOnlySpan<byte> value) => new(
            BinaryPrimitives.ReadUInt32BigEndian(value),
            BinaryPrimitives.ReadUInt32BigEndian(value[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(value[8..]));
    }

    private readonly record struct CategoryBinding(CategoryId Id, string Text);

}
