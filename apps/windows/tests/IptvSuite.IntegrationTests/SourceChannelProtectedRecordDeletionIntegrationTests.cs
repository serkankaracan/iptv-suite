using System.Security.Cryptography;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class SourceChannelProtectedRecordDeletionIntegrationTests
{
    private static readonly DateTimeOffset FixedInstant =
        new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TargetChannelDeletionPreservesSiblingAndConfigurationRecords()
    {
        using var store = new M4InMemorySecretStore();
        var sourceProtection = new SourceDraftProtectionService(store);
        var channelDeletion = new SourceChannelProtectedRecordDeletionService(store);
        var configurationDeletion = new SourceConfigurationProtectedRecordDeletionService(store);
        SourceId sourceId = SourceId.Generate();

        DomainResult<ValidatedSourceDraft> draft = await sourceProtection.ProtectRemotePlaylistAsync(
            sourceId,
            "Synthetic Channel Deletion Source",
            "https://fixtures.invalid/channel-deletion.m3u?fixture=owned");
        Assert.IsTrue(draft.IsSuccess);
        ContentSource source = CreateDeletionPendingSource(draft.Value!);
        var configuration = (RemotePlaylistSourceConfiguration)source.Configuration;
        PlaylistSnapshot snapshot = CreateSnapshot(sourceId);

        ChannelId targetId = ChannelId.Generate();
        ChannelId siblingId = ChannelId.Generate();
        ProtectedLocatorReference targetStream = await CreateLocatorAsync(
            store,
            sourceId,
            ProtectedValuePurpose.ChannelStreamLocator,
            targetId,
            "synthetic-target-stream");
        ProtectedLocatorReference targetLogo = await CreateLocatorAsync(
            store,
            sourceId,
            ProtectedValuePurpose.ChannelLogoLocator,
            targetId,
            "synthetic-target-logo");
        ProtectedLocatorReference siblingStream = await CreateLocatorAsync(
            store,
            sourceId,
            ProtectedValuePurpose.ChannelStreamLocator,
            siblingId,
            "synthetic-sibling-stream");
        ProtectedLocatorReference siblingLogo = await CreateLocatorAsync(
            store,
            sourceId,
            ProtectedValuePurpose.ChannelLogoLocator,
            siblingId,
            "synthetic-sibling-logo");
        LiveChannel target = CreateChannel(
            sourceId,
            snapshot.Id,
            targetId,
            targetStream,
            targetLogo,
            "Synthetic Target Channel");
        LiveChannel sibling = CreateChannel(
            sourceId,
            snapshot.Id,
            siblingId,
            siblingStream,
            siblingLogo,
            "Synthetic Sibling Channel");
        Assert.AreEqual(5, store.ActiveRecordCount);

        SecretStoreOperationResult firstDelete = await channelDeletion.DeleteAsync(
            source,
            snapshot,
            target);
        SecretStoreOperationResult repeatedDelete = await channelDeletion.DeleteAsync(
            source,
            snapshot,
            target);

        Assert.IsTrue(firstDelete.IsSuccess);
        Assert.IsTrue(repeatedDelete.IsSuccess);
        Assert.AreEqual(3, store.ActiveRecordCount);
        await AssertLocatorUnavailableAsync(
            store,
            sourceId,
            ProtectedValuePurpose.ChannelStreamLocator,
            targetId,
            targetStream);
        await AssertLocatorUnavailableAsync(
            store,
            sourceId,
            ProtectedValuePurpose.ChannelLogoLocator,
            targetId,
            targetLogo);
        await AssertLocatorReadableAsync(
            store,
            sourceId,
            ProtectedValuePurpose.ChannelStreamLocator,
            siblingId,
            siblingStream);
        await AssertLocatorReadableAsync(
            store,
            sourceId,
            ProtectedValuePurpose.ChannelLogoLocator,
            siblingId,
            siblingLogo);

        SecretStoreReadResult configurationRead = await store.ReadLocatorAsync(
            sourceId,
            ProtectedValuePurpose.RemotePlaylistLocator,
            ProtectedRecordOwner.ForSourceConfiguration(configuration.ConfigurationId),
            configuration.LocatorReference);
        Assert.IsTrue(configurationRead.IsSuccess);
        Assert.IsNotNull(configurationRead.Lease);
        configurationRead.Lease.Dispose();

        Assert.IsTrue((await channelDeletion.DeleteAsync(source, snapshot, sibling)).IsSuccess);
        Assert.IsTrue((await configurationDeletion.DeleteAsync(source)).IsSuccess);
        Assert.AreEqual(0, store.ActiveRecordCount);
        Assert.IsTrue(store.RetiredBuffersAreZeroed);
    }

    private static async Task<ProtectedLocatorReference> CreateLocatorAsync(
        M4InMemorySecretStore store,
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ChannelId channelId,
        string syntheticValue)
    {
        byte[] value = Encoding.UTF8.GetBytes(syntheticValue);
        try
        {
            ProtectedLocatorReferenceCreationResult result = await store.CreateLocatorAsync(
                sourceId,
                purpose,
                ProtectedRecordOwner.ForChannel(channelId),
                value);
            Assert.IsTrue(result.IsSuccess);
            Assert.IsNotNull(result.Reference);
            return result.Reference;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static ContentSource CreateDeletionPendingSource(ValidatedSourceDraft draft)
    {
        DomainResult<ContentSource> result = ContentSource.Create(
            draft,
            ContentSourceStatus.DeletionPending,
            FixedInstant,
            FixedInstant);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A deletion-pending source could not be created.");
    }

    private static PlaylistSnapshot CreateSnapshot(SourceId sourceId)
    {
        DomainResult<PlaylistSnapshot> result = PlaylistSnapshot.Create(
            SnapshotId.Generate(),
            sourceId,
            FixedInstant,
            new string('b', 64),
            parserVersion: 1,
            normalizationVersion: 1,
            schemaVersion: 1,
            itemCount: 2,
            warningCount: 0,
            PlaylistSnapshotState.Complete);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic playlist snapshot could not be created.");
    }

    private static LiveChannel CreateChannel(
        SourceId sourceId,
        SnapshotId snapshotId,
        ChannelId channelId,
        ProtectedLocatorReference streamReference,
        ProtectedLocatorReference logoReference,
        string name)
    {
        DomainResult<ChannelStableKey> stableKey = ChannelStableKeyBuilder.FromProviderStreamId(
            sourceId,
            "synthetic-provider",
            channelId.Value.ToString("N"));
        Assert.IsTrue(stableKey.IsSuccess);
        DomainResult<LiveChannel> result = LiveChannel.Create(
            channelId,
            stableKey.Value,
            snapshotId,
            CategoryId.Generate(),
            providerKey: null,
            providerPlaybackKey: null,
            name,
            number: null,
            logoReference,
            streamReference,
            ChannelContainerHint.Hls,
            isAdultHint: null,
            ChannelNormalizationWarnings.None);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic live channel could not be created.");
    }

    private static async Task AssertLocatorUnavailableAsync(
        M4InMemorySecretStore store,
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ChannelId channelId,
        ProtectedLocatorReference reference)
    {
        SecretStoreReadResult result = await store.ReadLocatorAsync(
            sourceId,
            purpose,
            ProtectedRecordOwner.ForChannel(channelId),
            reference);
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SecretStoreFailure.ProtectedRecordUnavailable, result.Failure);
        Assert.IsNull(result.Lease);
    }

    private static async Task AssertLocatorReadableAsync(
        M4InMemorySecretStore store,
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ChannelId channelId,
        ProtectedLocatorReference reference)
    {
        SecretStoreReadResult result = await store.ReadLocatorAsync(
            sourceId,
            purpose,
            ProtectedRecordOwner.ForChannel(channelId),
            reference);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Lease);
        result.Lease.Dispose();
    }
}
