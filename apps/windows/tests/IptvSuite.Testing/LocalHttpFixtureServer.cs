using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IptvSuite.Testing;

public sealed class LocalHttpFixtureServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly ConcurrentQueue<FixtureHttpRequest> _requests;
    private readonly X509Certificate2? _certificate;
    private readonly FixtureHttpMetrics _metrics;
    private readonly IReadOnlyList<byte[]> _ownedResponseBodies;
    private readonly Dictionary<string, FixtureHttpResponse> _routes;
    private readonly ControlledFixtureStreamRegistry _controlledStreams;
    private bool _disposed;

    private LocalHttpFixtureServer(
        WebApplication application,
        Uri baseAddress,
        ConcurrentQueue<FixtureHttpRequest> requests,
        X509Certificate2? certificate,
        FixtureHttpMetrics metrics,
        IReadOnlyList<byte[]> ownedResponseBodies,
        Dictionary<string, FixtureHttpResponse> routes,
        ControlledFixtureStreamRegistry controlledStreams)
    {
        _application = application;
        BaseAddress = baseAddress;
        _requests = requests;
        _certificate = certificate;
        _metrics = metrics;
        _ownedResponseBodies = ownedResponseBodies;
        _routes = routes;
        _controlledStreams = controlledStreams;
    }

    public Uri BaseAddress { get; }

    public int Port => BaseAddress.Port;

    public X509Certificate2? Certificate => _certificate;

    public IReadOnlyList<FixtureHttpRequest> Requests => [.. _requests];

    public int RequestCount => Volatile.Read(ref _metrics.RequestCount);

    public int CompletedResponseCount => Volatile.Read(ref _metrics.CompletedResponseCount);

    public long CompletedBodyBytes => Interlocked.Read(ref _metrics.CompletedBodyBytes);

    public int FailureCount => Volatile.Read(ref _metrics.FailureCount);

    public ControlledFixtureStreamControl EnableControlledStream(
        string route,
        ControlledFixtureStreamOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string validatedRoute = ValidateRoute(route);
        if (!_routes.TryGetValue(validatedRoute, out FixtureHttpResponse? response))
        {
            throw new ArgumentException("A controlled fixture stream must bind to an existing route.", nameof(route));
        }

        if (response.StatusCode != StatusCodes.Status200OK || response.Body.IsEmpty)
        {
            throw new ArgumentException(
                "A controlled fixture stream requires a non-empty successful response.",
                nameof(route));
        }

        ControlledFixtureStreamOptions validatedOptions =
            ControlledFixtureStreamOptions.Validate(options ?? new ControlledFixtureStreamOptions());
        var stream = new ControlledFixtureStreamControl(validatedOptions);
        _controlledStreams.Add(validatedRoute, stream);
        return stream;
    }

    public static async Task<LocalHttpFixtureServer> StartAsync(
        IReadOnlyDictionary<string, FixtureHttpResponse> routes,
        CancellationToken cancellationToken = default)
    {
        return await StartCoreAsync(routes, useHttps: false, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<LocalHttpFixtureServer> StartHttpsAsync(
        IReadOnlyDictionary<string, FixtureHttpResponse> routes,
        CancellationToken cancellationToken = default)
    {
        return await StartCoreAsync(routes, useHttps: true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LocalHttpFixtureServer> StartCoreAsync(
        IReadOnlyDictionary<string, FixtureHttpResponse> routes,
        bool useHttps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routes);
        (Dictionary<string, FixtureHttpResponse> routeSnapshot, List<byte[]> ownedResponseBodies) =
            CloneRoutes(routes);
        X509Certificate2? certificate = null;
        var metrics = new FixtureHttpMetrics();

        try
        {
            certificate = useHttps ? CreateLoopbackCertificate() : null;
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(
                IPAddress.Loopback,
                0,
                listenOptions =>
                {
                    if (certificate is not null)
                    {
                        listenOptions.UseHttps(certificate);
                    }
                }));

            WebApplication application = builder.Build();
            ConcurrentQueue<FixtureHttpRequest> requests = new();
            var controlledStreams = new ControlledFixtureStreamRegistry();

            application.Run(context => HandleRequestAsync(
                context,
                routeSnapshot,
                requests,
                metrics,
                controlledStreams));

            try
            {
                await application.StartAsync(cancellationToken).ConfigureAwait(false);

                IServer server = application.Services.GetRequiredService<IServer>();
                IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
                    ?? throw new InvalidOperationException("Kestrel did not expose its bound address.");
                string address = addresses.Addresses.Single();
                Uri baseAddress = new(address, UriKind.Absolute);

                if (!IPAddress.TryParse(baseAddress.Host, out IPAddress? boundAddress) ||
                    !IPAddress.IsLoopback(boundAddress))
                {
                    throw new InvalidOperationException("Fixture server must bind only to a loopback address.");
                }

                return new LocalHttpFixtureServer(
                    application,
                    baseAddress,
                    requests,
                    certificate,
                    metrics,
                    ownedResponseBodies,
                    routeSnapshot,
                    controlledStreams);
            }
            catch
            {
                await application.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            certificate?.Dispose();
            ZeroResponseBodies(ownedResponseBodies);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controlledStreams.DisableAll();
        try
        {
            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));
            try
            {
                await _application.StopAsync(stopTimeout.Token).ConfigureAwait(false);
            }
            finally
            {
                await _application.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _certificate?.Dispose();
            ZeroResponseBodies(_ownedResponseBodies);
            GC.SuppressFinalize(this);
        }
    }

    private static (Dictionary<string, FixtureHttpResponse> Routes, List<byte[]> OwnedBodies)
        CloneRoutes(IReadOnlyDictionary<string, FixtureHttpResponse> routes)
    {
        var snapshot = new Dictionary<string, FixtureHttpResponse>(routes.Count, StringComparer.Ordinal);
        var ownedBodies = new List<byte[]>(routes.Count);
        try
        {
            foreach ((string route, FixtureHttpResponse response) in routes)
            {
                ArgumentNullException.ThrowIfNull(response);
                string validatedRoute = ValidateRoute(route);
                byte[] body = response.Body.ToArray();
                if (response.SupportsByteRanges &&
                    (response.StatusCode != StatusCodes.Status200OK || body.Length == 0))
                {
                    CryptographicOperations.ZeroMemory(body);
                    throw new ArgumentException(
                        "A byte-range fixture must be a non-empty successful response.",
                        nameof(routes));
                }

                ownedBodies.Add(body);
                snapshot.Add(
                    validatedRoute,
                    new FixtureHttpResponse(
                        response.StatusCode,
                        response.ContentType,
                        body,
                        response.SupportsByteRanges));
            }

            return (snapshot, ownedBodies);
        }
        catch
        {
            ZeroResponseBodies(ownedBodies);
            throw;
        }
    }

    private static async Task HandleRequestAsync(
        HttpContext context,
        Dictionary<string, FixtureHttpResponse> routes,
        ConcurrentQueue<FixtureHttpRequest> requests,
        FixtureHttpMetrics metrics,
        ControlledFixtureStreamRegistry controlledStreams)
    {
        string method = context.Request.Method;
        string path = context.Request.Path.Value ?? "/";
        requests.Enqueue(new FixtureHttpRequest(method, path));
        Interlocked.Increment(ref metrics.RequestCount);

        try
        {
            if (!routes.TryGetValue(path, out FixtureHttpResponse? response))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                Interlocked.Increment(ref metrics.FailureCount);
                return;
            }

            if (controlledStreams.TryGet(path, out ControlledFixtureStreamControl controlledStream) &&
                await controlledStream.TryHandleAsync(context, response, metrics).ConfigureAwait(false))
            {
                return;
            }

            if (response.SupportsByteRanges)
            {
                await WriteByteRangeResponseAsync(context, response, metrics).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType;
            context.Response.ContentLength = response.Body.Length;
            if (!HttpMethods.IsHead(method))
            {
                await context.Response.Body.WriteAsync(response.Body, context.RequestAborted)
                    .ConfigureAwait(false);
                Interlocked.Add(ref metrics.CompletedBodyBytes, response.Body.Length);
            }

            if (response.StatusCode is >= 200 and < 400)
            {
                Interlocked.Increment(ref metrics.CompletedResponseCount);
            }
            else
            {
                Interlocked.Increment(ref metrics.FailureCount);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            Interlocked.Increment(ref metrics.FailureCount);
        }
        catch (IOException)
        {
            Interlocked.Increment(ref metrics.FailureCount);
        }
        catch
        {
            Interlocked.Increment(ref metrics.FailureCount);
        }
    }

    private static async Task WriteByteRangeResponseAsync(
        HttpContext context,
        FixtureHttpResponse response,
        FixtureHttpMetrics metrics)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            Interlocked.Increment(ref metrics.FailureCount);
            return;
        }

        int start = 0;
        int end = response.Body.Length - 1;
        string rangeHeader = context.Request.Headers.Range.ToString();
        bool partial = !string.IsNullOrEmpty(rangeHeader);
        if (partial && !TryParseSingleByteRange(rangeHeader, response.Body.Length, out start, out end))
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            context.Response.Headers.ContentRange = $"bytes */{response.Body.Length.ToString(CultureInfo.InvariantCulture)}";
            Interlocked.Increment(ref metrics.FailureCount);
            return;
        }

        int length = checked(end - start + 1);
        context.Response.StatusCode = partial
            ? StatusCodes.Status206PartialContent
            : StatusCodes.Status200OK;
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength = length;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.CacheControl = "no-store";
        if (partial)
        {
            context.Response.Headers.ContentRange = FormattableString.Invariant(
                $"bytes {start}-{end}/{response.Body.Length}");
        }

        if (HttpMethods.IsGet(context.Request.Method))
        {
            ReadOnlyMemory<byte> selectedBody = response.Body.Slice(start, length);
            await context.Response.Body.WriteAsync(selectedBody, context.RequestAborted)
                .ConfigureAwait(false);
            Interlocked.Add(ref metrics.CompletedBodyBytes, length);
        }

        Interlocked.Increment(ref metrics.CompletedResponseCount);
    }

    private static bool TryParseSingleByteRange(
        string value,
        int totalLength,
        out int start,
        out int end)
    {
        start = 0;
        end = totalLength - 1;
        const string prefix = "bytes=";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            value.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> range = value.AsSpan(prefix.Length);
        int separator = range.IndexOf('-');
        if (separator < 0 || range[(separator + 1)..].Contains('-'))
        {
            return false;
        }

        ReadOnlySpan<char> startValue = range[..separator];
        ReadOnlySpan<char> endValue = range[(separator + 1)..];
        if (startValue.IsEmpty)
        {
            if (!int.TryParse(endValue, NumberStyles.None, CultureInfo.InvariantCulture, out int suffixLength) ||
                suffixLength <= 0)
            {
                return false;
            }

            suffixLength = Math.Min(suffixLength, totalLength);
            start = totalLength - suffixLength;
            end = totalLength - 1;
            return true;
        }

        if (!int.TryParse(startValue, NumberStyles.None, CultureInfo.InvariantCulture, out start) ||
            start < 0 || start >= totalLength)
        {
            return false;
        }

        if (endValue.IsEmpty)
        {
            end = totalLength - 1;
            return true;
        }

        if (!int.TryParse(endValue, NumberStyles.None, CultureInfo.InvariantCulture, out end) ||
            end < start)
        {
            return false;
        }

        end = Math.Min(end, totalLength - 1);
        return true;
    }

    private static void ZeroResponseBodies(IEnumerable<byte[]> bodies)
    {
        foreach (byte[] body in bodies)
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private static X509Certificate2 CreateLoopbackCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=IPTVSuite Synthetic Loopback",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder subjectAlternativeNames = new();
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        subjectAlternativeNames.AddDnsName("localhost");
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")],
            true));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 ephemeralCertificate = request.CreateSelfSigned(
            now.AddMinutes(-5),
            now.AddHours(1));
        byte[] pkcs12 = ephemeralCertificate.Export(X509ContentType.Pkcs12);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pkcs12,
                password: null,
                X509KeyStorageFlags.UserKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    private static string ValidateRoute(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        if (!route.StartsWith('/') || route.Contains('?'))
        {
            throw new ArgumentException("Fixture routes must be absolute paths without a query string.", nameof(route));
        }

        return route;
    }
}

