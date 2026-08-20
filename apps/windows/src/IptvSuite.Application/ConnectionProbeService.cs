using System.Diagnostics;

namespace IptvSuite.Application;

[DebuggerDisplay("[CONNECTION-PROBE-RESULT]")]
public sealed class ConnectionProbeResult
{
    internal ConnectionProbeResult(int statusCode, int observedContentBytes)
    {
        StatusCode = statusCode;
        ObservedContentBytes = observedContentBytes;
    }

    public int StatusCode { get; }

    public int ObservedContentBytes { get; }

    public override string ToString() => "[CONNECTION-PROBE-RESULT]";
}

public sealed class ConnectionProbeService(IHttpTransport transport)
{
    private readonly IHttpTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async ValueTask<DomainResult<ConnectionProbeResult>> ProbeAsync(
        HttpTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        HttpTransportResult result = await _transport.GetAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return DomainResult.Failure<ConnectionProbeResult>(MapFailure(result.Failure));
        }

        using HttpResponseLease response = result.Response!;
        return DomainResult.Success(new ConnectionProbeResult(result.StatusCode, response.Content.Length));
    }

    private static DomainErrorCode MapFailure(HttpTransportFailure? failure) => failure switch
    {
        HttpTransportFailure.AuthenticationRejected => DomainErrorCode.AuthenticationRejected,
        HttpTransportFailure.RequestTimedOut => DomainErrorCode.RequestTimedOut,
        HttpTransportFailure.NetworkUnavailable => DomainErrorCode.NetworkUnreachable,
        HttpTransportFailure.TlsValidationFailed => DomainErrorCode.TlsValidationFailed,
        HttpTransportFailure.InvalidRequest or
        HttpTransportFailure.RedirectRejected or
        HttpTransportFailure.RedirectLimitExceeded or
        HttpTransportFailure.ResourceNotFound or
        HttpTransportFailure.RequestRejected or
        HttpTransportFailure.ResponseTooLarge => DomainErrorCode.PlaylistDownloadFailed,
        _ => DomainErrorCode.PlaylistDownloadFailed,
    };
}
