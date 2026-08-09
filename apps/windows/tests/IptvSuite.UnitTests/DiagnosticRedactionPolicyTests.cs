using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class DiagnosticRedactionPolicyTests
{
    [TestMethod]
    public void UriRedactionRemovesUserInfoPathQueryValuesFragmentAndNestedUri()
    {
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("URI-REDACTION");
        string nested = Uri.EscapeDataString($"https://nested.test/private?token={sensitiveValue}");
        string uri = $"https://user:{sensitiveValue}@b\u00fccher.example/private/{sensitiveValue}" +
            $"?to%256Ben={sensitiveValue}&next={nested}#{sensitiveValue}";

        string redacted = DiagnosticRedactionPolicy.RedactUri(uri);

        SecurityTestAssertions.DoesNotContainSensitive(redacted, sensitiveValue, nested);
        Assert.IsTrue(redacted.Contains("host=redacted", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("xn--bcher-kva.example", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("path=redacted", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("query-values=redacted", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("query-count=2", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("secret-parameter-count=1", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("userinfo-removed=True", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("fragment-removed=True", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HeaderRedactionHandlesKnownCustomAndEncodedSecretNames()
    {
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("HEADER-REDACTION");
        string[] secretNames =
        [
            "Authorization",
            "Proxy-Authorization",
            "Cookie",
            "Set-Cookie",
            "X-Api-Key",
            "%41uthorization",
        ];

        foreach (string name in secretNames)
        {
            string redacted = DiagnosticRedactionPolicy.RedactHeader(name, sensitiveValue);
            SecurityTestAssertions.DoesNotContainSensitive(redacted, sensitiveValue);
            Assert.AreEqual("header;classification=secret;value=redacted", redacted);
        }

        string ordinary = DiagnosticRedactionPolicy.RedactHeader("Accept", sensitiveValue);
        SecurityTestAssertions.DoesNotContainSensitive(ordinary, sensitiveValue);
        Assert.AreEqual("header;classification=other;value=redacted", ordinary);
    }

    [TestMethod]
    public void MalformedUriAndUntrustedTextNeverEchoInput()
    {
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("UNTRUSTED-TEXT");
        string malformed = sensitiveValue + "\ud800";

        string redactedUri = DiagnosticRedactionPolicy.RedactUri(malformed);
        string redactedText = DiagnosticRedactionPolicy.RedactUntrustedText(malformed);

        SecurityTestAssertions.DoesNotContainSensitive(redactedUri, sensitiveValue);
        SecurityTestAssertions.DoesNotContainSensitive(redactedText, sensitiveValue);
        Assert.IsTrue(redactedUri.StartsWith("uri;status=invalid", StringComparison.Ordinal));
        Assert.IsTrue(redactedText.StartsWith("text;status=invalid-redacted", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AllQueryValuesAreRedactedEvenWhenNamesLookOrdinary()
    {
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("ORDINARY-QUERY");
        string uri = $"https://example.test/list?language={sensitiveValue}&page={sensitiveValue}";

        string redacted = DiagnosticRedactionPolicy.RedactUri(uri);

        SecurityTestAssertions.DoesNotContainSensitive(redacted, sensitiveValue);
        Assert.IsTrue(redacted.Contains("query-count=2", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("secret-parameter-count=0", StringComparison.Ordinal));

        string astralPath = string.Concat(Enumerable.Repeat("\U0001F600", 2_049));
        string scalarBounded = DiagnosticRedactionPolicy.RedactUri(
            $"https://example.test/{astralPath}");
        Assert.IsTrue(scalarBounded.Contains("status=redacted", StringComparison.Ordinal));
    }
}
