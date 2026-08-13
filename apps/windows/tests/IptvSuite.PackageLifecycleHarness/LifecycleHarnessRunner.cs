using System.Security.Cryptography;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;

namespace IptvSuite.PackageLifecycleHarness;

internal static class LifecycleHarnessRunner
{
    private const int SyntheticPayloadLength = 96;
    private static ReadOnlySpan<byte> SyntheticCanaryMarker =>
        "IPTVSUITE_TEST_ONLY_CANARY_V1"u8;

    internal static async Task<int> RunAsync(string? arguments)
    {
        if (!HarnessInvocation.TryParse(arguments, out HarnessInvocation invocation))
        {
            return HarnessExitCode.InvalidArguments;
        }

        HarnessFileStore files;

        try
        {
            files = HarnessFileStore.Open(invocation);
        }
        catch (Exception exception) when (IsBoundedOperationalException(exception))
        {
            return HarnessExitCode.UnsafeState;
        }

        HarnessPhaseResult result;

        try
        {
            result = invocation.Phase switch
            {
                HarnessPhase.Create => await ExecuteCreateAsync(invocation, files).ConfigureAwait(false),
                HarnessPhase.VerifyDelete =>
                    await ExecuteVerifyDeleteAsync(invocation, files).ConfigureAwait(false),
                _ => Failed(invocation.Phase, HarnessFailure.InvalidArguments),
            };
        }
        catch (CryptographicException)
        {
            result = Failed(invocation.Phase, HarnessFailure.TicketInvalid);
        }
        catch (InvalidDataException)
        {
            result = Failed(invocation.Phase, HarnessFailure.TicketInvalid);
        }
        catch (Exception exception) when (IsFileSafetyException(exception))
        {
            result = Failed(invocation.Phase, HarnessFailure.UnsafePath);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            result = Failed(invocation.Phase, HarnessFailure.UnexpectedFailure);
        }

        try
        {
            files.WriteResult(result);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            return HarnessExitCode.UnsafeState;
        }

        try
        {
            if (!await files.WaitForReleaseAsync(invocation.Phase).ConfigureAwait(false))
            {
                TryWriteReleaseFailure(files, invocation.Phase);
                return HarnessExitCode.ReleaseFailure;
            }
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            TryWriteReleaseFailure(files, invocation.Phase);
            return HarnessExitCode.ReleaseFailure;
        }

        return result.Succeeded ? HarnessExitCode.Success : GetFailureExitCode(result.Failure);
    }

