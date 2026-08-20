using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

internal enum CatalogLocatorReadFailure
{
    None,
    Unavailable,
    AuthenticationFailed,
}

internal sealed record CatalogLocatorReadResult(SecretLease? Lease, CatalogLocatorReadFailure Failure)
{
    internal bool IsSuccess => Lease is not null && Failure == CatalogLocatorReadFailure.None;
}

[SupportedOSPlatform("windows")]
internal sealed class SqliteCatalogLocatorReader
{
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;

    internal SqliteCatalogLocatorReader(string databasePath)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    internal async ValueTask<CatalogLocatorReadResult> ReadAsync(
        SourceId sourceId,
        ChannelId channelId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference,
        CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty || channelId.IsEmpty || reference is null ||
            purpose is not (ProtectedValuePurpose.ChannelStreamLocator or ProtectedValuePurpose.ChannelLogoLocator))
        {
            throw new ArgumentException("An exact catalog locator tuple is required.");
        }

        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        SecretStoreKey key = SecretStoreKey.ForLocator(
            sourceId,
            purpose,
            ProtectedRecordOwner.ForChannel(channelId),
            reference);
        string referenceText = $"locator-ref-v1:{key.RecordIdentifier:N}";
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.snapshot_id, l.key_generation_id, k.wrapped_dek, l.nonce,
                   l.authentication_tag, l.ciphertext
            FROM protected_locators AS l
            JOIN snapshot_keys AS k
              ON k.snapshot_id = l.snapshot_id AND k.key_generation_id = l.key_generation_id
            JOIN sources AS s
              ON s.source_id = $source AND s.active_snapshot_id = l.snapshot_id
            WHERE l.locator_reference = $reference
              AND l.owner_kind = $ownerKind
              AND l.owner_id = $owner
              AND l.purpose = $purpose
              AND k.key_state = 1
              AND k.wrapped_dek IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N"));
        command.Parameters.AddWithValue("$reference", referenceText);
        command.Parameters.AddWithValue("$ownerKind", (int)ProtectedRecordOwnerKind.Channel);
        command.Parameters.AddWithValue("$owner", channelId.Value.ToString("N"));
        command.Parameters.AddWithValue("$purpose", (int)purpose);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(null, CatalogLocatorReadFailure.Unavailable);
        }

        string snapshotText = reader.GetString(0);
        string generationText = reader.GetString(1);
        byte[] wrappedDek = (byte[])reader[2];
        byte[] nonce = (byte[])reader[3];
        byte[] tag = (byte[])reader[4];
        byte[] ciphertext = (byte[])reader[5];
        byte[]? dek = null;
        byte[]? plaintext = null;
        byte[]? entropy = null;
        byte[]? aad = null;
        try
        {
            if (!Guid.TryParseExact(snapshotText, "N", out Guid snapshotGuid) ||
                !Guid.TryParseExact(generationText, "N", out Guid generationGuid) ||
                nonce.Length != 12 || tag.Length != 16 || ciphertext.Length is < 1 or > 65_536)
            {
                return new(null, CatalogLocatorReadFailure.AuthenticationFailed);
            }

            entropy = BuildEntropy(sourceId.Value, snapshotGuid);
            dek = ProtectedData.Unprotect(wrappedDek, entropy, DataProtectionScope.CurrentUser);
            if (dek.Length != 32)
            {
                return new(null, CatalogLocatorReadFailure.AuthenticationFailed);
            }

            aad = BuildAad(sourceId.Value, snapshotGuid, generationGuid, channelId.Value, purpose, key.RecordIdentifier);
            plaintext = GC.AllocateUninitializedArray<byte>(ciphertext.Length);
            using var aes = new AesGcm(dek, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            SecretLease lease = SecretLease.TakeOwnership(plaintext);
            plaintext = null;
            return new(lease, CatalogLocatorReadFailure.None);
        }
        catch (CryptographicException)
        {
            return new(null, CatalogLocatorReadFailure.AuthenticationFailed);
        }
        finally
        {
            Zero(wrappedDek);
            Zero(nonce);
            Zero(tag);
            Zero(ciphertext);
            Zero(dek);
            Zero(plaintext);
            Zero(entropy);
            Zero(aad);
        }
    }

    private static byte[] BuildEntropy(Guid sourceId, Guid snapshotId)
    {
        byte[] context = Encoding.UTF8.GetBytes($"PROTECTED-CATALOG-DEK-V1\0{sourceId:N}\0{snapshotId:N}");
        try
        {
            return SHA256.HashData(context);
        }
        finally
        {
            Zero(context);
        }
    }

    private static byte[] BuildAad(
        Guid sourceId,
        Guid snapshotId,
        Guid generationId,
        Guid channelId,
        ProtectedValuePurpose purpose,
        Guid referenceId)
    {
        byte[] aad = new byte[81];
        int offset = 0;
        WriteGuid(aad, ref offset, sourceId);
        WriteGuid(aad, ref offset, snapshotId);
        WriteGuid(aad, ref offset, generationId);
        WriteGuid(aad, ref offset, channelId);
        aad[offset++] = (byte)purpose;
        WriteGuid(aad, ref offset, referenceId);
        return aad;
    }

    private static void WriteGuid(byte[] destination, ref int offset, Guid value)
    {
        value.TryWriteBytes(destination.AsSpan(offset, 16), bigEndian: true, out int written);
        offset += written;
    }

    private static void Zero(byte[]? buffer)
    {
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
