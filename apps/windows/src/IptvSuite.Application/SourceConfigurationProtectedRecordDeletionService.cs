namespace IptvSuite.Application;

/// <summary>
/// Deletes the single configuration-owned protected record of a deletion-pending source.
/// </summary>
/// <remarks>
/// The source aggregate is the authoritative source of the source identifier, configuration
/// owner, purpose, and opaque reference. This operation does not delete channel records,
/// metadata, snapshots, caches, or unknown orphan records and is not a source-wide deletion
/// coordinator.
/// </remarks>
public sealed class SourceConfigurationProtectedRecordDeletionService
{
    private readonly ISecretStore _secretStore;

    public SourceConfigurationProtectedRecordDeletionService(ISecretStore secretStore)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        _secretStore = secretStore;
    }

    /// <summary>
    /// Idempotently deletes the exact protected record named by a deletion-pending source.
    /// </summary>
    /// <remarks>
    /// A successful store result is the delete commit boundary. Cancellation is checked before
    /// the store call and is not observed again after the store reports its result.
    /// </remarks>
    public ValueTask<SecretStoreOperationResult> DeleteAsync(
        ContentSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Status is not ContentSourceStatus.DeletionPending)
        {
            throw new InvalidOperationException("The source must already be deletion-pending.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ProtectedRecordOwner owner =
            ProtectedRecordOwner.ForSourceConfiguration(source.Configuration.ConfigurationId);

        return source.Configuration switch
        {
            XtreamSourceConfiguration configuration => _secretStore.DeleteCredentialsAsync(
                source.Id,
                owner,
                configuration.CredentialsReference,
                cancellationToken),
            RemotePlaylistSourceConfiguration configuration => _secretStore.DeleteLocatorAsync(
                source.Id,
                ProtectedValuePurpose.RemotePlaylistLocator,
                owner,
                configuration.LocatorReference,
                cancellationToken),
            _ => throw new InvalidOperationException("The source configuration kind is unsupported."),
        };
    }
}