public sealed record FixtureHttpRequest(string Method, string Path);

public sealed record FixtureHttpResponse(
    int StatusCode,
    string ContentType,
    ReadOnlyMemory<byte> Body,
    bool SupportsByteRanges = false);

internal sealed class FixtureHttpMetrics
{
    internal int RequestCount;
    internal int CompletedResponseCount;
    internal long CompletedBodyBytes;
    internal int FailureCount;
}

public enum ControlledFixtureStreamMode
{
    Enabled,
    Holding,
    RejectingNext,
    Disabled,
}

public sealed record ControlledFixtureStreamOptions
{
    public TimeSpan WriteInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    public int WriteSize { get; init; } = 12_032;

    public int MaximumRequestOrdinals { get; init; } = 64;

    internal static ControlledFixtureStreamOptions Validate(ControlledFixtureStreamOptions options)
    {
        if (options.WriteInterval < TimeSpan.FromMilliseconds(1) ||
            options.WriteInterval > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The controlled stream write interval must be between one millisecond and one second.");
        }

        if (options.WriteSize is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The controlled stream write size must be between one and 65536 bytes.");
        }

        if (options.MaximumRequestOrdinals is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The controlled stream request ordinal limit must be between one and 1024.");
        }

        return options with { };
    }
}

public sealed record ControlledFixtureStreamSnapshot(
    ControlledFixtureStreamMode Mode,
    long LastAssignedRequestOrdinal,
    long ActiveRequestOrdinal,
    int CurrentHeldRequestCount,
    int PeakHeldRequestCount,
    int PeakActiveRequestCount,
    int OverlapViolationCount,
    int ExpectedAbortCount,
    long LastExpectedAbortOrdinal,
    int ExpectedCompletionCount,
    long LastExpectedCompletionOrdinal,
    int ExpectedRejectCount,
    long LastExpectedRejectOrdinal,
    int ClientDetachCount,
    long LastClientDetachOrdinal,
    int DisabledFallbackCount,
    long LastDisabledFallbackOrdinal,
    int CapacityRejectCount,
    int UnexpectedFailureCount,
    long LastUnexpectedFailureOrdinal);

