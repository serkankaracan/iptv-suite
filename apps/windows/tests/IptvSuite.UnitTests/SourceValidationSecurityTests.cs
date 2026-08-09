using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class SourceValidationSecurityTests
{
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
    public void DisplayNameIsTrimmedNormalizedAndCountedByUnicodeScalar()
    {
        DomainResult<ValidatedSourceDraft> normalized = SourceConfigurationValidator.ValidateRemotePlaylist(
            "  Cafe\u0301  ",
            "https://example.test/list.m3u",
            ProtectedLocatorReference.Create());

        Assert.IsTrue(normalized.IsSuccess);
        Assert.AreEqual("Caf\u00e9", normalized.Value!.NormalizedDisplayName);

        string oneScalar = "\U0001F600";
        string maximum = string.Concat(Enumerable.Repeat(oneScalar, 100));
        string oversized = maximum + oneScalar;

        Assert.IsTrue(SourceConfigurationValidator.ValidateRemotePlaylist(
            maximum,
            "https://example.test/list.m3u",
            ProtectedLocatorReference.Create()).IsSuccess);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateRemotePlaylist(
                oversized,
                "https://example.test/list.m3u",
                ProtectedLocatorReference.Create()),
            DomainErrorCode.SourceNameTooLong);
    }

    [TestMethod]
    public void LocatorAndCredentialBoundsAreExact()
    {
        const string prefix = "https://example.test/";
        string maximumLocator = prefix + new string('a', 4096 - prefix.Length);
        string oversizedLocator = maximumLocator + "a";

        Assert.IsTrue(SourceConfigurationValidator.ValidateRemotePlaylist(
            "Source",
            maximumLocator,
            ProtectedLocatorReference.Create()).IsSuccess);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateRemotePlaylist(
                "Source",
                oversizedLocator,
                ProtectedLocatorReference.Create()),
            DomainErrorCode.EndpointTooLong);

        string maximumUsername = new('u', 256);
        string maximumPassword = new('p', 1024);
        Assert.IsTrue(SourceConfigurationValidator.ValidateXtream(
            "Source",
            "https://example.test/api",
            maximumUsername,
            maximumPassword,
            SecretReference.Create()).IsSuccess);

        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateXtream(
                "Source",
                "https://example.test/api",
                maximumUsername + "u",
                maximumPassword,
                SecretReference.Create()),
            DomainErrorCode.CredentialTooLong);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateXtream(
                "Source",
                "https://example.test/api",
                maximumUsername,
                maximumPassword + "p",
                SecretReference.Create()),
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
        ProtectedLocatorReference locatorReference = ProtectedLocatorReference.Create();
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateRemotePlaylist(
                "Source",
                "ftp://example.test/list",
                locatorReference),
            DomainErrorCode.EndpointSchemeUnsupported);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateRemotePlaylist(
                "Source",
                "http://example.test/list",
                locatorReference),
            DomainErrorCode.InsecureTransportRejected);
    }

    [TestMethod]
    public void InvalidUtf16ControlCharactersAndFragmentsAreRejectedWithoutEcho()
    {
        string invalidUtf16 = "https://example.test/\ud800";
        string controlled = "https://example.test/list\0";
        string fragmentValue = SecurityTestAssertions.CreateSensitiveValue("FRAGMENT");
        string withFragment = $"https://example.test/list#{fragmentValue}";

        DomainResult<ValidatedSourceDraft> invalid = SourceConfigurationValidator.ValidateRemotePlaylist(
            "Source",
            invalidUtf16,
            ProtectedLocatorReference.Create());
        DomainResult<ValidatedSourceDraft> control = SourceConfigurationValidator.ValidateRemotePlaylist(
            "Source",
            controlled,
            ProtectedLocatorReference.Create());
        DomainResult<ValidatedSourceDraft> fragment = SourceConfigurationValidator.ValidateRemotePlaylist(
            "Source",
            withFragment,
            ProtectedLocatorReference.Create());

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
            SourceConfigurationValidator.ValidateRemotePlaylist(
                invalidUtf16,
                "https://example.test/list",
                ProtectedLocatorReference.Create()),
            DomainErrorCode.SourceNameInvalid);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateRemotePlaylist(
                controlled,
                "https://example.test/list",
                ProtectedLocatorReference.Create()),
            DomainErrorCode.SourceNameInvalid);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateXtream(
                "Source",
                "https://example.test/api",
                invalidUtf16,
                "password",
                SecretReference.Create()),
            DomainErrorCode.CredentialInvalid);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateXtream(
                "Source",
                "https://example.test/api",
                "user",
                controlled,
                SecretReference.Create()),
            DomainErrorCode.CredentialInvalid);
    }

    [TestMethod]
    public void XtreamUserInfoIsRejectedAndNeverEchoed()
    {
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("USERINFO");
        string locator = $"https://user:{sensitiveValue}@example.test/api";

        DomainResult<ValidatedSourceDraft> result = SourceConfigurationValidator.ValidateXtream(
            "Source",
            locator,
            "user",
            sensitiveValue,
            SecretReference.Create());

        SecurityTestAssertions.IsFailure(result, DomainErrorCode.EndpointUserInfoNotAllowed);
        SecurityTestAssertions.DoesNotContainSensitive(result.ToString(), sensitiveValue);
    }

    [TestMethod]
    public void AcceptedXtreamPathAndQueryNeverReachConfigurationOutput()
    {
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("XTREAM-QUERY");
        string locator = $"https://example.test/private/{sensitiveValue}?token={sensitiveValue}";

        DomainResult<ValidatedSourceDraft> result = SourceConfigurationValidator.ValidateXtream(
            "Source",
            locator,
            "user",
            sensitiveValue,
            SecretReference.Create());

        Assert.IsTrue(result.IsSuccess);
        XtreamSourceConfiguration? configuration =
            result.Value!.Configuration as XtreamSourceConfiguration;
        Assert.IsNotNull(configuration);
        Assert.AreEqual("example.test", configuration!.SafeEndpoint.Host);
        SecurityTestAssertions.DoesNotContainSensitive(
            JsonSerializer.Serialize(configuration),
            sensitiveValue);
    }

    [TestMethod]
    public void RemoteLocatorIsRepresentedOnlyByProtectedReference()
    {
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("REMOTE-LOCATOR");
        string locator = $"https://user:{sensitiveValue}@example.test/private/{sensitiveValue}?key={sensitiveValue}";
        ProtectedLocatorReference locatorReference = ProtectedLocatorReference.Create();

        DomainResult<ValidatedSourceDraft> result = SourceConfigurationValidator.ValidateRemotePlaylist(
            "Source",
            locator,
            locatorReference);

        Assert.IsTrue(result.IsSuccess);
        RemotePlaylistSourceConfiguration? configuration =
            result.Value!.Configuration as RemotePlaylistSourceConfiguration;
        Assert.IsNotNull(configuration);
        Assert.AreEqual(locatorReference, configuration!.LocatorReference);
        Assert.AreEqual("example.test", configuration.SafeEndpoint.Host);
        SecurityTestAssertions.DoesNotContainSensitive(
            JsonSerializer.Serialize(configuration),
            sensitiveValue);
    }

    [TestMethod]
    public void MissingReferencesAndCredentialsUseStableTypedErrors()
    {
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateXtream(
                "Source",
                "https://example.test/api",
                null,
                "password",
                SecretReference.Create()),
            DomainErrorCode.UsernameRequired);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateXtream(
                "Source",
                "https://example.test/api",
                "user",
                null,
                SecretReference.Create()),
            DomainErrorCode.PasswordRequired);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateXtream(
                "Source",
                "https://example.test/api",
                "user",
                "password",
                null),
            DomainErrorCode.SecretReferenceInvalid);
        SecurityTestAssertions.IsFailure(
            SourceConfigurationValidator.ValidateRemotePlaylist(
                "Source",
                "https://example.test/list",
                null),
            DomainErrorCode.SecretReferenceInvalid);
    }

    private static SafeEndpoint GetRemoteEndpoint(string locator)
    {
        DomainResult<ValidatedSourceDraft> result = SourceConfigurationValidator.ValidateRemotePlaylist(
            "Source",
            locator,
            ProtectedLocatorReference.Create());
        Assert.IsTrue(result.IsSuccess, "A safe endpoint fixture was rejected.");
        return result.Value!.Configuration.SafeEndpoint;
    }
}
