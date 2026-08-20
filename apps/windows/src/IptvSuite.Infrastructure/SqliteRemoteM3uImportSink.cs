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
    private readonly Dictionary<string, CategoryId> _categories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _stableKeyOccurrences = new(StringComparer.Ordinal);
    private readonly HashSet<Nonce96> _nonces = [];
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    private SqliteCommand? _categoryInsert;
    private SqliteCommand? _locatorInsert;
    private SqliteCommand? _channelInsert;
    private IncrementalHash? _contentHash;
    private AesGcm? _aes;
    private ContentSource? _source;
    private SnapshotId _snapshotId;
    private Guid _syncRunId;
    private DateTimeOffset _startedAt;
    private Guid _keyGenerationId;
    private byte[]? _dek;
    private byte[]? _wrappedDek;
    private int _written;
    private int _warnings;

    internal SqliteRemoteM3uImportSink(string databasePath)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public async ValueTask<DomainResult<bool>> BeginAsync(
        ContentSource source,
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
            _syncRunId = Guid.NewGuid();
            _startedAt = DateTimeOffset.UtcNow;
            _keyGenerationId = Guid.NewGuid();
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
            await InitializePreparedCommandsAsync(cancellationToken).ConfigureAwait(false);
            await UpsertSourceAsync(source, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync("""
                INSERT INTO snapshots(snapshot_id, source_id, retrieved_utc, content_hash, http_etag,
                    http_last_modified_utc, parser_version, normalization_version, schema_version,
                    item_count, warning_count, state, cache_key)
                VALUES ($snapshot, $source, $retrieved, zeroblob(32), NULL, NULL, 1, 1, 2, 0, 0, 0, NULL);
                """, cancellationToken,
                ("$snapshot", Id(_snapshotId.Value)), ("$source", Id(source.Id.Value)),
                ("$retrieved", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
            await ExecuteAsync(
                "INSERT INTO snapshot_keys(snapshot_id, key_generation_id, wrapped_dek, key_state) VALUES ($snapshot, $generation, $wrapped, 0);",
                cancellationToken,
                ("$snapshot", Id(_snapshotId.Value)), ("$generation", Id(_keyGenerationId)),
                ("$wrapped", _wrappedDek)).ConfigureAwait(false);
            await ExecuteAsync("""
                INSERT INTO sync_runs(sync_run_id, source_id, started_utc, completed_utc, result_code,
                    parsed_count, persisted_count, warning_count, failure_code)
                VALUES ($run, $source, $started, NULL, NULL, 0, 0, 0, NULL);
                """, cancellationToken,
                ("$run", Id(_syncRunId)), ("$source", Id(source.Id.Value)),
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

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string groupName = string.IsNullOrWhiteSpace(entry.GroupTitle) ? "Uncategorized" : entry.GroupTitle;
            if (!_categories.TryGetValue(groupName, out CategoryId categoryId))
            {
                categoryId = CategoryId.Generate();
                DomainResult<ChannelCategory> category = ChannelCategory.Create(
                    categoryId,
                    _snapshotId,
                    entry.GroupTitle,
                    groupName,
                    _categories.Count,
                    string.IsNullOrWhiteSpace(entry.GroupTitle));
                if (!category.IsSuccess)
                {
                    return ValueTask.FromResult(DomainResult.Failure<bool>(category.Error!.Code));
                }

                InsertCategory(category.Value!, cancellationToken);
                _categories.Add(groupName, categoryId);
            }

            ChannelId channelId = ChannelId.Generate();
            DomainResult<ChannelStableKey> stableKey = BuildStableKey(entry, cancellationToken);
            if (!stableKey.IsSuccess)
            {
                return ValueTask.FromResult(DomainResult.Failure<bool>(stableKey.Error!.Code));
            }

            (ProtectedLocatorReference streamReference, SecretStoreKey streamKey) = SecretStoreKey.IssueLocator(
                _source.Id,
                ProtectedValuePurpose.ChannelStreamLocator,
                ProtectedRecordOwner.ForChannel(channelId));
            ProtectedLocatorReference? logoReference = null;
            SecretStoreKey? logoKey = null;
            if (!string.IsNullOrWhiteSpace(entry.Logo))
            {
                (ProtectedLocatorReference issued, SecretStoreKey key) = SecretStoreKey.IssueLocator(
                    _source.Id,
                    ProtectedValuePurpose.ChannelLogoLocator,
                    ProtectedRecordOwner.ForChannel(channelId));
                logoReference = issued;
                logoKey = key;
            }

            DomainResult<LiveChannel> channel = LiveChannel.Create(
                channelId,
                stableKey.Value,
                _snapshotId,
                categoryId,
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

            EncryptAndInsert(
                channelId,
                ProtectedValuePurpose.ChannelStreamLocator,
                streamKey,
                entry.Locator,
                cancellationToken);
            if (logoKey.HasValue)
            {
                EncryptAndInsert(
                    channelId,
                    ProtectedValuePurpose.ChannelLogoLocator,
                    logoKey.Value,
                    entry.Logo!,
                    cancellationToken);
            }

            InsertChannel(channel.Value!, streamKey, logoKey, cancellationToken);
            AppendHash(entry);
            _written++;
            if (entry.Warnings != ChannelNormalizationWarnings.None)
            {
                _warnings++;
            }

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
            byte[] cache = BuildCacheKey(hash);
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
                    ("$snapshot", Id(_snapshotId.Value)), ("$source", Id(_source.Id.Value)),
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
            SqliteParameterCollection parameters = _locatorInsert!.Parameters;
            parameters["$reference"].Value = $"locator-ref-v1:{key.RecordIdentifier:N}";
            parameters["$snapshot"].Value = Id(_snapshotId.Value);
            parameters["$generation"].Value = Id(_keyGenerationId);
            parameters["$ownerKind"].Value = (int)ProtectedRecordOwnerKind.Channel;
            parameters["$owner"].Value = Id(channelId.Value);
            parameters["$purpose"].Value = (int)purpose;
            parameters["$nonce"].Value = nonce;
            parameters["$tag"].Value = tag;
            parameters["$ciphertext"].Value = ciphertext;
            cancellationToken.ThrowIfCancellationRequested();
            _locatorInsert.ExecuteNonQuery();
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
        SecretStoreKey streamKey,
        SecretStoreKey? logoKey,
        CancellationToken cancellationToken)
    {
        SqliteParameterCollection parameters = _channelInsert!.Parameters;
        parameters["$id"].Value = Id(channel.Id.Value);
        parameters["$snapshot"].Value = Id(_snapshotId.Value);
        parameters["$category"].Value = Id(channel.CategoryId.Value);
        parameters["$version"].Value = channel.StableKey.AlgorithmVersion;
        parameters["$key"].Value = channel.StableKey.Value;
        parameters["$name"].Value = channel.Name;
        parameters["$number"].Value = channel.Number ?? (object)DBNull.Value;
        parameters["$stream"].Value = $"locator-ref-v1:{streamKey.RecordIdentifier:N}";
        parameters["$logo"].Value = logoKey.HasValue
            ? $"locator-ref-v1:{logoKey.Value.RecordIdentifier:N}"
            : DBNull.Value;
        parameters["$container"].Value = channel.ContainerHint?.ToString() ?? (object)DBNull.Value;
        parameters["$adult"].Value = channel.IsAdultHint == true ? 1 : 0;
        parameters["$warnings"].Value = (int)channel.NormalizationWarnings;
        cancellationToken.ThrowIfCancellationRequested();
        _channelInsert.ExecuteNonQuery();
    }

    private void InsertCategory(ChannelCategory category, CancellationToken cancellationToken)
    {
        SqliteParameterCollection parameters = _categoryInsert!.Parameters;
        parameters["$id"].Value = Id(category.Id.Value);
        parameters["$snapshot"].Value = Id(_snapshotId.Value);
        parameters["$key"].Value = category.ProviderKey ?? $"synthetic:{category.Id.Value:N}";
        parameters["$name"].Value = category.NormalizedName;
        parameters["$sort"].Value = category.SortOrder;
        cancellationToken.ThrowIfCancellationRequested();
        _categoryInsert.ExecuteNonQuery();
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

    private async Task InitializePreparedCommandsAsync(CancellationToken cancellationToken)
    {
        _categoryInsert = CreatePreparedCommand("""
            INSERT INTO categories(category_id, snapshot_id, stable_key, display_name, sort_order)
            VALUES ($id, $snapshot, $key, $name, $sort);
            """, "$id", "$snapshot", "$key", "$name", "$sort");
        _locatorInsert = CreatePreparedCommand("""
            INSERT INTO protected_locators(locator_reference, snapshot_id, key_generation_id,
                owner_kind, owner_id, purpose, nonce, authentication_tag, ciphertext)
            VALUES ($reference, $snapshot, $generation, $ownerKind, $owner, $purpose, $nonce, $tag, $ciphertext);
            """, "$reference", "$snapshot", "$generation", "$ownerKind", "$owner", "$purpose", "$nonce", "$tag", "$ciphertext");
        _channelInsert = CreatePreparedCommand("""
            INSERT INTO channels(channel_id, snapshot_id, category_id, stable_key_version, stable_key,
                display_name, channel_number, stream_reference, logo_reference, provider_item_kind,
                provider_item_id, container_hint, is_adult, warning_flags)
            VALUES ($id, $snapshot, $category, $version, $key, $name, $number, $stream, $logo,
                NULL, NULL, $container, $adult, $warnings);
            """, "$id", "$snapshot", "$category", "$version", "$key", "$name", "$number", "$stream", "$logo", "$container", "$adult", "$warnings");
        await _categoryInsert.PrepareAsync(cancellationToken).ConfigureAwait(false);
        await _locatorInsert.PrepareAsync(cancellationToken).ConfigureAwait(false);
        await _channelInsert.PrepareAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteCommand CreatePreparedCommand(string sql, params string[] parameterNames)
    {
        SqliteCommand command = _connection!.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (string parameterName in parameterNames)
        {
            command.Parameters.Add(new SqliteParameter(parameterName, DBNull.Value));
        }

        return command;
    }

    private async ValueTask DisposeSessionAsync()
    {
        if (_categoryInsert is not null)
        {
            await _categoryInsert.DisposeAsync().ConfigureAwait(false);
        }

        if (_locatorInsert is not null)
        {
            await _locatorInsert.DisposeAsync().ConfigureAwait(false);
        }

        if (_channelInsert is not null)
        {
            await _channelInsert.DisposeAsync().ConfigureAwait(false);
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

    private static byte[] BuildCacheKey(byte[] contentHash)
    {
        byte[] material = new byte[contentHash.Length + 3];
        contentHash.CopyTo(material, 0);
        material[^3] = 1;
        material[^2] = 1;
        material[^1] = 2;
        try { return SHA256.HashData(material); }
        finally { Zero(material); }
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

    private readonly record struct Nonce96(uint First, uint Second, uint Third)
    {
        internal static Nonce96 From(ReadOnlySpan<byte> value) => new(
            BinaryPrimitives.ReadUInt32BigEndian(value),
            BinaryPrimitives.ReadUInt32BigEndian(value[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(value[8..]));
    }

}
