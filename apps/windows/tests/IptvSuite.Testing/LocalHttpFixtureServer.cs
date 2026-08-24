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
    private bool _disposed;

    private LocalHttpFixtureServer(
        WebApplication application,
        Uri baseAddress,
        ConcurrentQueue<FixtureHttpRequest> requests,
        X509Certificate2? certificate,
        FixtureHttpMetrics metrics,
        IReadOnlyList<byte[]> ownedResponseBodies)
    {
        _application = application;
        BaseAddress = baseAddress;
        _requests = requests;
        _certificate = certificate;
        _metrics = metrics;
        _ownedResponseBodies = ownedResponseBodies;
    }

    public Uri BaseAddress { get; }

    public int Port => BaseAddress.Port;

    public X509Certificate2? Certificate => _certificate;

    public IReadOnlyList<FixtureHttpRequest> Requests => [.. _requests];

    public int RequestCount => Volatile.Read(ref _metrics.RequestCount);

    public int CompletedResponseCount => Volatile.Read(ref _metrics.CompletedResponseCount);

    public long CompletedBodyBytes => Interlocked.Read(ref _metrics.CompletedBodyBytes);

    public int FailureCount => Volatile.Read(ref _metrics.FailureCount);

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

            application.Run(context => HandleRequestAsync(
                context,
                routeSnapshot,
                requests,
                metrics));

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
                    ownedResponseBodies);
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
        FixtureHttpMetrics metrics)
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
