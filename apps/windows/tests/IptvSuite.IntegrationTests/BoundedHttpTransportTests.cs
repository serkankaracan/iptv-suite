using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class BoundedHttpTransportTests
{
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

    private static BoundedHttpTransport CreateTransport(HttpMessageHandler handler, TimeSpan timeout)
    {
        ConstructorInfo constructor = typeof(BoundedHttpTransport).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(HttpMessageHandler), typeof(TimeSpan)],
            modifiers: null) ?? throw new InvalidOperationException("The test seam constructor is unavailable.");
        return (BoundedHttpTransport)constructor.Invoke([handler, timeout]);
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
}
