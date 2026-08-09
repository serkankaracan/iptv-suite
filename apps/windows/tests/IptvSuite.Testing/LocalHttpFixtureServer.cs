using System.Collections.Concurrent;
using System.Net;
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
    private bool _disposed;

    private LocalHttpFixtureServer(
        WebApplication application,
        Uri baseAddress,
        ConcurrentQueue<FixtureHttpRequest> requests)
    {
        _application = application;
        BaseAddress = baseAddress;
        _requests = requests;
    }

    public Uri BaseAddress { get; }

    public int Port => BaseAddress.Port;

    public IReadOnlyList<FixtureHttpRequest> Requests => [.. _requests];

    public static async Task<LocalHttpFixtureServer> StartAsync(
        IReadOnlyDictionary<string, FixtureHttpResponse> routes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routes);
        Dictionary<string, FixtureHttpResponse> routeSnapshot = routes.ToDictionary(
            pair => ValidateRoute(pair.Key),
            pair => new FixtureHttpResponse(pair.Value.StatusCode, pair.Value.ContentType, pair.Value.Body.ToArray()),
            StringComparer.Ordinal);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

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

        return new LocalHttpFixtureServer(application, baseAddress, requests);
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
        _disposed = true;
        GC.SuppressFinalize(this);
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
