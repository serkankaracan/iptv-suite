using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IptvSuite.Domain;

[DebuggerDisplay("[SECRET-REFERENCE]")]
[JsonConverter(typeof(SecretReferenceJsonConverter))]
public sealed class SecretReference : IEquatable<SecretReference>
{
    private const string Prefix = "secret-ref-v1:";
    private readonly Guid _identifier;

    private SecretReference(Guid identifier)
    {
        _identifier = identifier;
    }

    public static SecretReference Create() => new(Guid.NewGuid());

    public static DomainResult<SecretReference> Parse(string? opaqueIdentifier)
    {
        return TryParseIdentifier(opaqueIdentifier, out Guid identifier)
            ? DomainResult.Success(new SecretReference(identifier))
            : DomainResult.Failure<SecretReference>(DomainErrorCode.SecretReferenceInvalid);
    }

    public bool Equals(SecretReference? other) =>
        other is not null && _identifier.Equals(other._identifier);

    public override bool Equals(object? obj) => Equals(obj as SecretReference);

    public override int GetHashCode() => _identifier.GetHashCode();

    public override string ToString() => "[SECRET-REFERENCE]";

    internal Guid Identifier => _identifier;

    internal string ToOpaqueIdentifier() => $"{Prefix}{_identifier:N}";

    private static bool TryParseIdentifier(string? value, out Guid identifier)
    {
        identifier = Guid.Empty;
        return value is not null &&
            value.Length == Prefix.Length + 32 &&
            value.StartsWith(Prefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(value[Prefix.Length..], "N", out identifier) &&
            identifier != Guid.Empty;
    }
}

[DebuggerDisplay("[PROTECTED-LOCATOR-REFERENCE]")]
[JsonConverter(typeof(ProtectedLocatorReferenceJsonConverter))]
public sealed class ProtectedLocatorReference : IEquatable<ProtectedLocatorReference>
{
    private const string Prefix = "locator-ref-v1:";
    private readonly Guid _identifier;

    private ProtectedLocatorReference(Guid identifier)
    {
        _identifier = identifier;
    }

    public static ProtectedLocatorReference Create() => new(Guid.NewGuid());

    public static DomainResult<ProtectedLocatorReference> Parse(string? opaqueIdentifier)
    {
        return TryParseIdentifier(opaqueIdentifier, out Guid identifier)
            ? DomainResult.Success(new ProtectedLocatorReference(identifier))
            : DomainResult.Failure<ProtectedLocatorReference>(DomainErrorCode.SecretReferenceInvalid);
    }

    public bool Equals(ProtectedLocatorReference? other) =>
        other is not null && _identifier.Equals(other._identifier);

    public override bool Equals(object? obj) => Equals(obj as ProtectedLocatorReference);

    public override int GetHashCode() => _identifier.GetHashCode();

    public override string ToString() => "[PROTECTED-LOCATOR-REFERENCE]";

    internal Guid Identifier => _identifier;

    internal string ToOpaqueIdentifier() => $"{Prefix}{_identifier:N}";

    private static bool TryParseIdentifier(string? value, out Guid identifier)
    {
        identifier = Guid.Empty;
        return value is not null &&
            value.Length == Prefix.Length + 32 &&
            value.StartsWith(Prefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(value[Prefix.Length..], "N", out identifier) &&
            identifier != Guid.Empty;
    }
}

public sealed class SecretReferenceJsonConverter : JsonConverter<SecretReference>
{
    public override SecretReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Secret reference JSON must be a string.");
        }

        DomainResult<SecretReference> result = SecretReference.Parse(reader.GetString());
        return result.IsSuccess
            ? result.Value!
            : throw new JsonException("Secret reference JSON is invalid.");
    }

    public override void Write(Utf8JsonWriter writer, SecretReference value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.ToOpaqueIdentifier());
    }
}

public sealed class ProtectedLocatorReferenceJsonConverter : JsonConverter<ProtectedLocatorReference>
{
    public override ProtectedLocatorReference Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Protected locator reference JSON must be a string.");
        }

        DomainResult<ProtectedLocatorReference> result = ProtectedLocatorReference.Parse(reader.GetString());
        return result.IsSuccess
            ? result.Value!
            : throw new JsonException("Protected locator reference JSON is invalid.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        ProtectedLocatorReference value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.ToOpaqueIdentifier());
    }
}
