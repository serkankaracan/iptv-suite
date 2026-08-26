using IptvSuite.Testing;

return await TestTool.RunAsync(args).ConfigureAwait(false);

internal static class TestTool
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            if (arguments is ["generate-fixtures", string specificationPath, string outputDirectory])
            {
                FixtureSpecification specification = SyntheticFixtureGenerator.LoadSpecification(specificationPath);
                GeneratedFixtureSet generated = await SyntheticFixtureGenerator.GenerateAsync(
                    specification,
                    outputDirectory).ConfigureAwait(false);
                Console.WriteLine($"Generated deterministic fixture set: {Path.GetFileName(generated.ManifestPath)}");
                Console.WriteLine($"Records SHA-256: {generated.RecordsSha256}");
                return 0;
            }

            if (arguments is ["scan-artifacts", string rootPath, string runScope, string caseId])
            {
                TestCanary canary = TestCanary.Create(runScope, caseId);
                IReadOnlyList<CanaryFinding> findings = ArtifactCanaryScanner.Scan(rootPath, canary);
                WriteFindings(findings);

                return findings.Count == 0 ? 0 : 2;
            }

            if (arguments is
                [
                    "scan-release-artifacts",
                    string releaseRootPath,
                    string releaseRunScope,
                    string releaseCaseId,
                ])
            {
                TestCanary canary = TestCanary.Create(releaseRunScope, releaseCaseId);
                IReadOnlyList<CanaryFinding> findings = ArtifactCanaryScanner.Scan(
                    releaseRootPath,
                    canary,
                    ArtifactCanaryScanProfile.M16ReleaseCandidate);
                WriteFindings(findings);

                return findings.Count == 0 ? 0 : 2;
            }

            if (arguments is
                [
                    "validate-native-playback-evidence",
                    string evidencePath,
                    string controllerPath,
                    string expectedCommitSha,
                    string expectedSdk,
                ])
            {
                NativePlaybackEvidenceValidator.Validate(
                    evidencePath,
                    controllerPath,
                    expectedCommitSha,
                    expectedSdk);
                Console.WriteLine("Native playback evidence validated.");
                return 0;
            }

            Console.Error.WriteLine(
                "Usage: generate-fixtures <specification> <output-directory> | " +
                "scan-artifacts <root> <run-scope> <case-id> | " +
                "scan-release-artifacts <root> <run-scope> <case-id> | " +
                "validate-native-playback-evidence <evidence> <controller> <commit> <sdk>");
            return 64;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"Test tool failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static void WriteFindings(IReadOnlyList<CanaryFinding> findings)
    {
        foreach (CanaryFinding finding in findings)
        {
            Console.Error.WriteLine(
                $"Test canary detected: {finding.RelativePath} ({finding.Encoding}, byte {finding.ByteOffset}).");
        }
    }
}
