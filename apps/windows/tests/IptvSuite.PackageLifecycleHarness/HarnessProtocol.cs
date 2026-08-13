using System.Globalization;

namespace IptvSuite.PackageLifecycleHarness;

internal enum HarnessPhase
{
    Create = 1,
    VerifyDelete = 2,
}

internal enum HarnessFailure
{
    None = 0,
    InvalidArguments = 1,
    UnsafePath = 2,
    TicketMissing = 3,
    TicketInvalid = 4,
    InvalidState = 5,
    ProtectedStorageUnavailable = 6,
    CreateFailed = 7,
    InitialReadFailed = 8,
    WrongOwnerReadAccepted = 9,
    WrongOwnerDeleteFailed = 10,
    CorrectRecordDamaged = 11,
    UpdateFailed = 12,
    UpdatedReadFailed = 13,
    DeleteFailed = 14,
    PostDeleteReadAccepted = 15,
    ResultWriteFailed = 16,
    ReleaseTimedOut = 17,
    UnexpectedFailure = 18,
}

internal static class HarnessExitCode
{
    internal const int Success = 0;
    internal const int InvalidArguments = 64;
    internal const int UnsafeState = 65;
    internal const int ProtectedStorageFailure = 66;
    internal const int OperationFailure = 67;
    internal const int ReleaseFailure = 68;
    internal const int UnexpectedFailure = 70;
}

internal readonly record struct HarnessInvocation(HarnessPhase Phase, Guid RunId)
{
    internal string RunDirectoryName => RunId.ToString("N", CultureInfo.InvariantCulture);

    internal static bool TryParse(string? arguments, out HarnessInvocation invocation)
    {
        invocation = default;

        if (arguments is null || arguments.Length is < 54 or > 80)
        {
            return false;
        }

        string[] segments = arguments.Split(' ', StringSplitOptions.None);

        if (segments.Length != 4 ||
            !string.Equals(segments[0], "--phase", StringComparison.Ordinal) ||
            !string.Equals(segments[2], "--run-id", StringComparison.Ordinal) ||
            segments[3].Length != 32 ||
            !Guid.TryParseExact(segments[3], "N", out Guid runId) ||
            runId == Guid.Empty)
        {
            return false;
        }

        HarnessPhase phase = segments[1] switch
        {
            "create" => HarnessPhase.Create,
            "verify-delete" => HarnessPhase.VerifyDelete,
            _ => default,
        };

        if (phase == default)
        {
            return false;
        }

        invocation = new HarnessInvocation(phase, runId);
        return true;
    }
}

internal sealed record HarnessPhaseResult
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public HarnessPhase Phase { get; init; }

    public bool Succeeded { get; init; }

    public HarnessFailure Failure { get; init; }

    public bool CreateCommitted { get; init; }

    public bool DuplicateCreateSuppressed { get; init; }

    public bool InitialReadVerified { get; init; }

    public bool WrongOwnerReadRejected { get; init; }

    public bool WrongOwnerDeleteIdempotent { get; init; }

    public bool CorrectRecordSurvivedWrongOwnerDelete { get; init; }

    public bool UpdateCommitted { get; init; }

    public bool UpdatedReadVerified { get; init; }

    public bool DeleteCommitted { get; init; }

    public bool PostDeleteUnavailable { get; init; }

    public bool TicketRemoved { get; init; }
}