    private static async Task<HarnessPhaseResult> ExecuteCreateAsync(
        HarnessInvocation invocation,
        HarnessFileStore files)
    {
        if (files.TicketExists())
        {
            using LifecycleControlTicket existing = ReadTicket(files, invocation.RunId);
            return new HarnessPhaseResult
            {
                Phase = HarnessPhase.Create,
                Failure = HarnessFailure.InvalidState,
                DuplicateCreateSuppressed = true,
            };
        }

        SourceId sourceId = SourceId.Generate();
        SourceConfigurationId sourceConfigurationId = SourceConfigurationId.Generate();
        ProtectedRecordOwner owner =
            ProtectedRecordOwner.ForSourceConfiguration(sourceConfigurationId);
        byte[] payload = CreateSyntheticPayload(discriminator: 1);
        byte[] digest = SHA256.HashData(payload);

        try
        {
            using LifecycleControlTicket ticket = LifecycleControlTicket.CreateCreating(
                invocation.RunId,
                sourceId.Value,
                sourceConfigurationId.Value,
                digest);
            WriteTicket(files, ticket, replaceExisting: false);

            var store = new DpapiCurrentUserSecretStore(files.ProtectedStoreDirectory);
            SecretReferenceCreationResult creation = await store.CreateCredentialsAsync(
                sourceId,
                owner,
                payload).ConfigureAwait(false);

            if (!creation.IsSuccess || creation.Reference is null)
            {
                return Failed(
                    HarnessPhase.Create,
                    creation.Failure is SecretStoreFailure.StorageUnavailable
                        ? HarnessFailure.ProtectedStorageUnavailable
                        : HarnessFailure.CreateFailed);
            }

            ticket.MarkCreated(creation.Reference);
            WriteTicket(files, ticket, replaceExisting: true);

            return new HarnessPhaseResult
            {
                Phase = HarnessPhase.Create,
                Succeeded = true,
                CreateCommitted = true,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static async Task<HarnessPhaseResult> ExecuteVerifyDeleteAsync(
        HarnessInvocation invocation,
        HarnessFileStore files)
    {
        if (!files.TicketExists())
        {
            return Failed(HarnessPhase.VerifyDelete, HarnessFailure.TicketMissing);
        }

        using LifecycleControlTicket ticket = ReadTicket(files, invocation.RunId);

        if (ticket.Phase is not ControlTicketPhase.Created)
        {
            return Failed(HarnessPhase.VerifyDelete, HarnessFailure.InvalidState);
        }

        DomainResult<SourceId> sourceResult = SourceId.Create(ticket.SourceId);
        DomainResult<SourceConfigurationId> configurationResult =
            SourceConfigurationId.Create(ticket.SourceConfigurationId);

        if (!sourceResult.IsSuccess || !configurationResult.IsSuccess)
        {
            return Failed(HarnessPhase.VerifyDelete, HarnessFailure.TicketInvalid);
        }

        SourceId sourceId = sourceResult.Value;
        SourceConfigurationId sourceConfigurationId = configurationResult.Value;
        ProtectedRecordOwner owner =
            ProtectedRecordOwner.ForSourceConfiguration(sourceConfigurationId);
        SecretReference reference = ticket.GetReference();
        var store = new DpapiCurrentUserSecretStore(files.ProtectedStoreDirectory);

        ticket.MarkConsuming();
        WriteTicket(files, ticket, replaceExisting: true);

        bool initialReadVerified = false;
        bool wrongOwnerReadRejected = false;
        bool wrongOwnerDeleteIdempotent = false;
        bool correctRecordSurvivedWrongOwnerDelete = false;
        bool updateCommitted = false;
        bool updatedReadVerified = false;
        bool deleteCommitted = false;
        bool postDeleteUnavailable = false;
        bool ticketRemoved = false;
        HarnessFailure failure = HarnessFailure.None;

        try
        {
            initialReadVerified = await ReadMatchesAsync(
                store,
                sourceId,
                owner,
                reference,
                ticket.PayloadDigest).ConfigureAwait(false);

            if (!initialReadVerified)
            {
                failure = HarnessFailure.InitialReadFailed;
                return BuildVerifyResult();
            }

            ProtectedRecordOwner wrongOwner = ProtectedRecordOwner.ForSourceConfiguration(
                SourceConfigurationId.Generate());
            SecretStoreReadResult wrongOwnerRead = await store.ReadCredentialsAsync(
                sourceId,
                wrongOwner,
                reference).ConfigureAwait(false);

            if (wrongOwnerRead.Lease is not null)
            {
                wrongOwnerRead.Lease.Dispose();
            }

            wrongOwnerReadRejected = !wrongOwnerRead.IsSuccess &&
                wrongOwnerRead.Failure is SecretStoreFailure.ProtectedRecordUnavailable;

            SecretStoreOperationResult wrongOwnerDelete = await store.DeleteCredentialsAsync(
                sourceId,
                wrongOwner,
                reference).ConfigureAwait(false);
            wrongOwnerDeleteIdempotent = wrongOwnerDelete.IsSuccess;

            correctRecordSurvivedWrongOwnerDelete = await ReadMatchesAsync(
                store,
                sourceId,
                owner,
                reference,
                ticket.PayloadDigest).ConfigureAwait(false);

            if (!wrongOwnerReadRejected)
            {
                failure = HarnessFailure.WrongOwnerReadAccepted;
                return BuildVerifyResult();
            }

            if (!wrongOwnerDeleteIdempotent)
            {
                failure = HarnessFailure.WrongOwnerDeleteFailed;
                return BuildVerifyResult();
            }

            if (!correctRecordSurvivedWrongOwnerDelete)
            {
                failure = HarnessFailure.CorrectRecordDamaged;
                return BuildVerifyResult();
            }

            byte[] updatedPayload = CreateSyntheticPayload(discriminator: 2);
            byte[] updatedDigest = SHA256.HashData(updatedPayload);

            try
            {
                SecretStoreOperationResult update = await store.UpdateCredentialsAsync(
                    sourceId,
                    owner,
                    reference,
                    updatedPayload).ConfigureAwait(false);
                updateCommitted = update.IsSuccess;

                if (!updateCommitted)
                {
                    failure = HarnessFailure.UpdateFailed;
                    return BuildVerifyResult();
                }

                updatedReadVerified = await ReadMatchesAsync(
                    store,
                    sourceId,
                    owner,
                    reference,
                    updatedDigest).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(updatedPayload);
                CryptographicOperations.ZeroMemory(updatedDigest);
            }

            if (!updatedReadVerified)
            {
                failure = HarnessFailure.UpdatedReadFailed;
                return BuildVerifyResult();
            }

            SecretStoreOperationResult delete = await store.DeleteCredentialsAsync(
                sourceId,
                owner,
                reference).ConfigureAwait(false);
            deleteCommitted = delete.IsSuccess;

            if (!deleteCommitted)
            {
                failure = HarnessFailure.DeleteFailed;
                return BuildVerifyResult();
            }

            SecretStoreReadResult afterDelete = await store.ReadCredentialsAsync(
                sourceId,
                owner,
                reference).ConfigureAwait(false);

            if (afterDelete.Lease is not null)
            {
                afterDelete.Lease.Dispose();
            }

            postDeleteUnavailable = !afterDelete.IsSuccess &&
                afterDelete.Failure is SecretStoreFailure.ProtectedRecordUnavailable;

            if (!postDeleteUnavailable)
            {
                failure = HarnessFailure.PostDeleteReadAccepted;
                return BuildVerifyResult();
            }

            files.DeleteTicket();
            ticketRemoved = true;
            return BuildVerifyResult();
        }
        finally
        {
            if (!postDeleteUnavailable)
            {
                await TryDeleteCorrectRecordAsync(store, sourceId, owner, reference).ConfigureAwait(false);
            }
        }

        HarnessPhaseResult BuildVerifyResult() => new()
        {
            Phase = HarnessPhase.VerifyDelete,
            Succeeded = failure is HarnessFailure.None &&
                initialReadVerified &&
                wrongOwnerReadRejected &&
                wrongOwnerDeleteIdempotent &&
                correctRecordSurvivedWrongOwnerDelete &&
                updateCommitted &&
                updatedReadVerified &&
                deleteCommitted &&
                postDeleteUnavailable &&
                ticketRemoved,
            Failure = failure,
            InitialReadVerified = initialReadVerified,
            WrongOwnerReadRejected = wrongOwnerReadRejected,
            WrongOwnerDeleteIdempotent = wrongOwnerDeleteIdempotent,
            CorrectRecordSurvivedWrongOwnerDelete = correctRecordSurvivedWrongOwnerDelete,
            UpdateCommitted = updateCommitted,
            UpdatedReadVerified = updatedReadVerified,
            DeleteCommitted = deleteCommitted,
            PostDeleteUnavailable = postDeleteUnavailable,
            TicketRemoved = ticketRemoved,
        };
    }

    private static async Task<bool> ReadMatchesAsync(
        DpapiCurrentUserSecretStore store,
        SourceId sourceId,
        ProtectedRecordOwner owner,
        SecretReference reference,
        ReadOnlyMemory<byte> expectedDigest)
    {
        byte[] expectedDigestCopy = expectedDigest.ToArray();

        try
        {
            SecretStoreReadResult read = await store.ReadCredentialsAsync(
                sourceId,
                owner,
                reference).ConfigureAwait(false);

            if (!read.IsSuccess || read.Lease is null)
            {
                read.Lease?.Dispose();
                return false;
            }

            using SecretLease lease = read.Lease;
            byte[] actualDigest = SHA256.HashData(lease.Value.Span);

            try
            {
                return CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigestCopy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualDigest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedDigestCopy);
        }
    }

    private static byte[] CreateSyntheticPayload(byte discriminator)
    {
        byte[] payload = GC.AllocateUninitializedArray<byte>(SyntheticPayloadLength);
        SyntheticCanaryMarker.CopyTo(payload);
        payload[SyntheticCanaryMarker.Length] = discriminator;
        RandomNumberGenerator.Fill(payload.AsSpan(SyntheticCanaryMarker.Length + 1));
        return payload;
    }

    private static async Task TryDeleteCorrectRecordAsync(
        DpapiCurrentUserSecretStore store,
        SourceId sourceId,
        ProtectedRecordOwner owner,
        SecretReference reference)
    {
        try
        {
            await store.DeleteCredentialsAsync(sourceId, owner, reference).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            // Best-effort cleanup deliberately emits no diagnostic or sensitive context.
        }
    }

    private static LifecycleControlTicket ReadTicket(HarnessFileStore files, Guid expectedRunId)
    {
        byte[] protectedTicket = files.ReadTicket(LifecycleControlTicket.MaximumProtectedBytes);

        try
        {
            return LifecycleControlTicket.Unprotect(protectedTicket, expectedRunId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedTicket);
        }
    }

    private static void WriteTicket(
        HarnessFileStore files,
        LifecycleControlTicket ticket,
        bool replaceExisting)
    {
        byte[] protectedTicket = ticket.Protect();

        try
        {
            files.WriteTicket(protectedTicket, replaceExisting);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedTicket);
        }
    }

    private static HarnessPhaseResult Failed(HarnessPhase phase, HarnessFailure failure) => new()
    {
        Phase = phase,
        Failure = failure,
    };

    private static int GetFailureExitCode(HarnessFailure failure) => failure switch
    {
        HarnessFailure.InvalidArguments => HarnessExitCode.InvalidArguments,
        HarnessFailure.UnsafePath or
        HarnessFailure.TicketMissing or
        HarnessFailure.TicketInvalid or
        HarnessFailure.InvalidState => HarnessExitCode.UnsafeState,
        HarnessFailure.ProtectedStorageUnavailable => HarnessExitCode.ProtectedStorageFailure,
        HarnessFailure.ReleaseTimedOut => HarnessExitCode.ReleaseFailure,
        HarnessFailure.UnexpectedFailure => HarnessExitCode.UnexpectedFailure,
        _ => HarnessExitCode.OperationFailure,
    };

    private static void TryWriteReleaseFailure(HarnessFileStore files, HarnessPhase phase)
    {
        try
        {
            files.WriteResult(Failed(phase, HarnessFailure.ReleaseTimedOut));
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            // No output is emitted when the sanitized failure channel is unavailable.
        }
    }

    private static bool IsBoundedOperationalException(Exception exception) =>
        IsFileSafetyException(exception) ||
        exception is ArgumentException or InvalidOperationException;

    private static bool IsFileSafetyException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
