namespace IptvSuite.DpapiUserBoundaryHarness;

internal enum HarnessExitCode
{
    Success = 0,
    InvalidInvocation = 2,
    UnsupportedRuntime = 3,
    WorkspaceRejected = 10,
    IdentityRejected = 11,
    ProtocolRejected = 12,
    FileSystemRejected = 13,
    RawDpapiBoundaryFailed = 14,
    AdapterBoundaryFailed = 15,
    VerificationFailed = 16,
    ReleaseBarrierTimedOut = 17,
    UnexpectedFailure = 18,
}

internal sealed class HarnessFailureException : Exception
{
    internal HarnessFailureException(HarnessExitCode exitCode)
        : base("The DPAPI user-boundary harness rejected the operation.")
    {
        ExitCode = exitCode;
    }

    internal HarnessExitCode ExitCode { get; }
}
