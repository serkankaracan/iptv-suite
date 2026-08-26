using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.ArchitectureTests;

[TestClass]
public sealed class M16FinalArtifactEvidenceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [TestMethod]
    public void FinalArtifactEvidenceContractIsFixedBoundedAndSanitized()
    {
        string helperPath = Path.Combine(
            RepositoryRoot,
            "eng",
            "WindowsM16FinalArtifactEvidence.ps1");
        Assert.IsTrue(File.Exists(helperPath), "The M16 final-artifact evidence helper is missing.");

        string helper = File.ReadAllText(helperPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (string invariant in new[]
        {
            "$script:m16FinalMaximumInputBytes = 128KB",
            "$script:m16FinalMaximumAggregateInputBytes = 256KB",
            "$script:m16FinalMaximumOutputBytes = 64KB",
            "$script:m16FinalMaximumArrayLength = 4",
            "PackageBoundFinalArtifactSurfaces",
            "FinalArtifactCanaryScan",
            "owned-app-data",
            "exact-package",
            "support-artifact",
            "full-log",
            "PackageSbomApplicationPackageSha256",
            "ExpectedExactPackageInventorySha256",
            "SameBuildBindingPassed",
            "RepositoryStable",
            "RawSurfacesUploaded",
            "ReleaseAcceptanceOnly",
            "Assert-WindowsM16FinalNoDuplicateJsonProperties",
            "Assert-WindowsM16FinalNoNamedStreams",
            "Get-Item -LiteralPath $fullPath -Stream * -ErrorAction Stop",
            "$drive.DriveFormat -ceq \"NTFS\"",
            "Write-WindowsM16FinalArtifactEvidenceAtomically",
        })
        {
            StringAssert.Contains(helper, invariant);
        }

        Assert.IsFalse(
            helper.Contains("ConvertTo-SecureString", StringComparison.OrdinalIgnoreCase),
            "The evidence combiner must not process credential material.");
        Assert.IsFalse(
            helper.Contains("RawPath", StringComparison.OrdinalIgnoreCase),
            "The sanitized evidence schema must not expose raw paths.");
    }

    [TestMethod]
    public void FinalArtifactEvidenceContractPassesItsPowerShell51AdversarialSelfTest()
    {
        string selfTest = Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.ArchitectureTests",
            "Test-WindowsM16FinalArtifactEvidence.ps1");
        Assert.IsTrue(File.Exists(selfTest), "The M16 final-artifact evidence self-test is missing.");

        string windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.IsTrue(
            File.Exists(windowsPowerShell),
            "Windows PowerShell 5.1 is required for the M16 final-artifact evidence contract.");

        ProcessStartInfo startInfo = new()
        {
            FileName = windowsPowerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(selfTest);

        using Process process = Process.Start(startInfo)
            ?? throw new AssertFailedException("The M16 final-artifact evidence self-test could not start.");
        bool completed = process.WaitForExit(120_000);
        if (!completed)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        Assert.IsTrue(
            completed && process.ExitCode == 0,
            $"M16 final-artifact evidence contract failed.{Environment.NewLine}" +
            standardOutput + standardError);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
