namespace IptvSuite.ArchitectureTests;

[TestClass]
public sealed class EndpointAddressPolicyRulesTests
{
    private static readonly string[] ExpectedExplicitPrivatePolicyCallers =
    [
        "apps/windows/src/IptvSuite.Infrastructure/XtreamProviderClient.cs",
    ];

    private static readonly string[] ExpectedRemotePlaylistPolicyCallers =
    [
        "apps/windows/src/IptvSuite.Infrastructure/RemotePlaylistCatalogLoader.cs",
    ];

    [TestMethod]
    public void ProductionTransportRechecksResolvedAddressesAtConnectTime()
    {
        string repositoryRoot = FindRepositoryRoot();
        string applicationContractPath = Path.Combine(
            repositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Application",
            "HttpTransportContracts.cs");
        string applicationContract = File.ReadAllText(applicationContractPath);
        string transport = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Infrastructure",
            "BoundedHttpTransport.cs"));
        string policy = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Infrastructure",
            "EndpointAddressPolicy.cs"));
        string onboarding = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Application",
            "RemotePlaylistSourceOnboarding.cs"));
        string loader = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Infrastructure",
            "RemotePlaylistCatalogLoader.cs"));
        string logoProvider = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Infrastructure",
            "SqliteChannelLogoProvider.cs"));
        string onboardingView = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Windows",
            "MainPage.xaml"));
        string securityBaseline = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "docs",
            "security",
            "SECURITY_AND_PRIVACY_BASELINE.md"));

        StringAssert.Contains(transport, "ConnectCallback = EndpointAddressPolicy.ConnectAsync");
        StringAssert.Contains(transport, "HttpTransportFailure.EndpointAddressRejected");
        StringAssert.Contains(transport, "_publicClient");
        StringAssert.Contains(transport, "_explicitPrivateSourceClient");
        StringAssert.Contains(transport, "CreateProductionHandler(TimeSpan.Zero)");
        StringAssert.Contains(transport, "SelectClient(addressPolicy)");
        StringAssert.Contains(transport, "UseProxy = false");
        StringAssert.Contains(policy, "Dns.GetHostAddressesAsync(host, cancellationToken)");
        StringAssert.Contains(policy, "IsBoundAuthorityAllowed(");
        StringAssert.Contains(policy, "authority.Port == port");
        StringAssert.Contains(policy, "AreEquivalentHosts(authority.Host, host)");
        StringAssert.Contains(policy, "UseStd3AsciiRules = true");
        StringAssert.Contains(policy, "idn.GetAscii(host).ToLowerInvariant()");
        StringAssert.Contains(policy, "bool allPublic = resolvedAddresses.All(IsPublicUnicast)");
        StringAssert.Contains(policy, "resolvedAddresses.All(IsExplicitPrivateOrLocalUnicast)");
        StringAssert.Contains(policy, "expectedEndpoint.Equals(currentEndpoint)");
        StringAssert.Contains(policy, "HttpEndpointAddressPolicy.PublicOnly");
        StringAssert.Contains(policy, "IsDisallowedCrossOriginLiteral(");
        StringAssert.Contains(policy, "Ipv4DeprecatedRelayPrefix");
        StringAssert.Contains(policy, "Ipv6SpecialPurposePrefix");
        StringAssert.Contains(policy, "Ipv6SixToFourPrefix");
        StringAssert.Contains(policy, "Ipv6DocumentationSecondPrefix");
        StringAssert.Contains(policy, "private static bool HasPrefix(");
        StringAssert.Contains(policy, "new NetworkStream(socket, ownsSocket: true)");
        StringAssert.Contains(applicationContract, "internal enum HttpEndpointAddressPolicy");
        StringAssert.Contains(applicationContract, "internal static HttpTransportRequest CreateForExplicitPrivateSourceOrigin(");
        StringAssert.Contains(applicationContract, "internal static HttpTransportRequest CreateForExplicitRemotePlaylistSourceOrigin(");
        StringAssert.Contains(applicationContract, "HttpEndpointAddressPolicy.PublicOnly");
        StringAssert.Contains(loader, "HttpTransportRequest.CreateForExplicitRemotePlaylistSourceOrigin(");
        Assert.IsFalse(onboarding.Contains("HttpTransportRequest", StringComparison.Ordinal));
        Assert.IsFalse(onboarding.Contains("ConnectionProbeService", StringComparison.Ordinal));
        Assert.IsFalse(onboarding.Contains("CreateForExplicitPrivateSourceOrigin", StringComparison.Ordinal));
        Assert.IsFalse(loader.Contains("CreateForExplicitPrivateSourceOrigin", StringComparison.Ordinal));
        StringAssert.Contains(logoProvider, "HttpTransportRequest.Create(uri, endpoint, MaximumLogoBytes)");
        Assert.IsFalse(logoProvider.Contains("CreateForExplicitPrivateSourceOrigin", StringComparison.Ordinal));
        Assert.IsFalse(logoProvider.Contains("CreateForExplicitRemotePlaylistSourceOrigin", StringComparison.Ordinal));
        StringAssert.Contains(onboardingView, "özel/yerel ağdaysa yalnızca bu tam sunucu ve porta");
        StringAssert.Contains(securityBaseline, "her zaman `PublicOnly` policy ile reddedilir");
        StringAssert.Contains(securityBaseline, "bu opt-in logo/image isteğine veya cross-origin redirect'e taşınmaz");
        StringAssert.Contains(securityBaseline, "Production transport OS/environment proxy'sini kullanmaz (`UseProxy=false`)");
        StringAssert.Contains(securityBaseline, "Bu pre-release modelde desteklenen legacy upgrade yoktur");
        StringAssert.Contains(securityBaseline, "Xtream production composition'a bağlanmadan önce de aynı açık consent UI/contract yolu");
        Assert.IsFalse(policy.Contains("DangerousAcceptAnyServerCertificateValidator", StringComparison.Ordinal));

        int authorityCheck = policy.IndexOf("if (!IsBoundAuthorityAllowed(", StringComparison.Ordinal);
        int dnsResolution = policy.IndexOf("Dns.GetHostAddressesAsync(host, cancellationToken)", StringComparison.Ordinal);
        Assert.IsTrue(
            authorityCheck >= 0 && dnsResolution > authorityCheck,
            "The request-bound exact authority must be checked before DNS resolution or socket connect.");

        string sourceRoot = Path.Combine(repositoryRoot, "apps", "windows", "src");
        string[] explicitPrivatePolicyCallers = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, applicationContractPath, StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(
                "HttpTransportRequest.CreateForExplicitPrivateSourceOrigin(",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            ExpectedExplicitPrivatePolicyCallers,
            explicitPrivatePolicyCallers);

        string[] remotePlaylistPolicyCallers = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, applicationContractPath, StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(
                "HttpTransportRequest.CreateForExplicitRemotePlaylistSourceOrigin(",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            ExpectedRemotePlaylistPolicyCallers,
            remotePlaylistPolicyCallers);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IPTVSuite.slnx")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
