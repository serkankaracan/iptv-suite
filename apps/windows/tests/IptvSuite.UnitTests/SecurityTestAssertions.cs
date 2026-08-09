using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

internal static class SecurityTestAssertions
{
    internal static string CreateSensitiveValue(string caseId)
    {
        TestCanary canary = TestCanary.Create("M3", caseId);
        using MemoryStream stream = new();
        canary.WriteTo(stream, TestCanaryEncoding.Utf8);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static void DoesNotContainSensitive(string? output, params string[] sensitiveValues)
    {
        Assert.IsNotNull(output);
        foreach (string sensitiveValue in sensitiveValues)
        {
            Assert.IsFalse(
                output!.Contains(sensitiveValue, StringComparison.Ordinal),
                "Sensitive test material reached an observable output.");
        }
    }

    internal static void IsFailure<T>(DomainResult<T> result, DomainErrorCode expectedCode)
    {
        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Value);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(expectedCode, result.Error!.Code);
    }
}
