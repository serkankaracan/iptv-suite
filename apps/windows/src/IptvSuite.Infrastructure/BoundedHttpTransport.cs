using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.Infrastructure;

public sealed class BoundedHttpTransport : IHttpTransport, IDisposable
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(2);
    private readonly HttpClient _client;
    private readonly TimeSpan _requestTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private bool _disposed;

    public BoundedHttpTransport()
        : this(CreateProductionHandler(), DefaultRequestTimeout)
    {
    }

    internal BoundedHttpTransport(HttpMessageHandler handler, TimeSpan requestTimeout)
        : this(handler, requestTimeout, Task.Delay)
    {
    }

    internal BoundedHttpTransport(
        HttpMessageHandler handler,
        TimeSpan requestTimeout,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(delayAsync);
        if (requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _requestTimeout = requestTimeout;
        _delayAsync = delayAsync;
    }

    public async ValueTask<HttpTransportResult> GetAsync(
        HttpTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using CancellationTokenSource timeoutSource = new(_requestTimeout);
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        Uri currentUri = request.RequestUri;
        SafeEndpoint currentEndpoint = request.ExpectedEndpoint;
        int redirectCount = 0;
        int retryCount = 0;
        while (true)
        {
            using HttpRequestMessage message = new(HttpMethod.Get, currentUri);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            if (request.HasAuthorization)
            {
                message.Headers.TryAddWithoutValidation(
                    "Authorization",
                    Encoding.ASCII.GetString(request.AuthorizationValue));
            }

            HttpResponseMessage? response = null;
            try
            {
                response = await _client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedSource.Token).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount >= HttpTransportLimits.MaximumRedirects)
                    {
                        return HttpTransportResult.Failed(
                            HttpTransportFailure.RedirectLimitExceeded,
                            HttpTransportRetryability.Never,
                            (int)response.StatusCode);
                    }

                    if (!TryResolveRedirect(currentUri, response.Headers.Location, out Uri? redirectUri))
                    {
                        return HttpTransportResult.Failed(
                            HttpTransportFailure.RedirectRejected,
                            HttpTransportRetryability.Never,
                            (int)response.StatusCode);
                    }

                    DomainResult<RedirectTargetAssessment> assessment =
                        RedirectTargetPolicy.Evaluate(currentEndpoint, redirectUri!.AbsoluteUri);
                    if (!assessment.IsSuccess)
                    {
                        return HttpTransportResult.Failed(
                            HttpTransportFailure.RedirectRejected,
                            HttpTransportRetryability.Never,
                            (int)response.StatusCode);
                    }

                    if (request.HasAuthorization &&
                        assessment.Value!.OriginRelation == RedirectOriginRelation.CrossOrigin)
                    {
                        return HttpTransportResult.Failed(
                            HttpTransportFailure.RedirectRejected,
                            HttpTransportRetryability.Never,
                            (int)response.StatusCode);
                    }

                    currentUri = redirectUri;
                    currentEndpoint = assessment.Value!.TargetEndpoint;
                    redirectCount++;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    HttpTransportResult statusResult = ClassifyStatus(response.StatusCode);
                    if (statusResult.Retryability == HttpTransportRetryability.BoundedTransient && retryCount < 2)
                    {
                        retryCount++;
                        if (!await DelayBeforeRetryAsync(
                            GetRetryDelay(response.Headers.RetryAfter, retryCount),
                            cancellationToken,
                            linkedSource.Token).ConfigureAwait(false))
                        {
                            return HttpTransportResult.Failed(
                                HttpTransportFailure.RequestTimedOut,
                                HttpTransportRetryability.BoundedTransient);
                        }
                        currentUri = request.RequestUri;
                        currentEndpoint = request.ExpectedEndpoint;
                        redirectCount = 0;
                        continue;
                    }

                    return statusResult;
                }

                long? declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength is < 0 || declaredLength > request.MaximumResponseBytes)
                {
                    return HttpTransportResult.Failed(
                        HttpTransportFailure.ResponseTooLarge,
                        HttpTransportRetryability.Never,
                        (int)response.StatusCode);
                }

                byte[] content = await ReadBoundedAsync(
                    response.Content,
                    request.MaximumResponseBytes,
                    linkedSource.Token).ConfigureAwait(false);
                return HttpTransportResult.Success((int)response.StatusCode, new HttpResponseLease(content));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return HttpTransportResult.Failed(
                    HttpTransportFailure.RequestTimedOut,
                    HttpTransportRetryability.BoundedTransient);
            }
            catch (HttpResponseTooLargeException)
            {
                return HttpTransportResult.Failed(
                    HttpTransportFailure.ResponseTooLarge,
                    HttpTransportRetryability.Never,
                    (int)(response?.StatusCode ?? 0));
            }
            catch (HttpRequestException exception) when (IsTlsFailure(exception))
            {
                return HttpTransportResult.Failed(
                    HttpTransportFailure.TlsValidationFailed,
                    HttpTransportRetryability.Never);
            }
            catch (HttpRequestException)
            {
                if (retryCount < 2)
                {
                    retryCount++;
                    if (!await DelayBeforeRetryAsync(
                        GetRetryDelay(retryAfter: null, retryCount),
                        cancellationToken,
                        linkedSource.Token).ConfigureAwait(false))
                    {
                        return HttpTransportResult.Failed(
                            HttpTransportFailure.RequestTimedOut,
                            HttpTransportRetryability.BoundedTransient);
                    }
                    currentUri = request.RequestUri;
                    currentEndpoint = request.ExpectedEndpoint;
                    redirectCount = 0;
                    continue;
                }

                return HttpTransportResult.Failed(
                    HttpTransportFailure.NetworkUnavailable,
                    HttpTransportRetryability.Manual);
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _client.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxConnectionsPerServer = 4,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        UseCookies = false,
    };

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool TryResolveRedirect(Uri currentUri, Uri? location, out Uri? redirectUri)
    {
        redirectUri = null;
        if (location is null)
        {
            return false;
        }

        try
        {
            redirectUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            return redirectUri.IsAbsoluteUri;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static HttpTransportResult ClassifyStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => HttpTransportResult.Failed(
            HttpTransportFailure.AuthenticationRejected,
            HttpTransportRetryability.Never,
            (int)statusCode),
        HttpStatusCode.NotFound => HttpTransportResult.Failed(
            HttpTransportFailure.ResourceNotFound,
            HttpTransportRetryability.Never,
            (int)statusCode),
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => HttpTransportResult.Failed(
            HttpTransportFailure.RequestRejected,
            HttpTransportRetryability.BoundedTransient,
            (int)statusCode),
        >= HttpStatusCode.InternalServerError => HttpTransportResult.Failed(
            HttpTransportFailure.RequestRejected,
            HttpTransportRetryability.BoundedTransient,
            (int)statusCode),
        _ => HttpTransportResult.Failed(
            HttpTransportFailure.RequestRejected,
            HttpTransportRetryability.Never,
            (int)statusCode),
    };

    private static TimeSpan GetRetryDelay(RetryConditionHeaderValue? retryAfter, int retryCount)
    {
        int baseMilliseconds = retryCount == 1 ? 100 : 300;
        TimeSpan fallback = TimeSpan.FromMilliseconds(baseMilliseconds + Random.Shared.Next(0, 101));
        TimeSpan? requested = retryAfter?.Delta;
        if (requested is null && retryAfter?.Date is DateTimeOffset date)
        {
            requested = date - DateTimeOffset.UtcNow;
        }

        if (requested is null || requested <= TimeSpan.Zero)
        {
            return fallback;
        }

        return requested > MaximumRetryAfter ? MaximumRetryAfter : requested.Value;
    }

    private async Task<bool> DelayBeforeRetryAsync(
        TimeSpan delay,
        CancellationToken callerCancellationToken,
        CancellationToken linkedCancellationToken)
    {
        try
        {
            await _delayAsync(delay, linkedCancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        byte[] contentBuffer = ArrayPool<byte>.Shared.Rent(maximumBytes);
        int contentLength = 0;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return contentBuffer.AsSpan(0, contentLength).ToArray();
                }

                if (contentLength + read > maximumBytes)
                {
                    throw new HttpResponseTooLargeException();
                }

                buffer.AsSpan(0, read).CopyTo(contentBuffer.AsSpan(contentLength));
                contentLength += read;
            }
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
            Array.Clear(contentBuffer, 0, contentBuffer.Length);
            ArrayPool<byte>.Shared.Return(contentBuffer);
        }
    }

    private static bool IsTlsFailure(HttpRequestException exception)
    {
        if (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return true;
        }

        Exception? current = exception;
        for (int depth = 0; current is not null && depth < 8; depth++)
        {
            if (current is AuthenticationException)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    private sealed class HttpResponseTooLargeException : Exception;
}
