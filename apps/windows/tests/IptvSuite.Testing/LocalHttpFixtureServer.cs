using System.Collections.Concurrent;
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
    private bool _disposed;

    private LocalHttpFixtureServer(
        WebApplication application,
        Uri baseAddress,
        ConcurrentQueue<FixtureHttpRequest> requests,
        X509Certificate2? certificate)
    {
        _application = application;
        BaseAddress = baseAddress;
        _requests = requests;
        _certificate = certificate;
    }

    public Uri BaseAddress { get; }

    public int Port => BaseAddress.Port;

    public X509Certificate2? Certificate => _certificate;

    public IReadOnlyList<FixtureHttpRequest> Requests => [.. _requests];

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
        Dictionary<string, FixtureHttpResponse> routeSnapshot = routes.ToDictionary(
            pair => ValidateRoute(pair.Key),
            pair => new FixtureHttpResponse(pair.Value.StatusCode, pair.Value.ContentType, pair.Value.Body.ToArray()),
            StringComparer.Ordinal);
        X509Certificate2? certificate = useHttps ? CreateLoopbackCertificate() : null;

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

        application.Run(async context =>
        {
            string method = context.Request.Method;
            string path = context.Request.Path.Value ?? "/";
            requests.Enqueue(new FixtureHttpRequest(method, path));

            if (!routeSnapshot.TryGetValue(path, out FixtureHttpResponse? response))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType;
            context.Response.ContentLength = response.Body.Length;
            await context.Response.Body.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
        });

        await application.StartAsync(cancellationToken).ConfigureAwait(false);

        IServer server = application.Services.GetRequiredService<IServer>();
        IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound address.");
        string address = addresses.Addresses.Single();
        Uri baseAddress = new(address, UriKind.Absolute);

        if (!IPAddress.TryParse(baseAddress.Host, out IPAddress? boundAddress) ||
            !IPAddress.IsLoopback(boundAddress))
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Fixture server must bind only to a loopback address.");
        }

        return new LocalHttpFixtureServer(application, baseAddress, requests, certificate);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));
        await _application.StopAsync(stopTimeout.Token).ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        _certificate?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
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
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(1));
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

public sealed record FixtureHttpResponse(int StatusCode, string ContentType, ReadOnlyMemory<byte> Body);
