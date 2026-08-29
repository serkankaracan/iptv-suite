using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using IptvSuite.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class BoundedHttpTransportTests
{
    private const int XtreamCatalogResponseBytes = 64 * 1024 * 1024;
    private static readonly string[] SingleAuthorization = ["Bearer synthetic-boundary-marker"];
    private static readonly string[] RedirectedAuthorization =
        ["Bearer synthetic-boundary-marker", "Bearer synthetic-boundary-marker"];

    [TestMethod]
    public async Task SuccessReturnsBoundedOwnedContentAndLeaseZeroesOnDispose()
    {
        byte[] payload = Encoding.UTF8.GetBytes("#EXTM3U\n# synthetic");
        using StubHandler handler = new((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, payload)));
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/list", 128));

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(200, result.StatusCode);
        HttpResponseLease lease = result.Response!;
        byte[] backing = lease.Content.ToArray();
        CollectionAssert.AreEqual(payload, backing);
        lease.Dispose();
        Assert.AreEqual(0, lease.Content.Length);
    }

    [TestMethod]
    public void RemotePlaylistFactoryUsesDedicatedBudgetAndTimeoutOnly()
    {
        using HttpTransportRequest general = CreateRequest(
            "https://example.test/general",
            HttpTransportLimits.MaximumAllowedResponseBytes);
        using HttpTransportRequest remoteHttps = CreateRemotePlaylistSourceRequest(
            "https://example.test/catalog.m3u");
        using HttpTransportRequest remoteHttp = CreateRemotePlaylistSourceRequest(
            "http://example.test/catalog.m3u?token=synthetic");

        Assert.AreEqual(HttpTransportLimits.MaximumAllowedResponseBytes, general.MaximumResponseBytes);
        Assert.IsNull(ReadRequestTimeoutOverride(general));
        Assert.AreEqual(128 * 1024 * 1024, remoteHttps.MaximumResponseBytes);
        Assert.AreEqual(TimeSpan.FromMinutes(2), ReadRequestTimeoutOverride(remoteHttps));
        Assert.AreEqual(128 * 1024 * 1024, remoteHttp.MaximumResponseBytes);
        Assert.AreEqual(TimeSpan.FromMinutes(2), ReadRequestTimeoutOverride(remoteHttp));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateRequest(
            "https://example.test/general-oversized",
            HttpTransportLimits.MaximumAllowedResponseBytes + 1));
    }

    [TestMethod]
    public async Task RemotePlaylistBudgetAcceptsStreamingBodyBeyondGeneralLimit()
    {
        byte[] payload = new byte[HttpTransportLimits.MaximumAllowedResponseBytes + 1];
        payload[0] = (byte)'#';

        using StubHandler streamingHandler = new((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, payload)));
        using BoundedHttpTransport streamingTransport = CreateTransport(
            streamingHandler,
            TimeSpan.FromSeconds(1));
        using HttpTransportRequest streamingRequest = CreateRemotePlaylistSourceRequest(
            "http://example.test/catalog.m3u?token=synthetic");

        HttpStreamingResult streaming = await streamingTransport.GetStreamAsync(streamingRequest);

        Assert.IsTrue(streaming.IsSuccess);
        using (HttpStreamingResponseLease response = streaming.Response!)
        {
            await response.Content.CopyToAsync(Stream.Null);
        }
    }

    [TestMethod]
    public async Task RemotePlaylistBudgetRejectsDeclaredBodyAboveDedicatedLimit()
    {
        using StubHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.OK, [1]);
            response.Content.Headers.ContentLength = (128L * 1024 * 1024) + 1;
            return Task.FromResult(response);
        });
        using BoundedHttpTransport transport = CreateTransport(
            handler,
            TimeSpan.FromSeconds(1));
        using HttpTransportRequest request = CreateRemotePlaylistSourceRequest(
            "http://example.test/catalog.m3u?token=synthetic");

        HttpStreamingResult result = await transport.GetStreamAsync(request);

        Assert.AreEqual(HttpTransportFailure.ResponseTooLarge, result.Failure);
    }

    [TestMethod]
    public async Task RemotePlaylistTimeoutOverridesTheGeneralTransportDefault()
    {
        using StubHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(75), cancellationToken);
            return Response(HttpStatusCode.OK, "#EXTM3U\n"u8.ToArray());
        });
        using BoundedHttpTransport transport = CreateTransport(
            handler,
            TimeSpan.FromMilliseconds(20));
        using HttpTransportRequest request = CreateRemotePlaylistSourceRequest(
            "https://example.test/catalog.m3u");

        HttpStreamingResult result = await transport.GetStreamAsync(request);

        Assert.IsTrue(result.IsSuccess);
        result.Response!.Dispose();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task XtreamSpecificBudgetAcceptsLoopbackBodyPastGeneralCapAndRejectsDeclaredOverflow()
    {
        byte[] pastGeneralCap = new byte[HttpTransportLimits.MaximumAllowedResponseBytes + 1];
        pastGeneralCap[0] = (byte)'[';
        pastGeneralCap[^1] = (byte)']';
        Dictionary<string, FixtureHttpResponse> routes = new(StringComparer.Ordinal)
        {
            ["/xtream-list"] = new FixtureHttpResponse(
                200,
                "application/json",
                pastGeneralCap),
            ["/xtream-over-limit"] = new FixtureHttpResponse(
                200,
                "application/json",
                "[]"u8.ToArray(),
                DeclaredContentLength: (long)XtreamCatalogResponseBytes + 1),
        };
        await using LocalHttpFixtureServer server = await LocalHttpFixtureServer.StartAsync(routes);
        using var transport = new BoundedHttpTransport();
        string acceptedLocator = new Uri(server.BaseAddress, "xtream-list").AbsoluteUri;
        string oversizedLocator = new Uri(server.BaseAddress, "xtream-over-limit").AbsoluteUri;
        using HttpTransportRequest acceptedRequest = CreateXtreamSourceRequest(
            acceptedLocator,
            XtreamCatalogResponseBytes);
        using HttpTransportRequest oversizedRequest = CreateXtreamSourceRequest(
            oversizedLocator,
            XtreamCatalogResponseBytes);

        HttpTransportResult accepted = await transport.GetAsync(acceptedRequest);
        HttpTransportResult oversized = await transport.GetAsync(oversizedRequest);

        Assert.IsTrue(accepted.IsSuccess);
        using (HttpResponseLease response = accepted.Response!)
        {
            Assert.AreEqual(pastGeneralCap.Length, response.Content.Length);
        }

        Assert.AreEqual(HttpTransportFailure.ResponseTooLarge, oversized.Failure);
        Assert.AreEqual(HttpTransportRetryability.Never, oversized.Retryability);
        string[] expectedRequestPaths = ["/xtream-list", "/xtream-over-limit"];
        CollectionAssert.AreEqual(
            expectedRequestPaths,
            server.Requests.Select(request => request.Path).ToArray());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateRequest(
            "https://example.test/general-remains-bounded",
            HttpTransportLimits.MaximumAllowedResponseBytes + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateXtreamSourceRequest(
            "https://example.test/xtream-over-ceiling",
            XtreamCatalogResponseBytes + 1));
    }

    [TestMethod]
    public async Task XtreamHttpsRequestRejectsSameOriginRedirectWithoutSecondRequest()
    {
        using StubHandler handler = new((_, _) =>
        {
            HttpResponseMessage redirect = Response(HttpStatusCode.Redirect, []);
            redirect.Headers.Location = new Uri("https://example.test/final");
            return Task.FromResult(redirect);
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));
        using HttpTransportRequest request = CreateXtreamSourceRequest(
            "https://example.test/start",
            32);

        HttpTransportResult result = await transport.GetAsync(request);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(HttpTransportFailure.RedirectRejected, result.Failure);
        Assert.AreEqual(HttpTransportRetryability.Never, result.Retryability);
        Assert.AreEqual((int)HttpStatusCode.Redirect, result.StatusCode);
        Assert.HasCount(1, handler.RequestUris);
    }

    [TestMethod]
    public void DisposingXtreamRequestReleasesCredentialBearingUriReference()
    {
        HttpTransportRequest request = CreateXtreamSourceRequest(
            "https://example.test/player_api.php?username=synthetic&password=synthetic-secret",
            32);
        FieldInfo requestUriField = typeof(HttpTransportRequest).GetField(
            "_requestUri",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The bounded request URI field is unavailable.");

        Assert.IsNotNull(requestUriField.GetValue(request));

        request.Dispose();

        Assert.IsNull(requestUriField.GetValue(request));
    }

    [TestMethod]
    public async Task RelativeHttpsRedirectIsFollowedManually()
    {
        using StubHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/start")
            {
                HttpResponseMessage redirect = Response(HttpStatusCode.Redirect, []);
                redirect.Headers.Location = new Uri("/final", UriKind.Relative);
                return Task.FromResult(redirect);
            }

            return Task.FromResult(Response(HttpStatusCode.OK, [1, 2, 3]));
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/start", 32));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, handler.RequestUris);
        Assert.AreEqual("https://example.test/final", handler.RequestUris[1].AbsoluteUri);
        result.Response!.Dispose();
    }

    [TestMethod]
    public async Task InsecureRedirectIsRejectedWithoutSecondRequest()
    {
        using StubHandler handler = new((_, _) =>
        {
            HttpResponseMessage redirect = Response(HttpStatusCode.Redirect, []);
            redirect.Headers.Location = new Uri("http://example.test/plaintext");
            return Task.FromResult(redirect);
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/start", 32));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(HttpTransportFailure.RedirectRejected, result.Failure);
        Assert.HasCount(1, handler.RequestUris);
    }

    [TestMethod]
    public async Task ExplicitHttpSourceFollowsOnlySameOriginHttpRedirects()
    {
        using StubHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/start")
            {
                HttpResponseMessage redirect = Response(HttpStatusCode.Redirect, []);
                redirect.Headers.Location = new Uri("/final", UriKind.Relative);
                return Task.FromResult(redirect);
            }

            return Task.FromResult(Response(HttpStatusCode.OK, [1]));
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));
        using HttpTransportRequest request = CreateRemotePlaylistSourceRequest(
            "http://example.test/start");

        HttpTransportResult result = await transport.GetAsync(request);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, handler.RequestUris);
        Assert.AreEqual("http://example.test/final", handler.RequestUris[1].AbsoluteUri);
        result.Response!.Dispose();
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ProductionTransportSupportsHttpSourceWithExplicitPortAndXtreamStyleQuery()
    {
        byte[] payload = "#EXTM3U\n#EXTINF:-1,Synthetic\nhttp://127.0.0.1/live.ts\n"u8.ToArray();
        Dictionary<string, FixtureHttpResponse> routes = new(StringComparer.Ordinal)
        {
            ["/get.php"] = new FixtureHttpResponse(200, "audio/x-mpegurl", payload),
        };

        await using LocalHttpFixtureServer server = await LocalHttpFixtureServer.StartAsync(routes);
        Uri locator = new(
            server.BaseAddress,
            "get.php?username=synthetic-user&password=synthetic-password&type=m3u_plus&output=ts");
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared =
            SourceConfigurationValidator.PrepareRemotePlaylistAllowingInsecureHttp(
                "Synthetic",
                locator.AbsoluteUri);
        Assert.IsTrue(prepared.IsSuccess);

        using var transport = new BoundedHttpTransport();
        using HttpTransportRequest request = CreateRemotePlaylistSourceRequest(
            locator.AbsoluteUri);
        HttpTransportResult result = await transport.GetAsync(request);

        Assert.IsTrue(result.IsSuccess);
        using HttpResponseLease response = result.Response!;
        CollectionAssert.AreEqual(payload, response.Content.ToArray());
        Assert.HasCount(1, server.Requests);
        Assert.AreEqual("/get.php", server.Requests[0].Path);
    }

    [TestMethod]
    public async Task ExplicitHttpSourceRejectsCrossOriginHttpRedirectWithoutSecondRequest()
    {
        using StubHandler handler = new((_, _) =>
        {
            HttpResponseMessage redirect = Response(HttpStatusCode.Redirect, []);
            redirect.Headers.Location = new Uri("http://other.test/final");
            return Task.FromResult(redirect);
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));
        using HttpTransportRequest request = CreateRemotePlaylistSourceRequest(
            "http://example.test/start");

        HttpTransportResult result = await transport.GetAsync(request);

        Assert.AreEqual(HttpTransportFailure.RedirectRejected, result.Failure);
        Assert.HasCount(1, handler.RequestUris);
    }

    [TestMethod]
    public async Task StreamingHttpSourceAllowsSameOriginRedirectAndHttpsUpgradeWithoutHeaders()
    {
        var sensitiveHeadersSeen = new List<bool>();
        using StubHandler handler = new((request, _) =>
        {
            sensitiveHeadersSeen.Add(
                request.Headers.Authorization is not null ||
                request.Headers.Referrer is not null ||
                request.Headers.Contains("Cookie"));
            if (request.RequestUri!.AbsolutePath == "/start")
            {
                HttpResponseMessage redirect = Response(HttpStatusCode.Redirect, []);
                redirect.Headers.Location = new Uri("/middle", UriKind.Relative);
                return Task.FromResult(redirect);
            }

            if (request.RequestUri.Scheme == Uri.UriSchemeHttp)
            {
                HttpResponseMessage upgrade = Response(HttpStatusCode.Redirect, []);
                upgrade.Headers.Location = new Uri("https://secure.test/final");
                return Task.FromResult(upgrade);
            }

            return Task.FromResult(Response(HttpStatusCode.OK, "#EXTM3U\n"u8.ToArray()));
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));
        using HttpTransportRequest request = CreateRemotePlaylistSourceRequest(
            "http://example.test/start?token=synthetic");

        HttpStreamingResult result = await transport.GetStreamAsync(request);

        Assert.IsTrue(result.IsSuccess);
        using HttpStreamingResponseLease response = result.Response!;
        Assert.HasCount(3, handler.RequestUris);
        Assert.AreEqual("http://example.test/middle", handler.RequestUris[1].AbsoluteUri);
        Assert.AreEqual("https://secure.test/final", handler.RequestUris[2].AbsoluteUri);
        Assert.IsTrue(sensitiveHeadersSeen.All(seen => !seen));
    }

    [TestMethod]
    public async Task StreamingHttpsUpgradeCannotDowngradeBackToHttp()
    {
        using StubHandler handler = new((request, _) =>
        {
            HttpResponseMessage redirect = Response(HttpStatusCode.Redirect, []);
            redirect.Headers.Location = request.RequestUri!.Scheme == Uri.UriSchemeHttp
                ? new Uri("https://secure.test/final")
                : new Uri("http://secure.test/downgrade");
            return Task.FromResult(redirect);
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));
        using HttpTransportRequest request = CreateRemotePlaylistSourceRequest(
            "http://example.test/start");

        HttpStreamingResult result = await transport.GetStreamAsync(request);

        Assert.AreEqual(HttpTransportFailure.RedirectRejected, result.Failure);
        Assert.HasCount(2, handler.RequestUris);
    }

    [TestMethod]
    public void GeneralAndAuthorizedRequestFactoriesRejectHttp()
    {
        const string locator = "http://example.test/list.m3u?token=synthetic";
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared =
            SourceConfigurationValidator.PrepareRemotePlaylistAllowingInsecureHttp(
                "Synthetic",
                locator);
        Assert.IsTrue(prepared.IsSuccess);
        var uri = new Uri(locator);

        Assert.ThrowsExactly<ArgumentException>(() =>
            HttpTransportRequest.Create(uri, prepared.Value!.SafeEndpoint, 64));
        Assert.ThrowsExactly<ArgumentException>(() =>
        {
            using HttpTransportRequest _ = HttpTransportRequest.CreateWithAuthorization(
                uri,
                prepared.Value!.SafeEndpoint,
                64,
                "Bearer synthetic"u8);
        });
    }

    [TestMethod]
    public async Task CredentialBearingCrossOriginRedirectIsRejectedWithoutForwarding()
    {
        List<string?> authorizationValues = [];
        using StubHandler handler = new((request, _) =>
        {
            authorizationValues.Add(request.Headers.Authorization?.ToString());
            HttpResponseMessage redirect = Response(HttpStatusCode.Redirect, []);
            redirect.Headers.Location = new Uri("https://other.example/final");
            return Task.FromResult(redirect);
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));
        using HttpTransportRequest request = CreateAuthorizedRequest(
            "https://example.test/start",
            "Bearer synthetic-boundary-marker"u8);

        HttpTransportResult result = await transport.GetAsync(request);

        Assert.AreEqual(HttpTransportFailure.RedirectRejected, result.Failure);
        Assert.HasCount(1, handler.RequestUris);
        CollectionAssert.AreEqual(SingleAuthorization, authorizationValues);
    }

    [TestMethod]
    public async Task CredentialBearingSameOriginRedirectPreservesAuthorization()
    {
        List<string?> authorizationValues = [];
        using StubHandler handler = new((request, _) =>
        {
            authorizationValues.Add(request.Headers.Authorization?.ToString());
            if (request.RequestUri!.AbsolutePath == "/start")
            {
                HttpResponseMessage redirect = Response(HttpStatusCode.Redirect, []);
                redirect.Headers.Location = new Uri("/final", UriKind.Relative);
                return Task.FromResult(redirect);
            }

            return Task.FromResult(Response(HttpStatusCode.OK, [9]));
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));
        using HttpTransportRequest request = CreateAuthorizedRequest(
            "https://example.test/start",
            "Bearer synthetic-boundary-marker"u8);

        HttpTransportResult result = await transport.GetAsync(request);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(RedirectedAuthorization, authorizationValues);
        result.Response!.Dispose();
    }

    [TestMethod]
    public async Task StreamingResponseFollowsAuthorizedRedirectWithoutBufferingBody()
    {
        using StubHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/start")
            {
                HttpResponseMessage redirect = Response(HttpStatusCode.Redirect, []);
                redirect.Headers.Location = new Uri("/final/list.m3u", UriKind.Relative);
                return Task.FromResult(redirect);
            }

            HttpResponseMessage response = Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes("#EXTM3U\n"));
            response.Headers.ETag = new EntityTagHeaderValue("\"catalog-v1\"");
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 8, 21, 12, 34, 56, TimeSpan.Zero);
            return Task.FromResult(response);
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));
        using HttpTransportRequest request = CreateRequest("https://example.test/start", 64);

        HttpStreamingResult result = await transport.GetStreamAsync(request);

        Assert.IsTrue(result.IsSuccess);
        using HttpStreamingResponseLease lease = result.Response!;
        Assert.AreEqual("\"catalog-v1\"", lease.EntityTag);
        Assert.AreEqual(
            new DateTimeOffset(2026, 8, 21, 12, 34, 56, TimeSpan.Zero),
            lease.LastModified);
        using var reader = new StreamReader(lease.Content, Encoding.UTF8, leaveOpen: true);
        Assert.AreEqual("#EXTM3U", await reader.ReadLineAsync());
    }

    [TestMethod]
    public async Task StreamingChunkedBodyEnforcesLimitDuringConsumption()
    {
        using StubHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.OK, new byte[65]);
            response.Content.Headers.ContentLength = null;
            return Task.FromResult(response);
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));
        using HttpTransportRequest request = CreateRequest("https://example.test/list", 64);
        HttpStreamingResult result = await transport.GetStreamAsync(request);
        Assert.IsTrue(result.IsSuccess);

        using HttpStreamingResponseLease lease = result.Response!;
        await using var destination = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(async () =>
            await lease.Content.CopyToAsync(destination));
    }

    [TestMethod]
    public async Task ChunkedBodyBeyondLimitFailsWithoutReturningContent()
    {
        using StubHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.OK, new byte[65]);
            response.Content.Headers.ContentLength = null;
            return Task.FromResult(response);
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/list", 64));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(HttpTransportFailure.ResponseTooLarge, result.Failure);
        Assert.IsNull(result.Response);
    }

    [TestMethod]
    public async Task CallerCancellationRemainsCancellation()
    {
        using StubHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return Response(HttpStatusCode.OK, []);
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(5));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await transport.GetAsync(CreateRequest("https://example.test/list", 64), cancellation.Token));
    }

    [TestMethod]
    public async Task InternalTimeoutReturnsTypedTransientFailure()
    {
        using StubHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return Response(HttpStatusCode.OK, []);
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromMilliseconds(20));

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/list", 64));

        Assert.AreEqual(HttpTransportFailure.RequestTimedOut, result.Failure);
        Assert.AreEqual(HttpTransportRetryability.BoundedTransient, result.Retryability);
    }

    [TestMethod]
    public async Task InternalTimeoutDuringRetryDelayReturnsTypedTransientFailure()
    {
        using StubHandler handler = new((_, _) =>
            Task.FromResult(Response(HttpStatusCode.ServiceUnavailable, [])));
        using BoundedHttpTransport transport = CreateTransport(
            handler,
            TimeSpan.FromMilliseconds(20),
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/list", 64));

        Assert.AreEqual(HttpTransportFailure.RequestTimedOut, result.Failure);
        Assert.AreEqual(HttpTransportRetryability.BoundedTransient, result.Retryability);
        Assert.HasCount(1, handler.RequestUris);
    }

    [TestMethod]
    public async Task TlsValidationFailureUsesStableTypedFailureThroughInjectedHandler()
    {
        using StubHandler handler = new((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("synthetic", new AuthenticationException("synthetic"))));
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/list", 64));

        Assert.AreEqual(HttpTransportFailure.TlsValidationFailed, result.Failure);
        Assert.IsNull(result.Response);
        Assert.HasCount(1, handler.RequestUris);
    }

    [TestMethod]
    public async Task ProductionIpLiteralLoopbackFailsClosedWithoutRetry()
    {
        RecordingObserver observer = new();
        using BoundedHttpTransport transport = new(observer);

        HttpTransportResult result = await transport.GetAsync(
            CreateRequest("https://127.0.0.1:65535/list", 64));

        Assert.AreEqual(HttpTransportFailure.EndpointAddressRejected, result.Failure);
        Assert.AreEqual(HttpTransportRetryability.Never, result.Retryability);
        Assert.HasCount(1, observer.Observations);
        Assert.AreEqual(1, observer.Observations[0].AttemptCount);
    }

    [TestMethod]
    public async Task ProductionDnsResolutionToLoopbackFailsClosedWithoutRetry()
    {
        RecordingObserver observer = new();
        using BoundedHttpTransport transport = new(observer);

        HttpTransportResult result = await transport.GetAsync(
            CreateRequest("https://localhost:65535/list", 64));

        Assert.AreEqual(HttpTransportFailure.EndpointAddressRejected, result.Failure);
        Assert.AreEqual(HttpTransportRetryability.Never, result.Retryability);
        Assert.HasCount(1, observer.Observations);
        Assert.AreEqual(1, observer.Observations[0].AttemptCount);
    }

    [TestMethod]
    public void ResolvedAddressPolicyRejectsSpecialPrivateAndMixedDnsAnswers()
    {
        Assert.IsTrue(AreResolvedAddressesAllowed(
            allowExplicitPrivateSourceOrigin: false,
            "public.example",
            "93.184.216.34",
            "2606:4700:4700::1111"));
        Assert.IsFalse(AreResolvedAddressesAllowed(false, "private.example", "127.0.0.1"));
        Assert.IsTrue(AreResolvedAddressesAllowed(true, "private.example", "127.0.0.1", "::1"));
        Assert.IsTrue(AreResolvedAddressesAllowed(true, "private.example", "10.0.0.1", "192.168.1.1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(
            allowExplicitPrivateSourceOrigin: true,
            "mixed.example",
            "93.184.216.34",
            "10.0.0.1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(false, "mapped.example", "::ffff:127.0.0.1"));
        Assert.IsTrue(AreResolvedAddressesAllowed(true, "mapped.example", "::ffff:127.0.0.1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(true, "unspecified.example", "0.0.0.0"));
        Assert.IsFalse(AreResolvedAddressesAllowed(true, "multicast.example", "239.1.1.1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(false, "special.example", "192.0.0.1"));
        Assert.IsTrue(AreResolvedAddressesAllowed(false, "public.example", "192.0.1.1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(false, "documentation.example", "192.0.2.1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(false, "relay.example", "192.88.99.1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(false, "special.example", "2001::1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(true, "special.example", "2001:1ff::1"));
        Assert.IsTrue(AreResolvedAddressesAllowed(false, "public.example", "2001:200::1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(false, "sixtofour.example", "2002::1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(true, "documentation.example", "2001:db8::1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(false, "documentation.example", "3fff:fff::1"));
        Assert.IsTrue(AreResolvedAddressesAllowed(false, "public.example", "3fff:1000::1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(false, "127.0.0.1", "127.0.0.1"));
        Assert.IsTrue(AreResolvedAddressesAllowed(true, "127.0.0.1", "127.0.0.1"));
        Assert.IsFalse(AreResolvedAddressesAllowed(true, "127.0.0.1", "127.0.0.2"));
    }

    [TestMethod]
    public void ExplicitPrivatePolicyIsBoundOnlyToTheExactSourceOrigin()
    {
        DomainResult<PreparedRemotePlaylistSourceDraft> source =
            SourceConfigurationValidator.PrepareRemotePlaylist(
                "Synthetic",
                "https://private.example:8443/list");
        DomainResult<PreparedRemotePlaylistSourceDraft> otherOrigin =
            SourceConfigurationValidator.PrepareRemotePlaylist(
                "Synthetic",
                "https://redirect.example:8443/list");
        Assert.IsTrue(source.IsSuccess);
        Assert.IsTrue(otherOrigin.IsSuccess);

        using HttpTransportRequest explicitPrivateRequest = CreateExplicitPrivateSourceRequest(
            new Uri("https://private.example:8443/list"),
            source.Value!.SafeEndpoint,
            64);
        Assert.AreEqual(
            "ExplicitPrivateSourceOrigin",
            BindAddressPolicy(explicitPrivateRequest, source.Value.SafeEndpoint));
        Assert.AreEqual(
            "PublicOnly",
            BindAddressPolicy(explicitPrivateRequest, otherOrigin.Value!.SafeEndpoint));

        using HttpTransportRequest publicOnlyRequest = HttpTransportRequest.Create(
            new Uri("https://private.example:8443/list"),
            source.Value.SafeEndpoint,
            64);
        Assert.AreEqual(
            "PublicOnly",
            BindAddressPolicy(publicOnlyRequest, source.Value.SafeEndpoint));

        Assert.IsTrue(IsBoundAuthorityAllowed(
            explicitPrivateRequest,
            source.Value.SafeEndpoint,
            "private.example",
            8443));
        Assert.IsFalse(IsBoundAuthorityAllowed(
            explicitPrivateRequest,
            source.Value.SafeEndpoint,
            "redirect.example",
            8443));
        Assert.IsFalse(IsBoundAuthorityAllowed(
            explicitPrivateRequest,
            source.Value.SafeEndpoint,
            "private.example",
            443));

        DomainResult<PreparedRemotePlaylistSourceDraft> internationalSource =
            SourceConfigurationValidator.PrepareRemotePlaylist(
                "Synthetic",
                "https://bücher.example:8443/list");
        Assert.IsTrue(internationalSource.IsSuccess);
        using HttpTransportRequest internationalRequest = CreateExplicitPrivateSourceRequest(
            new Uri("https://bücher.example:8443/list"),
            internationalSource.Value!.SafeEndpoint,
            64);
        Assert.IsTrue(IsBoundAuthorityAllowed(
            internationalRequest,
            internationalSource.Value.SafeEndpoint,
            "bücher.example",
            8443));
        Assert.IsTrue(IsBoundAuthorityAllowed(
            internationalRequest,
            internationalSource.Value.SafeEndpoint,
            "xn--bcher-kva.example",
            8443));
        Assert.IsFalse(IsBoundAuthorityAllowed(
            internationalRequest,
            internationalSource.Value.SafeEndpoint,
            "\ud800.example",
            8443));
    }

    [TestMethod]
    public void ProductionConnectionPoolsDisableProxyAndIsolatePrivateReuse()
    {
        MethodInfo factory = typeof(BoundedHttpTransport).GetMethod(
            "CreateProductionHandler",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The production HTTP handler factory is unavailable.");
        using var publicHandler = (SocketsHttpHandler)factory.Invoke(
            null,
            [TimeSpan.FromMinutes(10)])!;
        using var explicitPrivateHandler = (SocketsHttpHandler)factory.Invoke(
            null,
            [TimeSpan.Zero])!;

        Assert.IsFalse(publicHandler.UseProxy);
        Assert.IsFalse(explicitPrivateHandler.UseProxy);
        Assert.AreEqual(TimeSpan.FromMinutes(10), publicHandler.PooledConnectionLifetime);
        Assert.AreEqual(TimeSpan.Zero, explicitPrivateHandler.PooledConnectionLifetime);
    }

    [TestMethod]
    public async Task CrossOriginRedirectToPrivateLiteralIsRejectedBeforeSecondRequest()
    {
        using StubHandler handler = new((_, _) =>
        {
            HttpResponseMessage redirect = Response(HttpStatusCode.Redirect, []);
            redirect.Headers.Location = new Uri("https://127.0.0.1/private");
            return Task.FromResult(redirect);
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/start", 64));

        Assert.AreEqual(HttpTransportFailure.RedirectRejected, result.Failure);
        Assert.AreEqual(HttpTransportRetryability.Never, result.Retryability);
        Assert.HasCount(1, handler.RequestUris);
    }

    [TestMethod]
    public async Task BufferedResponseClassifiesOnlyExactImageMediaTypes()
    {
        static async Task<HttpResponseMediaType> ClassifyAsync(Action<HttpContentHeaders>? configure)
        {
            using StubHandler handler = new((_, _) =>
            {
                HttpResponseMessage response = Response(HttpStatusCode.OK, [1]);
                configure?.Invoke(response.Content.Headers);
                return Task.FromResult(response);
            });
            using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));
            HttpTransportResult result = await transport.GetAsync(
                CreateRequest("https://example.test/logo", 64));
            Assert.IsTrue(result.IsSuccess);
            using HttpResponseLease response = result.Response!;
            return response.MediaType;
        }

        Assert.AreEqual(HttpResponseMediaType.Png, await ClassifyAsync(headers =>
            headers.ContentType = new MediaTypeHeaderValue("image/png")));
        Assert.AreEqual(HttpResponseMediaType.Other, await ClassifyAsync(configure: null));
        Assert.AreEqual(HttpResponseMediaType.Other, await ClassifyAsync(headers =>
            headers.TryAddWithoutValidation("Content-Type", "not-a-media-type")));
        Assert.AreEqual(HttpResponseMediaType.Other, await ClassifyAsync(headers =>
            headers.TryAddWithoutValidation("Content-Type", ["image/png", "image/jpeg"])));
    }

    [TestMethod]
    public async Task RedirectCountIsBounded()
    {
        using StubHandler handler = new((_, _) =>
        {
            HttpResponseMessage redirect = Response(HttpStatusCode.Redirect, []);
            redirect.Headers.Location = new Uri("/again", UriKind.Relative);
            return Task.FromResult(redirect);
        });
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/start", 64));

        Assert.AreEqual(HttpTransportFailure.RedirectLimitExceeded, result.Failure);
        Assert.HasCount(HttpTransportLimits.MaximumRedirects + 1, handler.RequestUris);
    }

    [TestMethod]
    public async Task TransientStatusRetriesWithinBoundAndHonorsRetryAfterCap()
    {
        int responseCount = 0;
        List<TimeSpan> delays = [];
        using StubHandler handler = new((_, _) =>
        {
            responseCount++;
            if (responseCount < 3)
            {
                HttpResponseMessage transient = Response(HttpStatusCode.TooManyRequests, []);
                transient.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
                return Task.FromResult(transient);
            }

            return Task.FromResult(Response(HttpStatusCode.OK, [7]));
        });
        using BoundedHttpTransport transport = CreateTransport(
            handler,
            TimeSpan.FromSeconds(1),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/list", 64));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(3, handler.RequestUris);
        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2) },
            delays);
        result.Response!.Dispose();
    }

    [TestMethod]
    public async Task SafeObservationContainsOnlyBoundedOperationFacts()
    {
        int responseCount = 0;
        RecordingObserver observer = new();
        using StubHandler handler = new((_, _) =>
        {
            responseCount++;
            return Task.FromResult(responseCount == 1
                ? Response(HttpStatusCode.ServiceUnavailable, [])
                : Response(HttpStatusCode.OK, new byte[] { 8, 9 }));
        });
        using BoundedHttpTransport transport = CreateTransport(
            handler,
            TimeSpan.FromSeconds(1),
            (_, _) => Task.CompletedTask,
            observer);

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/list", 64));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, observer.Observations);
        HttpTransportObservation observation = observer.Observations[0];
        Assert.AreEqual(2, observation.AttemptCount);
        Assert.AreEqual(0, observation.RedirectCount);
        Assert.AreEqual(200, observation.StatusCode);
        Assert.AreEqual(2, observation.ResponseBytes);
        Assert.IsNull(observation.Failure);
        Assert.IsGreaterThanOrEqualTo(0, observation.ElapsedMilliseconds);
        result.Response!.Dispose();
    }

    [TestMethod]
    public async Task NetworkFailureUsesOnlyThreeAttemptsBeforeManualFailure()
    {
        List<TimeSpan> delays = [];
        using StubHandler handler = new((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic")));
        using BoundedHttpTransport transport = CreateTransport(
            handler,
            TimeSpan.FromSeconds(1),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/list", 64));

        Assert.AreEqual(HttpTransportFailure.NetworkUnavailable, result.Failure);
        Assert.AreEqual(HttpTransportRetryability.Manual, result.Retryability);
        Assert.HasCount(3, handler.RequestUris);
        Assert.HasCount(2, delays);
        Assert.IsTrue(delays.All(delay => delay >= TimeSpan.FromMilliseconds(100)));
        Assert.IsTrue(delays.All(delay => delay <= TimeSpan.FromMilliseconds(400)));
    }

    [TestMethod]
    [DataRow(401, HttpTransportFailure.AuthenticationRejected, HttpTransportRetryability.Never)]
    [DataRow(403, HttpTransportFailure.AuthenticationRejected, HttpTransportRetryability.Never)]
    [DataRow(404, HttpTransportFailure.ResourceNotFound, HttpTransportRetryability.Never)]
    [DataRow(400, HttpTransportFailure.RequestRejected, HttpTransportRetryability.Never)]
    [DataRow(408, HttpTransportFailure.RequestTimedOut, HttpTransportRetryability.BoundedTransient)]
    [DataRow(429, HttpTransportFailure.RateLimited, HttpTransportRetryability.BoundedTransient)]
    [DataRow(500, HttpTransportFailure.RemoteServiceUnavailable, HttpTransportRetryability.BoundedTransient)]
    [DataRow(503, HttpTransportFailure.RemoteServiceUnavailable, HttpTransportRetryability.BoundedTransient)]
    [DataRow(599, HttpTransportFailure.RemoteServiceUnavailable, HttpTransportRetryability.BoundedTransient)]
    [DataRow(600, HttpTransportFailure.RequestRejected, HttpTransportRetryability.Never)]
    public async Task StatusCodesMapToStableTypedFailures(
        int statusCode,
        HttpTransportFailure expectedFailure,
        HttpTransportRetryability expectedRetryability)
    {
        using StubHandler handler = new((_, _) => Task.FromResult(Response((HttpStatusCode)statusCode, [])));
        using BoundedHttpTransport transport = CreateTransport(
            handler,
            TimeSpan.FromSeconds(1),
            static (_, _) => Task.CompletedTask);

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/list", 64));

        Assert.AreEqual(expectedFailure, result.Failure);
        Assert.AreEqual(expectedRetryability, result.Retryability);
        Assert.AreEqual(statusCode, result.StatusCode);
        Assert.HasCount(expectedRetryability == HttpTransportRetryability.BoundedTransient ? 3 : 1, handler.RequestUris);
    }

    private static HttpTransportRequest CreateRequest(string locator, int maximumBytes)
    {
        Uri uri = new(locator);
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared =
            SourceConfigurationValidator.PrepareRemotePlaylist("Synthetic", locator);
        Assert.IsTrue(prepared.IsSuccess);
        return HttpTransportRequest.Create(uri, prepared.Value!.SafeEndpoint, maximumBytes);
    }

    private static bool AreResolvedAddressesAllowed(
        bool allowExplicitPrivateSourceOrigin,
        string host,
        params string[] addressTexts)
    {
        Type policy = typeof(BoundedHttpTransport).Assembly.GetType(
            "IptvSuite.Infrastructure.EndpointAddressPolicy",
            throwOnError: true)!;
        MethodInfo method = policy.GetMethod(
            "AreResolvedAddressesAllowed",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The endpoint address policy test seam is unavailable.");
        IPAddress[] addresses = addressTexts.Select(IPAddress.Parse).ToArray();
        Type addressPolicyType = typeof(HttpTransportRequest).Assembly.GetType(
            "IptvSuite.Application.HttpEndpointAddressPolicy",
            throwOnError: true)!;
        object addressPolicy = Enum.Parse(
            addressPolicyType,
            allowExplicitPrivateSourceOrigin
                ? "ExplicitPrivateSourceOrigin"
                : "PublicOnly");
        return (bool)method.Invoke(null, [host, addresses, addressPolicy])!;
    }

    private static HttpTransportRequest CreateExplicitPrivateSourceRequest(
        Uri requestUri,
        SafeEndpoint expectedEndpoint,
        int maximumBytes)
    {
        MethodInfo factory = typeof(HttpTransportRequest).GetMethod(
            "CreateForExplicitPrivateSourceOrigin",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The explicit private source request factory is unavailable.");
        return (HttpTransportRequest)factory.Invoke(
            null,
            [requestUri, expectedEndpoint, maximumBytes])!;
    }

    private static HttpTransportRequest CreateRemotePlaylistSourceRequest(string locator)
    {
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared =
            SourceConfigurationValidator.PrepareRemotePlaylistAllowingInsecureHttp(
                "Synthetic",
                locator);
        Assert.IsTrue(prepared.IsSuccess);
        MethodInfo factory = typeof(HttpTransportRequest).GetMethod(
            "CreateForExplicitRemotePlaylistSourceOrigin",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The explicit remote-playlist request factory is unavailable.");
        return (HttpTransportRequest)factory.Invoke(
            null,
            [new Uri(locator), prepared.Value!.SafeEndpoint])!;
    }

    private static HttpTransportRequest CreateXtreamSourceRequest(
        string locator,
        int maximumBytes)
    {
        DomainResult<PreparedXtreamSourceDraft> prepared =
            SourceConfigurationValidator.PrepareXtreamAllowingInsecureHttp(
                "Synthetic",
                locator,
                "synthetic-user",
                "synthetic-password");
        Assert.IsTrue(prepared.IsSuccess);
        MethodInfo factory = typeof(HttpTransportRequest).GetMethod(
            "CreateForExplicitXtreamSourceOrigin",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The explicit Xtream request factory is unavailable.");
        try
        {
            return (HttpTransportRequest)factory.Invoke(
                null,
                [new Uri(locator), prepared.Value!.SafeEndpoint, maximumBytes, true])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static TimeSpan? ReadRequestTimeoutOverride(HttpTransportRequest request)
    {
        PropertyInfo property = typeof(HttpTransportRequest).GetProperty(
            "RequestTimeoutOverride",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The request timeout override is unavailable.");
        return (TimeSpan?)property.GetValue(request);
    }

    private static string BindAddressPolicy(
        HttpTransportRequest request,
        SafeEndpoint currentEndpoint)
    {
        PropertyInfo requestPolicy = typeof(HttpTransportRequest).GetProperty(
            "EndpointAddressPolicy",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The endpoint address policy binding is unavailable.");
        PropertyInfo expectedEndpoint = typeof(HttpTransportRequest).GetProperty(
            "ExpectedEndpoint",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The expected endpoint binding is unavailable.");
        Type policy = typeof(BoundedHttpTransport).Assembly.GetType(
            "IptvSuite.Infrastructure.EndpointAddressPolicy",
            throwOnError: true)!;
        MethodInfo bind = policy.GetMethod(
            "BindRequest",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The endpoint address policy binder is unavailable.");
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://synthetic.invalid/");
        object effectivePolicy = bind.Invoke(
            null,
            [message, requestPolicy.GetValue(request), expectedEndpoint.GetValue(request), currentEndpoint])!;
        return effectivePolicy.ToString()!;
    }

    private static bool IsBoundAuthorityAllowed(
        HttpTransportRequest request,
        SafeEndpoint currentEndpoint,
        string candidateHost,
        int candidatePort)
    {
        PropertyInfo requestPolicy = typeof(HttpTransportRequest).GetProperty(
            "EndpointAddressPolicy",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The endpoint address policy binding is unavailable.");
        PropertyInfo expectedEndpoint = typeof(HttpTransportRequest).GetProperty(
            "ExpectedEndpoint",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The expected endpoint binding is unavailable.");
        Type policy = typeof(BoundedHttpTransport).Assembly.GetType(
            "IptvSuite.Infrastructure.EndpointAddressPolicy",
            throwOnError: true)!;
        MethodInfo bind = policy.GetMethod(
            "BindRequest",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The endpoint address policy binder is unavailable.");
        MethodInfo verify = policy.GetMethod(
            "IsBoundAuthorityAllowed",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The endpoint authority verifier is unavailable.");
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://synthetic.invalid/");
        _ = bind.Invoke(
            null,
            [message, requestPolicy.GetValue(request), expectedEndpoint.GetValue(request), currentEndpoint]);
        return (bool)verify.Invoke(null, [message, candidateHost, candidatePort])!;
    }

    private static HttpTransportRequest CreateAuthorizedRequest(
        string locator,
        ReadOnlySpan<byte> authorizationValue)
    {
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared =
            SourceConfigurationValidator.PrepareRemotePlaylist("Synthetic", locator);
        Assert.IsTrue(prepared.IsSuccess);
        return HttpTransportRequest.CreateWithAuthorization(
            new Uri(locator),
            prepared.Value!.SafeEndpoint,
            64,
            authorizationValue);
    }

    private static BoundedHttpTransport CreateTransport(HttpMessageHandler handler, TimeSpan timeout)
    {
        ConstructorInfo constructor = typeof(BoundedHttpTransport).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(HttpMessageHandler), typeof(TimeSpan)],
            modifiers: null) ?? throw new InvalidOperationException("The test seam constructor is unavailable.");
        return (BoundedHttpTransport)constructor.Invoke([handler, timeout]);
    }

    private static BoundedHttpTransport CreateTransport(
        HttpMessageHandler handler,
        TimeSpan timeout,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ConstructorInfo constructor = typeof(BoundedHttpTransport).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(HttpMessageHandler), typeof(TimeSpan), typeof(Func<TimeSpan, CancellationToken, Task>)],
            modifiers: null) ?? throw new InvalidOperationException("The retry test seam constructor is unavailable.");
        return (BoundedHttpTransport)constructor.Invoke([handler, timeout, delayAsync]);
    }

    private static BoundedHttpTransport CreateTransport(
        HttpMessageHandler handler,
        TimeSpan timeout,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        IHttpTransportObserver observer)
    {
        ConstructorInfo constructor = typeof(BoundedHttpTransport).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(HttpMessageHandler),
                typeof(TimeSpan),
                typeof(Func<TimeSpan, CancellationToken, Task>),
                typeof(IHttpTransportObserver),
            ],
            modifiers: null) ?? throw new InvalidOperationException("The observation test seam is unavailable.");
        return (BoundedHttpTransport)constructor.Invoke([handler, timeout, delayAsync, observer]);
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, byte[] body) => new(statusCode)
    {
        Content = new ByteArrayContent(body),
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send = send;

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(new Uri(request.RequestUri!.AbsoluteUri));
            return _send(request, cancellationToken);
        }
    }

    private sealed class RecordingObserver : IHttpTransportObserver
    {
        public List<HttpTransportObservation> Observations { get; } = [];

        public void Observe(HttpTransportObservation observation) => Observations.Add(observation);
    }
}
