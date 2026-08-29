using System.Diagnostics;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.Infrastructure;

public sealed class XtreamProviderClient : IXtreamProviderClient
{
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
        DomainResult<XtreamSourceRequestContext> context = await CreateRequestContextAsync(
            source,
            cancellationToken).ConfigureAwait(false);
        if (!context.IsSuccess)
        {
            return DomainResult.Failure<XtreamLiveCatalog>(context.Error!);
        }

        using XtreamSourceRequestContext requestContext = context.Value!;
        DomainResult<XtreamAccountStatus> account = await GetAndParseAsync(
            requestContext,
            null,
            XtreamTransportLimits.MaximumAccountResponseBytes,
            XtreamProviderJsonParser.ParseAccountStatus,
            cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return DomainResult.Failure<XtreamLiveCatalog>(account.Error!);
        }

        DomainResult<XtreamProviderPage<XtreamCategoryInput>> categories = await GetAndParseAsync(
            requestContext,
            "get_live_categories",
            XtreamTransportLimits.MaximumCategoryResponseBytes,
            content => XtreamProviderJsonParser.ParseCategories(content, ContentKind.LiveTv),
            cancellationToken).ConfigureAwait(false);
        if (!categories.IsSuccess)
        {
            return DomainResult.Failure<XtreamLiveCatalog>(categories.Error!);
        }

