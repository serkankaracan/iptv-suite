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
        byte[] initialInput = CreateSensitivePayload();
        byte[] initialExpected = initialInput.ToArray();
        byte[] updatedInput = CreateUpdatedPayload();
        byte[] updatedExpected = updatedInput.ToArray();

        try
        {
            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, initialInput);
            Assert.IsTrue(created.IsSuccess);
            Assert.IsNotNull(created.Reference);

            CryptographicOperations.ZeroMemory(initialInput);
            SecretStoreReadResult initialRead = await store.ReadCredentialsAsync(sourceId, created.Reference);
            Assert.IsTrue(initialRead.IsSuccess);
            Assert.IsNotNull(initialRead.Lease);
            using (initialRead.Lease)
            {
                Assert.IsTrue(Matches(initialExpected, initialRead.Lease.Value.Span));
            }

            SecretStoreOperationResult updated = await store.UpdateCredentialsAsync(
                sourceId,
                created.Reference,
                updatedInput);
            Assert.IsTrue(updated.IsSuccess);
            CryptographicOperations.ZeroMemory(updatedInput);

            SecretStoreReadResult updatedRead = await store.ReadCredentialsAsync(sourceId, created.Reference);
            Assert.IsTrue(updatedRead.IsSuccess);
            Assert.IsNotNull(updatedRead.Lease);
            using (updatedRead.Lease)
            {
                Assert.IsTrue(Matches(updatedExpected, updatedRead.Lease.Value.Span));
            }

            Assert.IsTrue((await store.DeleteCredentialsAsync(sourceId, created.Reference)).IsSuccess);
            Assert.IsTrue((await store.DeleteCredentialsAsync(sourceId, created.Reference)).IsSuccess);
            AssertUnavailable(await store.ReadCredentialsAsync(sourceId, created.Reference));
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
        byte[] payload = CreateSensitivePayload();

        try
        {
            ProtectedLocatorReferenceCreationResult created = await store.CreateLocatorAsync(
                sourceId,
                purpose,
                payload);
            Assert.IsTrue(created.IsSuccess);
            Assert.IsNotNull(created.Reference);

            SecretStoreReadResult read = await store.ReadLocatorAsync(sourceId, purpose, created.Reference);
            Assert.IsTrue(read.IsSuccess);
            Assert.IsNotNull(read.Lease);
            using (read.Lease)
            {
                Assert.IsTrue(Matches(payload, read.Lease.Value.Span));
            }

            Assert.IsTrue((await store.DeleteLocatorAsync(sourceId, purpose, created.Reference)).IsSuccess);
            AssertUnavailable(await store.ReadLocatorAsync(sourceId, purpose, created.Reference));
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
        byte[] payload = CreateSensitivePayload();

        try
        {
            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, payload);
            Assert.IsNotNull(created.Reference);
            SecretStoreReadResult read = await store.ReadCredentialsAsync(sourceId, created.Reference);
            Assert.IsNotNull(read.Lease);

            SecretLease lease = read.Lease;
            ReadOnlyMemory<byte> observedClone = lease.Value;
            lease.Dispose();

            Assert.IsTrue(IsZeroed(observedClone.Span));
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = lease.Value);

            SecretStoreReadResult secondRead = await store.ReadCredentialsAsync(sourceId, created.Reference);
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
        SourceId owner = SourceId.Generate();
        SourceId otherSource = SourceId.Generate();
        byte[] payload = CreateSensitivePayload();

        try
        {
            ProtectedLocatorReferenceCreationResult locator = await store.CreateLocatorAsync(
                owner,
                ProtectedValuePurpose.RemotePlaylistLocator,
                payload);
            Assert.IsNotNull(locator.Reference);

            AssertUnavailable(await store.ReadLocatorAsync(
                otherSource,
                ProtectedValuePurpose.RemotePlaylistLocator,
                locator.Reference));
            AssertUnavailable(await store.ReadLocatorAsync(
                owner,
                ProtectedValuePurpose.ChannelStreamLocator,
                locator.Reference));
            AssertUnavailable(await store.ReadLocatorAsync(
                owner,
                ProtectedValuePurpose.RemotePlaylistLocator,
                ProtectedLocatorReference.Create()));
            AssertUnavailable(await store.UpdateLocatorAsync(
                otherSource,
                ProtectedValuePurpose.RemotePlaylistLocator,
                locator.Reference,
                payload));

            SecretReferenceCreationResult credentials = await store.CreateCredentialsAsync(owner, payload);
            Assert.IsNotNull(credentials.Reference);
            AssertUnavailable(await store.ReadCredentialsAsync(otherSource, credentials.Reference));
            AssertUnavailable(await store.ReadCredentialsAsync(owner, SecretReference.Create()));

            Assert.IsTrue((await store.DeleteLocatorAsync(
                otherSource,
                ProtectedValuePurpose.RemotePlaylistLocator,
                locator.Reference)).IsSuccess);
            SecretStoreReadResult ownerRead = await store.ReadLocatorAsync(
                owner,
                ProtectedValuePurpose.RemotePlaylistLocator,
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
    public async Task EmptyAndOversizedValuesAreRejectedWithoutMutation()
    {
        using M4InMemorySecretStore fake = new();
        M4InMemorySecretStore store = fake;
        SourceId sourceId = SourceId.Generate();
        byte[] valid = CreateSensitivePayload();
        byte[] oversized = new byte[SecretStoreLimits.MaxProtectedValueBytes + 1];

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
                await store.CreateCredentialsAsync(sourceId, ReadOnlyMemory<byte>.Empty));
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
                await store.CreateLocatorAsync(
                    sourceId,
                    ProtectedValuePurpose.RemotePlaylistLocator,
                    oversized));
            Assert.AreEqual(0, fake.ActiveRecordCount);

            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, valid);
            Assert.IsNotNull(created.Reference);
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
                await store.UpdateCredentialsAsync(
                    sourceId,
                    created.Reference,
                    ReadOnlyMemory<byte>.Empty));

            SecretStoreReadResult read = await store.ReadCredentialsAsync(sourceId, created.Reference);
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
        byte[] payload = CreateSensitivePayload();
        byte[] updated = CreateUpdatedPayload();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await store.CreateCredentialsAsync(sourceId, payload, cancellation.Token));
            Assert.AreEqual(0, fake.ActiveRecordCount);

            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, payload);
            Assert.IsNotNull(created.Reference);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await store.ReadCredentialsAsync(sourceId, created.Reference, cancellation.Token));
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await store.UpdateCredentialsAsync(sourceId, created.Reference, updated, cancellation.Token));
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await store.DeleteCredentialsAsync(sourceId, created.Reference, cancellation.Token));

            SecretStoreReadResult read = await store.ReadCredentialsAsync(sourceId, created.Reference);
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
        byte[] payload = CreateSensitivePayload();

        try
        {
            Assert.IsTrue((await store.DeleteCredentialsAsync(
                sourceId,
                SecretReference.Create())).IsSuccess);

            ProtectedLocatorReferenceCreationResult created = await store.CreateLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelLogoLocator,
                payload);
            Assert.IsNotNull(created.Reference);
            Assert.IsTrue((await store.DeleteLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelLogoLocator,
                created.Reference)).IsSuccess);
            Assert.IsTrue((await store.DeleteLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelLogoLocator,
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
        byte[] payload = CreateSensitivePayload();

        try
        {
            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, payload);
            Assert.IsNotNull(created.Reference);
            SecretStoreReadResult read = await store.ReadCredentialsAsync(sourceId, created.Reference);
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
        byte[] initial = CreateSensitivePayload();
        byte[] updated = CreateUpdatedPayload();

        try
        {
            for (int iteration = 0; iteration < 16; iteration++)
            {
                SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, initial);
                Assert.IsNotNull(created.Reference);
                var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<SecretStoreReadResult> readTask = RunAfterSignalAsync(
                    start.Task,
                    () => store.ReadCredentialsAsync(sourceId, created.Reference));
                Task<SecretStoreOperationResult> updateTask = RunAfterSignalAsync(
                    start.Task,
                    () => store.UpdateCredentialsAsync(sourceId, created.Reference, updated));
                Task<SecretStoreOperationResult> deleteTask = RunAfterSignalAsync(
                    start.Task,
                    () => store.DeleteCredentialsAsync(sourceId, created.Reference));

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

                AssertUnavailable(await store.ReadCredentialsAsync(sourceId, created.Reference));
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
