using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IptvSuite.Application;

internal readonly record struct XtreamSourcePayloadLayout(
    int LocatorOffset,
    int LocatorLength,
    int UsernameOffset,
    int UsernameLength,
    int PasswordOffset,
    int PasswordLength);

internal readonly record struct RemotePlaylistPayloadLayout(int LocatorOffset, int LocatorLength);

internal static class ProtectedSourcePayloadDecoder
{
    // These parsers borrow the supplied memory and return numeric slice metadata only.
    // The future operation-specific consumer must keep the owning SecretLease alive and
    // dispose it after use; the decoder never creates a plaintext string or a second buffer.
    internal static bool TryDecodeXtream(
        ReadOnlyMemory<byte> payloadMemory,
        out XtreamSourcePayloadLayout layout)
    {
        layout = default;
        ReadOnlySpan<byte> payload = payloadMemory.Span;
        if (!HasValidEnvelope(
                payload,
                ProtectedSourcePayloadFormat.CredentialsMagic,
                ProtectedSourcePayloadFormat.CredentialsHeaderSize))
        {
            return false;
        }

        int locatorLength = BinaryPrimitives.ReadInt32BigEndian(
            payload.Slice(
                ProtectedSourcePayloadFormat.MagicSize + ProtectedSourcePayloadFormat.VersionSize,
                ProtectedSourcePayloadFormat.LengthSize));
        int usernameLength = BinaryPrimitives.ReadInt32BigEndian(
            payload.Slice(
                ProtectedSourcePayloadFormat.MagicSize + ProtectedSourcePayloadFormat.VersionSize +
                    ProtectedSourcePayloadFormat.LengthSize,
                ProtectedSourcePayloadFormat.LengthSize));
        int passwordLength = BinaryPrimitives.ReadInt32BigEndian(
            payload.Slice(
                ProtectedSourcePayloadFormat.MagicSize + ProtectedSourcePayloadFormat.VersionSize +
                    (2 * ProtectedSourcePayloadFormat.LengthSize),
                ProtectedSourcePayloadFormat.LengthSize));

        if (!HasExactCredentialsPayloadLength(
                payload.Length,
                locatorLength,
                usernameLength,
                passwordLength))
        {
            return false;
        }

        int locatorOffset = ProtectedSourcePayloadFormat.CredentialsHeaderSize;
        int usernameOffset = locatorOffset + locatorLength;
        int passwordOffset = usernameOffset + usernameLength;
        if (!IsValidUtf8Field(
                payload.Slice(locatorOffset, locatorLength),
                SourceConfigurationValidator.MaxLocatorUnicodeScalars,
                requireNonWhitespace: true) ||
            !IsValidUtf8Field(
                payload.Slice(usernameOffset, usernameLength),
                SourceConfigurationValidator.MaxUsernameUnicodeScalars,
                requireNonWhitespace: true) ||
            !IsValidUtf8Field(
                payload.Slice(passwordOffset, passwordLength),
                SourceConfigurationValidator.MaxPasswordUnicodeScalars,
                requireNonWhitespace: false))
        {
            return false;
        }

        layout = new XtreamSourcePayloadLayout(
            locatorOffset,
            locatorLength,
            usernameOffset,
            usernameLength,
            passwordOffset,
            passwordLength);
        return true;
    }

    internal static bool TryDecodeRemotePlaylist(
        ReadOnlyMemory<byte> payloadMemory,
        out RemotePlaylistPayloadLayout layout)
    {
        layout = default;
        ReadOnlySpan<byte> payload = payloadMemory.Span;
        if (!HasValidEnvelope(
                payload,
                ProtectedSourcePayloadFormat.RemotePlaylistMagic,
                ProtectedSourcePayloadFormat.RemotePlaylistHeaderSize))
        {
            return false;
        }

        int locatorLength = BinaryPrimitives.ReadInt32BigEndian(
            payload.Slice(
                ProtectedSourcePayloadFormat.MagicSize + ProtectedSourcePayloadFormat.VersionSize,
                ProtectedSourcePayloadFormat.LengthSize));
        if (!HasExactRemotePlaylistPayloadLength(payload.Length, locatorLength) ||
            !IsValidUtf8Field(
                payload.Slice(ProtectedSourcePayloadFormat.RemotePlaylistHeaderSize, locatorLength),
                SourceConfigurationValidator.MaxLocatorUnicodeScalars,
                requireNonWhitespace: true))
        {
            return false;
        }

        layout = new RemotePlaylistPayloadLayout(
            ProtectedSourcePayloadFormat.RemotePlaylistHeaderSize,
            locatorLength);
        return true;
    }

    private static bool HasValidEnvelope(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> expectedMagic,
        int headerSize)
    {
        if (payload.Length < headerSize || payload.Length > SecretStoreLimits.MaxProtectedValueBytes ||
            !payload[..ProtectedSourcePayloadFormat.MagicSize].SequenceEqual(expectedMagic))
        {
            return false;
        }

        return payload[ProtectedSourcePayloadFormat.MagicSize] == ProtectedSourcePayloadFormat.Version;
    }

    private static bool HasExactCredentialsPayloadLength(
        int actualLength,
        int locatorLength,
        int usernameLength,
        int passwordLength)
    {
        if (locatorLength <= 0 || usernameLength <= 0 || passwordLength <= 0)
        {
            return false;
        }

        long expectedLength = (long)ProtectedSourcePayloadFormat.CredentialsHeaderSize +
            locatorLength + usernameLength + passwordLength;
        return expectedLength <= SecretStoreLimits.MaxProtectedValueBytes &&
            expectedLength == actualLength;
    }

    private static bool HasExactRemotePlaylistPayloadLength(int actualLength, int locatorLength)
    {
        if (locatorLength <= 0)
        {
            return false;
        }

        long expectedLength = (long)ProtectedSourcePayloadFormat.RemotePlaylistHeaderSize +
            locatorLength;
        return expectedLength == actualLength;
    }

    private static bool IsValidUtf8Field(
        ReadOnlySpan<byte> field,
        int maximumUnicodeScalars,
        bool requireNonWhitespace)
    {
        int scalarCount = 0;
        bool hasNonWhitespace = false;
        while (!field.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf8(field, out Rune rune, out int consumed);
            if (status != OperationStatus.Done || consumed <= 0 ||
                Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                return false;
            }

            scalarCount++;
            if (scalarCount > maximumUnicodeScalars)
            {
                return false;
            }

            hasNonWhitespace |= !Rune.IsWhiteSpace(rune);
            field = field[consumed..];
        }

        return scalarCount > 0 && (!requireNonWhitespace || hasNonWhitespace);
    }
}
