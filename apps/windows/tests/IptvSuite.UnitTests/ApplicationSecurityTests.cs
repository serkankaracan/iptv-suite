using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class ApplicationSecurityTests
{
    [TestMethod]
    public void SecretLeaseHidesSensitiveValueFromObservableSurfaces()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("LEASE-SURFACES");
        byte[] bytes = Encoding.UTF8.GetBytes(sensitive);

        using SecretLease lease = SecretLease.CopyFrom(bytes);
        string serialized = JsonSerializer.Serialize(lease);
        string serializedResult = JsonSerializer.Serialize(SecretStoreReadResult.Succeeded(lease));

        SecurityTestAssertions.DoesNotContainSensitive(lease.ToString(), sensitive);
        SecurityTestAssertions.DoesNotContainSensitive(serialized, sensitive);
        SecurityTestAssertions.DoesNotContainSensitive(serializedResult, sensitive);
        StringAssert.Contains(serialized, "[SENSITIVE]");
        StringAssert.Contains(serializedResult, "[SENSITIVE]");
        Array.Clear(bytes);
    }

    [TestMethod]
    public void SecretLeaseZeroesOwnedMemoryAndRejectsAccessAfterDispose()
    {
        byte[] input = [11, 22, 33, 44];
        SecretLease lease = SecretLease.CopyFrom(input);
        ReadOnlyMemory<byte> observed = lease.Value;

        lease.Dispose();

        Assert.IsTrue(observed.Span.ToArray().All(value => value == 0));
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = lease.Value);
        CollectionAssert.AreEqual(new byte[] { 11, 22, 33, 44 }, input);
    }

    [TestMethod]
    public void SecretLeaseFinalizerZeroesOwnedMemoryWhenDisposeIsMissed()
    {
        (WeakReference abandonedLease, ReadOnlyMemory<byte> observed) = CreateAbandonedLease();

        for (int attempt = 0; attempt < 3 && observed.Span.ToArray().Any(value => value != 0); attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        GC.KeepAlive(abandonedLease);
        Assert.IsTrue(observed.Span.ToArray().All(value => value == 0));
    }

    [TestMethod]
    public void SecretLeaseRejectsValuesOutsideStoreBounds()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SecretLease.CopyFrom(ReadOnlySpan<byte>.Empty));

        byte[] oversized = new byte[SecretStoreLimits.MaxProtectedValueBytes + 1];
        try
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SecretLease.CopyFrom(oversized));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oversized);
        }
    }

    [TestMethod]
    public void SecretLeaseRejectsAndZeroesInvalidTransferredOwnership()
    {
        MethodInfo takeOwnership = typeof(SecretLease).GetMethod(
            "TakeOwnership",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        byte[] oversized = Enumerable.Repeat(
            (byte)0xA5,
            SecretStoreLimits.MaxProtectedValueBytes + 1).ToArray();

        TargetInvocationException exception = Assert.ThrowsExactly<TargetInvocationException>(() =>
            takeOwnership.Invoke(null, [oversized]));

        Assert.IsInstanceOfType<ArgumentOutOfRangeException>(exception.InnerException);
        Assert.IsTrue(oversized.All(value => value == 0));
    }

    [TestMethod]
    public void SecretLeaseHidesSensitiveMembersFromDefaultDebuggerExpansion()
    {
        FieldInfo buffer = typeof(SecretLease).GetField(
            "_buffer",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        PropertyInfo value = typeof(SecretLease).GetProperty(nameof(SecretLease.Value))!;

        Assert.AreEqual(
            DebuggerBrowsableState.Never,
            buffer.GetCustomAttribute<DebuggerBrowsableAttribute>()?.State);
        Assert.AreEqual(
            DebuggerBrowsableState.Never,
            value.GetCustomAttribute<DebuggerBrowsableAttribute>()?.State);
    }

    [TestMethod]
    public void DiagnosticSanitizerNeverEchoesExceptionOrUntrustedInput()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("SANITIZER");
        Exception exception = new InvalidOperationException(sensitive);

        string sanitizedException = DiagnosticSanitizer.SanitizeException(exception);
        string sanitizedText = DiagnosticSanitizer.SanitizeUntrustedText(sensitive);
        string sanitizedHeader = DiagnosticSanitizer.SanitizeHeader("X-Api-Key", sensitive);

        SecurityTestAssertions.DoesNotContainSensitive(
            string.Join('|', sanitizedException, sanitizedText, sanitizedHeader),
            sensitive);
        Assert.AreEqual("exception;classification=unexpected", sanitizedException);
        Assert.AreEqual("header;classification=secret;value=redacted", sanitizedHeader);
    }

    [TestMethod]
    public void StoreResultFactoriesRejectContradictoryFailureStates()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            SecretStoreInitializationResult.Succeeded(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SecretStoreInitializationResult.Failed(SecretStoreFailure.None));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SecretStoreInitializationResult.Failed(SecretStoreFailure.ProtectedRecordUnavailable));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SecretStoreOperationResult.Failed(SecretStoreFailure.None));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SecretReferenceCreationResult.Failed(SecretStoreFailure.None));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ProtectedLocatorReferenceCreationResult.Failed(SecretStoreFailure.None));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SecretStoreReadResult.Failed(SecretStoreFailure.None));
    }

    [TestMethod]
    public void StoreInitializationResultDoesNotExposeStoreOrDiagnosticContext()
    {
        string sensitive = SecurityTestAssertions.CreateSensitiveValue("STORE-INITIALIZATION");
        var store = new StubSecretStore(sensitive);
        SecretStoreInitializationResult succeeded = SecretStoreInitializationResult.Succeeded(store);
        SecretStoreInitializationResult failed = SecretStoreInitializationResult.Failed(
            SecretStoreFailure.StorageUnavailable);

        Assert.IsTrue(succeeded.IsSuccess);
        Assert.AreSame(store, succeeded.Store);
        Assert.AreEqual(SecretStoreFailure.None, succeeded.Failure);
        Assert.IsFalse(failed.IsSuccess);
        Assert.IsNull(failed.Store);
        Assert.AreEqual(SecretStoreFailure.StorageUnavailable, failed.Failure);

        string observable = string.Join(
            '|',
            succeeded,
            failed,
            JsonSerializer.Serialize(succeeded),
            JsonSerializer.Serialize(failed));
        SecurityTestAssertions.DoesNotContainSensitive(observable, sensitive);
        Assert.IsFalse(observable.Contains(nameof(SecretStoreInitializationResult.Store), StringComparison.Ordinal));
        StringAssert.Contains(observable, nameof(SecretStoreFailure.StorageUnavailable));
        Assert.AreEqual(
            DebuggerBrowsableState.Never,
            typeof(SecretStoreInitializationResult)
                .GetProperty(nameof(SecretStoreInitializationResult.Store))!
                .GetCustomAttribute<DebuggerBrowsableAttribute>()?
                .State);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Lease, ReadOnlyMemory<byte> Observed) CreateAbandonedLease()
    {
        byte[] input = [91, 82, 73, 64];
        SecretLease lease = SecretLease.CopyFrom(input);
        ReadOnlyMemory<byte> observed = lease.Value;
        CryptographicOperations.ZeroMemory(input);
        return (new WeakReference(lease), observed);
    }

    private sealed class StubSecretStore : ISecretStore
    {
        private readonly string _diagnosticContext;

        internal StubSecretStore(string diagnosticContext)
        {
            _diagnosticContext = diagnosticContext;
        }

        public override string ToString() => _diagnosticContext;

        public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<SecretStoreReadResult> ReadLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
            SourceId sourceId,
            ProtectedValuePurpose purpose,
            ProtectedRecordOwner owner,
            ProtectedLocatorReference reference,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
