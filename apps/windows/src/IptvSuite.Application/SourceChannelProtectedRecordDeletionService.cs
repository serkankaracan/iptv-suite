namespace IptvSuite.Application;

/// <summary>
/// Deletes the protected locator records owned by one channel of a deletion-pending source.
/// </summary>
/// <remarks>
/// The caller must supply a chain loaded from authoritative persistence. This service validates
/// the source-to-snapshot, snapshot-to-channel, and stable-key source relationships, but it does
/// not establish provenance or grant authorization. The chain supplies the source identifier,
/// channel owner, purposes, and opaque references. This operation does not delete
/// source-configuration records, other channels, snapshots, metadata, caches, or unknown orphan
/// records and is not a source-wide deletion coordinator.
/// </remarks>
public sealed class SourceChannelProtectedRecordDeletionService
{
    private readonly ISecretStore _secretStore;

    public SourceChannelProtectedRecordDeletionService(ISecretStore secretStore)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        _secretStore = secretStore;
    }

    /// <summary>
    /// Idempotently deletes the stream and logo records named by one supplied channel chain.
    /// </summary>
    /// <remarks>
    /// The stream record is deleted before the optional logo record. Cancellation is observed
    /// before the first delete commit. Once a delete succeeds, the remaining exact delete is
    /// attempted without observing newly requested cancellation so a retry can converge safely.
    /// </remarks>
    public async ValueTask<SecretStoreOperationResult> DeleteAsync(
        ContentSource source,
        PlaylistSnapshot snapshot,
        LiveChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(channel);

        if (source.Status is not ContentSourceStatus.DeletionPending)
        {
            throw new InvalidOperationException("The source must already be deletion-pending.");
        }

        if (snapshot.SourceId != source.Id)
        {
            throw new InvalidOperationException("The snapshot does not belong to the source.");
        }

        if (channel.SnapshotId != snapshot.Id)
        {
            throw new InvalidOperationException("The channel does not belong to the snapshot.");
        }

        if (channel.StableKey.SourceId != source.Id)
        {
            throw new InvalidOperationException("The channel stable key does not belong to the source.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ProtectedRecordOwner owner = ProtectedRecordOwner.ForChannel(channel.Id);

        if (channel.StreamReference is not null)
        {
            SecretStoreOperationResult streamDelete = await _secretStore.DeleteLocatorAsync(
                source.Id,
                ProtectedValuePurpose.ChannelStreamLocator,
                owner,
                channel.StreamReference,
                cancellationToken).ConfigureAwait(false);
            if (!streamDelete.IsSuccess || channel.LogoReference is null)
            {
                return streamDelete;
            }

            return await _secretStore.DeleteLocatorAsync(
                source.Id,
                ProtectedValuePurpose.ChannelLogoLocator,
                owner,
                channel.LogoReference,
                CancellationToken.None).ConfigureAwait(false);
        }

        return channel.LogoReference is null
            ? SecretStoreOperationResult.Succeeded()
            : await _secretStore.DeleteLocatorAsync(
                source.Id,
                ProtectedValuePurpose.ChannelLogoLocator,
                owner,
                channel.LogoReference,
                cancellationToken).ConfigureAwait(false);
    }
}
