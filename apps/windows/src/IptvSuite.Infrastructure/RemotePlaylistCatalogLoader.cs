using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.Infrastructure;

internal sealed class RemotePlaylistCatalogLoader
{
    private readonly ISecretStore _secretStore;
    private readonly IHttpTransport _transport;

    internal RemotePlaylistCatalogLoader(ISecretStore secretStore, IHttpTransport transport)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    internal async ValueTask<DomainResult<RemoteM3uParseResult>> LoadAsync(
        ContentSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Configuration is not RemotePlaylistSourceConfiguration configuration ||
            source.Status is ContentSourceStatus.DeletionPending)
        {
            return DomainResult.Failure<RemoteM3uParseResult>(DomainErrorCode.DomainInvariantViolation);
        }

        SecretStoreReadResult read = await _secretStore.ReadLocatorAsync(
            source.Id,
            ProtectedValuePurpose.RemotePlaylistLocator,
            ProtectedRecordOwner.ForSourceConfiguration(configuration.ConfigurationId),
            configuration.LocatorReference,
            cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return DomainResult.Failure<RemoteM3uParseResult>(read.Failure switch
            {
                SecretStoreFailure.ProtectedRecordUnavailable => DomainErrorCode.CredentialInvalid,
                _ => DomainErrorCode.StorageUnavailable,
            });
        }

        using SecretLease secret = read.Lease!;
        if (!ProtectedSourcePayloadDecoder.TryDecodeRemotePlaylist(secret.Value, out RemotePlaylistPayloadLayout layout))
        {
            return DomainResult.Failure<RemoteM3uParseResult>(DomainErrorCode.CredentialInvalid);
        }

        ReadOnlySpan<byte> payload = secret.Value.Span;
        string locator = Encoding.UTF8.GetString(payload.Slice(layout.LocatorOffset, layout.LocatorLength));
        if (!Uri.TryCreate(locator, UriKind.Absolute, out Uri? requestUri))
        {
            return DomainResult.Failure<RemoteM3uParseResult>(DomainErrorCode.CredentialInvalid);
        }

        HttpTransportRequest request;
        try
        {
            request = HttpTransportRequest.Create(
                requestUri,
                configuration.SafeEndpoint,
                HttpTransportLimits.MaximumAllowedResponseBytes);
        }
        catch (ArgumentException)
        {
            return DomainResult.Failure<RemoteM3uParseResult>(DomainErrorCode.CredentialInvalid);
        }

        using (request)
        {
            HttpTransportResult response = await _transport.GetAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                return DomainResult.Failure<RemoteM3uParseResult>(MapTransportFailure(response.Failure));
            }

            using HttpResponseLease responseLease = response.Response!;
            using Stream responseStream = responseLease.OpenReadStream();
            return await RemoteM3uPlaylistParser.ParseAsync(
                responseStream,
                responseLease.EffectiveUri ?? requestUri,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static DomainErrorCode MapTransportFailure(HttpTransportFailure? failure) => failure switch
    {
        HttpTransportFailure.AuthenticationRejected => DomainErrorCode.AuthenticationRejected,
        HttpTransportFailure.RequestTimedOut => DomainErrorCode.RequestTimedOut,
        HttpTransportFailure.NetworkUnavailable => DomainErrorCode.NetworkUnreachable,
        HttpTransportFailure.TlsValidationFailed => DomainErrorCode.TlsValidationFailed,
        _ => DomainErrorCode.PlaylistDownloadFailed,
    };
}
