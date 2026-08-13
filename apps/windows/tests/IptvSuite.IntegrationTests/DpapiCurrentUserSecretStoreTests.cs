using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
[DoNotParallelize]
[SupportedOSPlatform("windows")]
public sealed class DpapiCurrentUserSecretStoreTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task CredentialsSurviveAdapterRestartAndNeverPersistPlaintext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-restart");
        TestCanary canary = TestCanary.Create("M4", "DPAPI-RESTART");
        TestCanary updatedCanary = TestCanary.Create("M4", "DPAPI-UPDATED");
        byte[] payload = CreateCanaryPayload(canary);
        byte[] updated = CreateCanaryPayload(updatedCanary);
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateSourceConfigurationOwner();

        try
        {
            var firstInstance = new DpapiCurrentUserSecretStore(temporary.FullPath);
            SecretReferenceCreationResult created = await firstInstance.CreateCredentialsAsync(
                sourceId,
                recordOwner,
                payload);
            Assert.IsTrue(created.IsSuccess);
            Assert.IsNotNull(created.Reference);
            Assert.HasCount(0, ArtifactCanaryScanner.Scan(temporary.FullPath, canary));

            var restartedInstance = new DpapiCurrentUserSecretStore(temporary.FullPath);
            SecretStoreReadResult restartedRead = await restartedInstance.ReadCredentialsAsync(
                sourceId,
                recordOwner,
                created.Reference);
            AssertLeaseMatches(restartedRead, payload);

            ReadOnlyMemory<byte> expectedAfterUpdates = default;
            for (int iteration = 0; iteration < 8; iteration++)
            {
                expectedAfterUpdates = iteration % 2 == 0 ? updated : payload;
                SecretStoreOperationResult updatedResult = await restartedInstance.UpdateCredentialsAsync(
                    sourceId,
                    recordOwner,
                    created.Reference,
                    expectedAfterUpdates);
                Assert.IsTrue(
                    updatedResult.IsSuccess,
                    $"Bounded update iteration {iteration} failed with {updatedResult.Failure}.");
            }

            AssertLeaseMatches(
                await firstInstance.ReadCredentialsAsync(sourceId, recordOwner, created.Reference),
                expectedAfterUpdates.Span);
            Assert.HasCount(0, ArtifactCanaryScanner.Scan(temporary.FullPath, canary));
            Assert.HasCount(0, ArtifactCanaryScanner.Scan(temporary.FullPath, updatedCanary));

            Assert.IsTrue((await restartedInstance.DeleteCredentialsAsync(
                sourceId,
                recordOwner,
                created.Reference)).IsSuccess);
            Assert.IsTrue((await restartedInstance.DeleteCredentialsAsync(
                sourceId,
                recordOwner,
                created.Reference)).IsSuccess);
            AssertUnavailable(await firstInstance.ReadCredentialsAsync(sourceId, recordOwner, created.Reference));
            Assert.HasCount(0, Directory.EnumerateFiles(temporary.FullPath, "*.dpapi").ToArray());
            Assert.HasCount(0, Directory.EnumerateFiles(temporary.FullPath, "*.tmp").ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(updated);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task SwappedCiphertextsFailClosedAcrossOwnerAndReferenceBindings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-binding");
        var store = new DpapiCurrentUserSecretStore(temporary.FullPath);
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner firstOwner = CreateChannelOwner();
        ProtectedRecordOwner secondOwner = CreateChannelOwner();
        byte[] firstPayload = CreateCanaryPayload(TestCanary.Create("M4", "DPAPI-BINDING-A"));
        byte[] secondPayload = CreateCanaryPayload(TestCanary.Create("M4", "DPAPI-BINDING-B"));
        byte[]? firstCiphertext = null;
        byte[]? secondCiphertext = null;

        try
        {
            ProtectedLocatorReferenceCreationResult first = await store.CreateLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                firstOwner,
                firstPayload);
            Assert.IsNotNull(first.Reference);
            string firstPath = Directory.EnumerateFiles(temporary.FullPath, "*.dpapi").Single();

            ProtectedLocatorReferenceCreationResult second = await store.CreateLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                secondOwner,
                secondPayload);
            Assert.IsNotNull(second.Reference);
            string secondPath = Directory.EnumerateFiles(temporary.FullPath, "*.dpapi")
                .Single(path => !string.Equals(path, firstPath, StringComparison.OrdinalIgnoreCase));

            firstCiphertext = await File.ReadAllBytesAsync(firstPath);
            secondCiphertext = await File.ReadAllBytesAsync(secondPath);
            await File.WriteAllBytesAsync(firstPath, secondCiphertext);
            await File.WriteAllBytesAsync(secondPath, firstCiphertext);

            AssertUnavailable(await store.ReadLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                firstOwner,
                first.Reference));
            AssertUnavailable(await store.ReadLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                secondOwner,
                second.Reference));
            AssertUnavailable(await store.ReadLocatorAsync(
                SourceId.Generate(),
                ProtectedValuePurpose.ChannelStreamLocator,
                firstOwner,
                first.Reference));
            Assert.HasCount(0, Directory.EnumerateFiles(temporary.FullPath, "*.tmp").ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(firstPayload);
            CryptographicOperations.ZeroMemory(secondPayload);
            if (firstCiphertext is not null)
            {
                CryptographicOperations.ZeroMemory(firstCiphertext);
            }

            if (secondCiphertext is not null)
            {
                CryptographicOperations.ZeroMemory(secondCiphertext);
            }
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WrongOwnerOperationsAreUnavailableAndIdempotentDeletePreservesOwnerRecord()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-owner-binding");
        var store = new DpapiCurrentUserSecretStore(temporary.FullPath);
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner owner = CreateChannelOwner();
        ProtectedRecordOwner otherOwner = CreateChannelOwner();
        byte[] payload = CreateCanaryPayload(TestCanary.Create("M4", "DPAPI-OWNER-A"));
        byte[] updated = CreateCanaryPayload(TestCanary.Create("M4", "DPAPI-OWNER-B"));

        try
        {
            ProtectedLocatorReferenceCreationResult created = await store.CreateLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                owner,
                payload);
            Assert.IsNotNull(created.Reference);
            string recordPath = Directory.EnumerateFiles(temporary.FullPath, "*.dpapi").Single();
            StringAssert.Contains(Path.GetFileName(recordPath), "record-v2-");

            AssertUnavailable(await store.ReadLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                otherOwner,
                created.Reference));
            AssertUnavailable(await store.UpdateLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                otherOwner,
                created.Reference,
                updated));
            Assert.IsTrue((await store.DeleteLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                otherOwner,
                created.Reference)).IsSuccess);
            Assert.IsTrue((await store.DeleteLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                otherOwner,
                created.Reference)).IsSuccess);

            AssertLeaseMatches(
                await store.ReadLocatorAsync(
                    sourceId,
                    ProtectedValuePurpose.ChannelStreamLocator,
                    owner,
                    created.Reference),
                payload);
            Assert.IsTrue(File.Exists(recordPath));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(updated);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ChannelLocatorPurposeSwapIsUnavailableAndCannotDeleteTheOwnerRecord()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-purpose-binding");
        var store = new DpapiCurrentUserSecretStore(temporary.FullPath);
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner channelOwner = CreateChannelOwner();
        byte[] payload = CreateCanaryPayload(TestCanary.Create("M4", "DPAPI-PURPOSE-A"));
        byte[] updated = CreateCanaryPayload(TestCanary.Create("M4", "DPAPI-PURPOSE-B"));

        try
        {
            ProtectedLocatorReferenceCreationResult created = await store.CreateLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                channelOwner,
                payload);
            Assert.IsNotNull(created.Reference);
            string recordPath = Directory.EnumerateFiles(temporary.FullPath, "*.dpapi").Single();

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
            Assert.IsTrue(File.Exists(recordPath));
            Assert.HasCount(1, Directory.EnumerateFiles(temporary.FullPath, "*.dpapi").ToArray());

            Assert.IsTrue((await store.DeleteLocatorAsync(
                sourceId,
                ProtectedValuePurpose.ChannelStreamLocator,
                channelOwner,
                created.Reference)).IsSuccess);
            Assert.HasCount(0, Directory.EnumerateFiles(temporary.FullPath, "*.dpapi").ToArray());
            Assert.HasCount(0, Directory.EnumerateFiles(temporary.FullPath, "*.tmp").ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(updated);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CorruptTruncatedAndOversizedRecordsAreUnavailableWithoutSecretEcho()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-corrupt");
        var store = new DpapiCurrentUserSecretStore(temporary.FullPath);
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateSourceConfigurationOwner();
        TestCanary canary = TestCanary.Create("M4", "DPAPI-CORRUPT");
        byte[] payload = CreateCanaryPayload(canary);

        try
        {
            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, recordOwner, payload);
            Assert.IsNotNull(created.Reference);
            string recordPath = Directory.EnumerateFiles(temporary.FullPath, "*.dpapi").Single();

            await File.WriteAllBytesAsync(recordPath, [0x01, 0x02, 0x03]);
            SecretStoreReadResult corrupt = await store.ReadCredentialsAsync(sourceId, recordOwner, created.Reference);
            AssertUnavailable(corrupt);
            string sensitiveText = Encoding.UTF8.GetString(payload);
            Assert.IsFalse(corrupt.ToString().Contains(sensitiveText, StringComparison.Ordinal));

            byte[] oversizedCiphertext = new byte[(128 * 1024) + 1];
            try
            {
                await File.WriteAllBytesAsync(recordPath, oversizedCiphertext);
                AssertUnavailable(await store.ReadCredentialsAsync(sourceId, recordOwner, created.Reference));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(oversizedCiphertext);
            }

            Assert.IsTrue((await store.DeleteCredentialsAsync(sourceId, recordOwner, created.Reference)).IsSuccess);
            Assert.HasCount(0, Directory.EnumerateFiles(temporary.FullPath, "*.tmp").ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task V1RecordNamespaceIsIgnoredWithoutMigrationOrDeletion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-v1-namespace");
        var store = new DpapiCurrentUserSecretStore(temporary.FullPath);
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner owner = CreateSourceConfigurationOwner();
        byte[] payload = CreateCanaryPayload(TestCanary.Create("M4", "DPAPI-V1-NAMESPACE"));

        try
        {
            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, owner, payload);
            Assert.IsNotNull(created.Reference);
            string v2Path = Directory.EnumerateFiles(temporary.FullPath, "record-v2-*.dpapi").Single();
            string v1Path = Path.Combine(
                temporary.FullPath,
                Path.GetFileName(v2Path).Replace("record-v2-", "record-v1-", StringComparison.Ordinal));
            File.Move(v2Path, v1Path);

            _ = new DpapiCurrentUserSecretStore(temporary.FullPath);
            AssertUnavailable(await store.ReadCredentialsAsync(sourceId, owner, created.Reference));
            AssertUnavailable(await store.UpdateCredentialsAsync(sourceId, owner, created.Reference, payload));
            Assert.IsTrue((await store.DeleteCredentialsAsync(sourceId, owner, created.Reference)).IsSuccess);
            Assert.IsTrue(File.Exists(v1Path));
            Assert.HasCount(0, Directory.EnumerateFiles(temporary.FullPath, "record-v2-*.dpapi").ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PreCancelledOperationsDoNotCreateUpdateOrDeleteRecords()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-cancel");
        var store = new DpapiCurrentUserSecretStore(temporary.FullPath);
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateSourceConfigurationOwner();
        byte[] payload = CreateCanaryPayload(TestCanary.Create("M4", "DPAPI-CANCEL"));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await store.CreateCredentialsAsync(sourceId, recordOwner, payload, cancellation.Token));
            Assert.HasCount(0, Directory.EnumerateFiles(temporary.FullPath, "*.dpapi").ToArray());

            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, recordOwner, payload);
            Assert.IsNotNull(created.Reference);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await store.UpdateCredentialsAsync(
                    sourceId,
                    recordOwner,
                    created.Reference,
                    payload,
                    cancellation.Token));
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await store.DeleteCredentialsAsync(sourceId, recordOwner, created.Reference, cancellation.Token));
            AssertLeaseMatches(await store.ReadCredentialsAsync(sourceId, recordOwner, created.Reference), payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ConcurrentCreatesRemainIndependentlyBoundAndReadable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-concurrent");
        var store = new DpapiCurrentUserSecretStore(temporary.FullPath);
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateChannelOwner();
        TestCanary[] canaries = Enumerable.Range(0, 8)
            .Select(index => TestCanary.Create("M4", $"DPAPI-CONCURRENT-{index}"))
            .ToArray();
        byte[][] payloads = canaries.Select(CreateCanaryPayload).ToArray();

        try
        {
            Task<ProtectedLocatorReferenceCreationResult>[] operations = payloads
                .Select(payload => store.CreateLocatorAsync(
                    sourceId,
                    ProtectedValuePurpose.ChannelStreamLocator,
                    recordOwner,
                    payload).AsTask())
                .ToArray();
            ProtectedLocatorReferenceCreationResult[] created = await Task.WhenAll(operations);

            Assert.IsTrue(created.All(result => result.IsSuccess && result.Reference is not null));
            for (int index = 0; index < created.Length; index++)
            {
                AssertLeaseMatches(
                    await store.ReadLocatorAsync(
                        sourceId,
                        ProtectedValuePurpose.ChannelStreamLocator,
                        recordOwner,
                        created[index].Reference!),
                    payloads[index]);
                Assert.HasCount(0, ArtifactCanaryScanner.Scan(temporary.FullPath, canaries[index]));
            }
        }
        finally
        {
            foreach (byte[] payload in payloads)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task StartupCleanupDeletesOnlyExactStaleTopLevelTemporaryFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-temp-cleanup");
        DateTimeOffset now = new(2035, 7, 20, 12, 0, 0, TimeSpan.Zero);
        TimeProvider timeProvider = TestTime.Create(now);
        string stale = Path.Combine(
            temporary.FullPath,
            "temporary-v2-00000000000000000000000000000001.tmp");
        string fresh = Path.Combine(
            temporary.FullPath,
            "temporary-v2-00000000000000000000000000000002.tmp");
        string future = Path.Combine(
            temporary.FullPath,
            "temporary-v2-00000000000000000000000000000003.tmp");
        string boundary = Path.Combine(
            temporary.FullPath,
            "temporary-v2-00000000000000000000000000000007.tmp");
        string uppercaseIdentifier = Path.Combine(
            temporary.FullPath,
            "temporary-v2-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.tmp");
        string invalidIdentifier = Path.Combine(
            temporary.FullPath,
            "temporary-v2-gggggggggggggggggggggggggggggggg.tmp");
        string protectedRecord = Path.Combine(temporary.FullPath, "record-v1-preserve.dpapi");
        string legacyTemporary = Path.Combine(
            temporary.FullPath,
            "temporary-v1-00000000000000000000000000000009.tmp");
        string nestedDirectory = Path.Combine(temporary.FullPath, "nested");
        string nestedTemporary = Path.Combine(
            nestedDirectory,
            "temporary-v2-00000000000000000000000000000004.tmp");
        string[] seededFiles =
        [
            stale,
            fresh,
            future,
            boundary,
            uppercaseIdentifier,
            invalidIdentifier,
            protectedRecord,
            legacyTemporary,
            nestedTemporary,
        ];

        Directory.CreateDirectory(nestedDirectory);
        foreach (string path in seededFiles)
        {
            await File.WriteAllBytesAsync(path, [0x4D, 0x34]);
        }

        DateTime oldTimestamp = now.UtcDateTime - TimeSpan.FromHours(25);
        File.SetLastWriteTimeUtc(stale, oldTimestamp);
        File.SetLastWriteTimeUtc(uppercaseIdentifier, oldTimestamp);
        File.SetLastWriteTimeUtc(invalidIdentifier, oldTimestamp);
        File.SetLastWriteTimeUtc(protectedRecord, oldTimestamp);
        File.SetLastWriteTimeUtc(legacyTemporary, oldTimestamp);
        File.SetLastWriteTimeUtc(nestedTemporary, oldTimestamp);
        File.SetLastWriteTimeUtc(fresh, now.UtcDateTime - TimeSpan.FromMinutes(5));
        File.SetLastWriteTimeUtc(future, now.UtcDateTime + TimeSpan.FromHours(1));
        File.SetLastWriteTimeUtc(boundary, now.UtcDateTime - TimeSpan.FromHours(24));

        DpapiCurrentUserSecretStore store = CreateStoreForTest(temporary.FullPath, timeProvider);
        _ = CreateStoreForTest(temporary.FullPath, timeProvider);

        Assert.IsFalse(File.Exists(stale));
        Assert.IsTrue(File.Exists(fresh));
        Assert.IsTrue(File.Exists(future));
        Assert.IsFalse(File.Exists(boundary));
        Assert.IsTrue(File.Exists(uppercaseIdentifier));
        Assert.IsTrue(File.Exists(invalidIdentifier));
        Assert.IsTrue(File.Exists(protectedRecord));
        Assert.IsTrue(File.Exists(legacyTemporary));
        Assert.IsTrue(File.Exists(nestedTemporary));

        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateSourceConfigurationOwner();
        byte[] payload = CreateCanaryPayload(TestCanary.Create("M4", "DPAPI-TEMP-CLEANUP"));
        try
        {
            SecretReferenceCreationResult created = await store.CreateCredentialsAsync(sourceId, recordOwner, payload);
            Assert.IsTrue(created.IsSuccess);
            Assert.IsNotNull(created.Reference);
            AssertLeaseMatches(await store.ReadCredentialsAsync(sourceId, recordOwner, created.Reference), payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public void StartupCleanupFailsClosedForNonRegularExactTemporaryEntry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-temp-nonregular");
        DateTimeOffset now = new(2035, 7, 20, 12, 0, 0, TimeSpan.Zero);
        TimeProvider timeProvider = TestTime.Create(now);
        string directoryPath = Path.Combine(
            temporary.FullPath,
            "temporary-v2-00000000000000000000000000000005.tmp");
        Directory.CreateDirectory(directoryPath);
        Directory.SetLastWriteTimeUtc(directoryPath, now.UtcDateTime - TimeSpan.FromHours(25));

        Assert.ThrowsExactly<IOException>(() =>
            _ = CreateStoreForTest(temporary.FullPath, timeProvider));
        Assert.IsTrue(Directory.Exists(directoryPath));
    }

    [TestMethod]
    [Timeout(30_000)]
    public void StartupCleanupFailsOnLockedStaleTemporaryFileAndSucceedsOnRetry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-temp-locked");
        DateTimeOffset now = new(2035, 7, 20, 12, 0, 0, TimeSpan.Zero);
        TimeProvider timeProvider = TestTime.Create(now);
        string temporaryPath = Path.Combine(
            temporary.FullPath,
            "temporary-v2-00000000000000000000000000000006.tmp");
        File.WriteAllBytes(temporaryPath, [0x4D, 0x34]);
        File.SetLastWriteTimeUtc(temporaryPath, now.UtcDateTime - TimeSpan.FromHours(25));

        using (var blocker = new FileStream(
            temporaryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            Assert.ThrowsExactly<IOException>(() =>
                _ = CreateStoreForTest(temporary.FullPath, timeProvider));
            Assert.IsTrue(File.Exists(temporaryPath));
        }

        _ = CreateStoreForTest(temporary.FullPath, timeProvider);
        Assert.IsFalse(File.Exists(temporaryPath));
    }

    [TestMethod]
    [Timeout(30_000)]
    public void StartupCleanupFailsClosedWhenTemporaryEntryLimitIsExceeded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-temp-limit");
        DateTimeOffset freshNow = DateTimeOffset.UtcNow;
        TimeProvider freshTimeProvider = TestTime.Create(freshNow);
        List<string> exactTemporaryPaths = new(1025);

        for (int index = 0; index < 1024; index++)
        {
            string path = Path.Combine(
                temporary.FullPath,
                $"temporary-v2-{index:x32}.tmp");
            File.WriteAllBytes(path, [0x4D, 0x34]);
            exactTemporaryPaths.Add(path);
        }

        string lookalikePath = Path.Combine(
            temporary.FullPath,
            "temporary-v2-gggggggggggggggggggggggggggggggg.tmp");
        File.WriteAllBytes(lookalikePath, [0x4D, 0x34]);

        _ = CreateStoreForTest(temporary.FullPath, freshTimeProvider);
        Assert.IsTrue(exactTemporaryPaths.All(File.Exists));
        Assert.IsTrue(File.Exists(lookalikePath));

        string overflowPath = Path.Combine(
            temporary.FullPath,
            $"temporary-v2-{1024:x32}.tmp");
        File.WriteAllBytes(overflowPath, [0x4D, 0x34]);
        exactTemporaryPaths.Add(overflowPath);
        TimeProvider staleTimeProvider = TestTime.Create(freshNow + TimeSpan.FromDays(2));

        Assert.ThrowsExactly<IOException>(() =>
            _ = CreateStoreForTest(temporary.FullPath, staleTimeProvider));
        Assert.IsTrue(exactTemporaryPaths.All(File.Exists));
        Assert.IsTrue(File.Exists(lookalikePath));
    }

    [TestMethod]
    [Timeout(30_000)]
    public void PreCancelledInitializationDoesNotCreateStorageDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-init-cancel");
        string storagePath = Path.Combine(temporary.FullPath, "not-created");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            _ = new DpapiCurrentUserSecretStore(
                storagePath,
                cancellation.Token));
        Assert.IsFalse(Directory.Exists(storagePath));

        string existingStoragePath = Path.Combine(temporary.FullPath, "existing");
        Directory.CreateDirectory(existingStoragePath);
        string staleTemporaryPath = Path.Combine(
            existingStoragePath,
            "temporary-v2-00000000000000000000000000000008.tmp");
        File.WriteAllBytes(staleTemporaryPath, [0x4D, 0x34]);
        File.SetLastWriteTimeUtc(staleTemporaryPath, DateTime.UtcNow - TimeSpan.FromHours(25));

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            _ = new DpapiCurrentUserSecretStore(
                existingStoragePath,
                cancellation.Token));
        Assert.IsTrue(File.Exists(staleTemporaryPath));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task SameKeyUpdateReadAndDeleteAreSerializedAcrossAdapterInstances()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m4-dpapi-key-gate");
        var firstInstance = new DpapiCurrentUserSecretStore(temporary.FullPath);
        var secondInstance = new DpapiCurrentUserSecretStore(temporary.FullPath);
        SourceId sourceId = SourceId.Generate();
        ProtectedRecordOwner recordOwner = CreateSourceConfigurationOwner();
        byte[] initial = CreateCanaryPayload(TestCanary.Create("M4", "DPAPI-GATE-INITIAL"));
        byte[] updated = CreateCanaryPayload(TestCanary.Create("M4", "DPAPI-GATE-UPDATED"));

        try
        {
            SecretReferenceCreationResult created = await firstInstance.CreateCredentialsAsync(
                sourceId,
                recordOwner,
                initial);
            Assert.IsNotNull(created.Reference);
            string recordPath = Directory.EnumerateFiles(temporary.FullPath, "*.dpapi").Single();
            using CancellationTokenSource readCancellation = new();
            using var readPhaseBlocker = new FileStream(
                recordPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            Task<SecretStoreOperationResult> readPhaseUpdateTask = secondInstance.UpdateCredentialsAsync(
                sourceId,
                recordOwner,
                created.Reference,
                updated).AsTask();
            await WaitForTemporaryRecordAsync(temporary.FullPath);
            Task<SecretStoreReadResult> blockedReadTask = firstInstance.ReadCredentialsAsync(
                sourceId,
                recordOwner,
                created.Reference,
                readCancellation.Token).AsTask();

            await Task.Delay(25);
            bool readWaited = !blockedReadTask.IsCompleted;
            await Task.WhenAll(
                Task.Run(readCancellation.Cancel),
                Task.Run(readPhaseBlocker.Dispose));
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await blockedReadTask);
            SecretStoreOperationResult readPhaseUpdate = await readPhaseUpdateTask;

            Assert.IsTrue(readWaited, "The same-key read bypassed an in-flight update.");
            Assert.IsTrue(
                readPhaseUpdate.IsSuccess,
                $"The serialized read-phase update failed with {readPhaseUpdate.Failure}.");
            AssertLeaseMatches(
                await firstInstance.ReadCredentialsAsync(sourceId, recordOwner, created.Reference),
                updated);

            Task<SecretStoreOperationResult> deletePhaseUpdateTask;
            Task<SecretStoreOperationResult> blockedDeleteTask;
            bool deleteWaited;

            using (var deletePhaseBlocker = new FileStream(
                recordPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                deletePhaseUpdateTask = firstInstance.UpdateCredentialsAsync(
                    sourceId,
                    recordOwner,
                    created.Reference,
                    initial).AsTask();
                await WaitForTemporaryRecordAsync(temporary.FullPath);
                blockedDeleteTask = secondInstance.DeleteCredentialsAsync(
                    sourceId,
                    recordOwner,
                    created.Reference).AsTask();

                await Task.Delay(25);
                deleteWaited = !blockedDeleteTask.IsCompleted;
            }

            SecretStoreOperationResult deletePhaseUpdate = await deletePhaseUpdateTask;
            SecretStoreOperationResult delete = await blockedDeleteTask;

            Assert.IsTrue(deleteWaited, "The same-key delete bypassed an in-flight update.");
            Assert.IsTrue(
                deletePhaseUpdate.IsSuccess,
                $"The serialized delete-phase update failed with {deletePhaseUpdate.Failure}.");
            Assert.IsTrue(delete.IsSuccess, $"The serialized delete failed with {delete.Failure}.");
            AssertUnavailable(await secondInstance.ReadCredentialsAsync(sourceId, recordOwner, created.Reference));
            Assert.HasCount(0, Directory.EnumerateFiles(temporary.FullPath, "*.dpapi").ToArray());
            Assert.HasCount(0, Directory.EnumerateFiles(temporary.FullPath, "*.tmp").ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(initial);
            CryptographicOperations.ZeroMemory(updated);
        }
    }

    private static byte[] CreateCanaryPayload(TestCanary canary)
    {
        using MemoryStream stream = new();
        canary.WriteTo(stream, TestCanaryEncoding.Utf8);
        return stream.ToArray();
    }

    private static ProtectedRecordOwner CreateSourceConfigurationOwner() =>
        ProtectedRecordOwner.ForSourceConfiguration(SourceConfigurationId.Generate());

    private static ProtectedRecordOwner CreateChannelOwner() =>
        ProtectedRecordOwner.ForChannel(ChannelId.Generate());

    private static DpapiCurrentUserSecretStore CreateStoreForTest(
        string storageDirectoryPath,
        TimeProvider timeProvider)
    {
        ConstructorInfo constructor = typeof(DpapiCurrentUserSecretStore).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(TimeProvider), typeof(CancellationToken)],
            modifiers: null)!;

        try
        {
            return (DpapiCurrentUserSecretStore)constructor.Invoke(
                [storageDirectoryPath, timeProvider, CancellationToken.None]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static async Task WaitForTemporaryRecordAsync(string rootPath)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (Directory.EnumerateFiles(rootPath, "*.tmp").Any())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail("The bounded update did not reach its temporary-record stage.");
    }

    private static void AssertLeaseMatches(SecretStoreReadResult result, ReadOnlySpan<byte> expected)
    {
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Lease);
        using (result.Lease)
        {
            Assert.IsTrue(CryptographicOperations.FixedTimeEquals(expected, result.Lease.Value.Span));
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
