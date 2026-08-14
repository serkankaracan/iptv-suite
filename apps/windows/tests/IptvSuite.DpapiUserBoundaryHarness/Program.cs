using System.Security.Cryptography;

namespace IptvSuite.DpapiUserBoundaryHarness;

internal static class Program
{
    internal static async Task<int> Main(string[] arguments)
    {
        if (!HarnessInvocation.TryParse(arguments, out HarnessInvocation? invocation) || invocation is null)
        {
            return (int)HarnessExitCode.InvalidInvocation;
        }

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            return (int)HarnessExitCode.UnsupportedRuntime;
        }

        try
        {
            await DpapiUserBoundaryRunner.RunAsync(invocation).ConfigureAwait(false);
            return (int)HarnessExitCode.Success;
        }
        catch (HarnessFailureException exception)
        {
            return (int)exception.ExitCode;
        }
        catch (PlatformNotSupportedException)
        {
            return (int)HarnessExitCode.UnsupportedRuntime;
        }
        catch (InvalidDataException)
        {
            return (int)HarnessExitCode.ProtocolRejected;
        }
        catch (UnauthorizedAccessException)
        {
            return (int)HarnessExitCode.FileSystemRejected;
        }
        catch (IOException)
        {
            return (int)HarnessExitCode.FileSystemRejected;
        }
        catch (CryptographicException)
        {
            return (int)HarnessExitCode.RawDpapiBoundaryFailed;
        }
        catch (Exception)
        {
            // Never emit exception details: paths, identities, or protected-store context may be present.
            return (int)HarnessExitCode.UnexpectedFailure;
        }
    }
}
