using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

internal sealed record CatalogLocatorPlaintext(
    ChannelId ChannelId,
    ProtectedValuePurpose Purpose,
    ProtectedLocatorReference Reference,
    ReadOnlyMemory<byte> Plaintext);

internal sealed record CatalogSnapshotBatch(
    ContentSource Source,
    PlaylistSnapshot Snapshot,
    IReadOnlyList<ChannelCategory> Categories,
    IReadOnlyList<LiveChannel> Channels,
    IReadOnlyList<CatalogLocatorPlaintext> Locators);

internal enum CatalogActivationFaultPoint
{
    None,
    BeforeActivePointerSwitch,
}

[SupportedOSPlatform("windows")]
internal sealed class SqliteCatalogSnapshotWriter
{
    private const int DekSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;

    internal SqliteCatalogSnapshotWriter(string databasePath)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    internal async ValueTask ActivateAsync(
        CatalogSnapshotBatch batch,
        CatalogActivationFaultPoint faultPoint = CatalogActivationFaultPoint.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateBatch(batch);
        cancellationToken.ThrowIfCancellationRequested();
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);

        byte[] dek = GC.AllocateUninitializedArray<byte>(DekSize);
        byte[] entropy = BuildWrapEntropy(batch.Source.Id, batch.Snapshot.Id);
        byte[]? wrappedDek = null;
        try
        {
            RandomNumberGenerator.Fill(dek);
            wrappedDek = ProtectedData.Protect(dek, entropy, DataProtectionScope.CurrentUser);
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await UpsertSourceAsync(connection, transaction, batch.Source, cancellationToken).ConfigureAwait(false);
            await InsertSnapshotAsync(connection, transaction, batch.Snapshot, cancellationToken).ConfigureAwait(false);
            Guid keyGenerationId = Guid.NewGuid();
            await InsertSnapshotKeyAsync(
                connection,
                transaction,
                batch.Snapshot.Id,
                keyGenerationId,
                wrappedDek,
                cancellationToken).ConfigureAwait(false);
            await InsertCategoriesAsync(connection, transaction, batch.Categories, cancellationToken).ConfigureAwait(false);
            await InsertChannelsAsync(connection, transaction, batch.Channels, cancellationToken).ConfigureAwait(false);
            await InsertLocatorsAsync(
                connection,
                transaction,
                batch,
                keyGenerationId,
                dek,
                cancellationToken).ConfigureAwait(false);

            if (faultPoint == CatalogActivationFaultPoint.BeforeActivePointerSwitch)
            {
                throw new InvalidOperationException("Injected catalog activation fault.");
            }

            await RetirePreviousSnapshotKeyAsync(
                connection,
                transaction,
                batch.Source.Id,
                cancellationToken).ConfigureAwait(false);
            await SetActiveSnapshotAsync(
                connection,
                transaction,
                batch.Source.Id,
                batch.Snapshot.Id,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(entropy);
            if (wrappedDek is not null)
            {
                CryptographicOperations.ZeroMemory(wrappedDek);
            }
        }
    }

