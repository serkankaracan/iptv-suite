using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class M4SecretStoreContractTests
{
    [TestMethod]
    [Timeout(10_000)]
    public async Task CredentialsFollowCloneUpdateReadAndIdempotentDeleteContract()
    {
        using M4InMemorySecretStore fake = new();
        M4InMemorySecretStore store = fake;
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateSourceConfigurationOwner();
        byte[] initialInput = CreateSensitivePayload();
        byte[] initialExpected = initialInput.ToArray();
        byte[] updatedInput = CreateUpdatedPayload();
        byte[] updatedExpected = updatedInput.ToArray();

        try
        {
            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(
                sourceId,
                recordOwner,
                initialInput);
            Assert.IsTrue(created.IsSuccess);
            Assert.IsNotNull(created.Reference);

            CryptographicOperations.ZeroMemory(initialInput);
            SecretStoreReadResult initialRead = await store.ReadCredentialsAsync(
                sourceId,
                recordOwner,
                created.Reference);
            Assert.IsTrue(initialRead.IsSuccess);
            Assert.IsNotNull(initialRead.Lease);
            using (initialRead.Lease)
            {
                Assert.IsTrue(Matches(initialExpected, initialRead.Lease.Value.Span));
            }

            SecretStoreOperationResult updated = await store.UpdateCredentialsAsync(
                sourceId,
                recordOwner,
                created.Reference,
                updatedInput);
            Assert.IsTrue(updated.IsSuccess);
            CryptographicOperations.ZeroMemory(updatedInput);

            SecretStoreReadResult updatedRead = await store.ReadCredentialsAsync(
                sourceId,
                recordOwner,
                created.Reference);
            Assert.IsTrue(updatedRead.IsSuccess);
            Assert.IsNotNull(updatedRead.Lease);
            using (updatedRead.Lease)
            {
                Assert.IsTrue(Matches(updatedExpected, updatedRead.Lease.Value.Span));
            }

            Assert.IsTrue((await store.DeleteCredentialsAsync(sourceId, recordOwner, created.Reference)).IsSuccess);
            Assert.IsTrue((await store.DeleteCredentialsAsync(sourceId, recordOwner, created.Reference)).IsSuccess);
            AssertUnavailable(await store.ReadCredentialsAsync(sourceId, recordOwner, created.Reference));
            Assert.AreEqual(0, fake.ActiveRecordCount);
            Assert.IsTrue(fake.RetiredBuffersAreZeroed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(initialInput);
            CryptographicOperations.ZeroMemory(initialExpected);
            CryptographicOperations.ZeroMemory(updatedInput);
            CryptographicOperations.ZeroMemory(updatedExpected);
        }
    }

    [TestMethod]
    [DataRow(ProtectedValuePurpose.RemotePlaylistLocator)]
    [DataRow(ProtectedValuePurpose.ChannelStreamLocator)]
    [DataRow(ProtectedValuePurpose.ChannelLogoLocator)]
    [Timeout(10_000)]
    public async Task EveryLocatorPurposeRoundTripsThroughTheSameContract(ProtectedValuePurpose purpose)
    {
        using M4InMemorySecretStore fake = new();
        M4InMemorySecretStore store = fake;
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateOwner(purpose);
        byte[] payload = CreateSensitivePayload();

        try
        {
            ProtectedLocatorReferenceCreationResult created = await store.CreateLocatorAsync(
                sourceId,
                purpose,
                recordOwner,
                payload);
            Assert.IsTrue(created.IsSuccess);
            Assert.IsNotNull(created.Reference);

            SecretStoreReadResult read = await store.ReadLocatorAsync(
                sourceId,
                purpose,
                recordOwner,
                created.Reference);
            Assert.IsTrue(read.IsSuccess);
            Assert.IsNotNull(read.Lease);
            using (read.Lease)
            {
                Assert.IsTrue(Matches(payload, read.Lease.Value.Span));
            }

            Assert.IsTrue((await store.DeleteLocatorAsync(
                sourceId,
                purpose,
                recordOwner,
                created.Reference)).IsSuccess);
            AssertUnavailable(await store.ReadLocatorAsync(sourceId, purpose, recordOwner, created.Reference));
            Assert.IsTrue(fake.RetiredBuffersAreZeroed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task DisposingReadLeaseZerosOnlyItsOwnedClone()
    {
        using M4InMemorySecretStore fake = new();
        M4InMemorySecretStore store = fake;
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateSourceConfigurationOwner();
        byte[] payload = CreateSensitivePayload();

        try
        {
            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, recordOwner, payload);
            Assert.IsNotNull(created.Reference);
            SecretStoreReadResult read = await store.ReadCredentialsAsync(sourceId, recordOwner, created.Reference);
            Assert.IsNotNull(read.Lease);

            SecretLease lease = read.Lease;
            ReadOnlyMemory<byte> observedClone = lease.Value;
            lease.Dispose();

            Assert.IsTrue(IsZeroed(observedClone.Span));
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = lease.Value);

            SecretStoreReadResult secondRead = await store.ReadCredentialsAsync(
                sourceId,
                recordOwner,
                created.Reference);
            Assert.IsNotNull(secondRead.Lease);
            using (secondRead.Lease)
            {
                Assert.IsTrue(Matches(payload, secondRead.Lease.Value.Span));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task WrongSourcePurposeAndReferenceAreIndistinguishablyUnavailable()
    {
        using M4InMemorySecretStore fake = new();
        M4InMemorySecretStore store = fake;
        SourceId sourceOwner = SourceId.Generate();
        SourceId otherSource = SourceId.Generate();
        ProtectedRecordOwner locatorOwner = CreateSourceConfigurationOwner();
        ProtectedRecordOwner channelOwner = CreateChannelOwner();
        byte[] payload = CreateSensitivePayload();

        try
        {
            ProtectedLocatorReferenceCreationResult locator = await store.CreateLocatorAsync(
                sourceOwner,
                ProtectedValuePurpose.RemotePlaylistLocator,
                locatorOwner,
                payload);
            Assert.IsNotNull(locator.Reference);

            AssertUnavailable(await store.ReadLocatorAsync(
                otherSource,
                ProtectedValuePurpose.RemotePlaylistLocator,
                locatorOwner,
                locator.Reference));
            AssertUnavailable(await store.ReadLocatorAsync(
                sourceOwner,
                ProtectedValuePurpose.ChannelStreamLocator,
                channelOwner,
                locator.Reference));
            AssertUnavailable(await store.ReadLocatorAsync(
                sourceOwner,
                ProtectedValuePurpose.RemotePlaylistLocator,
                locatorOwner,
                CreateLocatorReference()));
            AssertUnavailable(await store.UpdateLocatorAsync(
                otherSource,
                ProtectedValuePurpose.RemotePlaylistLocator,
                locatorOwner,
                locator.Reference,
                payload));

            SecretReferenceCreationResult credentials = await store.CreateCredentialsAsync(
                sourceOwner,
                locatorOwner,
                payload);
            Assert.IsNotNull(credentials.Reference);
            AssertUnavailable(await store.ReadCredentialsAsync(otherSource, locatorOwner, credentials.Reference));
            AssertUnavailable(await store.ReadCredentialsAsync(sourceOwner, locatorOwner, CreateSecretReference()));

            Assert.IsTrue((await store.DeleteLocatorAsync(
                otherSource,
                ProtectedValuePurpose.RemotePlaylistLocator,
                locatorOwner,
                locator.Reference)).IsSuccess);
            SecretStoreReadResult ownerRead = await store.ReadLocatorAsync(
                sourceOwner,
                ProtectedValuePurpose.RemotePlaylistLocator,
                locatorOwner,
                locator.Reference);
            Assert.IsTrue(ownerRead.IsSuccess);
            ownerRead.Lease?.Dispose();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ChannelLocatorPurposeSwapIsUnavailableAndCannotDeleteTheOwnerRecord()
    {
        using M4InMemorySecretStore fake = new();
        M4InMemorySecretStore store = fake;
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner channelOwner = CreateChannelOwner();
        byte[] payload = CreateSensitivePayload();
        byte[] updated = CreateUpdatedPayload();

        try
        {
            ProtectedLocatorReferenceCreationResult created = await store.CreateLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                channelOwner,
                payload);
            Assert.IsNotNull(created.Reference);

            AssertUnavailable(await store.ReadLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelLogoLocator,
                channelOwner,
                created.Reference));
            AssertUnavailable(await store.UpdateLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelLogoLocator,
                channelOwner,
                created.Reference,
                updated));
            Assert.IsTrue((await store.DeleteLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelLogoLocator,
                channelOwner,
                created.Reference)).IsSuccess);
            Assert.IsTrue((await store.DeleteLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelLogoLocator,
                channelOwner,
                created.Reference)).IsSuccess);

            AssertLeaseMatches(
                await store.ReadLocatorAsync(
                    sourceId,
                    ProtectedValuePurpose.ChannelStreamLocator,
                    channelOwner,
                    created.Reference),
                payload);
            Assert.AreEqual(1, fake.ActiveRecordCount);

            Assert.IsTrue((await store.DeleteLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                channelOwner,
                created.Reference)).IsSuccess);
            Assert.AreEqual(0, fake.ActiveRecordCount);
            Assert.IsTrue(fake.RetiredBuffersAreZeroed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(updated);
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task SemanticOwnerBindingRejectsCrossOwnerReadUpdateAndDeleteWithoutAffectingOwner()
    {
        using M4InMemorySecretStore fake = new();
        M4InMemorySecretStore store = fake;
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner configurationOwner = CreateSourceConfigurationOwner();
        ProtectedRecordOwner otherConfigurationOwner = CreateSourceConfigurationOwner();
        ProtectedRecordOwner channelOwner = CreateChannelOwner();
        ProtectedRecordOwner otherChannelOwner = CreateChannelOwner();
        byte[] payload = CreateSensitivePayload();
        byte[] updated = CreateUpdatedPayload();

        try
        {
            SecretReferenceCreationResult credentials = await store.CreateCredentialsAsync(
                sourceId,
                configurationOwner,
                payload);
            Assert.IsNotNull(credentials.Reference);

            AssertUnavailable(await store.ReadCredentialsAsync(
                sourceId,
                otherConfigurationOwner,
                credentials.Reference));
            AssertUnavailable(await store.UpdateCredentialsAsync(
                sourceId,
                otherConfigurationOwner,
                credentials.Reference,
                updated));
            Assert.IsTrue((await store.DeleteCredentialsAsync(
                sourceId,
                otherConfigurationOwner,
                credentials.Reference)).IsSuccess);
            Assert.IsTrue((await store.DeleteCredentialsAsync(
                sourceId,
                otherConfigurationOwner,
                credentials.Reference)).IsSuccess);
            AssertLeaseMatches(
                await store.ReadCredentialsAsync(sourceId, configurationOwner, credentials.Reference),
                payload);

            ProtectedLocatorReferenceCreationResult channelLocator = await store.CreateLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                channelOwner,
                payload);
            Assert.IsNotNull(channelLocator.Reference);

            AssertUnavailable(await store.ReadLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                otherChannelOwner,
                channelLocator.Reference));
            AssertUnavailable(await store.UpdateLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                otherChannelOwner,
                channelLocator.Reference,
                updated));
            Assert.IsTrue((await store.DeleteLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                otherChannelOwner,
                channelLocator.Reference)).IsSuccess);
            AssertLeaseMatches(
                await store.ReadLocatorAsync(
                    sourceId,
                    ProtectedValuePurpose.ChannelStreamLocator,
                    channelOwner,
                    channelLocator.Reference),
                payload);

            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await store.ReadLocatorAsync(
                    sourceId,
                    ProtectedValuePurpose.ChannelStreamLocator,
                    configurationOwner,
                    channelLocator.Reference));
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await store.CreateLocatorAsync(
                    sourceId,
                    ProtectedValuePurpose.RemotePlaylistLocator,
                    channelOwner,
                    payload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(updated);
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task EmptyAndOversizedValuesAreRejectedWithoutMutation()
    {
        using M4InMemorySecretStore fake = new();
        M4InMemorySecretStore store = fake;
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateSourceConfigurationOwner();
        byte[] valid = CreateSensitivePayload();
        byte[] oversized = new byte[SecretStoreLimits.MaxProtectedValueBytes + 1];

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
                await store.CreateCredentialsAsync(sourceId, recordOwner, ReadOnlyMemory<byte>.Empty));
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
                await store.CreateLocatorAsync(
                    sourceId,
                    ProtectedValuePurpose.RemotePlaylistLocator,
                    recordOwner,
                    oversized));
            Assert.AreEqual(0, fake.ActiveRecordCount);

            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, recordOwner, valid);
            Assert.IsNotNull(created.Reference);
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
                await store.UpdateCredentialsAsync(
                    sourceId,
                    recordOwner,
                    created.Reference,
                    ReadOnlyMemory<byte>.Empty));

            SecretStoreReadResult read = await store.ReadCredentialsAsync(sourceId, recordOwner, created.Reference);
            Assert.IsNotNull(read.Lease);
            using (read.Lease)
            {
                Assert.IsTrue(Matches(valid, read.Lease.Value.Span));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(valid);
            CryptographicOperations.ZeroMemory(oversized);
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CancellationNeverMutatesOrDeletesARecord()
    {
        using M4InMemorySecretStore fake = new();
        M4InMemorySecretStore store = fake;
        using CancellationTokenSource cancellation = new();
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateSourceConfigurationOwner();
        byte[] payload = CreateSensitivePayload();
        byte[] updated = CreateUpdatedPayload();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await store.CreateCredentialsAsync(sourceId, recordOwner, payload, cancellation.Token));
            Assert.AreEqual(0, fake.ActiveRecordCount);

            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, recordOwner, payload);
            Assert.IsNotNull(created.Reference);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await store.ReadCredentialsAsync(sourceId, recordOwner, created.Reference, cancellation.Token));
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await store.UpdateCredentialsAsync(
                    sourceId,
                    recordOwner,
                    created.Reference,
                    updated,
                    cancellation.Token));
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await store.DeleteCredentialsAsync(sourceId, recordOwner, created.Reference, cancellation.Token));

            SecretStoreReadResult read = await store.ReadCredentialsAsync(sourceId, recordOwner, created.Reference);
            Assert.IsNotNull(read.Lease);
            using (read.Lease)
            {
                Assert.IsTrue(Matches(payload, read.Lease.Value.Span));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(updated);
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task DeleteIsIdempotentForExistingAndUnknownReferences()
    {
        using M4InMemorySecretStore fake = new();
        M4InMemorySecretStore store = fake;
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner credentialsOwner = CreateSourceConfigurationOwner();
        ProtectedRecordOwner locatorOwner = CreateChannelOwner();
        byte[] payload = CreateSensitivePayload();

        try
        {
            Assert.IsTrue((await store.DeleteCredentialsAsync(
                sourceId,
                credentialsOwner,
                CreateSecretReference())).IsSuccess);

            ProtectedLocatorReferenceCreationResult created = await store.CreateLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelLogoLocator,
                locatorOwner,
                payload);
            Assert.IsNotNull(created.Reference);
            Assert.IsTrue((await store.DeleteLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelLogoLocator,
                locatorOwner,
                created.Reference)).IsSuccess);
            Assert.IsTrue((await store.DeleteLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelLogoLocator,
                locatorOwner,
                created.Reference)).IsSuccess);
            Assert.AreEqual(0, fake.ActiveRecordCount);
            Assert.IsTrue(fake.RetiredBuffersAreZeroed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ObservableRepresentationsNeverEchoSensitiveInputOrSourceIdentifier()
    {
        using M4InMemorySecretStore fake = new();
        M4InMemorySecretStore store = fake;
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateSourceConfigurationOwner();
        byte[] payload = CreateSensitivePayload();

        try
        {
            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, recordOwner, payload);
            Assert.IsNotNull(created.Reference);
            SecretStoreReadResult read = await store.ReadCredentialsAsync(sourceId, recordOwner, created.Reference);
            Assert.IsNotNull(read.Lease);
            using SecretLease lease = read.Lease;

            string[] observable =
            [
                fake.ToString(),
                created.ToString(),
                created.Reference.ToString(),
                read.ToString(),
                lease.ToString(),
                JsonSerializer.Serialize(lease),
                SecretStoreReadResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable).ToString(),
                SecretStoreOperationResult.Failed(SecretStoreFailure.StorageUnavailable).ToString(),
            ];
            string sensitiveText = Encoding.UTF8.GetString(payload);
            string sourceIdentifier = sourceId.ToString();

            Assert.IsFalse(observable.Any(value => value.Contains(sensitiveText, StringComparison.Ordinal)));
            Assert.IsFalse(observable.Any(value => value.Contains(sourceIdentifier, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task ConcurrentReadUpdateAndDeleteConvergeOnDeletedState()
    {
        using M4InMemorySecretStore fake = new();
        M4InMemorySecretStore store = fake;
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateSourceConfigurationOwner();
        byte[] initial = CreateSensitivePayload();
        byte[] updated = CreateUpdatedPayload();

        try
        {
            for (int iteration = 0; iteration < 16; iteration++)
            {
                SecretReferenceCreationResult created = await store.CreateCredentialsAsync(
                    sourceId,
                    recordOwner,
                    initial);
                Assert.IsNotNull(created.Reference);
                var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<SecretStoreReadResult> readTask = RunAfterSignalAsync(
                    start.Task,
                    () => store.ReadCredentialsAsync(sourceId, recordOwner, created.Reference));
                Task<SecretStoreOperationResult> updateTask = RunAfterSignalAsync(
                    start.Task,
                    () => store.UpdateCredentialsAsync(sourceId, recordOwner, created.Reference, updated));
                Task<SecretStoreOperationResult> deleteTask = RunAfterSignalAsync(
                    start.Task,
                    () => store.DeleteCredentialsAsync(sourceId, recordOwner, created.Reference));

                start.SetResult(true);
                SecretStoreReadResult read = await readTask;
                SecretStoreOperationResult update = await updateTask;
                SecretStoreOperationResult delete = await deleteTask;

                Assert.IsTrue(delete.IsSuccess);
                Assert.IsTrue(
                    update.IsSuccess || update.Failure is SecretStoreFailure.ProtectedRecordUnavailable,
                    $"Concurrent update iteration {iteration} returned {update.Failure}.");

                if (read.IsSuccess)
                {
                    Assert.IsNotNull(read.Lease);
                    using (read.Lease)
                    {
                        ReadOnlySpan<byte> observed = read.Lease.Value.Span;
                        Assert.IsTrue(
                            Matches(initial, observed) || Matches(updated, observed),
                            $"Concurrent read iteration {iteration} observed a torn value.");
                    }
                }
                else
                {
                    AssertUnavailable(read);
                }

                AssertUnavailable(await store.ReadCredentialsAsync(sourceId, recordOwner, created.Reference));
                Assert.AreEqual(0, fake.ActiveRecordCount);
            }

            Assert.IsTrue(fake.RetiredBuffersAreZeroed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(initial);
            CryptographicOperations.ZeroMemory(updated);
        }
    }

    private static byte[] CreateSensitivePayload() => CreateCanaryPayload("CONTRACT");

    private static ProtectedRecordOwner CreateOwner(ProtectedValuePurpose purpose) =>
        purpose is ProtectedValuePurpose.SourceCredentials or ProtectedValuePurpose.RemotePlaylistLocator
            ? CreateSourceConfigurationOwner()
            : CreateChannelOwner();

    private static ProtectedRecordOwner CreateSourceConfigurationOwner() =>
        ProtectedRecordOwner.ForSourceConfiguration(SourceConfigurationId.Generate());

    private static ProtectedRecordOwner CreateChannelOwner() =>
        ProtectedRecordOwner.ForChannel(ChannelId.Generate());

    private static SecretReference CreateSecretReference()
    {
        DomainResult<SecretReference> result = SecretReference.Parse($"secret-ref-v1:{Guid.NewGuid():N}");
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic secret reference could not be created.");
    }

    private static ProtectedLocatorReference CreateLocatorReference()
    {
        DomainResult<ProtectedLocatorReference> result =
            ProtectedLocatorReference.Parse($"locator-ref-v1:{Guid.NewGuid():N}");
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException("A synthetic locator reference could not be created.");
    }

    private static byte[] CreateUpdatedPayload() => CreateCanaryPayload("CONTRACT-UPDATED");

    private static byte[] CreateCanaryPayload(string caseId)
    {
        TestCanary canary = TestCanary.Create("M4", caseId);
        using MemoryStream stream = new();
        canary.WriteTo(stream, TestCanaryEncoding.Utf8);
        return stream.ToArray();
    }

    private static bool IsZeroed(ReadOnlySpan<byte> value)
    {
        foreach (byte item in value)
        {
            if (item != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Matches(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);

    private static async Task<T> RunAfterSignalAsync<T>(Task signal, Func<ValueTask<T>> operation)
    {
        await signal.ConfigureAwait(false);
        return await operation().ConfigureAwait(false);
    }

    private static void AssertLeaseMatches(SecretStoreReadResult result, ReadOnlySpan<byte> expected)
    {
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Lease);
        using (result.Lease)
        {
            Assert.IsTrue(Matches(expected, result.Lease.Value.Span));
        }
    }

    private static void AssertUnavailable(SecretStoreReadResult result)
    {
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SecretStoreFailure.ProtectedRecordUnavailable, result.Failure);
        Assert.IsNull(result.Lease);
    }

    private static void AssertUnavailable(SecretStoreOperationResult result)
    {
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(SecretStoreFailure.ProtectedRecordUnavailable, result.Failure);
    }
}
