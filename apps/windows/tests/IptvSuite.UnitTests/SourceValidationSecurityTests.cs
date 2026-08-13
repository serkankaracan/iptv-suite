using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class SourceValidationSecurityTests
{
    private static readonly string[] ExpectedPublicValidatorMethods =
        ["PrepareRemotePlaylist", "PrepareXtream"];

    [TestMethod]
    public void SafeEndpointPublicStateContainsOnlySchemeHostAndEffectivePort()
    {
        Assert.AreEqual(0, typeof(XtreamSourceConfiguration).GetConstructors().Length);
        Assert.AreEqual(0, typeof(RemotePlaylistSourceConfiguration).GetConstructors().Length);

        string[] properties = typeof(SafeEndpoint)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] expectedProperties = ["Host", "Port", "Scheme"];
        CollectionAssert.AreEqual(expectedProperties, properties);
        Assert.AreEqual("[SAFE-ENDPOINT]", GetRemoteEndpoint("https://example.test/private").ToString());
    }

    [TestMethod]
    public void PublicSourceDraftValidationCannotAttachCallerIssuedReferences()
    {
        string[] publicMethods = typeof(SourceConfigurationValidator)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEquivalent(
            ExpectedPublicValidatorMethods,
            publicMethods);
        Assert.AreEqual(0, typeof(PreparedXtreamSourceDraft).GetConstructors().Length);
        Assert.AreEqual(0, typeof(PreparedRemotePlaylistSourceDraft).GetConstructors().Length);
        Assert.AreEqual(0, typeof(ValidatedSourceDraft).GetConstructors().Length);
    }

    [TestMethod]
    public void DisplayNameIsTrimmedNormalizedAndCountedByUnicodeScalar()
    {
        DomainResult<PreparedRemotePlaylistSourceDraft> normalized =
            SourceConfigurationValidator.PrepareRemotePlaylist(
                "  Cafe\u0301  ",
                "https://example.test/list.m3u");

        Assert.IsTrue(normalized.IsSuccess);
        Assert.AreEqual("Caf\u00e9", normalized.Value!.NormalizedDisplayName);

        string oneScalar = "\U0001F600";
        string maximum = string.Concat(Enumerable.Repeat(oneScalar, 100));
        string oversized = maximum + oneScalar;

        Assert.IsTrue(SourceConfigurationValidator.PrepareRemotePlaylist(
            maximum,
            "https://example.test/list.m3u").IsSuccess);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.PrepareRemotePlaylist(
                oversized,
                "https://example.test/list.m3u"),
            DomainErrorCode.SourceNameTooLong);
    }

    [TestMethod]
    public void LocatorAndCredentialBoundsAreExact()
    {
        const string prefix = "https://example.test/";
        string maximumLocator = prefix + new string('a', 4096 - prefix.Length);
        string oversizedLocator = maximumLocator + "a";

        Assert.IsTrue(SourceConfigurationValidator.PrepareRemotePlaylist(
            "Source",
            maximumLocator).IsSuccess);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.PrepareRemotePlaylist(
                "Source",
                oversizedLocator),
            DomainErrorCode.EndpointTooLong);

        string maximumUsername = new('u', 256);
        string maximumPassword = new('p', 1024);
        Assert.IsTrue(SourceConfigurationValidator.PrepareXtream(
            "Source",
            "https://example.test/api",
            maximumUsername,
            maximumPassword).IsSuccess);

        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.PrepareXtream(
                "Source",
                "https://example.test/api",
                maximumUsername + "u",
                maximumPassword),
            DomainErrorCode.CredentialTooLong);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.PrepareXtream(
                "Source",
                "https://example.test/api",
                maximumUsername,
                maximumPassword + "p"),
            DomainErrorCode.CredentialTooLong);
    }

    [TestMethod]
    public void SafeEndpointNormalizesIdnIpv4Ipv6AndEffectivePort()
    {
        SafeEndpoint idn = GetRemoteEndpoint("https://b\u00fccher.example/private/path?token=hidden");
        Assert.AreEqual("https", idn.Scheme);
        Assert.AreEqual("xn--bcher-kva.example", idn.Host);
        Assert.AreEqual(443, idn.Port);

        SafeEndpoint ipv4 = GetRemoteEndpoint("https://192.0.2.10:8443/list");
        Assert.AreEqual("192.0.2.10", ipv4.Host);
        Assert.AreEqual(8443, ipv4.Port);

        SafeEndpoint ipv6 = GetRemoteEndpoint("https://[2001:db8::1]/list");
        Assert.AreEqual("2001:db8::1", ipv6.Host);
        Assert.AreEqual(443, ipv6.Port);
    }

    [TestMethod]
    public void UnsupportedAndInsecureSchemesFailWithTypedErrors()
    {
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.PrepareRemotePlaylist(
                "Source",
                "ftp://example.test/list"),
            DomainErrorCode.EndpointSchemeUnsupported);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.PrepareRemotePlaylist(
                "Source",
                "http://example.test/list"),
            DomainErrorCode.InsecureTransportRejected);
    }

    [TestMethod]
    public void InvalidUtf16ControlCharactersAndFragmentsAreRejectedWithoutEcho()
    {
        string invalidUtf16 = "https://example.test/\ud800";
        string controlled = "https://example.test/list\0";
        string fragmentValue = SecurityTestAssertions.CreateSensitiveValue("FRAGMENT");
        string withFragment = $"https://example.test/list#{fragmentValue}";

        DomainResult<PreparedRemotePlaylistSourceDraft> invalid =
            SourceConfigurationValidator.PrepareRemotePlaylist("Source", invalidUtf16);
        DomainResult<PreparedRemotePlaylistSourceDraft> control =
            SourceConfigurationValidator.PrepareRemotePlaylist("Source", controlled);
        DomainResult<PreparedRemotePlaylistSourceDraft> fragment =
            SourceConfigurationValidator.PrepareRemotePlaylist("Source", withFragment);

        SecurityTestAssertions.IsFailure(invalid, DomainErrorCode.EndpointMalformed);
        SecurityTestAssertions.IsFailure(control, DomainErrorCode.EndpointMalformed);
        SecurityTestAssertions.IsFailure(fragment, DomainErrorCode.EndpointFragmentNotAllowed);
        SecurityTestAssertions.DoesNotContainSensitive(fragment.ToString(), fragmentValue);
    }

    [TestMethod]
    public void InvalidDisplayAndCredentialUnicodeUseTypedErrors()
    {
        string invalidUtf16 = "value\ud800";
        string controlled = "value\0";

        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.PrepareRemotePlaylist(
                invalidUtf16,
                "https://example.test/list"),
            DomainErrorCode.SourceNameInvalid);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.PrepareRemotePlaylist(
                controlled,
                "https://example.test/list"),
            DomainErrorCode.SourceNameInvalid);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.PrepareXtream(
                "Source",
                "https://example.test/api",
                invalidUtf16,
                "password"),
            DomainErrorCode.CredentialInvalid);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.PrepareXtream(
                "Source",
                "https://example.test/api",
                "user",
                controlled),
            DomainErrorCode.CredentialInvalid);
    }

    [TestMethod]
    public void XtreamUserInfoIsRejectedAndNeverEchoed()
    {
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("USERINFO");
        string locator = $"https://user:{sensitiveValue}@example.test/api";

        DomainResult<PreparedXtreamSourceDraft> result = SourceConfigurationValidator.PrepareXtream(
            "Source",
            locator,
            "user",
            sensitiveValue);

        SecurityTestAssertions.IsFailure(result, DomainErrorCode.EndpointUserInfoNotAllowed);
        SecurityTestAssertions.DoesNotContainSensitive(result.ToString(), sensitiveValue);
    }

    [TestMethod]
    public void AcceptedXtreamPathAndQueryNeverReachConfigurationOutput()
    {
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("XTREAM-QUERY");
        string locator = $"https://example.test/private/{sensitiveValue}?token={sensitiveValue}";

        DomainResult<PreparedXtreamSourceDraft> result = SourceConfigurationValidator.PrepareXtream(
            "Source",
            locator,
            "user",
            sensitiveValue);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("example.test", result.Value!.SafeEndpoint.Host);
        SecurityTestAssertions.DoesNotContainSensitive(
            JsonSerializer.Serialize(result.Value),
            sensitiveValue);
    }

    [TestMethod]
    public void RemoteLocatorPreparationRetainsOnlySafeEndpoint()
    {
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("REMOTE-LOCATOR");
        string locator = $"https://user:{sensitiveValue}@example.test/private/{sensitiveValue}?key={sensitiveValue}";
        DomainResult<PreparedRemotePlaylistSourceDraft> result =
            SourceConfigurationValidator.PrepareRemotePlaylist("Source", locator);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("example.test", result.Value!.SafeEndpoint.Host);
        SecurityTestAssertions.DoesNotContainSensitive(
            JsonSerializer.Serialize(result.Value),
            sensitiveValue);
    }

    [TestMethod]
    public void MissingCredentialsUseStableTypedErrors()
    {
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.PrepareXtream(
                "Source",
                "https://example.test/api",
                null,
                "password"),
            DomainErrorCode.UsernameRequired);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.PrepareXtream(
                "Source",
                "https://example.test/api",
                "user",
                null),
            DomainErrorCode.PasswordRequired);
    }

    private static SafeEndpoint GetRemoteEndpoint(string locator)
    {
        DomainResult<PreparedRemotePlaylistSourceDraft> result =
            SourceConfigurationValidator.PrepareRemotePlaylist("Source", locator);
        Assert.IsTrue(result.IsSuccess, "A safe endpoint fixture was rejected.");
        return result.Value!.SafeEndpoint;
    }
}
