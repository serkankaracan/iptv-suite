using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class SourceConfigurationProtectedRecordDeletionIntegrationTests
{
    [TestMethod]
    public async Task SameSourceConfigurationsDeleteOnlyTheirAuthoritativeRecord()
    {
        using var store = new M4InMemorySecretStore();
        var protection = new SourceDraftProtectionService(store);
        var deletion = new SourceConfigurationProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();

        DomainResult<ValidatedSourceDraft> firstDraft = await protection.ProtectRemotePlaylistAsync(
            sourceId,
            "Synthetic Source One",
            "https://fixtures.invalid/one.m3u?fixture=one");
        DomainResult<ValidatedSourceDraft> secondDraft = await protection.ProtectRemotePlaylistAsync(
            sourceId,
            "Synthetic Source Two",
            "https://fixtures.invalid/two.m3u?fixture=two");

        Assert.IsTrue(firstDraft.IsSuccess);
        Assert.IsTrue(secondDraft.IsSuccess);
        ContentSource first = CreateDeletionPendingSource(firstDraft.Value!);
        ContentSource second = CreateDeletionPendingSource(secondDraft.Value!);
        var firstConfiguration = (RemotePlaylistSourceConfiguration)first.Configuration;
        var secondConfiguration = (RemotePlaylistSourceConfiguration)second.Configuration;
        Assert.AreEqual(2, store.ActiveRecordCount);

        SecretStoreOperationResult firstDelete = await deletion.DeleteAsync(first);
        SecretStoreOperationResult repeatedDelete = await deletion.DeleteAsync(first);

        Assert.IsTrue(firstDelete.IsSuccess);
        Assert.IsTrue(repeatedDelete.IsSuccess);
        Assert.AreEqual(1, store.ActiveRecordCount);

        SecretStoreReadResult deletedRead = await store.ReadLocatorAsync(
            first.Id,
            ProtectedValuePurpose.RemotePlaylistLocator,
            ProtectedRecordOwner.ForSourceConfiguration(firstConfiguration.ConfigurationId),
            firstConfiguration.LocatorReference);
        Assert.IsFalse(deletedRead.IsSuccess);
        Assert.AreEqual(SecretStoreFailure.ProtectedRecordUnavailable, deletedRead.Failure);
        Assert.IsNull(deletedRead.Lease);

        SecretStoreReadResult survivingRead = await store.ReadLocatorAsync(
            second.Id,
            ProtectedValuePurpose.RemotePlaylistLocator,
            ProtectedRecordOwner.ForSourceConfiguration(secondConfiguration.ConfigurationId),
            secondConfiguration.LocatorReference);
        Assert.IsTrue(survivingRead.IsSuccess);
        Assert.IsNotNull(survivingRead.Lease);
        survivingRead.Lease.Dispose();

        Assert.IsTrue((await deletion.DeleteAsync(second)).IsSuccess);
        Assert.AreEqual(0, store.ActiveRecordCount);
        Assert.IsTrue(store.RetiredBuffersAreZeroed);
    }

    private static ContentSource CreateDeletionPendingSource(ValidatedSourceDraft draft)
    {
        DateTimeOffset instant = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
        DomainResult<ContentSource> result = ContentSource.Create(
            draft,
            ContentSourceStatus.DeletionPending,
            instant,
            instant);

        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A deletion-pending test source could not be created.");
    }
}
