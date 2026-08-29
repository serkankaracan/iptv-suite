using System.Diagnostics;
using System.Security.Cryptography;

namespace IptvSuite.Application;

public enum HttpTransportFailure
{
    InvalidRequest,
    RedirectRejected,
    RedirectLimitExceeded,
    AuthenticationRejected,
    ResourceNotFound,
    RequestRejected,
    ResponseTooLarge,
    RequestTimedOut,
    NetworkUnavailable,
    TlsValidationFailed,
    RateLimited,
    RemoteServiceUnavailable,
    EndpointAddressRejected,
}

public enum HttpTransportRetryability
{
    Never,
    BoundedTransient,
    Manual,
}

public enum HttpResponseMediaType
{
    Unspecified,
    Png,
    Jpeg,
    WebP,
    Other,
}

internal enum HttpEndpointAddressPolicy
{
    PublicOnly,
    ExplicitPrivateSourceOrigin,
}

internal enum HttpRedirectPolicy
{
    FollowValidated,
    RejectAll,
}

public readonly record struct HttpTransportObservation(
    int AttemptCount,
    int RedirectCount,
    int StatusCode,
    int ResponseBytes,
    long ElapsedMilliseconds,
    HttpTransportFailure? Failure);

public interface IHttpTransportObserver
{
    void Observe(HttpTransportObservation observation);
}

