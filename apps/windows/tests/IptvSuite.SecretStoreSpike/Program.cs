namespace IptvSuite.SecretStoreSpike;

internal static class Program
{
    private const int SuccessExitCode = 0;
    private const int InvalidInvocationExitCode = 2;
    private const int ExecutionFailureExitCode = 3;

    public static async Task<int> Main(string[] arguments)
    {
        if (!SpikeInvocation.TryParse(arguments, out SpikeInvocation? invocation) || invocation is null)
        {
            Console.Error.WriteLine("M4 secret-store spike rejected; failure=invalid-invocation.");
            return InvalidInvocationExitCode;
        }

        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            {
                Console.Error.WriteLine("M4 secret-store spike rejected; failure=unsupported-runtime.");
                return InvalidInvocationExitCode;
            }

            await SecretStoreSpikeRunner.RunAsync(invocation, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"M4 secret-store spike passed; mode={invocation.Mode}; evidence=written.");
            return SuccessExitCode;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("M4 secret-store spike failed; failure=cancelled.");
            return ExecutionFailureExitCode;
        }
        catch (Exception)
        {
            // Never surface exception messages: they can contain a local path or protected-store context.
            Console.Error.WriteLine("M4 secret-store spike failed; failure=execution-failed.");
            return ExecutionFailureExitCode;
        }
    }
}
