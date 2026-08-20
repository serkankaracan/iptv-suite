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
}

public enum HttpTransportRetryability
{
    Never,
    BoundedTransient,
    Manual,
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
    private byte[] _authorizationValue;
    private bool _disposed;

    private HttpTransportRequest(
        Uri requestUri,
        SafeEndpoint expectedEndpoint,
        int maximumResponseBytes,
        byte[] authorizationValue)
    {
        RequestUri = requestUri;
        ExpectedEndpoint = expectedEndpoint;
        MaximumResponseBytes = maximumResponseBytes;
        _authorizationValue = authorizationValue;
    }

    internal Uri RequestUri { get; }

    internal SafeEndpoint ExpectedEndpoint { get; }

    public int MaximumResponseBytes { get; }

    internal bool HasAuthorization
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _authorizationValue.Length > 0;
        }
    }

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
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(expectedEndpoint);
        if (!requestUri.IsAbsoluteUri ||
            !string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(requestUri.UserInfo) ||
            !string.IsNullOrEmpty(requestUri.Fragment) ||
            !SafeEndpoint.TryCreate(requestUri, out SafeEndpoint? actualEndpoint) ||
            !expectedEndpoint.Equals(actualEndpoint))
        {
            throw new ArgumentException(
                "The request URI must be an absolute HTTPS URI bound to the expected endpoint.",
                nameof(requestUri));
        }

        if (maximumResponseBytes <= 0 || maximumResponseBytes > HttpTransportLimits.MaximumAllowedResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        }

        return new HttpTransportRequest(new Uri(requestUri.AbsoluteUri), expectedEndpoint, maximumResponseBytes, []);
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

public sealed class HttpResponseLease : IDisposable
{
    private byte[] _content;
    private readonly Uri? _effectiveUri;

    internal HttpResponseLease(byte[] content, Uri? effectiveUri = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        _effectiveUri = effectiveUri;
    }

    public ReadOnlyMemory<byte> Content => _content;

    internal Uri? EffectiveUri => _effectiveUri;

    internal Stream OpenReadStream() => new MemoryStream(_content, writable: false);

    public static HttpResponseLease CopyFrom(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty || content.Length > HttpTransportLimits.MaximumAllowedResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }

        return new HttpResponseLease(content.ToArray());
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
