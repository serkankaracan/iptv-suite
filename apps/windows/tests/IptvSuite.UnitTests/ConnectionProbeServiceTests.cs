using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class ConnectionProbeServiceTests
{
    [TestMethod]
    public async Task SuccessfulProbeConsumesAndDisposesBoundedResponse()
    {
        HttpResponseLease lease = CreateLease([1, 2, 3]);
        StubTransport transport = new(HttpTransportResult.Success(200, lease));
        ConnectionProbeService service = new(transport);

        DomainResult<ConnectionProbeResult> result = await service.ProbeAsync(CreateRequest());

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(200, result.Value!.StatusCode);
        Assert.AreEqual(3, result.Value.ObservedContentBytes);
        Assert.AreEqual(0, lease.Content.Length);
    }

    [TestMethod]
    public async Task TransportFailuresMapToStableDomainErrors()
    {
        (HttpTransportFailure Failure, HttpTransportRetryability Retryability, DomainErrorCode Error)[] cases =
        [
            (HttpTransportFailure.InvalidRequest, HttpTransportRetryability.Never,
                DomainErrorCode.RemoteRequestRejected),
            (HttpTransportFailure.RedirectRejected, HttpTransportRetryability.Never,
                DomainErrorCode.RemoteRequestRejected),
            (HttpTransportFailure.RedirectLimitExceeded, HttpTransportRetryability.Never,
                DomainErrorCode.RemoteRequestRejected),
            (HttpTransportFailure.AuthenticationRejected, HttpTransportRetryability.Never,
                DomainErrorCode.AuthenticationRejected),
            (HttpTransportFailure.ResourceNotFound, HttpTransportRetryability.Never,
                DomainErrorCode.RemoteResourceNotFound),
            (HttpTransportFailure.RequestRejected, HttpTransportRetryability.Never,
                DomainErrorCode.RemoteRequestRejected),
            (HttpTransportFailure.ResponseTooLarge, HttpTransportRetryability.Never,
                DomainErrorCode.RemoteResponseTooLarge),
            (HttpTransportFailure.RequestTimedOut, HttpTransportRetryability.BoundedTransient,
                DomainErrorCode.RequestTimedOut),
            (HttpTransportFailure.NetworkUnavailable, HttpTransportRetryability.Manual,
                DomainErrorCode.NetworkUnreachable),
            (HttpTransportFailure.TlsValidationFailed, HttpTransportRetryability.Never,
                DomainErrorCode.TlsValidationFailed),
            (HttpTransportFailure.RateLimited, HttpTransportRetryability.BoundedTransient,
                DomainErrorCode.RequestRateLimited),
            (HttpTransportFailure.RemoteServiceUnavailable, HttpTransportRetryability.BoundedTransient,
                DomainErrorCode.RemoteServiceUnavailable),
            (HttpTransportFailure.EndpointAddressRejected, HttpTransportRetryability.Never,
                DomainErrorCode.RemoteRequestRejected),
        ];

        HttpTransportFailure[] failures = Enum.GetValues<HttpTransportFailure>();
        CollectionAssert.AreEqual(
            failures,
            cases.Select(item => item.Failure).ToArray(),
            "HTTP transport failures are a public append-only contract and must remain exhaustively mapped.");
        for (int ordinal = 0; ordinal < failures.Length; ordinal++)
        {
            Assert.AreEqual(
                ordinal,
                (int)failures[ordinal],
                $"HTTP transport failure ordinal changed for {failures[ordinal]}.");
        }

        foreach ((HttpTransportFailure failure, HttpTransportRetryability retryability, DomainErrorCode error) in cases)
        {
            ConnectionProbeService service = new(new StubTransport(
                HttpTransportResult.Failed(failure, retryability)));

            DomainResult<ConnectionProbeResult> result = await service.ProbeAsync(CreateRequest());

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(error, result.Error!.Code);
        }
    }

    [TestMethod]
    public async Task UnknownTransportFailureFailsClosed()
    {
        ConnectionProbeService service = new(new StubTransport(HttpTransportResult.Failed(
            (HttpTransportFailure)int.MaxValue,
            HttpTransportRetryability.Never)));

        DomainResult<ConnectionProbeResult> result = await service.ProbeAsync(CreateRequest());

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, result.Error!.Code);
        Assert.AreEqual(DomainRetryability.Never, result.Error.Retryability);
    }

    [TestMethod]
    public async Task CallerCancellationIsNotMappedToAResult()
    {
        StubTransport transport = new(HttpTransportResult.Failed(
            HttpTransportFailure.RequestRejected,
            HttpTransportRetryability.Never),
            throwCancellation: true);
        ConnectionProbeService service = new(transport);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await service.ProbeAsync(CreateRequest(), cancellation.Token));
    }

    private static HttpTransportRequest CreateRequest()
    {
        const string locator = "https://example.test/probe";
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared =
            SourceConfigurationValidator.PrepareRemotePlaylist("Synthetic", locator);
        Assert.IsTrue(prepared.IsSuccess);
        return HttpTransportRequest.Create(new Uri(locator), prepared.Value!.SafeEndpoint, 1024);
    }

    private static HttpResponseLease CreateLease(byte[] content)
    {
        var constructor = typeof(HttpResponseLease).GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        return (HttpResponseLease)constructor.Invoke([content]);
    }

    private sealed class StubTransport(
        HttpTransportResult result,
        bool throwCancellation = false) : IHttpTransport
    {
        public ValueTask<HttpTransportResult> GetAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (throwCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return ValueTask.FromResult(result);
        }
    }
}
