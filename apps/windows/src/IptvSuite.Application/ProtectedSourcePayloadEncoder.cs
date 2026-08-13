using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IptvSuite.Application;

internal static class ProtectedSourcePayloadEncoder
{
    private const byte FormatVersion = 1;
    private const int VersionSize = sizeof(byte);
    private const int LengthSize = sizeof(int);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static ReadOnlySpan<byte> CredentialsMagic => "SRCRED01"u8;

    private static ReadOnlySpan<byte> RemotePlaylistMagic => "SRCLOC01"u8;

    internal static byte[] EncodeXtreamSourceCredentials(
        string locator,
        string username,
        string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(locator);
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        int locatorByteCount = StrictUtf8.GetByteCount(locator);
        int usernameByteCount = StrictUtf8.GetByteCount(username);
        int passwordByteCount = StrictUtf8.GetByteCount(password);
        int payloadLength = checked(
            CredentialsMagic.Length + VersionSize + (3 * LengthSize) +
            locatorByteCount + usernameByteCount + passwordByteCount);
        ValidatePayloadLength(payloadLength);

        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        try
        {
            Span<byte> destination = payload;
            int offset = 0;
            CredentialsMagic.CopyTo(destination);
            offset += CredentialsMagic.Length;
            destination[offset++] = FormatVersion;
            BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset, LengthSize), locatorByteCount);
            offset += LengthSize;
            BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset, LengthSize), usernameByteCount);
            offset += LengthSize;
            BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset, LengthSize), passwordByteCount);
            offset += LengthSize;
            offset += StrictUtf8.GetBytes(locator.AsSpan(), destination[offset..]);
            offset += StrictUtf8.GetBytes(username.AsSpan(), destination[offset..]);
            offset += StrictUtf8.GetBytes(password.AsSpan(), destination[offset..]);

            if (offset != payload.Length)
            {
                throw new InvalidOperationException("The protected credential payload length is inconsistent.");
            }

            return payload;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(payload);
            throw;
        }
    }

    internal static byte[] EncodeRemotePlaylistLocator(string locator)
    {
        ArgumentException.ThrowIfNullOrEmpty(locator);

        int locatorByteCount = StrictUtf8.GetByteCount(locator);
        int payloadLength = checked(
            RemotePlaylistMagic.Length + VersionSize + LengthSize + locatorByteCount);
        ValidatePayloadLength(payloadLength);

        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        try
        {
            Span<byte> destination = payload;
            int offset = 0;
            RemotePlaylistMagic.CopyTo(destination);
            offset += RemotePlaylistMagic.Length;
            destination[offset++] = FormatVersion;
            BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset, LengthSize), locatorByteCount);
            offset += LengthSize;
            offset += StrictUtf8.GetBytes(locator.AsSpan(), destination[offset..]);

            if (offset != payload.Length)
            {
                throw new InvalidOperationException("The protected locator payload length is inconsistent.");
            }

            return payload;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(payload);
            throw;
        }
    }

    private static void ValidatePayloadLength(int payloadLength)
    {
        if (payloadLength <= 0 || payloadLength > SecretStoreLimits.MaxProtectedValueBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadLength),
                "The encoded protected payload length is outside the supported bounds.");
        }
    }
}
