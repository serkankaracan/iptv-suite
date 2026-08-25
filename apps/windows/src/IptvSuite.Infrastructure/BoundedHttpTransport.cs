using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.Infrastructure;

public sealed class BoundedHttpTransport : IHttpTransport, IStreamingHttpTransport, IDisposable
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(2);
    private readonly HttpClient _client;
    private readonly TimeSpan _requestTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly IHttpTransportObserver? _observer;
    private bool _disposed;

    public BoundedHttpTransport()
        : this(observer: null)
    {
    }

    public BoundedHttpTransport(IHttpTransportObserver? observer)
        : this(CreateProductionHandler(), DefaultRequestTimeout, Task.Delay, observer)
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
        : this(handler, requestTimeout, delayAsync, observer: null)
    {
    }

    internal BoundedHttpTransport(
        HttpMessageHandler handler,
        TimeSpan requestTimeout,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        IHttpTransportObserver? observer)
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
        _observer = observer;
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
        int attemptCount = 0;
        long startedAt = Stopwatch.GetTimestamp();
        HttpTransportResult Finish(HttpTransportResult result, int responseBytes = 0)
        {
            _observer?.Observe(new HttpTransportObservation(
                attemptCount,
                redirectCount,
                result.StatusCode,
                responseBytes,
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                result.Failure));
            return result;
        }

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
                attemptCount++;
                response = await _client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedSource.Token).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount >= HttpTransportLimits.MaximumRedirects)
                    {
                        return Finish(HttpTransportResult.Failed(
                            HttpTransportFailure.RedirectLimitExceeded,
                            HttpTransportRetryability.Never,
                            (int)response.StatusCode));
                    }

                    if (!TryResolveRedirect(currentUri, response.Headers.Location, out Uri? redirectUri))
                    {
                        return Finish(HttpTransportResult.Failed(
                            HttpTransportFailure.RedirectRejected,
                            HttpTransportRetryability.Never,
                            (int)response.StatusCode));
                    }

                    DomainResult<RedirectTargetAssessment> assessment =
                        RedirectTargetPolicy.Evaluate(currentEndpoint, redirectUri!.AbsoluteUri);
                    if (!assessment.IsSuccess)
                    {
                        return Finish(HttpTransportResult.Failed(
                            HttpTransportFailure.RedirectRejected,
                            HttpTransportRetryability.Never,
                            (int)response.StatusCode));
                    }

                    if (request.HasAuthorization &&
                        assessment.Value!.OriginRelation == RedirectOriginRelation.CrossOrigin)
                    {
                        return Finish(HttpTransportResult.Failed(
                            HttpTransportFailure.RedirectRejected,
                            HttpTransportRetryability.Never,
                            (int)response.StatusCode));
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
                            return Finish(HttpTransportResult.Failed(
                                HttpTransportFailure.RequestTimedOut,
                                HttpTransportRetryability.BoundedTransient));
                        }
                        currentUri = request.RequestUri;
                        currentEndpoint = request.ExpectedEndpoint;
                        redirectCount = 0;
                        continue;
                    }

                    return Finish(statusResult);
                }

                long? declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength is < 0 || declaredLength > request.MaximumResponseBytes)
                {
                    return Finish(HttpTransportResult.Failed(
                        HttpTransportFailure.ResponseTooLarge,
                        HttpTransportRetryability.Never,
                        (int)response.StatusCode));
                }

                byte[] content = await ReadBoundedAsync(
                    response.Content,
                    request.MaximumResponseBytes,
                    linkedSource.Token).ConfigureAwait(false);
                return Finish(
                    HttpTransportResult.Success(
                        (int)response.StatusCode,
                        new HttpResponseLease(content).BindEffectiveUri(currentUri)),
                    content.Length);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Finish(HttpTransportResult.Failed(
                    HttpTransportFailure.RequestTimedOut,
                    HttpTransportRetryability.BoundedTransient));
            }
            catch (HttpResponseTooLargeException)
            {
                return Finish(HttpTransportResult.Failed(
                    HttpTransportFailure.ResponseTooLarge,
                    HttpTransportRetryability.Never,
                    (int)(response?.StatusCode ?? 0)));
            }
            catch (HttpRequestException exception) when (IsTlsFailure(exception))
            {
                return Finish(HttpTransportResult.Failed(
                    HttpTransportFailure.TlsValidationFailed,
                    HttpTransportRetryability.Never));
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
                        return Finish(HttpTransportResult.Failed(
                            HttpTransportFailure.RequestTimedOut,
                            HttpTransportRetryability.BoundedTransient));
                    }
                    currentUri = request.RequestUri;
                    currentEndpoint = request.ExpectedEndpoint;
                    redirectCount = 0;
                    continue;
                }

                return Finish(HttpTransportResult.Failed(
                    HttpTransportFailure.NetworkUnavailable,
                    HttpTransportRetryability.Manual));
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    public async ValueTask<HttpStreamingResult> GetStreamAsync(
        HttpTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var timeoutSource = new CancellationTokenSource(_requestTimeout);
        var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        Uri currentUri = request.RequestUri;
        SafeEndpoint currentEndpoint = request.ExpectedEndpoint;
        int redirectCount = 0;
        int retryCount = 0;

        try
        {
            while (true)
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, currentUri);
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                if (request.HasAuthorization)
                {
                    message.Headers.TryAddWithoutValidation(
                        "Authorization",
                        Encoding.ASCII.GetString(request.AuthorizationValue));
                }

                HttpResponseMessage response;
                try
                {
                    response = await _client.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        linkedSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return HttpStreamingResult.Failed(
                        HttpTransportFailure.RequestTimedOut,
                        HttpTransportRetryability.BoundedTransient);
                }
                catch (HttpRequestException exception) when (IsTlsFailure(exception))
                {
                    return HttpStreamingResult.Failed(
                        HttpTransportFailure.TlsValidationFailed,
                        HttpTransportRetryability.Never);
                }
                catch (HttpRequestException)
                {
                    if (retryCount++ < 2)
                    {
                        if (!await DelayBeforeRetryAsync(
                            GetRetryDelay(null, retryCount),
                            cancellationToken,
                            linkedSource.Token).ConfigureAwait(false))
                        {
                            return HttpStreamingResult.Failed(
                                HttpTransportFailure.RequestTimedOut,
                                HttpTransportRetryability.BoundedTransient);
                        }

                        currentUri = request.RequestUri;
                        currentEndpoint = request.ExpectedEndpoint;
                        redirectCount = 0;
                        continue;
                    }

                    return HttpStreamingResult.Failed(
                        HttpTransportFailure.NetworkUnavailable,
                        HttpTransportRetryability.Manual);
                }

                if (IsRedirect(response.StatusCode))
                {
                    using (response)
                    {
                        if (redirectCount >= HttpTransportLimits.MaximumRedirects ||
                            !TryResolveRedirect(currentUri, response.Headers.Location, out Uri? redirectUri))
                        {
                            return HttpStreamingResult.Failed(
                                HttpTransportFailure.RedirectRejected,
                                HttpTransportRetryability.Never,
                                (int)response.StatusCode);
                        }

                        DomainResult<RedirectTargetAssessment> assessment =
                            RedirectTargetPolicy.Evaluate(currentEndpoint, redirectUri!.AbsoluteUri);
                        if (!assessment.IsSuccess ||
                            (request.HasAuthorization &&
                             assessment.Value!.OriginRelation == RedirectOriginRelation.CrossOrigin))
                        {
                            return HttpStreamingResult.Failed(
                                HttpTransportFailure.RedirectRejected,
                                HttpTransportRetryability.Never,
                                (int)response.StatusCode);
                        }

                        currentUri = redirectUri;
                        currentEndpoint = assessment.Value!.TargetEndpoint;
                        redirectCount++;
                        continue;
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    using (response)
                    {
                        HttpTransportResult status = ClassifyStatus(response.StatusCode);
                        if (status.Retryability == HttpTransportRetryability.BoundedTransient && retryCount++ < 2)
                        {
                            if (!await DelayBeforeRetryAsync(
                                GetRetryDelay(response.Headers.RetryAfter, retryCount),
                                cancellationToken,
                                linkedSource.Token).ConfigureAwait(false))
                            {
                                return HttpStreamingResult.Failed(
                                    HttpTransportFailure.RequestTimedOut,
                                    HttpTransportRetryability.BoundedTransient);
                            }

                            currentUri = request.RequestUri;
                            currentEndpoint = request.ExpectedEndpoint;
                            redirectCount = 0;
                            continue;
                        }

                        return HttpStreamingResult.Failed(
                            status.Failure!.Value,
                            status.Retryability,
                            status.StatusCode);
                    }
                }

                long? declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength is < 0 || declaredLength > request.MaximumResponseBytes)
                {
                    response.Dispose();
                    return HttpStreamingResult.Failed(
                        HttpTransportFailure.ResponseTooLarge,
                        HttpTransportRetryability.Never,
                        (int)response.StatusCode);
                }

                Stream rawStream;
                try
                {
                    rawStream = await response.Content.ReadAsStreamAsync(linkedSource.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
                CancellationToken responseLifetimeToken = linkedSource.Token;
                var owner = new StreamingResponseOwner(response, linkedSource, timeoutSource);
                linkedSource = null!;
                timeoutSource = null!;
                var boundedStream = new BoundedReadStream(
                    rawStream,
                    request.MaximumResponseBytes,
                    responseLifetimeToken);
                return HttpStreamingResult.Success(
                    (int)response.StatusCode,
                    new HttpStreamingResponseLease(
                        boundedStream,
                        currentUri,
                        owner,
                        NormalizeEntityTag(response.Headers.ETag?.ToString()),
                        response.Content.Headers.LastModified?.ToUniversalTime()));
            }
        }
        finally
        {
            linkedSource?.Dispose();
            timeoutSource?.Dispose();
        }
    }

    private static string? NormalizeEntityTag(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512)
        {
            return null;
        }

        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return null;
            }
        }

        return value;
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

    private static HttpTransportResult ClassifyStatus(HttpStatusCode statusCode) => (int)statusCode switch
    {
        (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden => HttpTransportResult.Failed(
            HttpTransportFailure.AuthenticationRejected,
            HttpTransportRetryability.Never,
            (int)statusCode),
        (int)HttpStatusCode.NotFound => HttpTransportResult.Failed(
            HttpTransportFailure.ResourceNotFound,
            HttpTransportRetryability.Never,
            (int)statusCode),
        (int)HttpStatusCode.RequestTimeout => HttpTransportResult.Failed(
            HttpTransportFailure.RequestTimedOut,
            HttpTransportRetryability.BoundedTransient,
            (int)statusCode),
        (int)HttpStatusCode.TooManyRequests => HttpTransportResult.Failed(
            HttpTransportFailure.RateLimited,
            HttpTransportRetryability.BoundedTransient,
            (int)statusCode),
        >= 500 and <= 599 => HttpTransportResult.Failed(
            HttpTransportFailure.RemoteServiceUnavailable,
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

    private sealed class HttpResponseTooLargeException : IOException;

    private sealed class StreamingResponseOwner(
        HttpResponseMessage response,
        CancellationTokenSource linkedSource,
        CancellationTokenSource timeoutSource) : IDisposable
    {
        public void Dispose()
        {
            response.Dispose();
            linkedSource.Dispose();
            timeoutSource.Dispose();
        }
    }

    private sealed class BoundedReadStream(
        Stream inner,
        int maximumBytes,
        CancellationToken lifetimeCancellationToken) : Stream
    {
        private int _readBytes;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _readBytes; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource? linked = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetimeCancellationToken)
                : null;
            CancellationToken effectiveToken = linked?.Token ?? lifetimeCancellationToken;
            int read = await inner.ReadAsync(buffer, effectiveToken).ConfigureAwait(false);
            _readBytes = checked(_readBytes + read);
            if (_readBytes > maximumBytes)
            {
                throw new HttpResponseTooLargeException();
            }

            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
