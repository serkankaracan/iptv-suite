using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace IptvSuite.Domain;

[DebuggerDisplay("[SAFE-ENDPOINT]")]
public sealed class SafeEndpoint : IEquatable<SafeEndpoint>
{
    private SafeEndpoint(string scheme, string host, int port)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
    }

    public string Scheme { get; }

    public string Host { get; }

    public int Port { get; }

    internal static bool TryCreate(Uri uri, out SafeEndpoint? endpoint)
    {
        ArgumentNullException.ThrowIfNull(uri);

        endpoint = null;
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        if (!TryNormalizeHost(uri, out string host))
        {
            return false;
        }

        int port = uri.IsDefaultPort ? 443 : uri.Port;
        if (port is < 1 or > 65535)
        {
            return false;
        }

        endpoint = new SafeEndpoint(Uri.UriSchemeHttps, host, port);
        return true;
    }

    public bool Equals(SafeEndpoint? other) =>
        other is not null &&
        Port == other.Port &&
        string.Equals(Scheme, other.Scheme, StringComparison.Ordinal) &&
        string.Equals(Host, other.Host, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as SafeEndpoint);

    public override int GetHashCode() => HashCode.Combine(Scheme, Host, Port);

    public override string ToString() => "[SAFE-ENDPOINT]";

    private static bool TryNormalizeHost(Uri uri, out string host)
    {
        host = string.Empty;

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            string addressText = uri.DnsSafeHost.Trim('[', ']');
            if (!IPAddress.TryParse(addressText, out IPAddress? address))
            {
                return false;
            }

            host = address.ToString().ToLowerInvariant();
            return true;
        }

        if (uri.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        try
        {
            IdnMapping idn = new()
            {
                UseStd3AsciiRules = true,
            };
            host = idn.GetAscii(uri.DnsSafeHost).ToLowerInvariant();
            return host.Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
