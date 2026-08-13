namespace IptvSuite.Application;

internal static class ProtectedSourcePayloadFormat
{
    internal const byte Version = 1;
    internal const int MagicSize = 8;
    internal const int VersionSize = sizeof(byte);
    internal const int LengthSize = sizeof(int);
    internal const int CredentialsHeaderSize = MagicSize + VersionSize + (3 * LengthSize);
    internal const int RemotePlaylistHeaderSize = MagicSize + VersionSize + LengthSize;

    internal static ReadOnlySpan<byte> CredentialsMagic => "SRCRED01"u8;

    internal static ReadOnlySpan<byte> RemotePlaylistMagic => "SRCLOC01"u8;
}