public sealed class ControlledFixtureStreamControl
{
    private readonly object _gate = new();
    private readonly ControlledFixtureStreamOptions _options;
    private TaskCompletionSource<bool> _stateChanged = CreateStateSignal();
    private ControlledFixtureStreamMode _mode = ControlledFixtureStreamMode.Enabled;
    private ActiveControlledRequest? _activeRequest;
    private long _lastAssignedRequestOrdinal;
    private int _currentHeldRequestCount;
    private int _peakHeldRequestCount;
    private int _peakActiveRequestCount;
    private int _overlapViolationCount;
    private int _expectedAbortCount;
    private long _lastExpectedAbortOrdinal;
    private int _expectedCompletionCount;
    private long _lastExpectedCompletionOrdinal;
    private int _expectedRejectCount;
    private long _lastExpectedRejectOrdinal;
    private int _clientDetachCount;
    private long _lastClientDetachOrdinal;
    private int _disabledFallbackCount;
    private long _lastDisabledFallbackOrdinal;
    private int _capacityRejectCount;
    private int _unexpectedFailureCount;
    private long _lastUnexpectedFailureOrdinal;

    internal ControlledFixtureStreamControl(ControlledFixtureStreamOptions options)
    {
        _options = options;
    }

    public ControlledFixtureStreamSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new ControlledFixtureStreamSnapshot(
                    _mode,
                    _lastAssignedRequestOrdinal,
                    _activeRequest?.Ordinal ?? 0,
                    _currentHeldRequestCount,
                    _peakHeldRequestCount,
                    _peakActiveRequestCount,
                    _overlapViolationCount,
                    _expectedAbortCount,
                    _lastExpectedAbortOrdinal,
                    _expectedCompletionCount,
                    _lastExpectedCompletionOrdinal,
                    _expectedRejectCount,
                    _lastExpectedRejectOrdinal,
                    _clientDetachCount,
                    _lastClientDetachOrdinal,
                    _disabledFallbackCount,
                    _lastDisabledFallbackOrdinal,
                    _capacityRejectCount,
                    _unexpectedFailureCount,
                    _lastUnexpectedFailureOrdinal);
            }
        }
    }

    public void HoldSubsequentRequests()
    {
        SetAdmissionMode(ControlledFixtureStreamMode.Holding);
    }

    public void RejectNextRequest()
    {
        SetAdmissionMode(ControlledFixtureStreamMode.RejectingNext);
    }

    public void Restore()
    {
        SetAdmissionMode(ControlledFixtureStreamMode.Enabled);
    }

    public bool TryAbortActive(long expectedRequestOrdinal)
    {
        if (expectedRequestOrdinal <= 0)
        {
            return false;
        }

        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_activeRequest is null ||
                _activeRequest.Ordinal != expectedRequestOrdinal ||
                _activeRequest.State != ActiveControlledRequestState.Active)
            {
                return false;
            }

            _activeRequest.State = ActiveControlledRequestState.AbortRequested;
            cancellation = _activeRequest.Cancellation;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    public bool TryCompleteActive(long expectedRequestOrdinal)
    {
        if (expectedRequestOrdinal <= 0)
        {
            return false;
        }

        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_activeRequest is null ||
                _activeRequest.Ordinal != expectedRequestOrdinal ||
                _activeRequest.State != ActiveControlledRequestState.Active)
            {
                return false;
            }

            _activeRequest.State = ActiveControlledRequestState.CompletionRequested;
            cancellation = _activeRequest.Cancellation;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    public void Disable()
    {
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (_mode == ControlledFixtureStreamMode.Disabled)
            {
                return;
            }

            _mode = ControlledFixtureStreamMode.Disabled;
            if (_activeRequest is { State: ActiveControlledRequestState.Active } activeRequest)
            {
                activeRequest.State = ActiveControlledRequestState.AbortRequested;
                cancellation = activeRequest.Cancellation;
            }

            PulseStateChangedLocked();
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async Task<bool> TryHandleAsync(
        HttpContext context,
        FixtureHttpResponse response,
        FixtureHttpMetrics metrics)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        ControlledRequestAdmission admission = await WaitForAdmissionAsync(context.RequestAborted)
            .ConfigureAwait(false);
        switch (admission.Kind)
        {
            case ControlledRequestAdmissionKind.DisabledFallback:
                return false;
            case ControlledRequestAdmissionKind.Rejected:
            case ControlledRequestAdmissionKind.CapacityRejected:
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentLength = 0;
                context.Response.Headers.CacheControl = "no-store";
                return true;
            case ControlledRequestAdmissionKind.ClientDetached:
                return true;
            case ControlledRequestAdmissionKind.Active:
                await WritePacedResponseAsync(context, response, metrics, admission.ActiveRequest!)
                    .ConfigureAwait(false);
                return true;
            default:
                throw new InvalidOperationException("Unknown controlled stream admission.");
        }
    }

    private async Task<ControlledRequestAdmission> WaitForAdmissionAsync(
        CancellationToken requestAborted)
    {
        long requestOrdinal;
        lock (_gate)
        {
            if (_mode == ControlledFixtureStreamMode.Disabled && _activeRequest is null)
            {
                return ControlledRequestAdmission.DisabledFallback();
            }

            if (_lastAssignedRequestOrdinal >= _options.MaximumRequestOrdinals)
            {
                IncrementBounded(ref _capacityRejectCount);
                return ControlledRequestAdmission.CapacityRejected();
            }

            requestOrdinal = ++_lastAssignedRequestOrdinal;
        }

        bool held = false;
        try
        {
            while (true)
            {
                Task stateChanged;
                lock (_gate)
                {
                    if (_mode == ControlledFixtureStreamMode.Disabled && _activeRequest is null)
                    {
                        IncrementBounded(ref _disabledFallbackCount);
                        _lastDisabledFallbackOrdinal = requestOrdinal;
                        return ControlledRequestAdmission.DisabledFallback();
                    }

                    if (_mode == ControlledFixtureStreamMode.RejectingNext)
                    {
                        _mode = ControlledFixtureStreamMode.Holding;
                        IncrementBounded(ref _expectedRejectCount);
                        _lastExpectedRejectOrdinal = requestOrdinal;
                        PulseStateChangedLocked();
                        return ControlledRequestAdmission.Rejected();
                    }

                    if (_mode == ControlledFixtureStreamMode.Enabled && _activeRequest is null)
                    {
                        var activeRequest = new ActiveControlledRequest(requestOrdinal);
                        _activeRequest = activeRequest;
                        _peakActiveRequestCount = Math.Max(_peakActiveRequestCount, 1);
                        return ControlledRequestAdmission.Active(activeRequest);
                    }

                    if (!held)
                    {
                        held = true;
                        _currentHeldRequestCount++;
                        _peakHeldRequestCount = Math.Max(
                            _peakHeldRequestCount,
                            _currentHeldRequestCount);
                    }

                    stateChanged = _stateChanged.Task;
                }

                await stateChanged.WaitAsync(requestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            lock (_gate)
            {
                IncrementBounded(ref _clientDetachCount);
                _lastClientDetachOrdinal = requestOrdinal;
            }

            return ControlledRequestAdmission.ClientDetached();
        }
        finally
        {
            if (held)
            {
                lock (_gate)
                {
                    _currentHeldRequestCount--;
                }
            }
        }
    }

    private async Task WritePacedResponseAsync(
        HttpContext context,
        FixtureHttpResponse response,
        FixtureHttpMetrics metrics,
        ActiveControlledRequest activeRequest)
    {
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            activeRequest.Cancellation.Token);
        CancellationToken cancellationToken = linkedCancellation.Token;
        ControlledRequestOutcome outcome = ControlledRequestOutcome.UnexpectedFailure;

        try
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = response.ContentType;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);

            int offset = 0;
            while (true)
            {
                int length = Math.Min(_options.WriteSize, response.Body.Length - offset);
                await context.Response.Body.WriteAsync(
                    response.Body.Slice(offset, length),
                    cancellationToken).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref metrics.CompletedBodyBytes, length);
                offset = offset + length == response.Body.Length ? 0 : offset + length;
                await Task.Delay(_options.WriteInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            outcome = ControlledRequestOutcome.ClientDetached;
        }
        catch (IOException)
        {
            outcome = ControlledRequestOutcome.ClientDetached;
        }
        catch
        {
            outcome = ControlledRequestOutcome.UnexpectedFailure;
        }
        finally
        {
            ControlledRequestOutcome terminalOutcome = CompleteActiveRequest(activeRequest, outcome);
            if (terminalOutcome == ControlledRequestOutcome.UnexpectedFailure)
            {
                Interlocked.Increment(ref metrics.FailureCount);
            }

            if (terminalOutcome == ControlledRequestOutcome.ExpectedCompletion)
            {
                Interlocked.Increment(ref metrics.CompletedResponseCount);
            }

            if (terminalOutcome == ControlledRequestOutcome.ExpectedAbort)
            {
                context.Abort();
            }

            activeRequest.Cancellation.Dispose();
        }
    }

    private ControlledRequestOutcome CompleteActiveRequest(
        ActiveControlledRequest activeRequest,
        ControlledRequestOutcome observedOutcome)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeRequest, activeRequest) ||
                activeRequest.State == ActiveControlledRequestState.Terminal)
            {
                IncrementBounded(ref _overlapViolationCount);
                return ControlledRequestOutcome.UnexpectedFailure;
            }

            ControlledRequestOutcome outcome = activeRequest.State switch
            {
                ActiveControlledRequestState.AbortRequested =>
                    ControlledRequestOutcome.ExpectedAbort,
                ActiveControlledRequestState.CompletionRequested =>
                    ControlledRequestOutcome.ExpectedCompletion,
                _ => observedOutcome,
            };
            activeRequest.State = ActiveControlledRequestState.Terminal;
            _activeRequest = null;
            switch (outcome)
            {
                case ControlledRequestOutcome.ExpectedAbort:
                    IncrementBounded(ref _expectedAbortCount);
                    _lastExpectedAbortOrdinal = activeRequest.Ordinal;
                    break;
                case ControlledRequestOutcome.ExpectedCompletion:
                    IncrementBounded(ref _expectedCompletionCount);
                    _lastExpectedCompletionOrdinal = activeRequest.Ordinal;
                    break;
                case ControlledRequestOutcome.ClientDetached:
                    IncrementBounded(ref _clientDetachCount);
                    _lastClientDetachOrdinal = activeRequest.Ordinal;
                    break;
                case ControlledRequestOutcome.UnexpectedFailure:
                    IncrementBounded(ref _unexpectedFailureCount);
                    _lastUnexpectedFailureOrdinal = activeRequest.Ordinal;
                    break;
                default:
                    throw new InvalidOperationException("Unknown controlled stream outcome.");
            }

            PulseStateChangedLocked();
            return outcome;
        }
    }

    private void SetAdmissionMode(ControlledFixtureStreamMode mode)
    {
        lock (_gate)
        {
            if (_mode == ControlledFixtureStreamMode.Disabled)
            {
                throw new InvalidOperationException("A disabled controlled fixture stream cannot change admission mode.");
            }

            _mode = mode;
            PulseStateChangedLocked();
        }
    }

    private void IncrementBounded(ref int value)
    {
        if (value < _options.MaximumRequestOrdinals)
        {
            value++;
        }
    }

    private void PulseStateChangedLocked()
    {
        TaskCompletionSource<bool> previous = _stateChanged;
        _stateChanged = CreateStateSignal();
        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CreateStateSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class ActiveControlledRequest(long ordinal)
    {
        internal long Ordinal { get; } = ordinal;

        internal CancellationTokenSource Cancellation { get; } = new();

        internal ActiveControlledRequestState State { get; set; } = ActiveControlledRequestState.Active;
    }
}

