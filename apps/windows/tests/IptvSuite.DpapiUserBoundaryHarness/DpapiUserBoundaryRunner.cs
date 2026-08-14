using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using IptvSuite.Testing;

namespace IptvSuite.DpapiUserBoundaryHarness;

[SupportedOSPlatform("windows")]
internal static class DpapiUserBoundaryRunner
{
    private const string CanaryScope = "M4_DPAPI_USER_BOUNDARY";
    private const string PrimaryCanaryCase = "PRIMARY_PAYLOAD";
    private const string SecondaryCanaryCase = "SECONDARY_PAYLOAD";

    internal static Task RunAsync(HarnessInvocation invocation) => invocation.Mode switch
    {
        HarnessMode.PreparePrimary => PreparePrimaryAsync(
            invocation.WorkspacePath,
            invocation.SecondarySid ?? throw new HarnessFailureException(HarnessExitCode.InvalidInvocation)),
        HarnessMode.ProbeSecondary => ProbeSecondaryAsync(invocation.WorkspacePath),
        HarnessMode.VerifyPrimary => VerifyPrimaryAsync(invocation.WorkspacePath),
        HarnessMode.ProtocolSelfTest => RunProtocolSelfTestAsync(invocation.WorkspacePath),
        _ => throw new HarnessFailureException(HarnessExitCode.InvalidInvocation),
    };

