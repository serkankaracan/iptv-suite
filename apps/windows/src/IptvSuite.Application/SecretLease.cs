using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IptvSuite.Application;

[DebuggerDisplay("[SENSITIVE]")]
[JsonConverter(typeof(SecretLeaseJsonConverter))]
public sealed class SecretLease : IDisposable
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private byte[]? _buffer;

    private SecretLease(byte[] ownedBuffer)
    {
        _buffer = ownedBuffer;
    }

    public int Length => GetBuffer().Length;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public ReadOnlyMemory<byte> Value => GetBuffer();

    public static SecretLease CopyFrom(ReadOnlySpan<byte> value)
    {
        ValidateLength(value.Length, nameof(value));
        return new SecretLease(value.ToArray());
    }

    internal static SecretLease TakeOwnership(byte[] ownedBuffer)
    {
        ArgumentNullException.ThrowIfNull(ownedBuffer);

        if (!IsSupportedLength(ownedBuffer.Length))
        {
            CryptographicOperations.ZeroMemory(ownedBuffer);
            throw new ArgumentOutOfRangeException(
                nameof(ownedBuffer),
                "The sensitive buffer length is outside the supported bounds.");
        }

        return new SecretLease(ownedBuffer);
    }

    ~SecretLease() => Clear();

    public void Dispose()
    {
        Clear();
        GC.SuppressFinalize(this);
    }

    public override string ToString() => "[SENSITIVE]";

    private void Clear()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private byte[] GetBuffer() =>
        Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(SecretLease));

    private static void ValidateLength(int length, string parameterName)
    {
        if (!IsSupportedLength(length))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The sensitive buffer length is outside the supported bounds.");
        }
    }

    private static bool IsSupportedLength(int length) =>
        length is > 0 and <= SecretStoreLimits.MaxProtectedValueBytes;
}

public sealed class SecretLeaseJsonConverter : JsonConverter<SecretLease>
{
    public override SecretLease Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("Sensitive leases cannot be deserialized.");

    public override void Write(Utf8JsonWriter writer, SecretLease value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue("[SENSITIVE]");
    }
}