internal sealed class ControlledFixtureStreamRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ControlledFixtureStreamControl> _streams = new(StringComparer.Ordinal);

    internal void Add(string route, ControlledFixtureStreamControl stream)
    {
        lock (_gate)
        {
            if (!_streams.TryAdd(route, stream))
            {
                throw new InvalidOperationException("The fixture route already has a controlled stream.");
            }
        }
    }

    internal bool TryGet(string route, out ControlledFixtureStreamControl stream)
    {
        lock (_gate)
        {
            return _streams.TryGetValue(route, out stream!);
        }
    }

    internal void DisableAll()
    {
        ControlledFixtureStreamControl[] streams;
        lock (_gate)
        {
            streams = [.. _streams.Values];
        }

        foreach (ControlledFixtureStreamControl stream in streams)
        {
            stream.Disable();
        }
    }
}

internal enum ControlledRequestAdmissionKind
{
    Active,
    Rejected,
    CapacityRejected,
    ClientDetached,
    DisabledFallback,
}

internal sealed record ControlledRequestAdmission(
    ControlledRequestAdmissionKind Kind,
    ControlledFixtureStreamControl.ActiveControlledRequest? ActiveRequest)
{
    internal static ControlledRequestAdmission Active(
        ControlledFixtureStreamControl.ActiveControlledRequest activeRequest) =>
        new(ControlledRequestAdmissionKind.Active, activeRequest);

    internal static ControlledRequestAdmission Rejected() =>
        new(ControlledRequestAdmissionKind.Rejected, null);

    internal static ControlledRequestAdmission CapacityRejected() =>
        new(ControlledRequestAdmissionKind.CapacityRejected, null);

    internal static ControlledRequestAdmission ClientDetached() =>
        new(ControlledRequestAdmissionKind.ClientDetached, null);

    internal static ControlledRequestAdmission DisabledFallback() =>
        new(ControlledRequestAdmissionKind.DisabledFallback, null);
}

internal enum ControlledRequestOutcome
{
    ExpectedAbort,
    ExpectedCompletion,
    ClientDetached,
    UnexpectedFailure,
}

internal enum ActiveControlledRequestState
{
    Active,
    AbortRequested,
    CompletionRequested,
    Terminal,
}