    private static async Task PreparePrimaryAsync(string workspacePath, string secondarySid)
    {
        if (!IdentityBoundary.IsCanonicalAccountSid(secondarySid))
        {
            throw new HarnessFailureException(HarnessExitCode.IdentityRejected);
        }

        BoundaryIdentity identity = BoundaryIdentity.Capture();

        if (string.Equals(identity.Sid, secondarySid, StringComparison.Ordinal))
        {
            throw new HarnessFailureException(HarnessExitCode.IdentityRejected);
        }

        BoundaryWorkspace workspace = BoundaryWorkspace.OpenForPrepare(workspacePath);
        byte[] primaryPayload = CreateCanaryPayload(PrimaryCanaryCase);
        byte[] entropy = RandomNumberGenerator.GetBytes(BoundaryTicket.EntropyLength);
        byte[]? protectedRaw = null;
        byte[]? rawRoundTrip = null;
        byte[]? rawDigest = null;
        byte[]? recordDigest = null;
        byte[]? encodedTicket = null;

        try
        {
            protectedRaw = ProtectedData.Protect(primaryPayload, entropy, DataProtectionScope.CurrentUser);

            if (protectedRaw.Length is <= 0 or > BoundaryTicket.MaximumProtectedFileBytes)
            {
                throw new HarnessFailureException(HarnessExitCode.RawDpapiBoundaryFailed);
            }

            rawRoundTrip = ProtectedData.Unprotect(protectedRaw, entropy, DataProtectionScope.CurrentUser);

            if (!FixedEquals(rawRoundTrip, primaryPayload))
            {
                throw new HarnessFailureException(HarnessExitCode.RawDpapiBoundaryFailed);
            }

            workspace.WriteFile(
                workspace.RawPath,
                protectedRaw,
                BoundaryTicket.MaximumProtectedFileBytes);
            rawDigest = SHA256.HashData(protectedRaw);

            var store = new DpapiCurrentUserSecretStore(workspace.PrimaryStorePath);
            SourceId sourceId = SourceId.Generate();
            SourceConfigurationId configurationId = SourceConfigurationId.Generate();
            ProtectedRecordOwner owner = ProtectedRecordOwner.ForSourceConfiguration(configurationId);
            SecretReferenceCreationResult creation = await store.CreateCredentialsAsync(
                sourceId,
                owner,
                primaryPayload).ConfigureAwait(false);

            if (!creation.IsSuccess || creation.Reference is null)
            {
                throw new HarnessFailureException(HarnessExitCode.AdapterBoundaryFailed);
            }

            if (!await ReadMatchesAsync(
                    store,
                    sourceId,
                    owner,
                    creation.Reference,
                    primaryPayload).ConfigureAwait(false))
            {
                throw new HarnessFailureException(HarnessExitCode.AdapterBoundaryFailed);
            }

            string recordPath = workspace.GetSinglePrimaryRecordPath();
            byte[] recordBytes = workspace.ReadBoundedFile(
                recordPath,
                BoundaryTicket.MaximumProtectedFileBytes);
            int recordLength = recordBytes.Length;

            try
            {
                recordDigest = SHA256.HashData(recordBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(recordBytes);
            }

            using BoundaryTicket ticket = BoundaryTicket.Create(
                workspace.RunId,
                identity.Sid,
                secondarySid,
                sourceId.Value,
                configurationId.Value,
                creation.Reference,
                entropy,
                protectedRaw.Length,
                rawDigest,
                Path.GetFileName(recordPath),
                recordLength,
                recordDigest);
            encodedTicket = ticket.Serialize();

            using (BoundaryTicket parsed = BoundaryTicket.Deserialize(encodedTicket, workspace.RunId))
            {
                if (!parsed.GetReference().Equals(creation.Reference))
                {
                    throw new HarnessFailureException(HarnessExitCode.ProtocolRejected);
                }
            }

            workspace.WriteFile(
                workspace.TicketPath,
                encodedTicket,
                BoundaryTicket.MaximumEncodedBytes);
            workspace.ValidateBeforeProbe(Path.GetFileName(recordPath));

            if (!AreCanariesAbsent(workspace.RootPath))
            {
                throw new HarnessFailureException(HarnessExitCode.VerificationFailed);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(primaryPayload);
            CryptographicOperations.ZeroMemory(entropy);
            Clear(protectedRaw);
            Clear(rawRoundTrip);
            Clear(rawDigest);
            Clear(recordDigest);
            Clear(encodedTicket);
        }
    }

    private static async Task ProbeSecondaryAsync(string workspacePath)
    {
        BoundaryWorkspace workspace = BoundaryWorkspace.OpenExisting(workspacePath);
        byte[] encodedTicket = workspace.ReadBoundedFile(
            workspace.TicketPath,
            BoundaryTicket.MaximumEncodedBytes);

        try
        {
            using BoundaryTicket ticket = BoundaryTicket.Deserialize(encodedTicket, workspace.RunId);
            workspace.ValidateBeforeProbe(ticket.RecordFileName);
            BoundaryIdentity identity = BoundaryIdentity.Capture();

            if (!string.Equals(identity.Sid, ticket.SecondarySid, StringComparison.Ordinal) ||
                string.Equals(identity.Sid, ticket.CreatorSid, StringComparison.Ordinal) ||
                identity.IsAdministrator)
            {
                throw new HarnessFailureException(HarnessExitCode.IdentityRejected);
            }

            ProbeEvidence evidence = ProbeEvidence.ExpectedSecondarySid |
                ProbeEvidence.DistinctFromCreatorSid |
                ProbeEvidence.SecondaryIsNonAdministrator;
            byte[] raw = workspace.ReadBoundedFile(
                workspace.RawPath,
                BoundaryTicket.MaximumProtectedFileBytes,
                ticket.RawLength);
            byte[]? rawDigest = null;
            byte[]? recordDigestBefore = null;
            byte[]? recordDigestAfter = null;
            byte[]? ticketDigest = null;

            try
            {
                rawDigest = SHA256.HashData(raw);
                evidence = AddIf(
                    evidence,
                    ProbeEvidence.RawInputDigestMatched,
                    CryptographicOperations.FixedTimeEquals(rawDigest, ticket.RawDigest));
                string recordPath = workspace.GetPrimaryRecordPath(ticket.RecordFileName);
                recordDigestBefore = workspace.ComputeFileDigest(recordPath, ticket.RecordLength);
                evidence = AddIf(
                    evidence,
                    ProbeEvidence.RecordInputDigestMatched,
                    CryptographicOperations.FixedTimeEquals(recordDigestBefore, ticket.RecordDigest));

                ProbeEvidence requiredInput = ProbeEvidence.RawInputDigestMatched |
                    ProbeEvidence.RecordInputDigestMatched;

                if ((evidence & requiredInput) != requiredInput)
                {
                    WriteProbeResultAndThrow(
                        workspace,
                        ticket,
                        evidence,
                        HarnessExitCode.VerificationFailed);
                }

                evidence = AddIf(
                    evidence,
                    ProbeEvidence.SecondaryRawRoundTripPassed,
                    RunSecondaryRawRoundTrip());
                evidence = AddIf(
                    evidence,
                    ProbeEvidence.CreatorRawRejectedCryptographically,
                    IsCreatorRawRejectedCryptographically(raw, ticket.Entropy));
                SecondaryAdapterOutcome secondary = await RunSecondaryAdapterRoundTripAsync(
                    workspace.SecondaryStorePath).ConfigureAwait(false);
                evidence = AddIf(
                    evidence,
                    ProbeEvidence.SecondaryAdapterRoundTripPassed,
                    secondary.RoundTripPassed);
                evidence = AddIf(
                    evidence,
                    ProbeEvidence.SecondaryStoreClean,
                    secondary.StoreClean);

                CreatorRecordOutcome creator = await ProbeCreatorRecordAsync(
                    workspace.PrimaryStorePath,
                    ticket).ConfigureAwait(false);
                evidence = AddIf(
                    evidence,
                    ProbeEvidence.CreatorRecordUnavailable,
                    creator.IsProtectedRecordUnavailable);
                evidence = AddIf(
                    evidence,
                    ProbeEvidence.CreatorRecordLeaseAbsent,
                    creator.IsLeaseAbsent);
                recordDigestAfter = workspace.ComputeFileDigest(recordPath, ticket.RecordLength);
                bool recordImmutable =
                    CryptographicOperations.FixedTimeEquals(recordDigestBefore, recordDigestAfter) &&
                    CryptographicOperations.FixedTimeEquals(recordDigestAfter, ticket.RecordDigest);
                evidence = AddIf(
                    evidence,
                    ProbeEvidence.CreatorRecordImmutable,
                    recordImmutable);
                workspace.ValidateBeforeProbe(ticket.RecordFileName);
                evidence = AddIf(
                    evidence,
                    ProbeEvidence.CanaryAbsent,
                    AreCanariesAbsent(workspace.RootPath));
                WriteProbeResult(workspace, ticket, evidence);

                if (evidence != ProbeEvidence.Required)
                {
                    throw new HarnessFailureException(ClassifyProbeFailure(evidence));
                }

                ticketDigest = ticket.TicketDigest.ToArray();
                await workspace.WaitForReleaseAsync(ticketDigest).ConfigureAwait(false);
                workspace.ValidateAfterVerify();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(raw);
                Clear(rawDigest);
                Clear(recordDigestBefore);
                Clear(recordDigestAfter);
                Clear(ticketDigest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedTicket);
        }
    }

    private static async Task VerifyPrimaryAsync(string workspacePath)
    {
        BoundaryWorkspace workspace = BoundaryWorkspace.OpenExisting(workspacePath);
        byte[] encodedTicket = workspace.ReadBoundedFile(
            workspace.TicketPath,
            BoundaryTicket.MaximumEncodedBytes);
        byte[]? encodedResult = null;
        byte[]? raw = null;
        byte[]? rawAfter = null;
        byte[]? rawDigestBefore = null;
        byte[]? rawDigestAfter = null;
        byte[]? recordDigestBefore = null;
        byte[]? recordDigestAfter = null;
        byte[]? primaryPayload = null;
        byte[]? rawPlaintext = null;
        byte[]? entropy = null;
        byte[]? release = null;

        try
        {
            using BoundaryTicket ticket = BoundaryTicket.Deserialize(encodedTicket, workspace.RunId);
            workspace.ValidateBeforeVerify(ticket.RecordFileName);
            BoundaryIdentity identity = BoundaryIdentity.Capture();

            if (!string.Equals(identity.Sid, ticket.CreatorSid, StringComparison.Ordinal) ||
                string.Equals(identity.Sid, ticket.SecondarySid, StringComparison.Ordinal))
            {
                throw new HarnessFailureException(HarnessExitCode.IdentityRejected);
            }

            encodedResult = workspace.ReadBoundedFile(
                workspace.ProbeResultPath,
                BoundaryProbeResult.EncodedLength,
                BoundaryProbeResult.EncodedLength);

            using (BoundaryProbeResult result = BoundaryProbeResult.Deserialize(
                       encodedResult,
                       workspace.RunId,
                       ticket.TicketDigest))
            {
                if (!result.IsComplete)
                {
                    throw new HarnessFailureException(HarnessExitCode.VerificationFailed);
                }
            }

            raw = workspace.ReadBoundedFile(
                workspace.RawPath,
                BoundaryTicket.MaximumProtectedFileBytes,
                ticket.RawLength);
            rawDigestBefore = SHA256.HashData(raw);
            string recordPath = workspace.GetPrimaryRecordPath(ticket.RecordFileName);
            recordDigestBefore = workspace.ComputeFileDigest(recordPath, ticket.RecordLength);

            if (!CryptographicOperations.FixedTimeEquals(rawDigestBefore, ticket.RawDigest) ||
                !CryptographicOperations.FixedTimeEquals(recordDigestBefore, ticket.RecordDigest))
            {
                throw new HarnessFailureException(HarnessExitCode.VerificationFailed);
            }

            primaryPayload = CreateCanaryPayload(PrimaryCanaryCase);
            entropy = ticket.Entropy.ToArray();
            rawPlaintext = ProtectedData.Unprotect(raw, entropy, DataProtectionScope.CurrentUser);

            if (!FixedEquals(rawPlaintext, primaryPayload))
            {
                throw new HarnessFailureException(HarnessExitCode.RawDpapiBoundaryFailed);
            }

            SourceId sourceId = RestoreSourceId(ticket.SourceId);
            SourceConfigurationId configurationId = RestoreConfigurationId(ticket.SourceConfigurationId);
            ProtectedRecordOwner owner = ProtectedRecordOwner.ForSourceConfiguration(configurationId);
            SecretReference reference = ticket.GetReference();
            var store = new DpapiCurrentUserSecretStore(workspace.PrimaryStorePath);

            if (!await ReadMatchesAsync(
                    store,
                    sourceId,
                    owner,
                    reference,
                    primaryPayload).ConfigureAwait(false))
            {
                throw new HarnessFailureException(HarnessExitCode.AdapterBoundaryFailed);
            }

            rawAfter = workspace.ReadBoundedFile(
                workspace.RawPath,
                BoundaryTicket.MaximumProtectedFileBytes,
                ticket.RawLength);
            rawDigestAfter = SHA256.HashData(rawAfter);
            recordDigestAfter = workspace.ComputeFileDigest(recordPath, ticket.RecordLength);

            if (!CryptographicOperations.FixedTimeEquals(rawDigestBefore, rawDigestAfter) ||
                !CryptographicOperations.FixedTimeEquals(rawDigestAfter, ticket.RawDigest) ||
                !CryptographicOperations.FixedTimeEquals(recordDigestBefore, recordDigestAfter) ||
                !CryptographicOperations.FixedTimeEquals(recordDigestAfter, ticket.RecordDigest) ||
                !AreCanariesAbsent(workspace.RootPath))
            {
                throw new HarnessFailureException(HarnessExitCode.VerificationFailed);
            }

            workspace.ValidateBeforeVerify(ticket.RecordFileName);

            SecretStoreOperationResult deletion = await store.DeleteCredentialsAsync(
                sourceId,
                owner,
                reference).ConfigureAwait(false);

            if (!deletion.IsSuccess ||
                !await IsUnavailableWithoutLeaseAsync(store, sourceId, owner, reference).ConfigureAwait(false))
            {
                throw new HarnessFailureException(HarnessExitCode.AdapterBoundaryFailed);
            }

            workspace.EnsureDirectoryEmpty(workspace.PrimaryStorePath);
            workspace.EnsureDirectoryEmpty(workspace.SecondaryStorePath);
            workspace.DeleteFile(workspace.RawPath);
            workspace.DeleteFile(workspace.TicketPath);
            workspace.EnsureDirectoryEmpty(workspace.InputPath);
            release = BoundaryRelease.Create(workspace.RunId, ticket.TicketDigest);
            workspace.WriteFile(
                workspace.ReleasePath,
                release,
                BoundaryRelease.EncodedLength);
            workspace.ValidateAfterVerify();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedTicket);
            Clear(encodedResult);
            Clear(raw);
            Clear(rawAfter);
            Clear(rawDigestBefore);
            Clear(rawDigestAfter);
            Clear(recordDigestBefore);
            Clear(recordDigestAfter);
            Clear(primaryPayload);
            Clear(rawPlaintext);
            Clear(entropy);
            Clear(release);
        }
    }

    private static async Task RunProtocolSelfTestAsync(string workspacePath)
    {
        BoundaryIdentity identity = BoundaryIdentity.Capture();
        string syntheticSecondarySid = CreateSyntheticDistinctSid(identity.Sid);
        await PreparePrimaryAsync(workspacePath, syntheticSecondarySid).ConfigureAwait(false);
        BoundaryWorkspace workspace = BoundaryWorkspace.OpenExisting(workspacePath);
        byte[] encodedTicket = workspace.ReadBoundedFile(
            workspace.TicketPath,
            BoundaryTicket.MaximumEncodedBytes);
        byte[]? tampered = null;
        byte[]? encodedResult = null;
        byte[]? encodedRelease = null;

        try
        {
            using BoundaryTicket ticket = BoundaryTicket.Deserialize(encodedTicket, workspace.RunId);
            workspace.ValidateBeforeProbe(ticket.RecordFileName);
            tampered = encodedTicket.ToArray();
            tampered[0] ^= 0x01;

            try
            {
                using BoundaryTicket _ = BoundaryTicket.Deserialize(tampered, workspace.RunId);
                throw new HarnessFailureException(HarnessExitCode.ProtocolRejected);
            }
            catch (InvalidDataException)
            {
            }

            using var result = new BoundaryProbeResult(
                workspace.RunId,
                ticket.TicketDigest,
                ProbeEvidence.Required);
            encodedResult = result.Serialize();

            using (BoundaryProbeResult parsedResult = BoundaryProbeResult.Deserialize(
                       encodedResult,
                       workspace.RunId,
                       ticket.TicketDigest))
            {
                if (!parsedResult.IsComplete)
                {
                    throw new HarnessFailureException(HarnessExitCode.ProtocolRejected);
                }
            }

            encodedRelease = BoundaryRelease.Create(workspace.RunId, ticket.TicketDigest);

            if (!BoundaryRelease.IsValid(encodedRelease, workspace.RunId, ticket.TicketDigest))
            {
                throw new HarnessFailureException(HarnessExitCode.ProtocolRejected);
            }

            SourceId sourceId = RestoreSourceId(ticket.SourceId);
            SourceConfigurationId configurationId = RestoreConfigurationId(ticket.SourceConfigurationId);
            ProtectedRecordOwner owner = ProtectedRecordOwner.ForSourceConfiguration(configurationId);
            SecretReference reference = ticket.GetReference();
            var store = new DpapiCurrentUserSecretStore(workspace.PrimaryStorePath);
            byte[] primaryPayload = CreateCanaryPayload(PrimaryCanaryCase);

            try
            {
                if (!await ReadMatchesAsync(
                        store,
                        sourceId,
                        owner,
                        reference,
                        primaryPayload).ConfigureAwait(false) ||
                    !(await store.DeleteCredentialsAsync(sourceId, owner, reference).ConfigureAwait(false)).IsSuccess ||
                    !await IsUnavailableWithoutLeaseAsync(store, sourceId, owner, reference).ConfigureAwait(false))
                {
                    throw new HarnessFailureException(HarnessExitCode.AdapterBoundaryFailed);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(primaryPayload);
            }

            workspace.DeleteFile(workspace.RawPath);
            workspace.DeleteFile(workspace.TicketPath);
            workspace.EnsureDirectoryEmpty(workspace.InputPath);
            workspace.EnsureDirectoryEmpty(workspace.PrimaryStorePath);
            workspace.EnsureDirectoryEmpty(workspace.SecondaryStorePath);
            workspace.EnsureDirectoryEmpty(workspace.ResultPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedTicket);
            Clear(tampered);
            Clear(encodedResult);
            Clear(encodedRelease);
        }
    }

    private static async Task<CreatorRecordOutcome> ProbeCreatorRecordAsync(
        string storePath,
        BoundaryTicket ticket)
    {
        SourceId sourceId = RestoreSourceId(ticket.SourceId);
        SourceConfigurationId configurationId = RestoreConfigurationId(ticket.SourceConfigurationId);
        ProtectedRecordOwner owner = ProtectedRecordOwner.ForSourceConfiguration(configurationId);
        SecretReference reference = ticket.GetReference();
        var store = new DpapiCurrentUserSecretStore(storePath);
        SecretStoreReadResult read = await store.ReadCredentialsAsync(
            sourceId,
            owner,
            reference).ConfigureAwait(false);
        bool unavailable = !read.IsSuccess &&
            read.Failure is SecretStoreFailure.ProtectedRecordUnavailable;
        bool leaseAbsent = read.Lease is null;
        read.Lease?.Dispose();
        return new CreatorRecordOutcome(unavailable, leaseAbsent);
    }

    private static async Task<SecondaryAdapterOutcome> RunSecondaryAdapterRoundTripAsync(string storePath)
    {
        var store = new DpapiCurrentUserSecretStore(storePath);
        SourceId sourceId = SourceId.Generate();
        SourceConfigurationId configurationId = SourceConfigurationId.Generate();
        ProtectedRecordOwner owner = ProtectedRecordOwner.ForSourceConfiguration(configurationId);
        byte[] payload = CreateCanaryPayload(SecondaryCanaryCase);
        SecretReference? reference = null;
        bool roundTripPassed = false;

        try
        {
            SecretReferenceCreationResult creation = await store.CreateCredentialsAsync(
                sourceId,
                owner,
                payload).ConfigureAwait(false);
            reference = creation.Reference;

            if (creation.IsSuccess && reference is not null)
            {
                bool readMatched = await ReadMatchesAsync(
                    store,
                    sourceId,
                    owner,
                    reference,
                    payload).ConfigureAwait(false);
                SecretStoreOperationResult deletion = await store.DeleteCredentialsAsync(
                    sourceId,
                    owner,
                    reference).ConfigureAwait(false);
                bool unavailable = await IsUnavailableWithoutLeaseAsync(
                    store,
                    sourceId,
                    owner,
                    reference).ConfigureAwait(false);
                roundTripPassed = readMatched && deletion.IsSuccess && unavailable;
            }
        }
        finally
        {
            try
            {
                if (reference is not null)
                {
                    await store.DeleteCredentialsAsync(sourceId, owner, reference).ConfigureAwait(false);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        bool storeClean = IsDirectoryExactlyEmpty(storePath);
        return new SecondaryAdapterOutcome(roundTripPassed, storeClean);
    }

    private static bool RunSecondaryRawRoundTrip()
    {
        byte[] payload = CreateCanaryPayload(SecondaryCanaryCase);
        byte[] entropy = RandomNumberGenerator.GetBytes(BoundaryTicket.EntropyLength);
        byte[]? protectedValue = null;
        byte[]? roundTrip = null;

        try
        {
            protectedValue = ProtectedData.Protect(payload, entropy, DataProtectionScope.CurrentUser);
            roundTrip = ProtectedData.Unprotect(protectedValue, entropy, DataProtectionScope.CurrentUser);
            return FixedEquals(roundTrip, payload);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(entropy);
            Clear(protectedValue);
            Clear(roundTrip);
        }
    }

    private static bool IsCreatorRawRejectedCryptographically(
        ReadOnlySpan<byte> protectedRaw,
        ReadOnlySpan<byte> entropy)
    {
        byte[] protectedCopy = protectedRaw.ToArray();
        byte[] entropyCopy = entropy.ToArray();
        byte[]? unexpectedPlaintext = null;

        try
        {
            try
            {
                unexpectedPlaintext = ProtectedData.Unprotect(
                    protectedCopy,
                    entropyCopy,
                    DataProtectionScope.CurrentUser);
                return false;
            }
            catch (CryptographicException)
            {
                return true;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedCopy);
            CryptographicOperations.ZeroMemory(entropyCopy);
            Clear(unexpectedPlaintext);
        }
    }

    private static async Task<bool> ReadMatchesAsync(
        DpapiCurrentUserSecretStore store,
        SourceId sourceId,
        ProtectedRecordOwner owner,
        SecretReference reference,
        ReadOnlyMemory<byte> expected)
    {
        SecretStoreReadResult read = await store.ReadCredentialsAsync(
            sourceId,
            owner,
            reference).ConfigureAwait(false);
        using SecretLease? lease = read.Lease;
        return read.IsSuccess && lease is not null && FixedEquals(lease.Value.Span, expected.Span);
    }

    private static async Task<bool> IsUnavailableWithoutLeaseAsync(
        DpapiCurrentUserSecretStore store,
        SourceId sourceId,
        ProtectedRecordOwner owner,
        SecretReference reference)
    {
        SecretStoreReadResult read = await store.ReadCredentialsAsync(
            sourceId,
            owner,
            reference).ConfigureAwait(false);
        bool unavailable = !read.IsSuccess &&
            read.Failure is SecretStoreFailure.ProtectedRecordUnavailable &&
            read.Lease is null;
        read.Lease?.Dispose();
        return unavailable;
    }

    private static void WriteProbeResultAndThrow(
        BoundaryWorkspace workspace,
        BoundaryTicket ticket,
        ProbeEvidence evidence,
        HarnessExitCode exitCode)
    {
        WriteProbeResult(workspace, ticket, evidence);
        throw new HarnessFailureException(exitCode);
    }

    private static void WriteProbeResult(
        BoundaryWorkspace workspace,
        BoundaryTicket ticket,
        ProbeEvidence evidence)
    {
        using var result = new BoundaryProbeResult(workspace.RunId, ticket.TicketDigest, evidence);
        byte[] encoded = result.Serialize();

        try
        {
            workspace.WriteFile(
                workspace.ProbeResultPath,
                encoded,
                BoundaryProbeResult.EncodedLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static ProbeEvidence AddIf(ProbeEvidence current, ProbeEvidence flag, bool condition) =>
        condition ? current | flag : current;

    private static HarnessExitCode ClassifyProbeFailure(ProbeEvidence evidence)
    {
        ProbeEvidence raw = ProbeEvidence.SecondaryRawRoundTripPassed |
            ProbeEvidence.CreatorRawRejectedCryptographically;
        ProbeEvidence adapter = ProbeEvidence.SecondaryAdapterRoundTripPassed |
            ProbeEvidence.SecondaryStoreClean |
            ProbeEvidence.CreatorRecordUnavailable |
            ProbeEvidence.CreatorRecordLeaseAbsent;

        if ((evidence & raw) != raw)
        {
            return HarnessExitCode.RawDpapiBoundaryFailed;
        }

        if ((evidence & adapter) != adapter)
        {
            return HarnessExitCode.AdapterBoundaryFailed;
        }

        return HarnessExitCode.VerificationFailed;
    }

    private static SourceId RestoreSourceId(Guid value)
    {
        DomainResult<SourceId> result = SourceId.Create(value);
        return result.IsSuccess && result.Value is SourceId sourceId
            ? sourceId
            : throw new InvalidDataException("The ticket source identifier is invalid.");
    }

    private static SourceConfigurationId RestoreConfigurationId(Guid value)
    {
        DomainResult<SourceConfigurationId> result = SourceConfigurationId.Create(value);
        return result.IsSuccess && result.Value is SourceConfigurationId configurationId
            ? configurationId
            : throw new InvalidDataException("The ticket configuration identifier is invalid.");
    }

    private static byte[] CreateCanaryPayload(string caseId)
    {
        TestCanary canary = TestCanary.Create(CanaryScope, caseId);
        using var stream = new MemoryStream(capacity: 256);
        try
        {
            canary.WriteTo(stream, TestCanaryEncoding.Utf8);
            return stream.ToArray();
        }
        finally
        {
            if (stream.TryGetBuffer(out ArraySegment<byte> buffer) && buffer.Array is not null)
            {
                CryptographicOperations.ZeroMemory(buffer.Array.AsSpan(0, checked((int)stream.Length)));
            }
        }
    }

    private static bool AreCanariesAbsent(string rootPath) =>
        ArtifactCanaryScanner.Scan(rootPath, TestCanary.Create(CanaryScope, PrimaryCanaryCase)).Count == 0 &&
        ArtifactCanaryScanner.Scan(rootPath, TestCanary.Create(CanaryScope, SecondaryCanaryCase)).Count == 0;

    private static bool FixedEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    private static bool IsDirectoryExactlyEmpty(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return !entries.MoveNext();
    }

    private static string CreateSyntheticDistinctSid(string currentSid)
    {
        var sid = new SecurityIdentifier(currentSid);
        SecurityIdentifier? domain = sid.AccountDomainSid;

        if (domain is null)
        {
            throw new HarnessFailureException(HarnessExitCode.IdentityRejected);
        }

        for (uint rid = 2_147_483_000; rid < 2_147_483_010; rid++)
        {
            string candidate = $"{domain.Value}-{rid}";

            if (!string.Equals(candidate, currentSid, StringComparison.Ordinal) &&
                IdentityBoundary.IsCanonicalAccountSid(candidate))
            {
                return candidate;
            }
        }

        throw new HarnessFailureException(HarnessExitCode.IdentityRejected);
    }

    private static void Clear(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private sealed record BoundaryIdentity(string Sid, bool IsAdministrator)
    {
        internal static BoundaryIdentity Capture()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier? user = identity.User;

            if (user is null || !IdentityBoundary.IsCanonicalAccountSid(user.Value))
            {
                throw new HarnessFailureException(HarnessExitCode.IdentityRejected);
            }

            bool isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            return new BoundaryIdentity(user.Value, isAdministrator);
        }
    }

    private readonly record struct CreatorRecordOutcome(
        bool IsProtectedRecordUnavailable,
        bool IsLeaseAbsent);

    private readonly record struct SecondaryAdapterOutcome(bool RoundTripPassed, bool StoreClean);
}
