namespace IptvSuite.DpapiUserBoundaryHarness;

internal enum HarnessMode
{
    PreparePrimary,
    ProbeSecondary,
    VerifyPrimary,
    ProtocolSelfTest,
}

internal sealed record HarnessInvocation(HarnessMode Mode, string WorkspacePath, string? SecondarySid)
{
    internal static bool TryParse(string[] arguments, out HarnessInvocation? invocation)
    {
        invocation = null;

        if (arguments.Length == 3 &&
            string.Equals(arguments[0], "prepare-primary", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(arguments[1]) &&
            !string.IsNullOrWhiteSpace(arguments[2]))
        {
            invocation = new HarnessInvocation(HarnessMode.PreparePrimary, arguments[1], arguments[2]);
            return true;
        }

        if (arguments.Length != 2 || string.IsNullOrWhiteSpace(arguments[1]))
        {
            return false;
        }

        HarnessMode? mode = arguments[0] switch
        {
            "probe-secondary" => HarnessMode.ProbeSecondary,
            "verify-primary" => HarnessMode.VerifyPrimary,
            "protocol-self-test" => HarnessMode.ProtocolSelfTest,
            _ => null,
        };

        if (mode is null)
        {
            return false;
        }

        invocation = new HarnessInvocation(mode.Value, arguments[1], null);
        return true;
    }
}
