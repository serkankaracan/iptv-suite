using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.ArchitectureTests;

[TestClass]
public sealed class M16AggregateEvidenceEncodingTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [TestMethod]
    [DataRow("Invoke-WindowsPackageSmoke.ps1")]
    [DataRow("Invoke-WindowsPackageLifecycleSmoke.ps1")]
    [DataRow("Invoke-WindowsDpapiUserBoundarySmoke.ps1")]
    public void AggregateEvidenceProducersWriteUtf8WithoutBom(string scriptName)
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot, "eng", scriptName))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        const string functionMarker = "function Write-JsonAtomically {";
        int functionStart = source.IndexOf(functionMarker, StringComparison.Ordinal);
        Assert.IsTrue(functionStart >= 0, $"Write-JsonAtomically was not found in {scriptName}.");

        int nextFunction = source.IndexOf(
            "\nfunction ",
            functionStart + functionMarker.Length,
            StringComparison.Ordinal);
        int nextTopLevelStatement = source.IndexOf(
            "\n}\n\ntry {",
            functionStart + functionMarker.Length,
            StringComparison.Ordinal);
        int functionEnd = new[] { nextFunction, nextTopLevelStatement }
            .Where(index => index > functionStart)
            .DefaultIfEmpty(-1)
            .Min();
        Assert.IsTrue(functionEnd > functionStart, $"Write-JsonAtomically was not bounded in {scriptName}.");

        string writer = source[functionStart..functionEnd];
        StringAssert.Contains(writer, "$json = $Value | ConvertTo-Json");
        StringAssert.Contains(writer, "$json + [Environment]::NewLine");
        StringAssert.Contains(writer, "[System.Text.UTF8Encoding]::new($false, $true)");
        StringAssert.Contains(writer, "[System.IO.File]::WriteAllText(");
        Assert.IsFalse(
            writer.Contains("Set-Content", StringComparison.Ordinal),
            $"{scriptName} must not use the PowerShell-version-dependent UTF-8 writer.");
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
