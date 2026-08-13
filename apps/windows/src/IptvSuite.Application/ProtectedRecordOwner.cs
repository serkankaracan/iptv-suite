using System.Diagnostics;
using System.Text.Json.Serialization;

namespace IptvSuite.Application;

public enum ProtectedRecordOwnerKind : byte
{
    SourceConfiguration = 1,
    Channel = 2,
}

/// <summary>
/// Identifies the semantic configuration or channel that owns a protected record.
/// </summary>
/// <remarks>
/// This value is integrity-binding context, not an authorization principal. Callers must derive it
/// from the containing domain entity and must not derive it from a protected-record reference.
/// </remarks>
[DebuggerDisplay("[PROTECTED-RECORD-OWNER]")]
public readonly record struct ProtectedRecordOwner
{
    private ProtectedRecordOwner(ProtectedRecordOwnerKind kind, Guid identifier)
    {
        Kind = kind;
        Identifier = identifier;
    }

    public ProtectedRecordOwnerKind Kind { get; }

    public bool IsEmpty => Identifier == Guid.Empty || !Enum.IsDefined(Kind);

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    [JsonIgnore]
    internal Guid Identifier { get; }

    public static ProtectedRecordOwner ForSourceConfiguration(SourceConfigurationId configurationId)
    {
        if (configurationId.IsEmpty)
        {
            throw new ArgumentException(
                "A non-empty source configuration identifier is required.",
                nameof(configurationId));
        }

        return new ProtectedRecordOwner(
            ProtectedRecordOwnerKind.SourceConfiguration,
            configurationId.Value);
    }

    public static ProtectedRecordOwner ForChannel(ChannelId channelId)
    {
        if (channelId.IsEmpty)
        {
            throw new ArgumentException("A non-empty channel identifier is required.", nameof(channelId));
        }

        return new ProtectedRecordOwner(ProtectedRecordOwnerKind.Channel, channelId.Value);
    }

    public override string ToString() => "[PROTECTED-RECORD-OWNER]";
}
