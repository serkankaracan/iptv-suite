using System.Diagnostics;
using System.Text.Json.Serialization;

namespace IptvSuite.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<SourceKind>))]
public enum SourceKind
{
    XtreamCompatible,
    RemotePlaylist,
}

[DebuggerDisplay("{DebuggerDisplay,nq}")]
public abstract class SourceConfiguration
{
    private protected SourceConfiguration(SourceKind kind, SafeEndpoint safeEndpoint)
    {
        ArgumentNullException.ThrowIfNull(safeEndpoint);
        Kind = kind;
        SafeEndpoint = safeEndpoint;
    }

    public SourceKind Kind { get; }

    public SafeEndpoint SafeEndpoint { get; }

    private string DebuggerDisplay => $"[SOURCE-CONFIGURATION:{Kind}]";

    public override string ToString() => $"[SOURCE-CONFIGURATION:{Kind}]";
}

public sealed class XtreamSourceConfiguration : SourceConfiguration
{
    internal XtreamSourceConfiguration(SafeEndpoint safeEndpoint, SecretReference credentialsReference)
        : base(SourceKind.XtreamCompatible, safeEndpoint)
    {
        ArgumentNullException.ThrowIfNull(credentialsReference);
        CredentialsReference = credentialsReference;
    }

    public SecretReference CredentialsReference { get; }
}

public sealed class RemotePlaylistSourceConfiguration : SourceConfiguration
{
    internal RemotePlaylistSourceConfiguration(
        SafeEndpoint safeEndpoint,
        ProtectedLocatorReference locatorReference)
        : base(SourceKind.RemotePlaylist, safeEndpoint)
    {
        ArgumentNullException.ThrowIfNull(locatorReference);
        LocatorReference = locatorReference;
    }

    public ProtectedLocatorReference LocatorReference { get; }
}

[DebuggerDisplay("[VALIDATED-SOURCE-DRAFT]")]
public sealed class ValidatedSourceDraft
{
    internal ValidatedSourceDraft(string normalizedDisplayName, SourceConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedDisplayName);
        ArgumentNullException.ThrowIfNull(configuration);
        NormalizedDisplayName = normalizedDisplayName;
        Configuration = configuration;
    }

    public string NormalizedDisplayName { get; }

    public SourceConfiguration Configuration { get; }

    public override string ToString() => "[VALIDATED-SOURCE-DRAFT]";
}
