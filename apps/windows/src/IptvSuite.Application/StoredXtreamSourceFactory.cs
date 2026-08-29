namespace IptvSuite.Application;

internal static class StoredXtreamSourceFactory
{
    internal static DomainResult<ContentSource> RestoreForRefresh(
        SourceId sourceId,
        SourceConfigurationId configurationId,
        string? displayName,
        string? endpointScheme,
        string? endpointHost,
        int endpointPort,
        SecretReference credentialsReference,
        bool allowsInsecureTransport,
        DateTimeOffset now)
    {
        if (sourceId.IsEmpty || configurationId.IsEmpty ||
            string.IsNullOrWhiteSpace(endpointScheme) || string.IsNullOrWhiteSpace(endpointHost) ||
            endpointPort is < 1 or > 65535 ||
            credentialsReference is null || now == default)
        {
            return DomainResult.Failure<ContentSource>(DomainErrorCode.DomainInvariantViolation);
        }

        var endpointBuilder = new UriBuilder(endpointScheme, endpointHost, endpointPort);
        if (!SafeEndpoint.TryCreate(endpointBuilder.Uri, out SafeEndpoint? safeEndpoint) ||
            safeEndpoint is null)
        {
            return DomainResult.Failure<ContentSource>(DomainErrorCode.DomainInvariantViolation);
        }

        DomainResult<SourceDisplayName> normalizedName = SourceDisplayName.Create(displayName);
        bool usesHttp = string.Equals(
            safeEndpoint.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.Ordinal);
        if (!normalizedName.IsSuccess || usesHttp != allowsInsecureTransport)
        {
            return DomainResult.Failure<ContentSource>(
                normalizedName.Error ?? DomainError.Create(DomainErrorCode.DomainInvariantViolation));
        }

        var prepared = new PreparedXtreamSourceDraft(
            normalizedName.Value.Value,
            safeEndpoint,
            allowsInsecureTransport);
        ValidatedSourceDraft draft = SourceConfigurationValidator.CompleteXtream(
            sourceId,
            configurationId,
            prepared,
            credentialsReference);
        return ContentSource.Create(
            draft,
            ContentSourceStatus.Testing,
            now,
            now);
    }
}
