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
    private protected SourceConfiguration(
        SourceConfigurationId configurationId,
        SourceKind kind,
        SafeEndpoint safeEndpoint)
    {
        if (configurationId.IsEmpty)
        {
            throw new ArgumentException(
                "A non-empty source configuration identifier is required.",
                nameof(configurationId));
        }

        ArgumentNullException.ThrowIfNull(safeEndpoint);
        ConfigurationId = configurationId;
        Kind = kind;
        SafeEndpoint = safeEndpoint;
    }

    public SourceConfigurationId ConfigurationId { get; }

    public SourceKind Kind { get; }

    public SafeEndpoint SafeEndpoint { get; }

    private string DebuggerDisplay => $"[SOURCE-CONFIGURATION:{Kind}]";

    public override string ToString() => $"[SOURCE-CONFIGURATION:{Kind}]";
}

public sealed class XtreamSourceConfiguration : SourceConfiguration
{
    internal XtreamSourceConfiguration(
        SourceConfigurationId configurationId,
        SafeEndpoint safeEndpoint,
        SecretReference credentialsReference)
        : base(configurationId, SourceKind.XtreamCompatible, safeEndpoint)
    {
        ArgumentNullException.ThrowIfNull(credentialsReference);
        CredentialsReference = credentialsReference;
    }

    public SecretReference CredentialsReference { get; }
}

public sealed class RemotePlaylistSourceConfiguration : SourceConfiguration
{
    internal RemotePlaylistSourceConfiguration(
        SourceConfigurationId configurationId,
        SafeEndpoint safeEndpoint,
        ProtectedLocatorReference locatorReference)
        : base(configurationId, SourceKind.RemotePlaylist, safeEndpoint)
    {
        ArgumentNullException.ThrowIfNull(locatorReference);
        LocatorReference = locatorReference;
    }

    public ProtectedLocatorReference LocatorReference { get; }
}

[DebuggerDisplay("[PREPARED-XTREAM-SOURCE-DRAFT]")]
public sealed class PreparedXtreamSourceDraft
{
    internal PreparedXtreamSourceDraft(string normalizedDisplayName, SafeEndpoint safeEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedDisplayName);
        ArgumentNullException.ThrowIfNull(safeEndpoint);
        NormalizedDisplayName = normalizedDisplayName;
        SafeEndpoint = safeEndpoint;
    }

    public string NormalizedDisplayName { get; }

    public SafeEndpoint SafeEndpoint { get; }

    public override string ToString() => "[PREPARED-XTREAM-SOURCE-DRAFT]";
}

[DebuggerDisplay("[PREPARED-REMOTE-PLAYLIST-SOURCE-DRAFT]")]
public sealed class PreparedRemotePlaylistSourceDraft
{
    internal PreparedRemotePlaylistSourceDraft(string normalizedDisplayName, SafeEndpoint safeEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedDisplayName);
        ArgumentNullException.ThrowIfNull(safeEndpoint);
        NormalizedDisplayName = normalizedDisplayName;
        SafeEndpoint = safeEndpoint;
    }

    public string NormalizedDisplayName { get; }

    public SafeEndpoint SafeEndpoint { get; }

    public override string ToString() => "[PREPARED-REMOTE-PLAYLIST-SOURCE-DRAFT]";
}

[DebuggerDisplay("[VALIDATED-SOURCE-DRAFT]")]
public sealed class ValidatedSourceDraft
{
    internal ValidatedSourceDraft(
        SourceId sourceId,
        string normalizedDisplayName,
        SourceConfiguration configuration)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A non-empty source identifier is required.", nameof(sourceId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedDisplayName);
        ArgumentNullException.ThrowIfNull(configuration);
        SourceId = sourceId;
        NormalizedDisplayName = normalizedDisplayName;
        Configuration = configuration;
    }

    public SourceId SourceId { get; }

    public string NormalizedDisplayName { get; }

    public SourceConfiguration Configuration { get; }

    public override string ToString() => "[VALIDATED-SOURCE-DRAFT]";
}
