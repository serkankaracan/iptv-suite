using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class RedirectTargetPolicyTests
{
    [TestMethod]
    public void SameOriginHttpsRedirectPreservesCredentialPolicyWithoutReturningRawTarget()
    {
        SafeEndpoint source = GetEndpoint("https://example.test/base");
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("SAME-ORIGIN");
        string target = $"https://example.test:443/private/{sensitiveValue}?token={sensitiveValue}";

        DomainResult<RedirectTargetAssessment> result = RedirectTargetPolicy.Evaluate(source, target);

        Assert.IsTrue(result.IsSuccess);
        RedirectTargetAssessment assessment = result.Value!;
        Assert.AreEqual(RedirectOriginRelation.SameOrigin, assessment.OriginRelation);
        Assert.AreEqual(
            RedirectCredentialPolicy.PreserveForSameOrigin,
            assessment.CredentialPolicy);
        SecurityTestAssertions.DoesNotContainSensitive(
            JsonSerializer.Serialize(assessment),
            sensitiveValue);
    }

    [TestMethod]
    public void CrossOriginHttpsRedirectRequiresCredentialStripping()
    {
        SafeEndpoint source = GetEndpoint("https://example.test/base");

        DomainResult<RedirectTargetAssessment> result = RedirectTargetPolicy.Evaluate(
            source,
            "https://other.test/target");

        Assert.IsTrue(result.IsSuccess);
        RedirectTargetAssessment assessment = result.Value!;
        Assert.AreEqual(RedirectOriginRelation.CrossOrigin, assessment.OriginRelation);
        Assert.AreEqual(RedirectCredentialPolicy.Strip, assessment.CredentialPolicy);
    }

    [TestMethod]
    public void IdnAndAsciiOriginsCompareUsingCanonicalHost()
    {
        SafeEndpoint source = GetEndpoint("https://b\u00fccher.example/base");

        DomainResult<RedirectTargetAssessment> result = RedirectTargetPolicy.Evaluate(
            source,
            "https://xn--bcher-kva.example/target");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(RedirectOriginRelation.SameOrigin, result.Value!.OriginRelation);
    }

    [TestMethod]
    public void HttpRedirectsStayOnOriginOrUpgradeToHttps()
    {
        SafeEndpoint source = GetEndpoint(
            "http://example.test/base",
            allowInsecureHttp: true);

        DomainResult<RedirectTargetAssessment> sameOrigin = RedirectTargetPolicy.Evaluate(
            source,
            "http://example.test:80/next?token=synthetic");
        DomainResult<RedirectTargetAssessment> crossOriginHttp = RedirectTargetPolicy.Evaluate(
            source,
            "http://other.test/next");
        DomainResult<RedirectTargetAssessment> httpsUpgrade = RedirectTargetPolicy.Evaluate(
            source,
            "https://other.test/next");

        Assert.IsTrue(sameOrigin.IsSuccess);
        Assert.AreEqual(RedirectOriginRelation.SameOrigin, sameOrigin.Value!.OriginRelation);
        Assert.AreEqual(
            RedirectCredentialPolicy.PreserveForSameOrigin,
            sameOrigin.Value.CredentialPolicy);
        SecurityTestAssertions.IsFailure(
            crossOriginHttp,
            DomainErrorCode.InsecureTransportRejected);
        Assert.IsTrue(httpsUpgrade.IsSuccess);
        Assert.AreEqual(RedirectOriginRelation.CrossOrigin, httpsUpgrade.Value!.OriginRelation);
        Assert.AreEqual(RedirectCredentialPolicy.Strip, httpsUpgrade.Value.CredentialPolicy);
    }

    [TestMethod]
    public void DowngradeUnsupportedSchemeUserInfoAndFragmentAreRejected()
    {
        SafeEndpoint source = GetEndpoint("https://example.test/base");
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("REDIRECT-REJECT");

        DomainResult<RedirectTargetAssessment> downgrade = RedirectTargetPolicy.Evaluate(
            source,
            $"http://example.test/{sensitiveValue}");
        DomainResult<RedirectTargetAssessment> unsupported = RedirectTargetPolicy.Evaluate(
            source,
            $"ftp://example.test/{sensitiveValue}");
        DomainResult<RedirectTargetAssessment> userInfo = RedirectTargetPolicy.Evaluate(
            source,
            $"https://user:{sensitiveValue}@example.test/target");
        DomainResult<RedirectTargetAssessment> fragment = RedirectTargetPolicy.Evaluate(
            source,
            $"https://example.test/target#{sensitiveValue}");

        SecurityTestAssertions.IsFailure(downgrade, DomainErrorCode.InsecureTransportRejected);
        SecurityTestAssertions.IsFailure(unsupported, DomainErrorCode.EndpointSchemeUnsupported);
        SecurityTestAssertions.IsFailure(userInfo, DomainErrorCode.EndpointUserInfoNotAllowed);
        SecurityTestAssertions.IsFailure(fragment, DomainErrorCode.EndpointFragmentNotAllowed);
        SecurityTestAssertions.DoesNotContainSensitive(downgrade.ToString(), sensitiveValue);
        SecurityTestAssertions.DoesNotContainSensitive(unsupported.ToString(), sensitiveValue);
        SecurityTestAssertions.DoesNotContainSensitive(userInfo.ToString(), sensitiveValue);
        SecurityTestAssertions.DoesNotContainSensitive(fragment.ToString(), sensitiveValue);
    }

    private static SafeEndpoint GetEndpoint(
        string locator,
        bool allowInsecureHttp = false)
    {
        DomainResult<PreparedRemotePlaylistSourceDraft> result =
            allowInsecureHttp
                ? SourceConfigurationValidator.PrepareRemotePlaylistAllowingInsecureHttp(
                    "Source",
                    locator)
                : SourceConfigurationValidator.PrepareRemotePlaylist("Source", locator);
        Assert.IsTrue(result.IsSuccess, "A redirect source fixture was rejected.");
        return result.Value!.SafeEndpoint;
    }
}
