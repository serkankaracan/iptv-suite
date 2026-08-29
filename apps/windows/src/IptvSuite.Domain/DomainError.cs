using System.Text.Json.Serialization;

namespace IptvSuite.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<DomainErrorCode>))]
public enum DomainErrorCode
{
    SourceNameRequired,
    SourceNameInvalid,
    SourceNameTooLong,
    EndpointRequired,
    EndpointMalformed,
    EndpointTooLong,
    EndpointSchemeUnsupported,
    InsecureTransportRejected,
    EndpointUserInfoNotAllowed,
    EndpointFragmentNotAllowed,
    UsernameRequired,
    PasswordRequired,
    CredentialInvalid,
    CredentialTooLong,
    SecretReferenceInvalid,
    DomainInvariantViolation,
    NetworkUnreachable,
    AuthenticationRejected,
    PlaylistDownloadFailed,
    UnsupportedPlaylistFormat,
    RequestTimedOut,
    TlsValidationFailed,
    PlaybackStartFailed,
    StreamInterrupted,
    ReconnectExhausted,
    StorageUnavailable,
    OperationCancelled,
    PlaybackControlFailed,
    RemoteResourceNotFound,
    RemoteRequestRejected,
    RequestRateLimited,
    RemoteServiceUnavailable,
    RemoteResponseTooLarge,
    PlaylistResponseAddressRejected,
    PlaylistHeaderInvalid,
    PlaylistTextEncodingInvalid,
    PlaylistSafeLimitExceeded,
    PlaylistStructureInvalid,
    PlaylistNoUsableEntries,
    PlaylistEntriesRejectedByAddressPolicy,
    PlaylistHlsManifestUnsupported,
    PlaylistLineLimitExceeded,
    PlaylistTotalLimitExceeded,
    PlaylistEntryLimitExceeded,
    PlaybackNetworkFailed,
    PlaybackSourceUnsupported,
    PlaybackDecodingFailed,
}

[JsonConverter(typeof(JsonStringEnumConverter<DomainRetryability>))]
public enum DomainRetryability
{
    Never,
    BoundedTransient,
    Manual,
}

public sealed record DomainError
{
    private DomainError(
        DomainErrorCode code,
        DomainRetryability retryability,
        string resourceKey)
    {
        Code = code;
        Retryability = retryability;
        ResourceKey = resourceKey;
    }

    public DomainErrorCode Code { get; }

    public DomainRetryability Retryability { get; }

    public string ResourceKey { get; }

