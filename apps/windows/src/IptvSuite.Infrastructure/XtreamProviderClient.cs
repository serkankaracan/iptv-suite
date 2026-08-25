using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.Infrastructure;

public sealed class XtreamProviderClient : IXtreamProviderClient
{
    private const int MaximumAccountResponseBytes = 64 * 1024;
    private const int MaximumCategoryResponseBytes = 1024 * 1024;
    private readonly ISecretStore _secretStore;
    private readonly IHttpTransport _transport;

    public XtreamProviderClient(ISecretStore secretStore, IHttpTransport transport)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async ValueTask<DomainResult<XtreamLiveCatalog>> LoadLiveCatalogAsync(
        ContentSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Configuration is not XtreamSourceConfiguration configuration ||
            source.Status is ContentSourceStatus.DeletionPending)
        {
            return DomainResult.Failure<XtreamLiveCatalog>(DomainErrorCode.DomainInvariantViolation);
        }

        SecretStoreReadResult read = await _secretStore.ReadCredentialsAsync(
            source.Id,
            ProtectedRecordOwner.ForSourceConfiguration(configuration.ConfigurationId),
            configuration.CredentialsReference,
            cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return DomainResult.Failure<XtreamLiveCatalog>(MapStoreFailure(read.Failure));
        }

        using SecretLease lease = read.Lease!;
        if (!ProtectedSourcePayloadDecoder.TryDecodeXtream(lease.Value, out XtreamSourcePayloadLayout layout))
        {
            return DomainResult.Failure<XtreamLiveCatalog>(DomainErrorCode.CredentialInvalid);
        }

        // HttpClient requires transient managed URI/query strings. They remain operation-local
        // and are never returned, observed, persisted, or included in diagnostics.
        ReadOnlySpan<byte> payload = lease.Value.Span;
        string locator = Encoding.UTF8.GetString(payload.Slice(layout.LocatorOffset, layout.LocatorLength));
        string username = Encoding.UTF8.GetString(payload.Slice(layout.UsernameOffset, layout.UsernameLength));
        string password = Encoding.UTF8.GetString(payload.Slice(layout.PasswordOffset, layout.PasswordLength));
        if (!TryBuildRequests(
                locator,
                username,
                password,
                configuration.SafeEndpoint,
                out Uri? accountUri,
                out Uri? categoriesUri,
                out Uri? streamsUri))
        {
            return DomainResult.Failure<XtreamLiveCatalog>(DomainErrorCode.CredentialInvalid);
        }

        DomainResult<XtreamAccountStatus> account = await GetAndParseAsync(
            accountUri!,
            configuration.SafeEndpoint,
            MaximumAccountResponseBytes,
            XtreamProviderJsonParser.ParseAccountStatus,
            cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return DomainResult.Failure<XtreamLiveCatalog>(account.Error!);
        }

        DomainResult<XtreamProviderPage<XtreamCategoryInput>> categories = await GetAndParseAsync(
            categoriesUri!,
            configuration.SafeEndpoint,
            MaximumCategoryResponseBytes,
            XtreamProviderJsonParser.ParseCategories,
            cancellationToken).ConfigureAwait(false);
        if (!categories.IsSuccess)
        {
            return DomainResult.Failure<XtreamLiveCatalog>(categories.Error!);
        }

        DomainResult<XtreamProviderPage<XtreamStreamInput>> streams = await GetAndParseAsync(
            streamsUri!,
            configuration.SafeEndpoint,
            HttpTransportLimits.MaximumAllowedResponseBytes,
            XtreamProviderJsonParser.ParseLiveStreams,
            cancellationToken).ConfigureAwait(false);
        return streams.IsSuccess
            ? DomainResult.Success(new XtreamLiveCatalog(categories.Value!, streams.Value!))
            : DomainResult.Failure<XtreamLiveCatalog>(streams.Error!);
    }

    private async ValueTask<DomainResult<T>> GetAndParseAsync<T>(
        Uri uri,
        SafeEndpoint expectedEndpoint,
        int maximumResponseBytes,
        Func<ReadOnlyMemory<byte>, DomainResult<T>> parser,
        CancellationToken cancellationToken)
    {
        using HttpTransportRequest request = HttpTransportRequest.Create(
            uri,
            expectedEndpoint,
            maximumResponseBytes);
        HttpTransportResult response = await _transport.GetAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return DomainResult.Failure<T>(HttpTransportDomainErrorMapper.Map(response.Failure));
        }

        using HttpResponseLease lease = response.Response!;
        return parser(lease.Content);
    }

    private static bool TryBuildRequests(
        string locator,
        string username,
        string password,
        SafeEndpoint expectedEndpoint,
        out Uri? account,
        out Uri? categories,
        out Uri? streams)
    {
        account = null;
        categories = null;
        streams = null;
        if (!Uri.TryCreate(locator, UriKind.Absolute, out Uri? baseUri))
        {
            return false;
        }

        try
        {
            using HttpTransportRequest endpointProbe = HttpTransportRequest.Create(
                baseUri,
                expectedEndpoint,
                1);
        }
        catch (ArgumentException)
        {
            return false;
        }

        string basePath = baseUri.AbsolutePath.EndsWith('/')
            ? baseUri.AbsolutePath
            : string.Concat(baseUri.AbsolutePath, "/");
        var apiBuilder = new UriBuilder(baseUri)
        {
            Path = string.Concat(basePath, "player_api.php"),
            Query = string.Empty,
            Fragment = string.Empty,
        };
        string credentialQuery = string.Concat(
            "username=", Uri.EscapeDataString(username),
            "&password=", Uri.EscapeDataString(password));
        apiBuilder.Query = credentialQuery;
        account = apiBuilder.Uri;
        apiBuilder.Query = string.Concat(credentialQuery, "&action=get_live_categories");
        categories = apiBuilder.Uri;
        apiBuilder.Query = string.Concat(credentialQuery, "&action=get_live_streams");
        streams = apiBuilder.Uri;
        return true;
    }

    private static DomainErrorCode MapStoreFailure(SecretStoreFailure failure) => failure switch
    {
        SecretStoreFailure.ProtectedRecordUnavailable => DomainErrorCode.CredentialInvalid,
        SecretStoreFailure.StorageUnavailable => DomainErrorCode.StorageUnavailable,
        _ => DomainErrorCode.StorageUnavailable,
    };
}
