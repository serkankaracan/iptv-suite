namespace IptvSuite.ArchitectureTests;

[TestClass]
public sealed class CatalogLocatorAeadBindingRulesTests
{
    [TestMethod]
    public void ProductionWriterAndReaderKeepCanonicalAeadAndEntropyBindings()
    {
        string repositoryRoot = FindRepositoryRoot();
        string infrastructureRoot = Path.Combine(
            repositoryRoot,
            "apps",
            "windows",
            "src",
            "IptvSuite.Infrastructure");
        string writer = File.ReadAllText(Path.Combine(infrastructureRoot, "SqliteCatalogSnapshotWriter.cs"));
        string reader = File.ReadAllText(Path.Combine(infrastructureRoot, "SqliteCatalogLocatorReader.cs"));
        string integration = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "apps",
            "windows",
            "tests",
            "IptvSuite.IntegrationTests",
            "SqliteCatalogSnapshotWriterTests.cs"));
        string riskRegister = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "docs",
            "risks",
            "RISK_REGISTER.md"));

        AssertCanonicalAadOrder(writer, "sourceId.Value", "snapshotId.Value", "locator.ChannelId.Value");
        AssertCanonicalAadOrder(reader, "sourceId", "snapshotId", "channelId");
        StringAssert.Contains(writer, "PROTECTED-CATALOG-DEK-V1\\0{sourceId.Value:N}\\0{snapshotId.Value:N}");
        StringAssert.Contains(reader, "PROTECTED-CATALOG-DEK-V1\\0{sourceId:N}\\0{snapshotId:N}");
        StringAssert.Contains(reader, "catch (CryptographicException)");
        StringAssert.Contains(reader, "CatalogLocatorReadFailure.AuthenticationFailed");
        StringAssert.Contains(riskRegister, "R27 M16 dynamic binding update — LOCAL VERIFIED, 2026-08-27");

        foreach (string mutation in new[]
        {
            "CiphertextTamper",
            "AuthenticationTagTamper",
            "NonceTamper",
            "SourceContextReplay",
            "SnapshotContextReplay",
            "GenerationContextReplay",
            "ChannelContextReplay",
            "PurposeContextReplay",
            "ReferenceContextReplay",
        })
        {
            StringAssert.Contains(integration, mutation);
        }
    }

    private static void AssertCanonicalAadOrder(
        string source,
        string sourceIdExpression,
        string snapshotIdExpression,
        string channelIdExpression)
    {
        int method = source.IndexOf("private static byte[] BuildAad(", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, method);
        int methodEnd = source.IndexOf("private static void WriteGuid(", method, StringComparison.Ordinal);
        Assert.IsGreaterThan(method, methodEnd);
        string aad = source[method..methodEnd];

        int sourceBinding = aad.IndexOf($"WriteGuid(aad, ref offset, {sourceIdExpression});", StringComparison.Ordinal);
        int snapshotBinding = aad.IndexOf($"WriteGuid(aad, ref offset, {snapshotIdExpression});", StringComparison.Ordinal);
        int generationBinding = aad.IndexOf("WriteGuid(aad, ref offset, keyGenerationId", StringComparison.Ordinal);
        if (generationBinding < 0)
        {
            generationBinding = aad.IndexOf("WriteGuid(aad, ref offset, generationId", StringComparison.Ordinal);
        }
        int channelBinding = aad.IndexOf($"WriteGuid(aad, ref offset, {channelIdExpression});", StringComparison.Ordinal);
        int purposeBinding = aad.IndexOf("aad[offset++] = (byte)", StringComparison.Ordinal);
        int referenceBinding = aad.LastIndexOf("WriteGuid(aad, ref offset,", StringComparison.Ordinal);

        Assert.IsTrue(
            sourceBinding >= 0 &&
            sourceBinding < snapshotBinding &&
            snapshotBinding < generationBinding &&
            generationBinding < channelBinding &&
            channelBinding < purposeBinding &&
            purposeBinding < referenceBinding,
            "Canonical AEAD binding order changed.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IPTVSuite.slnx")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
