using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
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
    public async Task ProductionTlsValidationRejectsSyntheticUntrustedCertificate()
    {
        await using LocalHttpFixtureServer fixture = await LocalHttpFixtureServer.StartHttpsAsync(
            new Dictionary<string, FixtureHttpResponse>
            {
                ["/list"] = new(200, "application/octet-stream", new byte[] { 1 }),
            });
        using BoundedHttpTransport transport = new();

        HttpTransportResult result = await transport.GetAsync(
            CreateRequest(new Uri(fixture.BaseAddress, "/list").AbsoluteUri, 64));

        Assert.AreEqual(HttpTransportFailure.TlsValidationFailed, result.Failure);
        Assert.IsNull(result.Response);
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
    [DataRow(408, HttpTransportFailure.RequestRejected, HttpTransportRetryability.BoundedTransient)]
    [DataRow(429, HttpTransportFailure.RequestRejected, HttpTransportRetryability.BoundedTransient)]
    [DataRow(500, HttpTransportFailure.RequestRejected, HttpTransportRetryability.BoundedTransient)]
    public async Task StatusCodesMapToStableTypedFailures(
        int statusCode,
        HttpTransportFailure expectedFailure,
        HttpTransportRetryability expectedRetryability)
    {
        using StubHandler handler = new((_, _) => Task.FromResult(Response((HttpStatusCode)statusCode, [])));
        using BoundedHttpTransport transport = CreateTransport(handler, TimeSpan.FromSeconds(1));

        HttpTransportResult result = await transport.GetAsync(CreateRequest("https://example.test/list", 64));

        Assert.AreEqual(expectedFailure, result.Failure);
        Assert.AreEqual(expectedRetryability, result.Retryability);
        Assert.AreEqual(statusCode, result.StatusCode);
    }

    private static HttpTransportRequest CreateRequest(string locator, int maximumBytes)
    {
        Uri uri = new(locator);
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared =
            SourceConfigurationValidator.PrepareRemotePlaylist("Synthetic", locator);
        Assert.IsTrue(prepared.IsSuccess);
        return HttpTransportRequest.Create(uri, prepared.Value!.SafeEndpoint, maximumBytes);
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
