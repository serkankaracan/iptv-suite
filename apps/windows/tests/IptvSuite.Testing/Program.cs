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
                foreach (CanaryFinding finding in findings)
                {
                    Console.Error.WriteLine(
                        $"Test canary detected: {finding.RelativePath} ({finding.Encoding}, byte {finding.ByteOffset}).");
                }

                return findings.Count == 0 ? 0 : 2;
            }

            Console.Error.WriteLine(
                "Usage: generate-fixtures <specification> <output-directory> | scan-artifacts <root> <run-scope> <case-id>");
            return 64;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"Test tool failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }
}
