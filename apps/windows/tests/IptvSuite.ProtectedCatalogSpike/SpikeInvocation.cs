namespace IptvSuite.ProtectedCatalogSpike;

internal enum SpikeMode
{
    Smoke,
    Decision,
}

internal sealed record SpikeInvocation(SpikeMode Mode)
{
    private const string DecisionAcknowledgement = "--acknowledge-long-running-decision";

    internal static bool TryParse(string[] arguments, out SpikeInvocation? invocation)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        invocation = null;

        if (arguments is ["--mode", string smoke] &&
            string.Equals(smoke, nameof(SpikeMode.Smoke), StringComparison.OrdinalIgnoreCase))
        {
            invocation = new SpikeInvocation(SpikeMode.Smoke);
            return true;
        }

        if (arguments is ["--mode", string decision, DecisionAcknowledgement] &&
            string.Equals(decision, nameof(SpikeMode.Decision), StringComparison.OrdinalIgnoreCase))
        {
            invocation = new SpikeInvocation(SpikeMode.Decision);
            return true;
        }

        return false;
    }
}
