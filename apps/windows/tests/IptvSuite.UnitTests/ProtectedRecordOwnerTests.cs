using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class ProtectedRecordOwnerTests
{
    [TestMethod]
    public void OwnerFactoriesRejectEmptyIdentifiersAndPreserveTypedIdentity()
    {
        Guid identifier = Guid.Parse("447b9510-4822-465b-badf-26ea6dad4fc2");
        SourceConfigurationId configurationId = SourceConfigurationId.Create(identifier).Value;
        ChannelId channelId = ChannelId.Create(identifier).Value;

        ProtectedRecordOwner configurationOwner =
            ProtectedRecordOwner.ForSourceConfiguration(configurationId);
        ProtectedRecordOwner channelOwner = ProtectedRecordOwner.ForChannel(channelId);

        Assert.IsFalse(configurationOwner.IsEmpty);
        Assert.IsFalse(channelOwner.IsEmpty);
        Assert.AreEqual(ProtectedRecordOwnerKind.SourceConfiguration, configurationOwner.Kind);
        Assert.AreEqual(ProtectedRecordOwnerKind.Channel, channelOwner.Kind);
        Assert.AreNotEqual(configurationOwner, channelOwner);
        Assert.IsTrue(default(ProtectedRecordOwner).IsEmpty);
        Assert.ThrowsExactly<ArgumentException>(() =>
            ProtectedRecordOwner.ForSourceConfiguration(default));
        Assert.ThrowsExactly<ArgumentException>(() => ProtectedRecordOwner.ForChannel(default));
    }

    [TestMethod]
    public void OwnerIdentifierIsHiddenFromToStringDebuggerAndJsonSurfaces()
    {
        Guid identifier = Guid.Parse("9ee4345e-9017-435e-bad4-6a77be96bc75");
        SourceConfigurationId configurationId = SourceConfigurationId.Create(identifier).Value;
        ProtectedRecordOwner owner = ProtectedRecordOwner.ForSourceConfiguration(configurationId);
        string observable = string.Join('|', owner, JsonSerializer.Serialize(owner));
        DebuggerDisplayAttribute? debuggerDisplay = typeof(ProtectedRecordOwner)
            .GetCustomAttribute<DebuggerDisplayAttribute>();

        Assert.AreEqual("[PROTECTED-RECORD-OWNER]", owner.ToString());
        Assert.AreEqual("[PROTECTED-RECORD-OWNER]", debuggerDisplay?.Value);
        Assert.IsFalse(observable.Contains(identifier.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(observable.Contains(identifier.ToString("N"), StringComparison.OrdinalIgnoreCase));
        Assert.IsNull(typeof(ProtectedRecordOwner).GetProperty(
            "Identifier",
            BindingFlags.Instance | BindingFlags.Public));
    }

    [TestMethod]
    public void EverySecretStoreOperationRequiresOneSemanticOwner()
    {
        Dictionary<string, int> expectedOwnerIndexes = new(StringComparer.Ordinal)
        {
            [nameof(ISecretStore.CreateCredentialsAsync)] = 1,
            [nameof(ISecretStore.CreateLocatorAsync)] = 2,
            [nameof(ISecretStore.ReadCredentialsAsync)] = 1,
            [nameof(ISecretStore.ReadLocatorAsync)] = 2,
            [nameof(ISecretStore.UpdateCredentialsAsync)] = 1,
            [nameof(ISecretStore.UpdateLocatorAsync)] = 2,
            [nameof(ISecretStore.DeleteCredentialsAsync)] = 1,
            [nameof(ISecretStore.DeleteLocatorAsync)] = 2,
        };

        MethodInfo[] operations = typeof(ISecretStore).GetMethods(BindingFlags.Instance | BindingFlags.Public);
        Assert.AreEqual(expectedOwnerIndexes.Count, operations.Length);

        foreach (MethodInfo operation in operations)
        {
            ParameterInfo[] parameters = operation.GetParameters();
            Assert.IsTrue(expectedOwnerIndexes.TryGetValue(operation.Name, out int ownerIndex));
            Assert.AreEqual(typeof(ProtectedRecordOwner), parameters[ownerIndex].ParameterType, operation.Name);
            Assert.AreEqual(
                1,
                parameters.Count(parameter => parameter.ParameterType == typeof(ProtectedRecordOwner)),
                operation.Name);
        }
    }

    [TestMethod]
    public void SameSourcePurposeAndReferenceRemainDistinctAcrossConfigurationOwners()
    {
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner firstOwner = ProtectedRecordOwner.ForSourceConfiguration(
            SourceConfigurationId.Generate());
        ProtectedRecordOwner secondOwner = ProtectedRecordOwner.ForSourceConfiguration(
            SourceConfigurationId.Generate());
        SecretReference credentialReference = SourceDraftTestFixtures.CreateSecretReference();
        ProtectedLocatorReference locatorReference = SourceDraftTestFixtures.CreateLocatorReference();

        object firstCredentialsKey = InvokeSecretStoreKeyFactory(
            "ForCredentials",
            sourceId,
            firstOwner,
            credentialReference);
        object secondCredentialsKey = InvokeSecretStoreKeyFactory(
            "ForCredentials",
            sourceId,
            secondOwner,
            credentialReference);
        object firstLocatorKey = InvokeSecretStoreKeyFactory(
            "ForLocator",
            sourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            firstOwner,
            locatorReference);
        object secondLocatorKey = InvokeSecretStoreKeyFactory(
            "ForLocator",
            sourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            secondOwner,
            locatorReference);

        Assert.AreNotEqual(firstOwner, secondOwner);
        Assert.AreNotEqual(firstCredentialsKey, secondCredentialsKey);
        Assert.AreNotEqual(firstLocatorKey, secondLocatorKey);
        Assert.AreEqual("[PROTECTED-STORE-KEY]", firstCredentialsKey.ToString());
        Assert.AreEqual("[PROTECTED-STORE-KEY]", firstLocatorKey.ToString());
    }

    [TestMethod]
    public void SecretStoreKeyRejectsEveryPurposeOwnerKindMismatch()
    {
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner configurationOwner = ProtectedRecordOwner.ForSourceConfiguration(
            SourceConfigurationId.Generate());
        ProtectedRecordOwner channelOwner = ProtectedRecordOwner.ForChannel(ChannelId.Generate());

        _ = InvokeSecretStoreKeyFactory("IssueCredentials", sourceId, configurationOwner);
        _ = InvokeSecretStoreKeyFactory(
            "IssueLocator",
            sourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            configurationOwner);
        _ = InvokeSecretStoreKeyFactory(
            "IssueLocator",
            sourceId,
            ProtectedValuePurpose.ChannelStreamLocator,
            channelOwner);
        _ = InvokeSecretStoreKeyFactory(
            "IssueLocator",
            sourceId,
            ProtectedValuePurpose.ChannelLogoLocator,
            channelOwner);

        AssertOwnerMismatch("IssueCredentials", sourceId, channelOwner);
        AssertOwnerMismatch(
            "IssueLocator",
            sourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            channelOwner);
        AssertOwnerMismatch(
            "IssueLocator",
            sourceId,
            ProtectedValuePurpose.ChannelStreamLocator,
            configurationOwner);
        AssertOwnerMismatch(
            "IssueLocator",
            sourceId,
            ProtectedValuePurpose.ChannelLogoLocator,
            configurationOwner);
        AssertOwnerMismatch("IssueCredentials", sourceId, default(ProtectedRecordOwner));
    }

    private static void AssertOwnerMismatch(string methodName, params object?[] parameters)
    {
        TargetInvocationException exception = Assert.ThrowsExactly<TargetInvocationException>(() =>
            InvokeSecretStoreKeyFactory(methodName, parameters));
        Assert.IsInstanceOfType<ArgumentException>(exception.InnerException);
    }

    private static object InvokeSecretStoreKeyFactory(string methodName, params object?[] parameters)
    {
        Type keyType = typeof(SourceDraftProtectionService).Assembly.GetType(
            "IptvSuite.Application.SecretStoreKey",
            throwOnError: true)!;
        MethodInfo method = keyType.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The protected-store key factory is unavailable.");
        return method.Invoke(null, parameters)
            ?? throw new InvalidOperationException("The protected-store key factory returned no value.");
    }
}
