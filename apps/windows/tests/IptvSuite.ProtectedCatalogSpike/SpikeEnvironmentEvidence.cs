using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace IptvSuite.ProtectedCatalogSpike;

internal sealed record RuntimeEvidence(
    string SdkVersion,
    string RuntimeVersion,
    string OperatingSystemBuild,
    string OperatingSystemArchitecture,
    string ProcessArchitecture,
    int LogicalProcessorCount);

internal sealed record RepositoryEvidence(string Commit, bool IsDirty, bool DecisionEligible);

internal sealed record InputEvidence(
    string SpecificationSha256,
    string LicenseSha256,
    string PackageLockSha256,
    string RunnerAssemblySha256,
    string TestingAssemblySha256,
    string RunnerDepsJsonSha256,
    string LicenseExpression,
    string LicenseStatus);

internal sealed record SpikeEnvironmentEvidence(
    RuntimeEvidence Runtime,
    RepositoryEvidence Repository,
    InputEvidence Inputs);

internal static class SpikeEnvironmentEvidenceCollector
{
    private const string ValidatedSdkVariable = "IPTVSUITE_PROTECTED_CATALOG_SPIKE_VALIDATED_SDK";
    private const string RunnerHashVariable =
        "IPTVSUITE_PROTECTED_CATALOG_SPIKE_RUNNER_ASSEMBLY_SHA256";

    internal static async Task<SpikeEnvironmentEvidence> CollectAsync(
        SafeSpikeWorkspace workspace,
        SpikeSpecification specification,
        SpikeMode mode,
        CancellationToken cancellationToken)
    {
        string expectedSdk = await ReadExpectedSdkAsync(workspace.GlobalJsonPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
            Environment.GetEnvironmentVariable(ValidatedSdkVariable),
            expectedSdk,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The spike requires an exact-SDK wrapper invocation.");
        }

        (string commit, bool isDirty) = await ReadRepositoryStateAsync(
            workspace.RepositoryRoot,
            cancellationToken).ConfigureAwait(false);
        bool decisionEligible = !isDirty && OperatingSystem.IsWindows() && Environment.Is64BitProcess;
        if (mode is SpikeMode.Decision && !decisionEligible)
        {
            throw new InvalidOperationException("Decision mode requires a clean eligible repository.");
        }

        string assemblyHash = await ComputeSha256Async(
            Assembly.GetExecutingAssembly().Location,
            cancellationToken).ConfigureAwait(false);
        string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ??
            throw new InvalidOperationException("The runner output directory is unavailable.");
        string testingAssemblyPath = Path.Combine(assemblyDirectory, "IptvSuite.Testing.dll");
        string runnerDepsPath = Path.Combine(assemblyDirectory, "IptvSuite.ProtectedCatalogSpike.deps.json");
        if (!File.Exists(testingAssemblyPath) || !File.Exists(runnerDepsPath))
        {
            throw new InvalidOperationException("The runner dependency evidence is unavailable.");
        }
        string? expectedAssemblyHash = Environment.GetEnvironmentVariable(RunnerHashVariable);
        if (!IsLowerHexSha256(expectedAssemblyHash) ||
            !string.Equals(expectedAssemblyHash, assemblyHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The spike runner assembly could not be validated.");
        }

        return new SpikeEnvironmentEvidence(
            new RuntimeEvidence(
                expectedSdk,
                RuntimeInformation.FrameworkDescription,
                Environment.OSVersion.Version.ToString(),
                RuntimeInformation.OSArchitecture.ToString(),
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount),
            new RepositoryEvidence(commit, isDirty, decisionEligible),
            new InputEvidence(
                await ComputeSha256Async(workspace.SpecificationPath, cancellationToken).ConfigureAwait(false),
                await ComputeSha256Async(workspace.LicensePath, cancellationToken).ConfigureAwait(false),
                await ComputeSha256Async(workspace.PackageLockPath, cancellationToken).ConfigureAwait(false),
                assemblyHash,
                await ComputeSha256Async(testingAssemblyPath, cancellationToken).ConfigureAwait(false),
                await ComputeSha256Async(runnerDepsPath, cancellationToken).ConfigureAwait(false),
                specification.License.Expression,
                specification.License.Status));
    }

    internal static async Task AssertRepositoryStateUnchangedAsync(
        SafeSpikeWorkspace workspace,
        RepositoryEvidence initial,
        SpikeMode mode,
        CancellationToken cancellationToken)
    {
        if (mode is not SpikeMode.Decision)
        {
            return;
        }

        (string commit, bool dirty) = await ReadRepositoryStateAsync(
            workspace.RepositoryRoot,
            cancellationToken).ConfigureAwait(false);
        if (dirty || !initial.DecisionEligible || !string.Equals(commit, initial.Commit, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The repository changed during the decision spike.");
        }
    }

    private static async Task<string> ReadExpectedSdkAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement sdk = document.RootElement.GetProperty("sdk");
        string? version = sdk.GetProperty("version").GetString();
        if (string.IsNullOrWhiteSpace(version) ||
            !string.Equals(sdk.GetProperty("rollForward").GetString(), "disable", StringComparison.Ordinal) ||
            sdk.GetProperty("allowPrerelease").GetBoolean())
        {
            throw new InvalidDataException("The exact-SDK contract is invalid.");
        }

        return version;
    }

    private static async Task<(string Commit, bool Dirty)> ReadRepositoryStateAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        string commit = (await InvokeGitAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "HEAD"],
            cancellationToken).ConfigureAwait(false)).Trim().ToLowerInvariant();
        if (commit.Length != 40 || commit.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The repository commit is invalid.");
        }

        string status = await InvokeGitAsync(
            repositoryRoot,
            ["status", "--porcelain=v1", "--untracked-files=normal"],
            cancellationToken).ConfigureAwait(false);
        return (commit, status.Length != 0);
    }

    private static async Task<string> InvokeGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("The repository probe could not start.");
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        _ = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("The repository probe failed.");
        }

        return output;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        try
        {
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static bool IsLowerHexSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
