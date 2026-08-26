namespace IptvSuite.Application;

internal static class HttpTransportDomainErrorMapper
{
    internal static DomainErrorCode Map(HttpTransportFailure? failure) => failure switch
    {
        HttpTransportFailure.AuthenticationRejected => DomainErrorCode.AuthenticationRejected,
        HttpTransportFailure.ResourceNotFound => DomainErrorCode.RemoteResourceNotFound,
        HttpTransportFailure.InvalidRequest or
        HttpTransportFailure.RedirectRejected or
        HttpTransportFailure.RedirectLimitExceeded or
        HttpTransportFailure.EndpointAddressRejected or
        HttpTransportFailure.RequestRejected => DomainErrorCode.RemoteRequestRejected,
        HttpTransportFailure.ResponseTooLarge => DomainErrorCode.RemoteResponseTooLarge,
        HttpTransportFailure.RequestTimedOut => DomainErrorCode.RequestTimedOut,
        HttpTransportFailure.RateLimited => DomainErrorCode.RequestRateLimited,
        HttpTransportFailure.RemoteServiceUnavailable => DomainErrorCode.RemoteServiceUnavailable,
        HttpTransportFailure.NetworkUnavailable => DomainErrorCode.NetworkUnreachable,
        HttpTransportFailure.TlsValidationFailed => DomainErrorCode.TlsValidationFailed,
        _ => DomainErrorCode.DomainInvariantViolation,
    };
}