[DebuggerDisplay("[HTTP-TRANSPORT-REQUEST]")]
public sealed class HttpTransportRequest : IDisposable
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private Uri? _requestUri;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private byte[] _authorizationValue;
    private bool _disposed;

    private HttpTransportRequest(
        Uri requestUri,
        SafeEndpoint expectedEndpoint,
        int maximumResponseBytes,
        byte[] authorizationValue,
        HttpEndpointAddressPolicy endpointAddressPolicy,
        HttpRedirectPolicy redirectPolicy,
        TimeSpan? requestTimeoutOverride)
    {
        _requestUri = requestUri;
        ExpectedEndpoint = expectedEndpoint;
        MaximumResponseBytes = maximumResponseBytes;
        _authorizationValue = authorizationValue;
        EndpointAddressPolicy = endpointAddressPolicy;
        RedirectPolicy = redirectPolicy;
        RequestTimeoutOverride = requestTimeoutOverride;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal Uri RequestUri =>
        Volatile.Read(ref _requestUri) ?? throw new ObjectDisposedException(nameof(HttpTransportRequest));

    internal SafeEndpoint ExpectedEndpoint { get; }

    internal HttpEndpointAddressPolicy EndpointAddressPolicy { get; }

    internal HttpRedirectPolicy RedirectPolicy { get; }

    internal TimeSpan? RequestTimeoutOverride { get; }

    public int MaximumResponseBytes { get; }

    internal bool HasAuthorization
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _authorizationValue.Length > 0;
        }
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal ReadOnlySpan<byte> AuthorizationValue
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _authorizationValue;
        }
    }

    public static HttpTransportRequest Create(
        Uri requestUri,
        SafeEndpoint expectedEndpoint,
        int maximumResponseBytes)
    {
        return CreateCore(
            requestUri,
            expectedEndpoint,
            maximumResponseBytes,
            HttpEndpointAddressPolicy.PublicOnly,
            HttpRedirectPolicy.FollowValidated,
            allowInsecureHttp: false,
            maximumPermittedResponseBytes: HttpTransportLimits.MaximumAllowedResponseBytes,
            requestTimeoutOverride: null);
    }

    internal static HttpTransportRequest CreateForExplicitPrivateSourceOrigin(
        Uri requestUri,
        SafeEndpoint expectedEndpoint,
        int maximumResponseBytes)
    {
        return CreateCore(
            requestUri,
            expectedEndpoint,
            maximumResponseBytes,
            HttpEndpointAddressPolicy.ExplicitPrivateSourceOrigin,
            HttpRedirectPolicy.FollowValidated,
            allowInsecureHttp: false,
            maximumPermittedResponseBytes: HttpTransportLimits.MaximumAllowedResponseBytes,
            requestTimeoutOverride: null);
    }

    internal static HttpTransportRequest CreateForExplicitXtreamSourceOrigin(
        Uri requestUri,
        SafeEndpoint expectedEndpoint,
        int maximumResponseBytes,
        bool allowInsecureHttp)
    {
        return CreateCore(
            requestUri,
            expectedEndpoint,
            maximumResponseBytes,
            HttpEndpointAddressPolicy.ExplicitPrivateSourceOrigin,
            HttpRedirectPolicy.RejectAll,
            allowInsecureHttp,
            maximumPermittedResponseBytes: XtreamTransportLimits.MaximumCatalogResponseBytes,
            requestTimeoutOverride: null);
    }

    internal static HttpTransportRequest CreateForExplicitRemotePlaylistSourceOrigin(
        Uri requestUri,
        SafeEndpoint expectedEndpoint)
    {
        return CreateCore(
            requestUri,
            expectedEndpoint,
            RemotePlaylistTransportLimits.MaximumResponseBytes,
            HttpEndpointAddressPolicy.ExplicitPrivateSourceOrigin,
            HttpRedirectPolicy.FollowValidated,
            allowInsecureHttp: true,
            maximumPermittedResponseBytes: RemotePlaylistTransportLimits.MaximumResponseBytes,
            requestTimeoutOverride: RemotePlaylistTransportLimits.RequestTimeout);
    }

    private static HttpTransportRequest CreateCore(
        Uri requestUri,
        SafeEndpoint expectedEndpoint,
        int maximumResponseBytes,
        HttpEndpointAddressPolicy endpointAddressPolicy,
        HttpRedirectPolicy redirectPolicy,
        bool allowInsecureHttp,
        int maximumPermittedResponseBytes,
        TimeSpan? requestTimeoutOverride)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(expectedEndpoint);
        bool isHttps = string.Equals(
            requestUri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);
        bool isAllowedHttp = allowInsecureHttp && string.Equals(
            requestUri.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase);
        if (!requestUri.IsAbsoluteUri || (!isHttps && !isAllowedHttp) ||
            !string.IsNullOrEmpty(requestUri.UserInfo) ||
            !string.IsNullOrEmpty(requestUri.Fragment) ||
            !SafeEndpoint.TryCreate(requestUri, out SafeEndpoint? actualEndpoint) ||
            !expectedEndpoint.Equals(actualEndpoint))
        {
            throw new ArgumentException(
                "The request URI must use an allowed web scheme and match the expected endpoint.",
                nameof(requestUri));
        }

        if (maximumResponseBytes <= 0 || maximumResponseBytes > maximumPermittedResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        }

        if (requestTimeoutOverride.HasValue &&
            (requestTimeoutOverride.Value <= TimeSpan.Zero ||
             requestTimeoutOverride.Value > TimeSpan.FromMinutes(2)))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeoutOverride));
        }

        return new HttpTransportRequest(
            new Uri(requestUri.AbsoluteUri),
            expectedEndpoint,
            maximumResponseBytes,
            [],
            endpointAddressPolicy,
            redirectPolicy,
            requestTimeoutOverride);
    }

    public static HttpTransportRequest CreateWithAuthorization(
        Uri requestUri,
        SafeEndpoint expectedEndpoint,
        int maximumResponseBytes,
        ReadOnlySpan<byte> authorizationValue)
    {
        HttpTransportRequest request = Create(requestUri, expectedEndpoint, maximumResponseBytes);
        if (authorizationValue.IsEmpty || authorizationValue.Length > 4096)
        {
            request.Dispose();
            throw new ArgumentOutOfRangeException(nameof(authorizationValue));
        }

        foreach (byte value in authorizationValue)
        {
            if (value is < 0x20 or > 0x7e)
            {
                request.Dispose();
                throw new ArgumentException(
                    "The authorization value must contain only visible ASCII bytes.",
                    nameof(authorizationValue));
            }
        }

        request._authorizationValue = authorizationValue.ToArray();
        return request;
    }

    public override string ToString() => "[HTTP-TRANSPORT-REQUEST]";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _ = Interlocked.Exchange(ref _requestUri, null);
        byte[] authorizationValue = Interlocked.Exchange(ref _authorizationValue, []);
        if (authorizationValue.Length > 0)
        {
            CryptographicOperations.ZeroMemory(authorizationValue);
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

public static class HttpTransportLimits
{
    public const int MaximumAllowedResponseBytes = 4 * 1024 * 1024;
    public const int MaximumRedirects = 5;
}

internal static class XtreamTransportLimits
{
    internal const int MaximumAccountResponseBytes = 64 * 1024;
    internal const int MaximumCategoryResponseBytes = 1024 * 1024;
    internal const int MaximumCatalogResponseBytes = 64 * 1024 * 1024;
    internal const int MaximumSeriesDetailsResponseBytes = 16 * 1024 * 1024;
}

internal static class RemotePlaylistTransportLimits
{
    internal const int MaximumResponseBytes = 128 * 1024 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
}

public sealed class HttpResponseLease : IDisposable
{
    private byte[] _content;
    private Uri? _effectiveUri;
    private HttpResponseMediaType _mediaType;

    internal HttpResponseLease(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
    }

    public ReadOnlyMemory<byte> Content => _content;

    public HttpResponseMediaType MediaType => _mediaType;

    internal Uri? EffectiveUri => _effectiveUri;

    internal Stream OpenReadStream() => new MemoryStream(_content, writable: false);

    internal HttpResponseLease BindEffectiveUri(Uri effectiveUri)
    {
        ArgumentNullException.ThrowIfNull(effectiveUri);
        if (_effectiveUri is not null)
        {
            throw new InvalidOperationException("The effective URI is already bound.");
        }

        _effectiveUri = effectiveUri;
        return this;
    }

    internal HttpResponseLease BindMediaType(HttpResponseMediaType mediaType)
    {
        if (_mediaType != HttpResponseMediaType.Unspecified)
        {
            throw new InvalidOperationException("The response media type is already bound.");
        }

        _mediaType = mediaType;
        return this;
    }

    public static HttpResponseLease CopyFrom(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty || content.Length > HttpTransportLimits.MaximumAllowedResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }

        return new HttpResponseLease(content.ToArray());
    }

    public static HttpResponseLease CopyFrom(
        ReadOnlySpan<byte> content,
        HttpResponseMediaType mediaType)
    {
        if (mediaType == HttpResponseMediaType.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(mediaType));
        }

        return CopyFrom(content).BindMediaType(mediaType);
    }

    public void Dispose()
    {
        byte[] content = Interlocked.Exchange(ref _content, []);
        if (content.Length > 0)
        {
            CryptographicOperations.ZeroMemory(content);
        }

        GC.SuppressFinalize(this);
    }
}

