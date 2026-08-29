namespace IptvSuite.Application;

internal static class StoredRemotePlaylistSourceFactory
{
    internal static DomainResult<ContentSource> RestoreForRefresh(
        SourceId sourceId,
        SourceConfigurationId configurationId,
        string? displayName,
        string? endpointScheme,
        string? endpointHost,
        int endpointPort,
        ProtectedLocatorReference locatorReference,
        DateTimeOffset now)
    {
        if (sourceId.IsEmpty || configurationId.IsEmpty ||
            string.IsNullOrWhiteSpace(endpointScheme) ||
            string.IsNullOrWhiteSpace(endpointHost) ||
            endpointPort is < 1 or > 65535 || locatorReference is null || now == default)
        {
            return DomainResult.Failure<ContentSource>(
                DomainErrorCode.DomainInvariantViolation);
        }

        var endpointBuilder = new UriBuilder(endpointScheme, endpointHost, endpointPort);
        if (!SafeEndpoint.TryCreate(endpointBuilder.Uri, out SafeEndpoint? safeEndpoint) ||
            safeEndpoint is null)
        {
            return DomainResult.Failure<ContentSource>(
                DomainErrorCode.DomainInvariantViolation);
        }

        DomainResult<SourceDisplayName> normalizedName = SourceDisplayName.Create(displayName);
        if (!normalizedName.IsSuccess)
        {
            return DomainResult.Failure<ContentSource>(normalizedName.Error!);
        }

        var prepared = new PreparedRemotePlaylistSourceDraft(
            normalizedName.Value.Value,
            safeEndpoint);
        ValidatedSourceDraft draft = SourceConfigurationValidator.CompleteRemotePlaylist(
            sourceId,
            configurationId,
            prepared,
            locatorReference);
        return ContentSource.Create(
            draft,
            ContentSourceStatus.Testing,
            now,
            now);
    }
}
