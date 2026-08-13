using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace IptvSuite.SecretStoreSpike;

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
    string RunnerAssemblySha256,
    string LicenseExpression,
    string LicenseStatus);

internal sealed record SpikeEnvironmentEvidence(
    RuntimeEvidence Runtime,
    RepositoryEvidence Repository,
    InputEvidence Inputs);

internal static class SpikeEnvironmentEvidenceCollector
{
    private const string ValidatedSdkEnvironmentVariable = "IPTVSUITE_SPIKE_VALIDATED_SDK";
    private const string RunnerAssemblySha256EnvironmentVariable =
        "IPTVSUITE_SPIKE_RUNNER_ASSEMBLY_SHA256";

    internal static async Task<SpikeEnvironmentEvidence> CollectAsync(
        SafeSpikeWorkspace workspace,
        SpikeSpecification specification,
        SpikeMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(specification);
        string expectedSdk = await ReadExpectedSdkAsync(workspace.GlobalJsonPath, cancellationToken)
            .ConfigureAwait(false);
        string? validatedSdk = Environment.GetEnvironmentVariable(ValidatedSdkEnvironmentVariable);
        bool sdkValidated = string.Equals(validatedSdk, expectedSdk, StringComparison.Ordinal);
        if (!sdkValidated)
        {
            throw new InvalidOperationException("The spike requires an exact-SDK wrapper invocation.");
        }

        (string commit, bool isDirty) = await ReadRepositoryStateAsync(
            workspace.RepositoryRoot,
            cancellationToken).ConfigureAwait(false);
        bool decisionEligible = !isDirty &&
            OperatingSystem.IsWindows() &&
            Environment.Is64BitProcess &&
            sdkValidated;
        if (mode is SpikeMode.Decision && !decisionEligible)
        {
            throw new InvalidOperationException("Decision mode requires an eligible clean repository state.");
        }

        string specificationHash = await ComputeSha256Async(
            workspace.SpecificationPath,
            cancellationToken).ConfigureAwait(false);
        string licenseHash = await ComputeSha256Async(
            workspace.LicensePath,
            cancellationToken).ConfigureAwait(false);
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyHash = await ComputeSha256Async(assemblyPath, cancellationToken).ConfigureAwait(false);
        string? expectedAssemblyHash = Environment.GetEnvironmentVariable(
            RunnerAssemblySha256EnvironmentVariable);
        if (!IsLowerHexSha256(expectedAssemblyHash) ||
            !string.Equals(assemblyHash, expectedAssemblyHash, StringComparison.Ordinal))
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
                specificationHash,
                licenseHash,
                assemblyHash,
                specification.License.Expression,
                specification.License.Status));
    }

    internal static async Task AssertRepositoryStateUnchangedAsync(
        SafeSpikeWorkspace workspace,
        RepositoryEvidence initialState,
        SpikeMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(initialState);
        if (mode is not SpikeMode.Decision)
        {
            return;
        }

        (string commit, bool isDirty) = await ReadRepositoryStateAsync(
            workspace.RepositoryRoot,
            cancellationToken).ConfigureAwait(false);
        if (isDirty ||
            !string.Equals(commit, initialState.Commit, StringComparison.Ordinal) ||
            !initialState.DecisionEligible)
        {
            throw new InvalidOperationException("The repository changed during the decision spike.");
        }
    }

    private static async Task<string> ReadExpectedSdkAsync(
        string globalJsonPath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(globalJsonPath);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement sdk = document.RootElement.GetProperty("sdk");
        string? version = sdk.GetProperty("version").GetString();
        string? rollForward = sdk.GetProperty("rollForward").GetString();
        bool allowPrerelease = sdk.GetProperty("allowPrerelease").GetBoolean();

        if (string.IsNullOrWhiteSpace(version) ||
            !string.Equals(rollForward, "disable", StringComparison.Ordinal) ||
            allowPrerelease)
        {
            throw new InvalidDataException("The repository exact-SDK contract is invalid.");
        }

        return version;
    }

    private static async Task<(string Commit, bool IsDirty)> ReadRepositoryStateAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        string commit = (await InvokeGitAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "HEAD"],
            cancellationToken).ConfigureAwait(false)).Trim();
        if (commit.Length != 40 || commit.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The repository commit could not be validated.");
        }

        string status = await InvokeGitAsync(
            repositoryRoot,
            ["status", "--porcelain=v1", "--untracked-files=normal"],
            cancellationToken).ConfigureAwait(false);
        return (commit.ToLowerInvariant(), status.Length != 0);
    }

    private static async Task<string> InvokeGitAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryRoot,
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
            throw new InvalidOperationException("The repository state probe could not start.");
        string standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        _ = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("The repository state probe failed.");
        }

        return standardOutput;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
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

    private static bool IsLowerHexSha256(string? value)
    {
        return value is { Length: 64 } &&
            value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