public sealed class HttpTransportResult
{
    private HttpTransportResult(
        int statusCode,
        HttpResponseLease? response,
        HttpTransportFailure? failure,
        HttpTransportRetryability retryability)
    {
        StatusCode = statusCode;
        Response = response;
        Failure = failure;
        Retryability = retryability;
    }

    public bool IsSuccess => Response is not null;

    public int StatusCode { get; }

    public HttpResponseLease? Response { get; }

    public HttpTransportFailure? Failure { get; }

    public HttpTransportRetryability Retryability { get; }

    public static HttpTransportResult Success(int statusCode, HttpResponseLease response) =>
        new(statusCode, response, null, HttpTransportRetryability.Never);

    public static HttpTransportResult Failed(
        HttpTransportFailure failure,
        HttpTransportRetryability retryability,
        int statusCode = 0) =>
        new(statusCode, null, failure, retryability);
}

public interface IHttpTransport
{
    ValueTask<HttpTransportResult> GetAsync(
        HttpTransportRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class HttpStreamingResponseLease : IDisposable
{
    private Stream? _content;
    private IDisposable? _owner;

    internal HttpStreamingResponseLease(
        Stream content,
        Uri effectiveUri,
        IDisposable owner,
        string? entityTag = null,
        DateTimeOffset? lastModified = null)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        EffectiveUri = effectiveUri ?? throw new ArgumentNullException(nameof(effectiveUri));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        EntityTag = entityTag;
        LastModified = lastModified;
    }

    public Stream Content => _content ?? throw new ObjectDisposedException(nameof(HttpStreamingResponseLease));

    internal Uri EffectiveUri { get; }

    public string? EntityTag { get; }

    public DateTimeOffset? LastModified { get; }

    public void Dispose()
    {
        Stream? content = Interlocked.Exchange(ref _content, null);
        IDisposable? owner = Interlocked.Exchange(ref _owner, null);
        content?.Dispose();
        owner?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class HttpStreamingResult
{
    private HttpStreamingResult(
        int statusCode,
        HttpStreamingResponseLease? response,
        HttpTransportFailure? failure,
        HttpTransportRetryability retryability)
    {
        StatusCode = statusCode;
        Response = response;
        Failure = failure;
        Retryability = retryability;
    }

    public bool IsSuccess => Response is not null;
    public int StatusCode { get; }
    public HttpStreamingResponseLease? Response { get; }
    public HttpTransportFailure? Failure { get; }
    public HttpTransportRetryability Retryability { get; }

    public static HttpStreamingResult Success(int statusCode, HttpStreamingResponseLease response) =>
        new(statusCode, response, null, HttpTransportRetryability.Never);

    public static HttpStreamingResult Failed(
        HttpTransportFailure failure,
        HttpTransportRetryability retryability,
        int statusCode = 0) => new(statusCode, null, failure, retryability);
}

public interface IStreamingHttpTransport
{
    ValueTask<HttpStreamingResult> GetStreamAsync(
        HttpTransportRequest request,
        CancellationToken cancellationToken = default);
}