    private static void ValidateBatch(CatalogSnapshotBatch batch)
    {
        if (batch.Source.Id.IsEmpty || batch.Snapshot.SourceId != batch.Source.Id ||
            batch.Snapshot.State != PlaylistSnapshotState.Complete ||
            batch.Snapshot.ItemCount != batch.Channels.Count ||
            batch.Categories.Any(category => category.SnapshotId != batch.Snapshot.Id) ||
            batch.Channels.Any(channel =>
                channel.SnapshotId != batch.Snapshot.Id || channel.StableKey.SourceId != batch.Source.Id) ||
            batch.Channels.Select(channel => channel.Id).Distinct().Count() != batch.Channels.Count ||
            batch.Categories.Select(category => category.Id).Distinct().Count() != batch.Categories.Count)
        {
            throw new ArgumentException("Catalog snapshot aggregate is inconsistent.", nameof(batch));
        }

        var channels = batch.Channels.ToDictionary(channel => channel.Id);
        var expected = new HashSet<(ChannelId ChannelId, ProtectedValuePurpose Purpose, ProtectedLocatorReference Reference)>();
        foreach (LiveChannel channel in batch.Channels)
        {
            if (channel.StreamReference is not null)
            {
                expected.Add((channel.Id, ProtectedValuePurpose.ChannelStreamLocator, channel.StreamReference));
            }

            if (channel.LogoReference is not null)
            {
                expected.Add((channel.Id, ProtectedValuePurpose.ChannelLogoLocator, channel.LogoReference));
            }
        }

        var actual = new HashSet<(ChannelId, ProtectedValuePurpose, ProtectedLocatorReference)>();
        foreach (CatalogLocatorPlaintext locator in batch.Locators)
        {
            if (!channels.ContainsKey(locator.ChannelId) || locator.Plaintext.IsEmpty ||
                locator.Plaintext.Length > 65_536 ||
                locator.Purpose is not (ProtectedValuePurpose.ChannelStreamLocator or
                    ProtectedValuePurpose.ChannelLogoLocator) ||
                !actual.Add((locator.ChannelId, locator.Purpose, locator.Reference)))
            {
                throw new ArgumentException("Catalog locator tuple is invalid.", nameof(batch));
            }
        }

        if (!expected.SetEquals(actual))
        {
            throw new ArgumentException("Catalog locator set is incomplete.", nameof(batch));
        }
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, null, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "PRAGMA synchronous = EXTRA;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ContentSource source,
        CancellationToken cancellationToken)
    {
        SecretStoreKey configurationKey = source.Configuration switch
        {
            XtreamSourceConfiguration xtream => SecretStoreKey.ForCredentials(
                source.Id,
                ProtectedRecordOwner.ForSourceConfiguration(xtream.ConfigurationId),
                xtream.CredentialsReference),
            RemotePlaylistSourceConfiguration remote => SecretStoreKey.ForLocator(
                source.Id,
                ProtectedValuePurpose.RemotePlaylistLocator,
                ProtectedRecordOwner.ForSourceConfiguration(remote.ConfigurationId),
                remote.LocatorReference),
            _ => throw new ArgumentException("Source configuration is unsupported.", nameof(source)),
        };
        const string sql = """
            INSERT INTO sources(
                source_id, configuration_id, source_kind, display_name, endpoint_scheme, endpoint_host,
                endpoint_port, configuration_reference, status, active_snapshot_id, created_utc, updated_utc,
                last_error_code)
            VALUES ($source, $configuration, $kind, $name, $scheme, $host, $port, $reference, $status,
                NULL, $created, $updated, $error)
            ON CONFLICT(source_id) DO UPDATE SET
                configuration_id = excluded.configuration_id,
                source_kind = excluded.source_kind,
                display_name = excluded.display_name,
                endpoint_scheme = excluded.endpoint_scheme,
                endpoint_host = excluded.endpoint_host,
                endpoint_port = excluded.endpoint_port,
                configuration_reference = excluded.configuration_reference,
                status = excluded.status,
                updated_utc = excluded.updated_utc,
                last_error_code = excluded.last_error_code;
            """;
        await ExecuteAsync(connection, transaction, sql, cancellationToken,
            ("$source", Id(source.Id.Value)),
            ("$configuration", Id(source.Configuration.ConfigurationId.Value)),
            ("$kind", (int)source.Kind),
            ("$name", source.DisplayName),
            ("$scheme", source.SafeEndpoint.Scheme),
            ("$host", source.SafeEndpoint.Host),
            ("$port", source.SafeEndpoint.Port),
            ("$reference", $"{(configurationKey.ReferenceKind == ProtectedReferenceKind.Secret ? "secret-ref-v1:" : "locator-ref-v1:")}{configurationKey.RecordIdentifier:N}"),
            ("$status", (int)source.Status),
            ("$created", Timestamp(source.CreatedAt)),
            ("$updated", Timestamp(source.UpdatedAt)),
            ("$error", source.LastErrorCode.HasValue ? (int)source.LastErrorCode.Value : DBNull.Value))
            .ConfigureAwait(false);
    }

