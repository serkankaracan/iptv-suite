namespace IptvSuite.ArchitectureTests;

[TestClass]
public sealed class ChannelLogoSecurityRulesTests
{
    [TestMethod]
    public void LogoPayloadIsTypeAndPixelBoundBeforeWinUiDecode()
    {
        string repositoryRoot = FindRepositoryRoot();
        string provider = Read(repositoryRoot,
            "apps", "windows", "src", "IptvSuite.Infrastructure", "SqliteChannelLogoProvider.cs");
        string validator = Read(repositoryRoot,
            "apps", "windows", "src", "IptvSuite.Infrastructure", "ChannelLogoMetadataValidator.cs");
        string transport = Read(repositoryRoot,
            "apps", "windows", "src", "IptvSuite.Infrastructure", "BoundedHttpTransport.cs");
        string mainPage = Read(repositoryRoot,
            "apps", "windows", "src", "IptvSuite.Windows", "MainPage.xaml.cs");
        string contract = Read(repositoryRoot,
            "apps", "windows", "src", "IptvSuite.Application", "ChannelLogoContracts.cs");
        string securityBaseline = Read(repositoryRoot,
            "docs", "security", "SECURITY_AND_PRIVACY_BASELINE.md");

        StringAssert.Contains(provider, "public const int MaximumLogoBytes = 512 * 1024;");
        StringAssert.Contains(provider, "public const int MaximumLogoDimension = 4096;");
        StringAssert.Contains(provider, "public const long MaximumLogoPixels = 4L * 1024 * 1024;");
        StringAssert.Contains(provider, "responseLease.MediaType");
        StringAssert.Contains(provider, "ChannelLogoMetadataValidator.TryValidate(");
        StringAssert.Contains(validator, "HttpResponseMediaType.Png");
        StringAssert.Contains(validator, "HttpResponseMediaType.Jpeg");
        StringAssert.Contains(validator, "HttpResponseMediaType.WebP");
        StringAssert.Contains(validator, "(long)width * height > maximumPixels");
        StringAssert.Contains(validator, "TryReadPng(");
        StringAssert.Contains(validator, "TryReadJpeg(");
        StringAssert.Contains(validator, "TryReadWebP(");
        StringAssert.Contains(transport, "headers.NonValidated.TryGetValues(\"Content-Type\"");
        StringAssert.Contains(transport, "rawValues.Count != 1");
        StringAssert.Contains(transport, "MediaTypeHeaderValue.TryParse(");
        StringAssert.Contains(mainPage, "DecodePixelWidth = logo.PixelWidth");
        StringAssert.Contains(mainPage, "DecodePixelHeight = logo.PixelHeight");
        StringAssert.Contains(contract, "int pixelWidth,");
        StringAssert.Contains(contract, "int pixelHeight)");
        StringAssert.Contains(contract, "ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth)");
        StringAssert.Contains(contract, "ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight)");
        Assert.IsFalse(contract.Contains("int PixelWidth = 0", StringComparison.Ordinal));
        Assert.IsFalse(contract.Contains("int PixelHeight = 0", StringComparison.Ordinal));
        StringAssert.Contains(securityBaseline, "bounded image header signature/dimension/pixel metadata");
        StringAssert.Contains(securityBaseline, "tam bitstream geçerliliğini veya decode-time bütçesini kanıtlamaz");
        StringAssert.Contains(securityBaseline, "OS image decoder'ın codec karmaşıklığı/failure maliyeti residual risk");

        int dimensionGuard = mainPage.IndexOf(
            "logo.PixelWidth is <= 0",
            StringComparison.Ordinal);
        int decoderCreation = mainPage.IndexOf(
            "var image = new BitmapImage",
            StringComparison.Ordinal);
        int decode = mainPage.IndexOf(
            "await image.SetSourceAsync(stream)",
            StringComparison.Ordinal);
        Assert.IsTrue(
            dimensionGuard >= 0 && decoderCreation > dimensionGuard && decode > decoderCreation,
            "Validated pixel bounds must be applied before WinUI decodes the logo payload.");
    }

    private static string Read(string root, params string[] segments) =>
        File.ReadAllText(Path.Combine([root, .. segments]));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
