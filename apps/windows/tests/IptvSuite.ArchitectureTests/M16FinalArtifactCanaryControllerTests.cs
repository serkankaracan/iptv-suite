using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.ArchitectureTests;

[TestClass]
public sealed class M16FinalArtifactCanaryControllerTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [TestMethod]
    public void FinalArtifactControllerContractIsFixedBoundedAndFailClosed()
    {
        string controller = ReadRepositoryFile(
            "eng",
            "Invoke-WindowsFinalArtifactCanaryScan.ps1");
        string helper = ReadRepositoryFile(
            "eng",
            "WindowsM16FinalArtifactEvidence.ps1");
        string boundedProcess = ReadRepositoryFile(
            "eng",
            "WindowsBoundedProcess.ps1");
        string packageSmoke = ReadRepositoryFile(
            "eng",
            "Invoke-WindowsPackageSmoke.ps1");
        string normalizedController = NormalizeWhitespace(controller);
        string parameterBlock = controller[..IndexOfOrFail(
            controller,
            "Set-StrictMode -Version Latest",
            0)];

        Assert.IsTrue(
            Regex.IsMatch(parameterBlock, @"(?m)^param\(\)\s*$"),
            "The controller must expose an empty parameter block.");
        Assert.AreEqual(
            0,
            Regex.Count(parameterBlock, @"\$[A-Za-z][A-Za-z0-9]*"),
            "Roots, profiles, limits, and tool paths must not be caller-controlled.");

        foreach (string invariant in new[]
        {
            "$script:windowsFinalExpectedSdk = \"10.0.302\"",
            "$script:windowsFinalMaximumLogBytes = 20MB",
            "$script:windowsFinalPackageTimeoutMilliseconds = 2700000",
            "$script:windowsFinalScannerTimeoutMilliseconds = 600000",
            "(Split-Path -Parent $PSScriptRoot)",
            "\"m16-final-artifact-scan\"",
            "\"m16-final-artifact-surfaces.json\"",
            "\"m16-final-artifact-binding.json\"",
            "\"last-success.json\"",
            "\"M16\"",
            "\"FINAL_ARTIFACTS\"",
            "\"scan-release-artifacts\"",
            "$repositoryDotNetPath",
            "Get-Command",
            "dotnet.exe",
            "$Paths.DotNetPath = [System.IO.Path]::GetFullPath(",
            "$actual = @(& $Paths.DotNetPath --version 2>$null)",
            "status `\n                --porcelain=v1 --untracked-files=all",
            "rev-parse HEAD",
            "GetEnvironmentVariable(\"GITHUB_SHA\", \"Process\")",
            "RepositoryDirty",
            "RepositoryBindingFailed",
            "RepositoryChanged",
            "throw \"WindowsFinalArtifactCanaryScan:$Code\"",
            ". $Paths.BoundedProcessHelperPath",
            "Enter-WindowsFinalRunMutex",
            "Enter-WindowsFinalPackageIdentityMutex",
            "Exit-WindowsFinalRunMutex",
            "Remove-WindowsFinalStalePackageOwnership",
            "Get-WindowsFinalOuterPackageExpectation",
            "Initialize-WindowsFinalPackageOwnership",
            "Remove-WindowsFinalPackageSideState",
            "Stop-WindowsFinalExactPackageProcesses",
        })
        {
            StringAssert.Contains(controller, invariant);
        }

        string packageInvocation = ExtractBetween(
            controller,
            "$packageProcess = Invoke-WindowsFinalBoundedProcess",
            "$combinedLogPath = [System.IO.Path]::Combine");
        StringAssert.Contains(
            NormalizeWhitespace(packageInvocation),
            "-ArgumentValues @( \"-NoProfile\", \"-NonInteractive\", " +
            "\"-ExecutionPolicy\", \"Bypass\", \"-File\", " +
            "$Paths.PackageSmokePath, \"-Configuration\", \"Release\", " +
            "\"-DotNetPath\", $Paths.DotNetPath, " +
            "\"-EmitM16FinalArtifactSurfaces\", \"-M16RunToken\", " +
            "$RunToken)");
        Assert.AreEqual(
            1,
            Regex.Count(
                packageInvocation,
                Regex.Escape("\"-EmitM16FinalArtifactSurfaces\"")),
            "The package child must receive exactly one fixed M16 capture switch.");
        foreach (string invariant in new[]
        {
            "CreateSuspended = 0x00000004",
            "JobObjectLimitKillOnJobClose = 0x00002000",
            "AssignProcessToJobObject",
            "ReserveBytes",
            "FileMode.CreateNew",
            "WindowsBoundedProcess:",
            "StartupInfoEx",
            "ProcThreadAttributeHandleList",
            "ExtendedStartupInfoPresent",
            "UpdateProcThreadAttribute",
        })
        {
            StringAssert.Contains(boundedProcess, invariant);
        }
        Assert.IsFalse(
            controller.Contains("Start-Process", StringComparison.Ordinal),
            "The controller must use hard-capped pipes and a kill-on-close job object.");

        int mainStart = IndexOfOrFail(
            controller,
            "function Invoke-WindowsFinalArtifactCanaryScanCore",
            0);
        int packageChild = IndexOfOrFail(
            controller,
            "$packageProcess = Invoke-WindowsFinalBoundedProcess",
            mainStart);
        int combinedLog = IndexOfOrFail(
            controller,
            "New-WindowsFinalCombinedLog",
            packageChild);
        int removeStdout = IndexOfOrFail(
            controller,
            "-Path $packageProcess.StandardOutputPath",
            combinedLog);
        int exactStdout = IndexOfOrFail(
            controller,
            "\"package.stdout\"",
            removeStdout);
        int removeStderr = IndexOfOrFail(
            controller,
            "-Path $packageProcess.StandardErrorPath",
            exactStdout);
        int exactStderr = IndexOfOrFail(
            controller,
            "\"package.stderr\"",
            removeStderr);
        int packageExit = IndexOfOrFail(
            controller,
            "($packageProcess.ExitCode -eq 0) \"PackageSmokeFailed\"",
            exactStderr);
        int fullLogScan = IndexOfOrFail(
            controller,
            "$fullLogReportPath = New-WindowsFinalFullLogScannerReport",
            packageExit);
        int outerPackageExpectation = IndexOfOrFail(
            controller,
            "$outerPackageExpectation = Get-WindowsFinalOuterPackageExpectation",
            fullLogScan);
        int independentBinding = IndexOfOrFail(
            controller,
            "[void](Get-WindowsFinalPackageBinding",
            outerPackageExpectation);
        int combineFourSurfaces = IndexOfOrFail(
            controller,
            "$finalEvidence = New-WindowsM16FinalArtifactEvidence",
            fullLogScan);
        AssertAscending(
            mainStart,
            packageChild,
            combinedLog,
            removeStdout,
            exactStdout,
            removeStderr,
            exactStderr,
            packageExit,
            fullLogScan,
            outerPackageExpectation,
            independentBinding,
            combineFourSurfaces);
        StringAssert.Contains(
            controller[independentBinding..combineFourSurfaces],
            "-ExpectedRunId $RunToken");
        StringAssert.Contains(
            controller[independentBinding..combineFourSurfaces],
            "-ExpectedPackageSha256 $outerPackageExpectation.PackageSha256");
        StringAssert.Contains(
            controller[independentBinding..combineFourSurfaces],
            "$outerPackageExpectation.ExactPackageInventorySha256");
        StringAssert.Contains(
            controller[removeStdout..packageExit],
            "-ParentRoot $Paths.ProcessIoRoot");
        Assert.AreEqual(
            2,
            Regex.Count(
                controller[removeStdout..packageExit],
                Regex.Escape("-MaximumBytes $script:windowsFinalMaximumLogBytes")),
            "Both raw child streams must be removed through the bounded exact-file primitive.");

        StringAssert.Contains(
            NormalizeWhitespace(helper),
            "$surfaceIds = @(\"owned-app-data\", \"exact-package\", " +
            "\"support-artifact\")");
        int packageSurfaces = IndexOfOrFail(
            helper,
            "$surfaceIds = @(",
            0);
        int fullLogSurface = IndexOfOrFail(
            helper,
            "-ExpectedSurfaceId \"full-log\"",
            packageSurfaces);
        int fixedSurfaceCount = IndexOfOrFail(
            helper,
            "SurfaceCount = 4",
            fullLogSurface);
        AssertAscending(packageSurfaces, fullLogSurface, fixedSurfaceCount);

        string outerExpectationContract = ExtractBetween(
            controller,
            "function Get-WindowsFinalOuterPackageExpectation",
            "function Get-WindowsFinalPackageBinding");
        StringAssert.Contains(outerExpectationContract, "\"exact-package\"");
        StringAssert.Contains(outerExpectationContract, "\"package.msix\"");
        StringAssert.Contains(outerExpectationContract, "[System.IO.FileShare]::Read");
        StringAssert.Contains(
            outerExpectationContract,
            "Get-WindowsFinalOuterExactPackageInventory");
        Assert.AreEqual(
            2,
            Regex.Count(
                outerExpectationContract,
                Regex.Escape("Get-WindowsFinalLockedStreamSha256")),
            "Outer ownership must hash the locked package before and after its own scan.");
        string strictBindingContract = ExtractBetween(
            controller,
            "function Get-WindowsFinalPackageBinding",
            "function Get-WindowsFinalStableFailureCode");
        StringAssert.Contains(
            strictBindingContract,
            "$packageSha256 -ceq $ExpectedPackageSha256");
        StringAssert.Contains(
            strictBindingContract,
            "$exactPackageInventorySha256 -ceq");
        StringAssert.Contains(
            strictBindingContract,
            "$ExpectedExactPackageInventorySha256");

        int mainFinally = IndexOfOrFail(controller, "\n    finally {", combineFourSurfaces);
        StringAssert.Contains(
            controller[combineFourSurfaces..mainFinally],
            "-ExpectedExactPackageInventorySha256");
        StringAssert.Contains(
            controller[combineFourSurfaces..mainFinally],
            "-ExpectedPackageSha256 $outerPackageExpectation.PackageSha256");
        int removeIntermediate = IndexOfOrFail(
            controller,
            "-Path $Paths.PackageIntermediatePath",
            mainFinally);
        int removePackageSideState = IndexOfOrFail(
            controller,
            "Remove-WindowsFinalPackageSideState",
            mainFinally);
        int removeWorkRoot = IndexOfOrFail(
            controller,
            "-Path $Paths.WorkRoot",
            removeIntermediate);
        int publishGuard = IndexOfOrFail(
            controller,
            "Assert-WindowsFinalOutputRootContract -Paths $Paths",
            removeWorkRoot);
        int publish = IndexOfOrFail(
            controller,
            "-DestinationPath $Paths.FinalEvidencePath",
            publishGuard);
        int publishedContract = IndexOfOrFail(
            controller,
            "Assert-WindowsFinalOutputRootContract -Paths $Paths -RequireEvidence",
            publish);
        AssertAscending(
            mainFinally,
            removePackageSideState,
            removeIntermediate,
            removeWorkRoot,
            publishGuard,
            publish,
            publishedContract);

        string outputContract = ExtractBetween(
            controller,
            "function Assert-WindowsFinalOutputRootContract",
            "function Invoke-WindowsFinalArtifactCanaryScan");
        StringAssert.Contains(outputContract, "$entries.Count -eq $expectedCount");
        StringAssert.Contains(outputContract, "$entry.Name -ceq \"last-success.json\"");
        StringAssert.Contains(outputContract, "$entry.Length -le 64KB");
        StringAssert.Contains(outputContract, "Assert-WindowsFinalNoNamedStreams");

        int wrapperStart = IndexOfOrFail(
            controller,
            "function Invoke-WindowsFinalArtifactCanaryScan {",
            publishedContract);
        int mutexAcquisition = IndexOfOrFail(
            controller,
            "$runMutex = Enter-WindowsFinalRunMutex",
            wrapperStart);
        int staleEvidenceInvalidation = IndexOfOrFail(
            controller,
            "Initialize-WindowsFinalWorkspace -Paths $paths",
            mutexAcquisition);
        int identityMutexAcquisition = IndexOfOrFail(
            controller,
            "$packageIdentityMutex = Enter-WindowsFinalPackageIdentityMutex",
            staleEvidenceInvalidation);
        int staleOwnershipRecovery = IndexOfOrFail(
            controller,
            "Remove-WindowsFinalStalePackageOwnership -Paths $paths",
            identityMutexAcquisition);
        int coreInvocation = IndexOfOrFail(
            controller,
            "Invoke-WindowsFinalArtifactCanaryScanCore `",
            staleOwnershipRecovery);
        int mutexRelease = IndexOfOrFail(
            controller,
            "Exit-WindowsFinalRunMutex -Mutex $mutex",
            coreInvocation);
        int successMessage = IndexOfOrFail(
            controller,
            "Write-Output \"M16 final artifact canary scan passed.\"",
            mutexRelease);
        AssertAscending(
            wrapperStart,
            mutexAcquisition,
            staleEvidenceInvalidation,
            identityMutexAcquisition,
            staleOwnershipRecovery,
            coreInvocation,
            mutexRelease,
            successMessage);
        Assert.IsFalse(
            controller[mainStart..wrapperStart].Contains(
                "Initialize-WindowsFinalWorkspace",
                StringComparison.Ordinal),
            "Stale success invalidation must occur in the mutex-held wrapper before core validation.");
        StringAssert.Contains(
            controller[mutexRelease..successMessage],
            "Remove-WindowsFinalExactFile");

        string staleRecovery = ExtractBetween(
            controller,
            "function Remove-WindowsFinalStalePackageOwnership",
            "function Enter-WindowsFinalRunMutex");
        StringAssert.Contains(staleRecovery, "$staleEntries.Count -le 1");
        StringAssert.Contains(staleRecovery, "'\\A[0-9a-f]{32}\\z'");
        StringAssert.Contains(staleRecovery, "$ownershipEntries.Count -le");
        StringAssert.Contains(staleRecovery, "Remove-WindowsFinalPackageSideState");

        string packageCleanup = ExtractBetween(
            controller,
            "function Remove-WindowsFinalPackageSideState",
            "function Enter-WindowsFinalRunMutex");
        int packageIntentRead = IndexOfOrFail(
            packageCleanup,
            "$packageIntent = Get-WindowsFinalPackageRegistrationIntent",
            0);
        int exactProcessStop = IndexOfOrFail(
            packageCleanup,
            "Stop-WindowsFinalExactPackageProcesses",
            packageIntentRead);
        int exactPackageRemoval = IndexOfOrFail(
            packageCleanup,
            "Remove-AppxPackage",
            exactProcessStop);
        AssertAscending(packageIntentRead, exactProcessStop, exactPackageRemoval);
        StringAssert.Contains(controller, "\"SchemaVersion\":1,\"RunToken\"");
        StringAssert.Contains(packageCleanup, "$packageIntent.ExpectedPackageFullName");
        StringAssert.Contains(packageCleanup, "-MaximumBytes 9GB");
        StringAssert.Contains(packageCleanup, "-MaximumBytes 8GB");

        int signingCertificateCreation = IndexOfOrFail(
            packageSmoke,
            "$certificate = New-SelfSignedCertificate",
            0);
        int signingOwnership = IndexOfOrFail(
            packageSmoke,
            "-Name \"signing-certificate.thumbprint\"",
            signingCertificateCreation);
        int signingCertificateImport = IndexOfOrFail(
            packageSmoke,
            "Import-Certificate -FilePath $publicCertificatePath",
            signingOwnership);
        AssertAscending(
            signingCertificateCreation,
            signingOwnership,
            signingCertificateImport);
        StringAssert.Contains(
            packageSmoke[signingCertificateCreation..signingOwnership],
            "-FriendlyName $certificateFriendlyName");

        int removeExistingPackage = IndexOfOrFail(
            packageSmoke,
            "Remove-ExactDevelopmentPackage",
            signingCertificateImport);
        int packageRegistrationIntent = IndexOfOrFail(
            packageSmoke,
            "-Name \"package-registration.intent\"",
            removeExistingPackage);
        int packageInstall = IndexOfOrFail(
            packageSmoke,
            "Add-AppxPackage -Path $packages[0].FullName",
            packageRegistrationIntent);
        AssertAscending(
            removeExistingPackage,
            packageRegistrationIntent,
            packageInstall);

        int onboardingOwnership = IndexOfOrFail(
            packageSmoke,
            "-Name \"onboarding-loopback.thumbprint\"",
            packageInstall);
        int onboardingImport = IndexOfOrFail(
            packageSmoke,
            "Import-Certificate `\n                -FilePath $onboardingPublicCertificatePath",
            onboardingOwnership);
        int playbackOwnership = IndexOfOrFail(
            packageSmoke,
            "-Name \"playback-loopback.thumbprint\"",
            onboardingImport);
        int playbackImport = IndexOfOrFail(
            packageSmoke,
            "Import-Certificate `\n                -FilePath $playbackPublicCertificatePath",
            playbackOwnership);
        AssertAscending(
            onboardingOwnership,
            onboardingImport,
            playbackOwnership,
            playbackImport);
        Assert.IsFalse(
            normalizedController.Contains("continue-on-error", StringComparison.OrdinalIgnoreCase),
            "The final-artifact controller must fail closed.");
    }

    [TestMethod]
    public void FinalArtifactControllerPassesItsPowerShell51AdversarialSelfTest()
    {
        string selfTest = Path.Combine(
            RepositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.ArchitectureTests",
            "Test-WindowsFinalArtifactCanaryScan.ps1");
        Assert.IsTrue(
            File.Exists(selfTest),
            "The M16 final-artifact controller self-test is missing.");

        string windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.IsTrue(
            File.Exists(windowsPowerShell),
            "Windows PowerShell 5.1 is required for the M16 controller contract.");

        ProcessStartInfo startInfo = new()
        {
            FileName = windowsPowerShell,
            WorkingDirectory = RepositoryRoot,
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
            ?? throw new AssertFailedException(
                "The M16 final-artifact controller self-test could not start.");
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
            $"M16 final-artifact controller contract failed.{Environment.NewLine}" +
            standardOutput + standardError);
    }

    private static string ReadRepositoryFile(params string[] pathSegments)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. pathSegments]))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string NormalizeWhitespace(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    private static string ExtractBetween(string value, string startMarker, string endMarker)
    {
        int start = IndexOfOrFail(value, startMarker, 0);
        int end = IndexOfOrFail(value, endMarker, start + startMarker.Length);
        return value[start..end];
    }

    private static int IndexOfOrFail(string value, string marker, int startIndex)
    {
        int index = value.IndexOf(marker, startIndex, StringComparison.Ordinal);
        Assert.IsTrue(index >= 0, $"Required contract marker was not found: {marker}");
        return index;
    }

    private static void AssertAscending(params int[] indices)
    {
        for (int index = 1; index < indices.Length; index++)
        {
            Assert.IsTrue(
                indices[index - 1] < indices[index],
                $"Contract operation {index} must precede operation {index + 1}.");
        }
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

        throw new DirectoryNotFoundException(
            "Repository root was not found from the test output directory.");
    }
}
