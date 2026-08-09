namespace IptvSuite.Application;

public enum ProtectedValuePurpose : byte
{
    SourceCredentials = 1,
    RemotePlaylistLocator = 2,
    ChannelStreamLocator = 3,
    ChannelLogoLocator = 4,
}

public static class SecretStoreLimits
{
    public const int MaxProtectedValueBytes = 65_536;
}
