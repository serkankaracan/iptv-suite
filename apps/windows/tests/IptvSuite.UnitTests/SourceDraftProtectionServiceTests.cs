using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class SourceDraftProtectionServiceTests
{
    [TestMethod]
    public void ConstructorRequiresASecretStore()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new SourceDraftProtectionService(null!));
    }

    [TestMethod]
    public async Task InvalidInputsFailBeforeEncodingOrStoreMutation()
    {
        var store = new NonConformingPayloadProbeSecretStore();
        var service = new SourceDraftProtectionService(store);
        SourceId sourceId = SourceId.Generate();

        SecurityTestAssertions.IsFailure(
            await service.ProtectRemotePlaylistAsync(
                sourceId,
                " ",
                "https://example.test/list.m3u"),
            DomainErrorCode.SourceNameRequired);
        SecurityTestAssertions.IsFailure(
            await service.ProtectRemotePlaylistAsync(
                sourceId,
                "Source",
                "http://example.test/list.m3u"),
            DomainErrorCode.InsecureTransportRejected);
        SecurityTestAssertions.IsFailure(
            await service.ProtectRemotePlaylistAsync(
                sourceId,
                "Source",
                "https://example.test/list\ud800"),
            DomainErrorCode.EndpointMalformed);
        SecurityTestAssertions.IsFailure(
            await service.ProtectXtreamAsync(
                sourceId,
                "Source",
                "https://example.test/api",
                null,
                "password"),
            DomainErrorCode.UsernameRequired);
        SecurityTestAssertions.IsFailure(
            await service.ProtectXtreamAsync(
                sourceId,
                "Source",
                "https://example.test/api",
                "username",
                new string('p', SourceConfigurationValidator.MaxPasswordUnicodeScalars + 1)),
            DomainErrorCode.CredentialTooLong);

        Assert.AreEqual(0, store.CredentialsCreateCount);
        Assert.AreEqual(0, store.LocatorCreateCount);
        Assert.AreEqual(0, store.PayloadSnapshots.Count);
        Assert.AreEqual(0, store.BorrowedPayloads.Count);
        Assert.AreEqual(0, store.CredentialOwners.Count);
        Assert.AreEqual(0, store.LocatorOwners.Count);
    }

    [TestMethod]
    public async Task XtreamProtectionUsesDeterministicBoundedPayloadAndStoreOwnedReferences()
    {
        var store = new NonConformingPayloadProbeSecretStore();
        var service = new SourceDraftProtectionService(store);
        SourceId sourceId = SourceId.Generate();
        string username = SecurityTestAssertions.CreateSensitiveValue("SOURCE-DRAFT-USERNAME");
        string password = SecurityTestAssertions.CreateSensitiveValue("SOURCE-DRAFT-PASSWORD");
        string locatorSecret = SecurityTestAssertions.CreateSensitiveValue("SOURCE-DRAFT-XTREAM-LOCATOR");
        string locator = $"https://example.test/private/{locatorSecret}?token={locatorSecret}";
        byte[] expectedPayload = BuildExpectedCredentialsPayload(locator, username, password);

        try
        {
            DomainResult<ValidatedSourceDraft> first = await service.ProtectXtreamAsync(
                sourceId,
                "  Source  ",
                locator,
                username,
                password);
            DomainResult<ValidatedSourceDraft> second = await service.ProtectXtreamAsync(
                sourceId,
                "Source",
                locator,
                username,
                password);

            Assert.IsTrue(first.IsSuccess);
            Assert.IsTrue(second.IsSuccess);
            Assert.AreEqual(2, store.CredentialsCreateCount);
            Assert.AreEqual(0, store.LocatorCreateCount);
            Assert.IsTrue(store.CredentialSources.All(item => item == sourceId));
            Assert.AreEqual(2, store.IssuedCredentialReferences.Count);
            Assert.AreEqual(2, store.CredentialOwners.Count);
            Assert.AreNotEqual(
                store.IssuedCredentialReferences[0],
                store.IssuedCredentialReferences[1]);

            var firstConfiguration = first.Value!.Configuration as XtreamSourceConfiguration;
            var secondConfiguration = second.Value!.Configuration as XtreamSourceConfiguration;
            Assert.IsNotNull(firstConfiguration);
            Assert.IsNotNull(secondConfiguration);
            Assert.IsFalse(firstConfiguration.ConfigurationId.IsEmpty);
            Assert.IsFalse(secondConfiguration.ConfigurationId.IsEmpty);
            Assert.AreNotEqual(
                firstConfiguration.ConfigurationId,
                secondConfiguration.ConfigurationId);
            Assert.AreEqual(
                ProtectedRecordOwner.ForSourceConfiguration(firstConfiguration.ConfigurationId),
                store.CredentialOwners[0]);
            Assert.AreEqual(
                ProtectedRecordOwner.ForSourceConfiguration(secondConfiguration.ConfigurationId),
                store.CredentialOwners[1]);
            Assert.AreSame(store.IssuedCredentialReferences[0], firstConfiguration.CredentialsReference);
            Assert.AreSame(store.IssuedCredentialReferences[1], secondConfiguration.CredentialsReference);
            Assert.AreEqual(sourceId, first.Value.SourceId);
            Assert.AreEqual(sourceId, second.Value.SourceId);
            Assert.AreEqual("Source", first.Value.NormalizedDisplayName);
            Assert.AreEqual("example.test", firstConfiguration.SafeEndpoint.Host);
            Assert.AreEqual(443, firstConfiguration.SafeEndpoint.Port);

            Assert.AreEqual(2, store.PayloadSnapshots.Count);
            Assert.IsTrue(Matches(expectedPayload, store.PayloadSnapshots[0]));
            Assert.IsTrue(Matches(expectedPayload, store.PayloadSnapshots[1]));
            Assert.IsTrue(Matches(store.PayloadSnapshots[0], store.PayloadSnapshots[1]));
            Assert.IsTrue(expectedPayload.Length <= SecretStoreLimits.MaxProtectedValueBytes);
            Assert.IsTrue(store.BorrowedPayloads.All(IsZeroed));

            string observable = string.Join(
                '|',
                first,
                second,
                JsonSerializer.Serialize(first),
                JsonSerializer.Serialize(second));
            SecurityTestAssertions.DoesNotContainSensitive(
                observable,
                username,
                password,
                locatorSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedPayload);
            store.ZeroSnapshots();
        }
    }

    [TestMethod]
    public async Task RemotePlaylistProtectionBindsExactPurposeAndZeroesEncodedLocator()
    {
        var store = new NonConformingPayloadProbeSecretStore();
        var service = new SourceDraftProtectionService(store);
        SourceId sourceId = SourceId.Generate();
        string locatorSecret = SecurityTestAssertions.CreateSensitiveValue("SOURCE-DRAFT-REMOTE-LOCATOR");
        string locator = $"https://user:{locatorSecret}@example.test/private/list.m3u?key={locatorSecret}";
        byte[] expectedPayload = BuildExpectedLocatorPayload(locator);

        try
        {
            DomainResult<ValidatedSourceDraft> result = await service.ProtectRemotePlaylistAsync(
                sourceId,
                "Remote Source",
                locator);

            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual(0, store.CredentialsCreateCount);
            Assert.AreEqual(1, store.LocatorCreateCount);
            Assert.AreEqual(sourceId, store.LocatorSources.Single());
            Assert.AreEqual(
                ProtectedValuePurpose.RemotePlaylistLocator,
                store.LocatorPurposes.Single());

            var configuration = result.Value!.Configuration as RemotePlaylistSourceConfiguration;
            Assert.IsNotNull(configuration);
            Assert.IsFalse(configuration.ConfigurationId.IsEmpty);
            Assert.AreEqual(
                ProtectedRecordOwner.ForSourceConfiguration(configuration.ConfigurationId),
                store.LocatorOwners.Single());
            Assert.AreSame(store.IssuedLocatorReferences.Single(), configuration.LocatorReference);
            Assert.AreEqual(sourceId, result.Value.SourceId);
            Assert.AreEqual("example.test", configuration.SafeEndpoint.Host);
            Assert.AreEqual(443, configuration.SafeEndpoint.Port);
            Assert.IsTrue(Matches(expectedPayload, store.PayloadSnapshots.Single()));
            Assert.IsTrue(expectedPayload.Length <= SecretStoreLimits.MaxProtectedValueBytes);
            Assert.IsTrue(store.BorrowedPayloads.All(IsZeroed));

            string observable = string.Join('|', result, JsonSerializer.Serialize(result));
            SecurityTestAssertions.DoesNotContainSensitive(observable, locatorSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedPayload);
            store.ZeroSnapshots();
        }
    }

    [TestMethod]
    public async Task RemotePlaylistHttpOptInRejectsHttpsUserInfoBeforeStoreMutation()
    {
        var store = new NonConformingPayloadProbeSecretStore();
        var service = new SourceDraftProtectionService(store);

        DomainResult<ValidatedSourceDraft> result =
            await service.ProtectRemotePlaylistAllowingInsecureHttpAsync(
                SourceId.Generate(),
                "Remote Source",
                "https://synthetic-user:synthetic-password@example.test/list.m3u");

        SecurityTestAssertions.IsFailure(
            result,
            DomainErrorCode.EndpointUserInfoNotAllowed);
        Assert.AreEqual(0, store.LocatorCreateCount);
    }

    [TestMethod]
    public async Task RemotePlaylistHttpProtectionRequiresExplicitOptInAndBindsPort80()
    {
        var store = new NonConformingPayloadProbeSecretStore();
        var service = new SourceDraftProtectionService(store);
        SourceId sourceId = SourceId.Generate();
        string locatorSecret = SecurityTestAssertions.CreateSensitiveValue(
            "SOURCE-DRAFT-REMOTE-HTTP-LOCATOR");
        string locator = $"http://example.test/private/list.m3u?key={locatorSecret}";
        byte[] expectedPayload = BuildExpectedLocatorPayload(locator);

        try
        {
            DomainResult<ValidatedSourceDraft> rejected =
                await service.ProtectRemotePlaylistAsync(
                    sourceId,
                    "Remote HTTP Source",
                    locator);

            SecurityTestAssertions.IsFailure(
                rejected,
                DomainErrorCode.InsecureTransportRejected);
            Assert.AreEqual(0, store.LocatorCreateCount);

            DomainResult<ValidatedSourceDraft> accepted =
                await service.ProtectRemotePlaylistAllowingInsecureHttpAsync(
                    sourceId,
                    "Remote HTTP Source",
                    locator);

            Assert.IsTrue(accepted.IsSuccess);
            Assert.AreEqual(1, store.LocatorCreateCount);
            var configuration = accepted.Value!.Configuration as RemotePlaylistSourceConfiguration;
            Assert.IsNotNull(configuration);
            Assert.AreEqual(Uri.UriSchemeHttp, configuration.SafeEndpoint.Scheme);
            Assert.AreEqual(80, configuration.SafeEndpoint.Port);
            Assert.IsTrue(Matches(expectedPayload, store.PayloadSnapshots.Single()));
            Assert.IsTrue(store.BorrowedPayloads.All(IsZeroed));
            SecurityTestAssertions.DoesNotContainSensitive(
                string.Join('|', accepted, JsonSerializer.Serialize(accepted)),
                locatorSecret,
                locator);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedPayload);
            store.ZeroSnapshots();
        }
    }

    [TestMethod]
    [DataRow(SecretStoreFailure.ProtectedRecordUnavailable)]
    [DataRow(SecretStoreFailure.StorageUnavailable)]
    public async Task StoreCreateFailuresMapToSafeStorageErrorAndZeroPayload(
        SecretStoreFailure failure)
    {
        var store = new NonConformingPayloadProbeSecretStore
        {
            CredentialsFailure = failure,
            LocatorFailure = failure,
        };
        var service = new SourceDraftProtectionService(store);
        SourceId sourceId = SourceId.Generate();
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("SOURCE-DRAFT-STORE-FAILURE");

        try
        {
            DomainResult<ValidatedSourceDraft> credentials = await service.ProtectXtreamAsync(
                sourceId,
                "Source",
                "https://example.test/api",
                "username",
                sensitive);
            DomainResult<ValidatedSourceDraft> locator = await service.ProtectRemotePlaylistAsync(
                sourceId,
                "Source",
                $"https://example.test/list.m3u?token={sensitive}");

            SecurityTestAssertions.IsFailure(credentials, DomainErrorCode.StorageUnavailable);
            SecurityTestAssertions.IsFailure(locator, DomainErrorCode.StorageUnavailable);
            Assert.AreEqual(1, store.CredentialsCreateCount);
            Assert.AreEqual(1, store.LocatorCreateCount);
            Assert.IsTrue(store.BorrowedPayloads.All(IsZeroed));
            SecurityTestAssertions.DoesNotContainSensitive(
                string.Join('|', credentials, locator),
                sensitive);
        }
        finally
        {
            store.ZeroSnapshots();
        }
    }

    [TestMethod]
    public async Task PreCancelledRequestsNeverReachTheSecretStore()
    {
        var store = new NonConformingPayloadProbeSecretStore();
        var service = new SourceDraftProtectionService(store);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await service.ProtectRemotePlaylistAsync(
                SourceId.Generate(),
                "Source",
                "https://example.test/list.m3u",
                cancellation.Token));

        Assert.AreEqual(0, store.CredentialsCreateCount);
        Assert.AreEqual(0, store.LocatorCreateCount);
        Assert.AreEqual(0, store.PayloadSnapshots.Count);
    }

    [TestMethod]
    public async Task CancellationObservedAtSuccessfulStoreCommitStillReturnsBoundDraft()
    {
        using CancellationTokenSource cancellation = new();
        var store = new NonConformingPayloadProbeSecretStore
        {
            CancellationToSignalAfterLocatorCommit = cancellation,
        };
        var service = new SourceDraftProtectionService(store);
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("SOURCE-DRAFT-COMMIT-BOUNDARY");

        try
        {
            DomainResult<ValidatedSourceDraft> result = await service.ProtectRemotePlaylistAsync(
                SourceId.Generate(),
                "Source",
                $"https://example.test/list.m3u?token={sensitive}",
                cancellation.Token);

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.IsTrue(result.IsSuccess);
            var configuration = result.Value!.Configuration as RemotePlaylistSourceConfiguration;
            Assert.IsNotNull(configuration);
            Assert.AreSame(store.IssuedLocatorReferences.Single(), configuration.LocatorReference);
            Assert.AreEqual(1, store.LocatorCreateCount);
            Assert.IsTrue(store.BorrowedPayloads.All(IsZeroed));
            SecurityTestAssertions.DoesNotContainSensitive(
                string.Join('|', result, JsonSerializer.Serialize(result)),
                sensitive);
        }
        finally
        {
            store.ZeroSnapshots();
        }
    }

    [TestMethod]
    public async Task CredentialCommitCancellationStillReturnsSourceBoundDraft()
    {
        using CancellationTokenSource cancellation = new();
        var store = new NonConformingPayloadProbeSecretStore
        {
            CancellationToSignalAfterCredentialsCommit = cancellation,
        };
        var service = new SourceDraftProtectionService(store);
        SourceId sourceId = SourceId.Generate();

        try
        {
            DomainResult<ValidatedSourceDraft> result = await service.ProtectXtreamAsync(
                sourceId,
                "Source",
                "https://example.test/api",
                "username",
                "password",
                cancellation.Token);

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual(sourceId, result.Value!.SourceId);
            Assert.IsTrue(store.BorrowedPayloads.All(IsZeroed));
        }
        finally
        {
            store.ZeroSnapshots();
        }
    }

    [TestMethod]
    public async Task StoreThrownCancellationAndUnexpectedExceptionZeroBorrowedPayloads()
    {
        using CancellationTokenSource cancellation = new();
        var cancelledStore = new NonConformingPayloadProbeSecretStore
        {
            LocatorException = new OperationCanceledException(cancellation.Token),
        };
        var faultedStore = new NonConformingPayloadProbeSecretStore
        {
            CredentialsException = new InvalidOperationException("Synthetic store failure."),
        };
        var cancelledService = new SourceDraftProtectionService(cancelledStore);
        var faultedService = new SourceDraftProtectionService(faultedStore);

        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await cancelledService.ProtectRemotePlaylistAsync(
                    SourceId.Generate(),
                    "Source",
                    "https://example.test/list.m3u",
                    cancellation.Token));
            Assert.IsTrue(cancelledStore.BorrowedPayloads.All(IsZeroed));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await faultedService.ProtectXtreamAsync(
                    SourceId.Generate(),
                    "Source",
                    "https://example.test/api",
                    "username",
                    "password"));
            Assert.IsTrue(faultedStore.BorrowedPayloads.All(IsZeroed));
        }
        finally
        {
            cancelledStore.ZeroSnapshots();
            faultedStore.ZeroSnapshots();
        }
    }

    [TestMethod]
    public async Task EmptySourceIdentifierIsRejectedBeforeStoreMutation()
    {
        var store = new NonConformingPayloadProbeSecretStore();
        var service = new SourceDraftProtectionService(store);

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await service.ProtectRemotePlaylistAsync(
                default,
                "Source",
                "https://example.test/list.m3u"));

        Assert.AreEqual(0, store.CredentialsCreateCount);
        Assert.AreEqual(0, store.LocatorCreateCount);
    }

    private static byte[] BuildExpectedCredentialsPayload(
        string locator,
        string username,
        string password)
    {
        int locatorByteCount = Encoding.UTF8.GetByteCount(locator);
        int usernameByteCount = Encoding.UTF8.GetByteCount(username);
        int passwordByteCount = Encoding.UTF8.GetByteCount(password);
        byte[] payload = GC.AllocateUninitializedArray<byte>(
            8 + 1 + (3 * sizeof(int)) + locatorByteCount + usernameByteCount + passwordByteCount);
        Span<byte> destination = payload;
        "SRCRED01"u8.CopyTo(destination);
        destination[8] = 1;
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(9, sizeof(int)), locatorByteCount);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(13, sizeof(int)), usernameByteCount);
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(17, sizeof(int)), passwordByteCount);
        int offset = 21;
        offset += Encoding.UTF8.GetBytes(locator.AsSpan(), destination[offset..]);
        offset += Encoding.UTF8.GetBytes(username.AsSpan(), destination[offset..]);
        _ = Encoding.UTF8.GetBytes(password.AsSpan(), destination[offset..]);
        return payload;
    }

    private static byte[] BuildExpectedLocatorPayload(string locator)
    {
        int locatorByteCount = Encoding.UTF8.GetByteCount(locator);
        byte[] payload = GC.AllocateUninitializedArray<byte>(8 + 1 + sizeof(int) + locatorByteCount);
        Span<byte> destination = payload;
        "SRCLOC01"u8.CopyTo(destination);
        destination[8] = 1;
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(9, sizeof(int)), locatorByteCount);
        _ = Encoding.UTF8.GetBytes(locator.AsSpan(), destination[13..]);
        return payload;
    }

    private static bool Matches(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);

    private static bool IsZeroed(ReadOnlyMemory<byte> value) =>
        value.Span.IndexOfAnyExcept((byte)0) < 0;

    // Deliberately violates the borrowed-memory lifetime contract so tests can prove
    // that the service zeroes its payload after every completion path. It is a probe,
    // not a conforming ISecretStore fake and must never be reused as contract evidence.
    private sealed class NonConformingPayloadProbeSecretStore : ISecretStore
    {
        internal int CredentialsCreateCount { get; private set; }

        internal int LocatorCreateCount { get; private set; }

        internal SecretStoreFailure CredentialsFailure { get; init; }

        internal SecretStoreFailure LocatorFailure { get; init; }

        internal CancellationTokenSource? CancellationToSignalAfterLocatorCommit { get; init; }

        internal CancellationTokenSource? CancellationToSignalAfterCredentialsCommit { get; init; }

        internal Exception? CredentialsException { get; init; }

        internal Exception? LocatorException { get; init; }

        internal List<SourceId> CredentialSources { get; } = [];

        internal List<ProtectedRecordOwner> CredentialOwners { get; } = [];

        internal List<SourceId> LocatorSources { get; } = [];

        internal List<ProtectedRecordOwner> LocatorOwners { get; } = [];

        internal List<ProtectedValuePurpose> LocatorPurposes { get; } = [];

        internal List<SecretReference> IssuedCredentialReferences { get; } = [];

        internal List<ProtectedLocatorReference> IssuedLocatorReferences { get; } = [];

        internal List<ReadOnlyMemory<byte>> BorrowedPayloads { get; } = [];

        internal List<byte[]> PayloadSnapshots { get; } = [];

        public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CredentialsCreateCount++;
            CredentialSources.Add(sourceId);
            CredentialOwners.Add(owner);
            Capture(value);
            if (CredentialsException is not null)
            {
                throw CredentialsException;
            }

            if (CredentialsFailure is not SecretStoreFailure.None)
            {
                return ValueTask.FromResult(
                    SecretReferenceCreationResult.Failed(CredentialsFailure));
            }

            SecretReference reference = SourceDraftTestFixtures.CreateSecretReference();
            IssuedCredentialReferences.Add(reference);
            CancellationToSignalAfterCredentialsCommit?.Cancel();
            return ValueTask.FromResult(SecretReferenceCreationResult.Succeeded(reference));
        }

        public ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocatorCreateCount++;
            LocatorSources.Add(sourceId);
            LocatorPurposes.Add(purpose);
            LocatorOwners.Add(owner);
            Capture(value);
            if (LocatorException is not null)
            {
                throw LocatorException;
            }

            if (LocatorFailure is not SecretStoreFailure.None)
            {
                return ValueTask.FromResult(
                    ProtectedLocatorReferenceCreationResult.Failed(LocatorFailure));
            }

            ProtectedLocatorReference reference = SourceDraftTestFixtures.CreateLocatorReference();
            IssuedLocatorReferences.Add(reference);
            CancellationToSignalAfterLocatorCommit?.Cancel();
            return ValueTask.FromResult(ProtectedLocatorReferenceCreationResult.Succeeded(reference));
        }

        public ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreReadResult> ReadLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) => throw UnexpectedOperation();

        internal void ZeroSnapshots()
        {
            foreach (byte[] payload in PayloadSnapshots)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        private static InvalidOperationException UnexpectedOperation() =>
            new("The source-draft protection service called an unexpected store operation.");

        private void Capture(ReadOnlyMemory<byte> value)
        {
            BorrowedPayloads.Add(value);
            PayloadSnapshots.Add(value.ToArray());
        }
    }
}
