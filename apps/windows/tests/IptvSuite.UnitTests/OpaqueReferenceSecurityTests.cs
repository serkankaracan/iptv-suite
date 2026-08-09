using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class OpaqueReferenceSecurityTests
{
    [TestMethod]
    public void SecretReferencesAreRandomOpaqueAndJsonRoundTripSafe()
    {
        SecretReference first = SecretReference.Create();
        SecretReference second = SecretReference.Create();

        Assert.AreNotEqual(first, second);
        Assert.AreEqual("[SECRET-REFERENCE]", first.ToString());

        string serialized = JsonSerializer.Serialize(first);
        SecretReference? restored = JsonSerializer.Deserialize<SecretReference>(serialized);

        Assert.IsNotNull(restored);
        Assert.AreEqual(first, restored);
        Assert.IsTrue(serialized.StartsWith("\"secret-ref-v1:", StringComparison.Ordinal));
        Assert.AreEqual("[SECRET-REFERENCE]", GetDebuggerDisplay<SecretReference>());
    }

    [TestMethod]
    public void ProtectedLocatorReferencesAreRandomOpaqueAndJsonRoundTripSafe()
    {
        ProtectedLocatorReference first = ProtectedLocatorReference.Create();
        ProtectedLocatorReference second = ProtectedLocatorReference.Create();

        Assert.AreNotEqual(first, second);
        Assert.AreEqual("[PROTECTED-LOCATOR-REFERENCE]", first.ToString());

        string serialized = JsonSerializer.Serialize(first);
        ProtectedLocatorReference? restored =
            JsonSerializer.Deserialize<ProtectedLocatorReference>(serialized);

        Assert.IsNotNull(restored);
        Assert.AreEqual(first, restored);
        Assert.IsTrue(serialized.StartsWith("\"locator-ref-v1:", StringComparison.Ordinal));
        Assert.AreEqual(
            "[PROTECTED-LOCATOR-REFERENCE]",
            GetDebuggerDisplay<ProtectedLocatorReference>());
    }

    [TestMethod]
    public void InvalidOpaqueIdentifiersFailWithoutEchoingInput()
    {
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("REFERENCE-PARSE");

        DomainResult<SecretReference> secret = SecretReference.Parse(sensitiveValue);
        DomainResult<ProtectedLocatorReference> locator =
            ProtectedLocatorReference.Parse(sensitiveValue);

        SecurityTestAssertions.IsFailure(secret, DomainErrorCode.SecretReferenceInvalid);
        SecurityTestAssertions.IsFailure(locator, DomainErrorCode.SecretReferenceInvalid);
        SecurityTestAssertions.DoesNotContainSensitive(secret.ToString(), sensitiveValue);
        SecurityTestAssertions.DoesNotContainSensitive(locator.ToString(), sensitiveValue);
    }

    [TestMethod]
    public void InvalidReferenceJsonThrowsOnlyConstantSafeMessages()
    {
        string sensitiveValue = SecurityTestAssertions.CreateSensitiveValue("REFERENCE-JSON");
        string json = JsonSerializer.Serialize(sensitiveValue);

        JsonException secretException = Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<SecretReference>(json));
        JsonException locatorException = Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<ProtectedLocatorReference>(json));

        SecurityTestAssertions.DoesNotContainSensitive(secretException.Message, sensitiveValue);
        SecurityTestAssertions.DoesNotContainSensitive(locatorException.Message, sensitiveValue);
    }

    private static string GetDebuggerDisplay<T>()
    {
        DebuggerDisplayAttribute? attribute = typeof(T).GetCustomAttribute<DebuggerDisplayAttribute>();
        Assert.IsNotNull(attribute);
        return attribute.Value;
    }
}
