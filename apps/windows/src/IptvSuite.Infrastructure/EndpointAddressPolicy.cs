using System.Globalization;
using System.Net;
using System.Net.Sockets;
using IptvSuite.Application;
using IptvSuite.Domain;

namespace IptvSuite.Infrastructure;

internal static class EndpointAddressPolicy
{
    private const int MaximumResolvedAddresses = 32;
    private static readonly byte[] Ipv4DeprecatedRelayPrefix = [192, 88, 99];
    private static readonly byte[] Ipv6SpecialPurposePrefix = [0x20, 0x01, 0x00];
    private static readonly byte[] Ipv6SixToFourPrefix = [0x20, 0x02];
    private static readonly byte[] Ipv6DocumentationPrefix = [0x20, 0x01, 0x0d, 0xb8];
    private static readonly byte[] Ipv6DocumentationSecondPrefix = [0x3f, 0xff, 0x00];
    private static readonly HttpRequestOptionsKey<HttpEndpointAddressPolicy> AddressPolicyKey =
        new("IptvSuite.EndpointAddressPolicy");
    private static readonly HttpRequestOptionsKey<EndpointAuthority> AuthorityKey =
        new("IptvSuite.EndpointAuthority");

    internal static HttpEndpointAddressPolicy BindRequest(
        HttpRequestMessage request,
        HttpEndpointAddressPolicy requestedPolicy,
        SafeEndpoint expectedEndpoint,
        SafeEndpoint currentEndpoint)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedEndpoint);
        ArgumentNullException.ThrowIfNull(currentEndpoint);

        HttpEndpointAddressPolicy effectivePolicy =
            requestedPolicy == HttpEndpointAddressPolicy.ExplicitPrivateSourceOrigin &&
            expectedEndpoint.Equals(currentEndpoint)
                ? HttpEndpointAddressPolicy.ExplicitPrivateSourceOrigin
                : HttpEndpointAddressPolicy.PublicOnly;
        request.Options.Set(AddressPolicyKey, effectivePolicy);
        request.Options.Set(AuthorityKey, new EndpointAuthority(currentEndpoint.Host, currentEndpoint.Port));
        return effectivePolicy;
    }

    internal static bool IsBoundAuthorityAllowed(
        HttpRequestMessage request,
        string host,
        int port)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Options.TryGetValue(AuthorityKey, out EndpointAuthority authority) &&
               authority.Port == port &&
               AreEquivalentHosts(authority.Host, host);
    }

    internal static bool IsDisallowedCrossOriginLiteral(
        RedirectTargetAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return assessment.OriginRelation == RedirectOriginRelation.CrossOrigin &&
               IPAddress.TryParse(assessment.TargetEndpoint.Host, out IPAddress? address) &&
               !IsPublicUnicast(address);
    }

    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        string host = context.DnsEndPoint.Host;
        if (!IsBoundAuthorityAllowed(
                context.InitialRequestMessage,
                host,
                context.DnsEndPoint.Port))
        {
            throw new EndpointAddressRejectedException();
        }

        HttpEndpointAddressPolicy addressPolicy =
            context.InitialRequestMessage.Options.TryGetValue(AddressPolicyKey, out HttpEndpointAddressPolicy boundPolicy)
                ? boundPolicy
                : HttpEndpointAddressPolicy.PublicOnly;
        IPAddress[] resolvedAddresses = IPAddress.TryParse(host, out IPAddress? literalAddress)
            ? [literalAddress]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        if (!AreResolvedAddressesAllowed(host, resolvedAddresses, addressPolicy))
        {
            throw new EndpointAddressRejectedException();
        }

        SocketException? lastSocketFailure = null;
        foreach (IPAddress address in resolvedAddresses.Distinct())
        {
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                lastSocketFailure = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw lastSocketFailure ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static bool AreResolvedAddressesAllowed(
        string host,
        IPAddress[] resolvedAddresses,
        HttpEndpointAddressPolicy addressPolicy)
    {
        if (resolvedAddresses.Length is 0 or > MaximumResolvedAddresses)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out IPAddress? literalAddress))
        {
            bool exactLiteralResolution = resolvedAddresses.All(address => address.Equals(literalAddress));
            return exactLiteralResolution && IsAllowedUnicast(literalAddress, addressPolicy);
        }

        bool allPublic = resolvedAddresses.All(IsPublicUnicast);
        if (allPublic || addressPolicy != HttpEndpointAddressPolicy.ExplicitPrivateSourceOrigin)
        {
            return allPublic;
        }

        return resolvedAddresses.All(IsExplicitPrivateOrLocalUnicast);
    }

    private static bool IsAllowedUnicast(
        IPAddress address,
        HttpEndpointAddressPolicy addressPolicy) =>
        IsPublicUnicast(address) ||
        (addressPolicy == HttpEndpointAddressPolicy.ExplicitPrivateSourceOrigin &&
         IsExplicitPrivateOrLocalUnicast(address));

    private static bool IsExplicitPrivateOrLocalUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsExplicitPrivateOrLocalUnicast(address.MapToIPv4());
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                10 or 127 => true,
                100 when bytes[1] is >= 64 and <= 127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 168 => true,
                _ => false,
            };
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
               (IPAddress.IsLoopback(address) ||
                address.IsIPv6LinkLocal ||
                address.IsIPv6SiteLocal ||
                (bytes[0] & 0xfe) == 0xfc);
    }

    private static bool IsPublicUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicUnicast(address.MapToIPv4());
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 0 && bytes[2] is 0 or 2 => false,
                192 when HasPrefix(bytes, Ipv4DeprecatedRelayPrefix, 24) => false,
                192 when bytes[1] == 168 => false,
                198 when bytes[1] is 18 or 19 => false,
                198 when bytes[1] == 51 && bytes[2] == 100 => false,
                203 when bytes[1] == 0 && bytes[2] == 113 => false,
                >= 224 => false,
                _ => true,
            };
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
            IPAddress.IsLoopback(address) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast ||
            address.IsIPv6SiteLocal ||
            (bytes[0] & 0xfe) == 0xfc ||
            (bytes[0] & 0xe0) != 0x20)
        {
            return false;
        }

        return !HasPrefix(bytes, Ipv6SpecialPurposePrefix, 23) &&
               !HasPrefix(bytes, Ipv6SixToFourPrefix, 16) &&
               !HasPrefix(bytes, Ipv6DocumentationPrefix, 32) &&
               !HasPrefix(bytes, Ipv6DocumentationSecondPrefix, 20);
    }

    private static bool HasPrefix(
        ReadOnlySpan<byte> address,
        ReadOnlySpan<byte> prefix,
        int prefixLength)
    {
        int wholeBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        if (prefixLength <= 0 ||
            prefixLength > address.Length * 8 ||
            prefix.Length * 8 < prefixLength ||
            !address[..wholeBytes].SequenceEqual(prefix[..wholeBytes]))
        {
            return false;
        }

        if (remainingBits == 0)
        {
            return true;
        }

        int mask = 0xff << (8 - remainingBits);
        return (address[wholeBytes] & mask) == (prefix[wholeBytes] & mask);
    }

    private static bool AreEquivalentHosts(string expectedHost, string actualHost)
    {
        bool expectedIsAddress = IPAddress.TryParse(expectedHost, out IPAddress? expectedAddress);
        bool actualIsAddress = IPAddress.TryParse(actualHost, out IPAddress? actualAddress);
        if (expectedIsAddress || actualIsAddress)
        {
            return expectedAddress is not null &&
                   actualAddress is not null &&
                   expectedAddress.Equals(actualAddress);
        }

        return TryNormalizeDnsHost(expectedHost, out string expectedCanonicalHost) &&
               TryNormalizeDnsHost(actualHost, out string actualCanonicalHost) &&
               string.Equals(expectedCanonicalHost, actualCanonicalHost, StringComparison.Ordinal);
    }

    private static bool TryNormalizeDnsHost(string host, out string canonicalHost)
    {
        canonicalHost = string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        try
        {
            var idn = new IdnMapping
            {
                UseStd3AsciiRules = true,
            };
            canonicalHost = idn.GetAscii(host).ToLowerInvariant();
            return canonicalHost.Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private readonly record struct EndpointAuthority(string Host, int Port);
}

internal sealed class EndpointAddressRejectedException : IOException
{
    internal EndpointAddressRejectedException()
        : base("The resolved endpoint address is not permitted.")
    {
    }
}
