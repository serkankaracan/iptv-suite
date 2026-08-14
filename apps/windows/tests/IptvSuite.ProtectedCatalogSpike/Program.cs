namespace IptvSuite.ProtectedCatalogSpike;

internal static class Program
{
    public static async Task<int> Main(string[] arguments)
    {
        if (!SpikeInvocation.TryParse(arguments, out SpikeInvocation? invocation) || invocation is null)
        {
            Console.Error.WriteLine("M4 protected-catalog spike rejected; failure=invalid-invocation.");
            return 2;
        }

        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            {
                Console.Error.WriteLine("M4 protected-catalog spike rejected; failure=unsupported-runtime.");
                return 2;
            }

            await ProtectedCatalogSpikeRunner.RunAsync(invocation, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"M4 protected-catalog spike passed; mode={invocation.Mode}; evidence=written.");
            return 0;
        }
        catch (Exception)
        {
            // Never surface exception details: they may contain protected-store or local path context.
            Console.Error.WriteLine("M4 protected-catalog spike failed; failure=execution-failed.");
            return 3;
        }
    }
}