    private static Task InsertSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlaylistSnapshot snapshot,
        CancellationToken cancellationToken) => ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO snapshots(snapshot_id, source_id, retrieved_utc, content_hash, http_etag,
                http_last_modified_utc, parser_version, normalization_version, schema_version,
                item_count, warning_count, state)
            VALUES ($id, $source, $retrieved, $hash, $etag, $modified, $parser, $normalization,
                $schema, $items, $warnings, $state);
            """,
            cancellationToken,
            ("$id", Id(snapshot.Id.Value)),
            ("$source", Id(snapshot.SourceId.Value)),
            ("$retrieved", Timestamp(snapshot.RetrievedAt)),
            ("$hash", Convert.FromHexString(snapshot.ContentHash)),
            ("$etag", snapshot.EntityTag ?? (object)DBNull.Value),
            ("$modified", snapshot.LastModified.HasValue ? Timestamp(snapshot.LastModified.Value) : DBNull.Value),
            ("$parser", snapshot.ParserVersion),
            ("$normalization", snapshot.NormalizationVersion),
            ("$schema", snapshot.SchemaVersion),
            ("$items", snapshot.ItemCount),
            ("$warnings", snapshot.WarningCount),
            ("$state", (int)snapshot.State));

    private static Task InsertSnapshotKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SnapshotId snapshotId,
        Guid keyGenerationId,
        byte[] wrappedDek,
        CancellationToken cancellationToken) => ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO snapshot_keys(snapshot_id, key_generation_id, wrapped_dek, key_state) VALUES ($snapshot, $generation, $wrapped, 1);",
            cancellationToken,
            ("$snapshot", Id(snapshotId.Value)),
            ("$generation", Id(keyGenerationId)),
            ("$wrapped", wrappedDek));

    private static async Task InsertCategoriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ChannelCategory> categories,
        CancellationToken cancellationToken)
    {
        foreach (ChannelCategory category in categories)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO categories(category_id, snapshot_id, stable_key, display_name, sort_order) VALUES ($id, $snapshot, $key, $name, $sort);",
                cancellationToken,
                ("$id", Id(category.Id.Value)), ("$snapshot", Id(category.SnapshotId.Value)),
                ("$key", category.ProviderKey ?? $"synthetic:{category.Id.Value:N}"),
                ("$name", category.NormalizedName), ("$sort", category.SortOrder)).ConfigureAwait(false);
        }
    }

    private static async Task InsertChannelsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<LiveChannel> channels,
        CancellationToken cancellationToken)
    {
        foreach (LiveChannel channel in channels)
        {
            string? streamReference = channel.StreamReference is null ? null : LocatorReference(channel, ProtectedValuePurpose.ChannelStreamLocator, channel.StreamReference);
            string? logoReference = channel.LogoReference is null ? null : LocatorReference(channel, ProtectedValuePurpose.ChannelLogoLocator, channel.LogoReference);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO channels(channel_id, snapshot_id, category_id, stable_key_version, stable_key,
                    display_name, channel_number, stream_reference, logo_reference, provider_item_kind,
                    provider_item_id, container_hint, is_adult, warning_flags)
                VALUES ($id, $snapshot, $category, $version, $key, $name, $number, $stream, $logo,
                    $providerKind, $providerId, $container, $adult, $warnings);
                """, cancellationToken,
                ("$id", Id(channel.Id.Value)), ("$snapshot", Id(channel.SnapshotId.Value)),
                ("$category", Id(channel.CategoryId.Value)), ("$version", channel.StableKey.AlgorithmVersion),
                ("$key", channel.StableKey.Value), ("$name", channel.Name),
                ("$number", channel.Number ?? (object)DBNull.Value), ("$stream", streamReference ?? (object)DBNull.Value),
                ("$logo", logoReference ?? (object)DBNull.Value),
                ("$providerKind", channel.ProviderPlaybackKey.HasValue ? 1 : DBNull.Value),
                ("$providerId", channel.ProviderPlaybackKey?.Value ?? (object)DBNull.Value),
                ("$container", channel.ContainerHint?.ToString() ?? (object)DBNull.Value),
                ("$adult", channel.IsAdultHint == true ? 1 : 0), ("$warnings", (int)channel.NormalizationWarnings))
                .ConfigureAwait(false);
        }
    }

    private static async Task InsertLocatorsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CatalogSnapshotBatch batch,
        Guid keyGenerationId,
        byte[] dek,
        CancellationToken cancellationToken)
    {
        var nonces = new HashSet<string>(StringComparer.Ordinal);
        using var aes = new AesGcm(dek, TagSize);
        foreach (CatalogLocatorPlaintext locator in batch.Locators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] nonce = NewUniqueNonce(nonces);
            byte[] tag = GC.AllocateUninitializedArray<byte>(TagSize);
            byte[] ciphertext = GC.AllocateUninitializedArray<byte>(locator.Plaintext.Length);
            byte[] aad = BuildAad(batch.Source.Id, batch.Snapshot.Id, keyGenerationId, locator);
            try
            {
                aes.Encrypt(nonce, locator.Plaintext.Span, ciphertext, tag, aad);
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO protected_locators(locator_reference, snapshot_id, key_generation_id,
                        owner_kind, owner_id, purpose, nonce, authentication_tag, ciphertext)
                    VALUES ($reference, $snapshot, $generation, $ownerKind, $owner, $purpose, $nonce, $tag, $ciphertext);
                    """, cancellationToken,
                    ("$reference", LocatorReference(
                        batch.Source.Id,
                        locator.ChannelId,
                        locator.Purpose,
                        locator.Reference)),
                    ("$snapshot", Id(batch.Snapshot.Id.Value)), ("$generation", Id(keyGenerationId)),
                    ("$ownerKind", (int)ProtectedRecordOwnerKind.Channel), ("$owner", Id(locator.ChannelId.Value)),
                    ("$purpose", (int)locator.Purpose), ("$nonce", nonce), ("$tag", tag),
                    ("$ciphertext", ciphertext)).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce);
                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(aad);
            }
        }
    }

    private static Task SetActiveSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceId sourceId,
        SnapshotId snapshotId,
        CancellationToken cancellationToken) => ExecuteAsync(
            connection,
            transaction,
            "UPDATE sources SET active_snapshot_id = $snapshot, status = $ready WHERE source_id = $source;",
            cancellationToken,
            ("$snapshot", Id(snapshotId.Value)), ("$ready", (int)ContentSourceStatus.Ready),
            ("$source", Id(sourceId.Value)));

    private static Task RetirePreviousSnapshotKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceId sourceId,
        CancellationToken cancellationToken) => ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE snapshot_keys
            SET wrapped_dek = NULL, key_state = 2
            WHERE snapshot_id = (
                SELECT active_snapshot_id FROM sources WHERE source_id = $source
            );
            """,
            cancellationToken,
            ("$source", Id(sourceId.Value)));

    private static string LocatorReference(
        LiveChannel channel,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference) => LocatorReference(
            channel.StableKey.SourceId,
            channel.Id,
            purpose,
            reference);

    private static string LocatorReference(
        SourceId sourceId,
        ChannelId channelId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference)
    {
        SecretStoreKey key = SecretStoreKey.ForLocator(
            sourceId,
            purpose,
            ProtectedRecordOwner.ForChannel(channelId),
            reference);
        return $"locator-ref-v1:{key.RecordIdentifier:N}";
    }

    private static byte[] BuildWrapEntropy(SourceId sourceId, SnapshotId snapshotId)
    {
        byte[] context = Encoding.UTF8.GetBytes($"PROTECTED-CATALOG-DEK-V1\0{sourceId.Value:N}\0{snapshotId.Value:N}");
        try
        {
            return SHA256.HashData(context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(context);
        }
    }

    private static byte[] BuildAad(
        SourceId sourceId,
        SnapshotId snapshotId,
        Guid keyGenerationId,
        CatalogLocatorPlaintext locator)
    {
        byte[] aad = new byte[16 + 16 + 16 + 16 + 1 + 16];
        int offset = 0;
        WriteGuid(aad, ref offset, sourceId.Value);
        WriteGuid(aad, ref offset, snapshotId.Value);
        WriteGuid(aad, ref offset, keyGenerationId);
        WriteGuid(aad, ref offset, locator.ChannelId.Value);
        aad[offset++] = (byte)locator.Purpose;
        SecretStoreKey key = SecretStoreKey.ForLocator(
            sourceId,
            locator.Purpose,
            ProtectedRecordOwner.ForChannel(locator.ChannelId),
            locator.Reference);
        WriteGuid(aad, ref offset, key.RecordIdentifier);
        return aad;
    }

    private static void WriteGuid(byte[] destination, ref int offset, Guid value)
    {
        value.TryWriteBytes(destination.AsSpan(offset, 16), bigEndian: true, out int written);
        offset += written;
    }

    private static byte[] NewUniqueNonce(HashSet<string> nonces)
    {
        while (true)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            if (nonces.Add(Convert.ToHexString(nonce)))
            {
                return nonce;
            }

            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Id(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