        DomainResult<XtreamProviderPage<XtreamStreamInput>> streams = await GetAndParseAsync(
            requestContext,
            "get_live_streams",
            XtreamTransportLimits.MaximumCatalogResponseBytes,
            XtreamProviderJsonParser.ParseLiveStreams,
            cancellationToken).ConfigureAwait(false);
        return streams.IsSuccess
            ? DomainResult.Success(new XtreamLiveCatalog(categories.Value!, streams.Value!))
            : DomainResult.Failure<XtreamLiveCatalog>(streams.Error!);
    }

    public async ValueTask<DomainResult<XtreamContentCatalog>> LoadContentCatalogAsync(
        ContentSource source,
        CancellationToken cancellationToken = default)
    {
        DomainResult<XtreamSourceRequestContext> context = await CreateRequestContextAsync(
            source,
            cancellationToken).ConfigureAwait(false);
        if (!context.IsSuccess)
        {
            return DomainResult.Failure<XtreamContentCatalog>(context.Error!);
        }

        using XtreamSourceRequestContext requestContext = context.Value!;
        DomainResult<XtreamAccountStatus> account = await GetAndParseAsync(
            requestContext,
            null,
            XtreamTransportLimits.MaximumAccountResponseBytes,
            XtreamProviderJsonParser.ParseAccountStatus,
            cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return DomainResult.Failure<XtreamContentCatalog>(account.Error!);
        }

        DomainResult<XtreamProviderPage<XtreamCategoryInput>> liveCategories = await GetAndParseAsync(
            requestContext,
            "get_live_categories",
            XtreamTransportLimits.MaximumCategoryResponseBytes,
            content => XtreamProviderJsonParser.ParseCategories(content, ContentKind.LiveTv),
            cancellationToken).ConfigureAwait(false);
        if (!liveCategories.IsSuccess)
        {
            return DomainResult.Failure<XtreamContentCatalog>(liveCategories.Error!);
        }

        DomainResult<XtreamProviderPage<XtreamStreamInput>> liveStreams = await GetAndParseAsync(
            requestContext,
            "get_live_streams",
            XtreamTransportLimits.MaximumCatalogResponseBytes,
            XtreamProviderJsonParser.ParseLiveStreams,
            cancellationToken).ConfigureAwait(false);
        if (!liveStreams.IsSuccess)
        {
            return DomainResult.Failure<XtreamContentCatalog>(liveStreams.Error!);
        }

        DomainResult<XtreamProviderPage<XtreamCategoryInput>> movieCategories = await GetAndParseAsync(
            requestContext,
            "get_vod_categories",
            XtreamTransportLimits.MaximumCategoryResponseBytes,
            content => XtreamProviderJsonParser.ParseCategories(content, ContentKind.Movie),
            cancellationToken).ConfigureAwait(false);
        if (!movieCategories.IsSuccess)
        {
            return DomainResult.Failure<XtreamContentCatalog>(movieCategories.Error!);
        }

        DomainResult<XtreamProviderPage<XtreamMovieInput>> movies = await GetAndParseAsync(
            requestContext,
            "get_vod_streams",
            XtreamTransportLimits.MaximumCatalogResponseBytes,
            XtreamProviderJsonParser.ParseMovies,
            cancellationToken).ConfigureAwait(false);
        if (!movies.IsSuccess)
        {
            return DomainResult.Failure<XtreamContentCatalog>(movies.Error!);
        }

        DomainResult<XtreamProviderPage<XtreamCategoryInput>> seriesCategories = await GetAndParseAsync(
            requestContext,
            "get_series_categories",
            XtreamTransportLimits.MaximumCategoryResponseBytes,
            content => XtreamProviderJsonParser.ParseCategories(content, ContentKind.Series),
            cancellationToken).ConfigureAwait(false);
        if (!seriesCategories.IsSuccess)
        {
            return DomainResult.Failure<XtreamContentCatalog>(seriesCategories.Error!);
        }

        DomainResult<XtreamProviderPage<XtreamSeriesInput>> series = await GetAndParseAsync(
            requestContext,
            "get_series",
            XtreamTransportLimits.MaximumCatalogResponseBytes,
            XtreamProviderJsonParser.ParseSeries,
            cancellationToken).ConfigureAwait(false);
        return series.IsSuccess
            ? DomainResult.Success(new XtreamContentCatalog(
                account.Value!,
                liveCategories.Value!,
                liveStreams.Value!,
                movieCategories.Value!,
                movies.Value!,
                seriesCategories.Value!,
                series.Value!))
            : DomainResult.Failure<XtreamContentCatalog>(series.Error!);
    }

    public async ValueTask<DomainResult<XtreamSeriesDetails>> LoadSeriesDetailsAsync(
        ContentSource source,
        ProviderItemKey seriesKey,
        CancellationToken cancellationToken = default)
    {
        if (seriesKey.IsEmpty)
        {
            return DomainResult.Failure<XtreamSeriesDetails>(
                DomainErrorCode.DomainInvariantViolation);
        }

        DomainResult<XtreamSourceRequestContext> context = await CreateRequestContextAsync(
            source,
            cancellationToken).ConfigureAwait(false);
        if (!context.IsSuccess)
        {
            return DomainResult.Failure<XtreamSeriesDetails>(context.Error!);
        }

        using XtreamSourceRequestContext requestContext = context.Value!;
        return await GetAndParseAsync(
            requestContext,
            "get_series_info",
            XtreamTransportLimits.MaximumSeriesDetailsResponseBytes,
            XtreamProviderJsonParser.ParseSeriesDetails,
            cancellationToken,
            seriesKey).ConfigureAwait(false);
    }

    private async ValueTask<DomainResult<XtreamSourceRequestContext>> CreateRequestContextAsync(
        ContentSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Configuration is not XtreamSourceConfiguration configuration ||
            source.Status is ContentSourceStatus.DeletionPending ||
            (string.Equals(configuration.SafeEndpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
             !configuration.AllowsInsecureTransport))
        {
            return DomainResult.Failure<XtreamSourceRequestContext>(
                DomainErrorCode.DomainInvariantViolation);
        }

        SecretStoreReadResult read = await _secretStore.ReadCredentialsAsync(
            source.Id,
            ProtectedRecordOwner.ForSourceConfiguration(configuration.ConfigurationId),
            configuration.CredentialsReference,
            cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return DomainResult.Failure<XtreamSourceRequestContext>(MapStoreFailure(read.Failure));
        }

        SecretLease? credentials = read.Lease!;
        try
        {
            if (!ProtectedSourcePayloadDecoder.TryDecodeXtream(
                    credentials.Value,
                    out XtreamSourcePayloadLayout layout) ||
                !IsLocatorCompatible(
                    credentials.Value,
                    layout,
                    configuration))
            {
                return DomainResult.Failure<XtreamSourceRequestContext>(
                    DomainErrorCode.CredentialInvalid);
            }

            var requestContext = new XtreamSourceRequestContext(
                configuration,
                credentials,
                layout);
            credentials = null;
            return DomainResult.Success(requestContext);
        }
        finally
        {
            credentials?.Dispose();
        }
    }

    private async ValueTask<DomainResult<T>> GetAndParseAsync<T>(
        XtreamSourceRequestContext requestContext,
        string? action,
        int maximumResponseBytes,
        Func<ReadOnlyMemory<byte>, DomainResult<T>> parser,
        CancellationToken cancellationToken,
        ProviderItemKey? seriesKey = null)
    {
        if (!requestContext.TryCreateRequest(
                action,
                seriesKey,
                maximumResponseBytes,
                out HttpTransportRequest? request))
        {
            return DomainResult.Failure<T>(DomainErrorCode.CredentialInvalid);
        }

        using HttpTransportRequest ownedRequest = request!;
        HttpTransportResult response = await _transport.GetAsync(
            ownedRequest,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return DomainResult.Failure<T>(HttpTransportDomainErrorMapper.Map(response.Failure));
        }

        using HttpResponseLease lease = response.Response!;
        return parser(lease.Content);
    }

    private static bool IsLocatorCompatible(
        ReadOnlyMemory<byte> credentialPayload,
        XtreamSourcePayloadLayout layout,
        XtreamSourceConfiguration configuration)
    {
        ReadOnlySpan<byte> payload = credentialPayload.Span;
        string locator = Encoding.UTF8.GetString(
            payload.Slice(layout.LocatorOffset, layout.LocatorLength));
        if (!Uri.TryCreate(locator, UriKind.Absolute, out Uri? baseUri))
        {
            return false;
        }

        try
        {
            using HttpTransportRequest endpointProbe =
                HttpTransportRequest.CreateForExplicitXtreamSourceOrigin(
                    baseUri,
                    configuration.SafeEndpoint,
                    1,
                    configuration.AllowsInsecureTransport);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryBuildRequest(
        ReadOnlyMemory<byte> credentialPayload,
        XtreamSourcePayloadLayout layout,
        XtreamSourceConfiguration configuration,
        string? action,
        ProviderItemKey? seriesKey,
        int maximumResponseBytes,
        out HttpTransportRequest? request)
    {
        request = null;
        ReadOnlySpan<byte> payload = credentialPayload.Span;
        string locator = Encoding.UTF8.GetString(
            payload.Slice(layout.LocatorOffset, layout.LocatorLength));
        string username = Encoding.UTF8.GetString(
            payload.Slice(layout.UsernameOffset, layout.UsernameLength));
        string password = Encoding.UTF8.GetString(
            payload.Slice(layout.PasswordOffset, layout.PasswordLength));
        if (!Uri.TryCreate(locator, UriKind.Absolute, out Uri? baseUri))
        {
            return false;
        }

        try
        {
            string basePath = NormalizeApiBasePath(baseUri.AbsolutePath);
            var apiBuilder = new UriBuilder(baseUri)
            {
                Path = string.Concat(basePath, "player_api.php"),
                Query = string.Empty,
                Fragment = string.Empty,
            };
            string credentialQuery = string.Concat(
                "username=", Uri.EscapeDataString(username),
                "&password=", Uri.EscapeDataString(password));
            apiBuilder.Query = action is null
                ? credentialQuery
                : string.Concat(credentialQuery, "&action=", action);
            if (seriesKey.HasValue)
            {
                if (seriesKey.Value.IsEmpty)
                {
                    return false;
                }

                apiBuilder.Query = string.Concat(
                    apiBuilder.Query.TrimStart('?'),
                    "&series_id=",
                    Uri.EscapeDataString(seriesKey.Value.Value));
            }

            request = HttpTransportRequest.CreateForExplicitXtreamSourceOrigin(
                apiBuilder.Uri,
                configuration.SafeEndpoint,
                maximumResponseBytes,
                configuration.AllowsInsecureTransport);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static string NormalizeApiBasePath(string absolutePath)
    {
        string path = absolutePath;
        string fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "get.php", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "player_api.php", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^fileName.Length];
        }

        return path.EndsWith('/') ? path : string.Concat(path, "/");
    }

    private static DomainErrorCode MapStoreFailure(SecretStoreFailure failure) => failure switch
    {
        SecretStoreFailure.ProtectedRecordUnavailable => DomainErrorCode.CredentialInvalid,
        SecretStoreFailure.StorageUnavailable => DomainErrorCode.StorageUnavailable,
        _ => DomainErrorCode.StorageUnavailable,
    };

    [DebuggerDisplay("[XTREAM-SOURCE-REQUEST-CONTEXT]")]
    private sealed class XtreamSourceRequestContext : IDisposable
    {
        private readonly XtreamSourcePayloadLayout _layout;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private SecretLease? _credentials;

        internal XtreamSourceRequestContext(
            XtreamSourceConfiguration configuration,
            SecretLease credentials,
            XtreamSourcePayloadLayout layout)
        {
            Configuration = configuration;
            _credentials = credentials;
            _layout = layout;
        }

        internal XtreamSourceConfiguration Configuration { get; }

        internal bool TryCreateRequest(
            string? action,
            ProviderItemKey? seriesKey,
            int maximumResponseBytes,
            out HttpTransportRequest? request)
        {
            SecretLease? credentials = Volatile.Read(ref _credentials);
            if (credentials is null)
            {
                request = null;
                return false;
            }

            try
            {
                return TryBuildRequest(
                    credentials.Value,
                    _layout,
                    Configuration,
                    action,
                    seriesKey,
                    maximumResponseBytes,
                    out request);
            }
            catch (ObjectDisposedException)
            {
                request = null;
                return false;
            }
        }

        public void Dispose()
        {
            SecretLease? credentials = Interlocked.Exchange(ref _credentials, null);
            credentials?.Dispose();
            GC.SuppressFinalize(this);
        }

        public override string ToString() => "[XTREAM-SOURCE-REQUEST-CONTEXT]";
    }
}