    public static DomainError Create(DomainErrorCode code) => code switch
    {
        DomainErrorCode.SourceNameRequired => Never(code, "Errors.Source.NameRequired"),
        DomainErrorCode.SourceNameInvalid => Never(code, "Errors.Source.NameInvalid"),
        DomainErrorCode.SourceNameTooLong => Never(code, "Errors.Source.NameTooLong"),
        DomainErrorCode.EndpointRequired => Never(code, "Errors.Endpoint.Required"),
        DomainErrorCode.EndpointMalformed => Never(code, "Errors.Endpoint.Malformed"),
        DomainErrorCode.EndpointTooLong => Never(code, "Errors.Endpoint.TooLong"),
        DomainErrorCode.EndpointSchemeUnsupported => Never(code, "Errors.Endpoint.SchemeUnsupported"),
        DomainErrorCode.InsecureTransportRejected => Never(code, "Errors.Endpoint.InsecureTransportRejected"),
        DomainErrorCode.EndpointUserInfoNotAllowed => Never(code, "Errors.Endpoint.UserInfoNotAllowed"),
        DomainErrorCode.EndpointFragmentNotAllowed => Never(code, "Errors.Endpoint.FragmentNotAllowed"),
        DomainErrorCode.UsernameRequired => Never(code, "Errors.Credentials.UsernameRequired"),
        DomainErrorCode.PasswordRequired => Never(code, "Errors.Credentials.PasswordRequired"),
        DomainErrorCode.CredentialInvalid => Never(code, "Errors.Credentials.Invalid"),
        DomainErrorCode.CredentialTooLong => Never(code, "Errors.Credentials.TooLong"),
        DomainErrorCode.SecretReferenceInvalid => Never(code, "Errors.SecretReference.Invalid"),
        DomainErrorCode.DomainInvariantViolation => Never(code, "Errors.Domain.InvariantViolation"),
        DomainErrorCode.NetworkUnreachable => Manual(code, "Errors.Network.Unreachable"),
        DomainErrorCode.AuthenticationRejected => Never(code, "Errors.Authentication.Rejected"),
        DomainErrorCode.PlaylistDownloadFailed => Transient(code, "Errors.Playlist.DownloadFailed"),
        DomainErrorCode.UnsupportedPlaylistFormat => Never(code, "Errors.Playlist.UnsupportedFormat"),
        DomainErrorCode.RequestTimedOut => Transient(code, "Errors.Network.RequestTimedOut"),
        DomainErrorCode.TlsValidationFailed => Never(code, "Errors.Network.TlsValidationFailed"),
        DomainErrorCode.PlaybackStartFailed => Manual(code, "Errors.Playback.StartFailed"),
        DomainErrorCode.PlaybackControlFailed => Manual(code, "Errors.Playback.ControlFailed"),
        DomainErrorCode.StreamInterrupted => Transient(code, "Errors.Playback.StreamInterrupted"),
        DomainErrorCode.ReconnectExhausted => Manual(code, "Errors.Playback.ReconnectExhausted"),
        DomainErrorCode.StorageUnavailable => Manual(code, "Errors.Storage.Unavailable"),
        DomainErrorCode.OperationCancelled => Never(code, "Errors.Operation.Cancelled"),
        DomainErrorCode.RemoteResourceNotFound => Never(code, "Errors.Network.ResourceNotFound"),
        DomainErrorCode.RemoteRequestRejected => Never(code, "Errors.Network.RequestRejected"),
        DomainErrorCode.RequestRateLimited => Transient(code, "Errors.Network.RateLimited"),
        DomainErrorCode.RemoteServiceUnavailable => Transient(code, "Errors.Network.ServiceUnavailable"),
        DomainErrorCode.RemoteResponseTooLarge => Never(code, "Errors.Network.ResponseTooLarge"),
        DomainErrorCode.PlaylistResponseAddressRejected =>
            Never(code, "Errors.Playlist.ResponseAddressRejected"),
        DomainErrorCode.PlaylistHeaderInvalid => Never(code, "Errors.Playlist.HeaderInvalid"),
        DomainErrorCode.PlaylistTextEncodingInvalid =>
            Never(code, "Errors.Playlist.TextEncodingInvalid"),
        DomainErrorCode.PlaylistSafeLimitExceeded =>
            Never(code, "Errors.Playlist.SafeLimitExceeded"),
        DomainErrorCode.PlaylistStructureInvalid =>
            Never(code, "Errors.Playlist.StructureInvalid"),
        DomainErrorCode.PlaylistNoUsableEntries =>
            Never(code, "Errors.Playlist.NoUsableEntries"),
        DomainErrorCode.PlaylistEntriesRejectedByAddressPolicy =>
            Never(code, "Errors.Playlist.EntriesRejectedByAddressPolicy"),
        DomainErrorCode.PlaylistHlsManifestUnsupported =>
            Never(code, "Errors.Playlist.HlsManifestUnsupported"),
        DomainErrorCode.PlaylistLineLimitExceeded =>
            Never(code, "Errors.Playlist.LineLimitExceeded"),
        DomainErrorCode.PlaylistTotalLimitExceeded =>
            Never(code, "Errors.Playlist.TotalLimitExceeded"),
        DomainErrorCode.PlaylistEntryLimitExceeded =>
            Never(code, "Errors.Playlist.EntryLimitExceeded"),
        DomainErrorCode.PlaybackNetworkFailed =>
            Transient(code, "Errors.Playback.NetworkFailed"),
        DomainErrorCode.PlaybackSourceUnsupported =>
            Never(code, "Errors.Playback.SourceUnsupported"),
        DomainErrorCode.PlaybackDecodingFailed =>
            Never(code, "Errors.Playback.DecodingFailed"),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown domain error code."),
    };

    private static DomainError Never(DomainErrorCode code, string resourceKey) =>
        new(code, DomainRetryability.Never, resourceKey);

    private static DomainError Transient(DomainErrorCode code, string resourceKey) =>
        new(code, DomainRetryability.BoundedTransient, resourceKey);

    private static DomainError Manual(DomainErrorCode code, string resourceKey) =>
        new(code, DomainRetryability.Manual, resourceKey);
}
