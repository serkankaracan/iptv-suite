using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class NativePlaybackEvidenceValidatorTests
{
    private const string CommitSha = "0123456789abcdef0123456789abcdef01234567";
    private const string ExpectedSdk = "10.0.302";
    private const string RunId = "0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void ExactValidEvidenceAcceptsEitherEmptyRootCleanupOutcome()
    {
        using ValidationFiles files = ValidationFiles.Create("native-evidence-valid");
        string evidence = CreateValidEvidence(files.ControllerSha256);

        files.WriteEvidence(evidence);
        files.Validate();

        files.WriteEvidence(ReplaceOnce(
            evidence,
            "\"PackageAppDataEmptyRootCleanupUsed\":false",
            "\"PackageAppDataEmptyRootCleanupUsed\":true"));
        files.Validate();
    }

    [TestMethod]
    public void SharedRuntimeAdditionsAreAcceptedOnlyWithPositiveCount()
    {
        using ValidationFiles files = ValidationFiles.Create("native-evidence-shared-runtime");
        string evidence = CreateValidEvidence(files.ControllerSha256);
        string sharedEvidence = ReplaceOnce(
            ReplaceOnce(
                evidence,
                "\"RuntimePackageGraphDisposition\":\"ExactRestored\"",
                "\"RuntimePackageGraphDisposition\":\"SharedAdditionsPreserved\""),
            "\"RuntimePackageSharedAdditionCount\":0",
            "\"RuntimePackageSharedAdditionCount\":2");

        files.WriteEvidence(sharedEvidence);
        files.Validate();

        files.WriteEvidence(ReplaceOnce(
            sharedEvidence,
            "\"RuntimePackageSharedAdditionCount\":2",
            "\"RuntimePackageSharedAdditionCount\":0"));
        _ = Assert.ThrowsExactly<InvalidDataException>(files.Validate);
    }

    [TestMethod]
    public void StringCoercionIsRejectedForNumericAndBooleanProperties()
    {
        using ValidationFiles files = ValidationFiles.Create("native-evidence-coercion");
        string evidence = CreateValidEvidence(files.ControllerSha256);
        string[] coercedEvidence =
        [
            ReplaceOnce(evidence, "\"SwitchCount\":100", "\"SwitchCount\":\"100\""),
            ReplaceOnce(evidence, "\"ProbeRunIdBound\":true", "\"ProbeRunIdBound\":\"true\""),
        ];

        foreach (string candidate in coercedEvidence)
        {
            files.WriteEvidence(candidate);
            InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(files.Validate);
            StringAssert.Contains(exception.Message, "Native playback evidence property");
        }
    }

    [TestMethod]
    public void DuplicateOrReorderedPropertiesAreRejectedBeforeValueLookup()
    {
        using ValidationFiles files = ValidationFiles.Create("native-evidence-property-order");
        string evidence = CreateValidEvidence(files.ControllerSha256);
        string duplicate = ReplaceOnce(
            evidence,
            $"\"RunId\":\"{RunId}\"",
            $"\"RunId\":\"{RunId}\",\"RunId\":\"{RunId}\"");
        string reordered = ReplaceOnce(
            evidence,
            "\"Stage\":\"M10NativeTierAPlayback\",\"Result\":\"Passed\"",
            "\"Result\":\"Passed\",\"Stage\":\"M10NativeTierAPlayback\"");

        foreach (string candidate in new[] { duplicate, reordered })
        {
            files.WriteEvidence(candidate);
            InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(files.Validate);
            Assert.AreEqual(
                "Native playback evidence property sequence is invalid.",
                exception.Message);
        }
    }

    [TestMethod]
    public void TierABudgetsRuntimeIdentityAndCleanupSemanticsFailClosed()
    {
        using ValidationFiles files = ValidationFiles.Create("native-evidence-semantics");
        string evidence = CreateValidEvidence(files.ControllerSha256);
        (string Original, string Replacement)[] mutations =
        [
            ("\"StartupP95Milliseconds\":250.125", "\"StartupP95Milliseconds\":3000.001"),
            ("\"StartupMaximumMilliseconds\":2500.5", "\"StartupMaximumMilliseconds\":5000.001"),
            ("\"SourceDetachP95Milliseconds\":8.25", "\"SourceDetachP95Milliseconds\":3000.001"),
            ("\"NetworkRecoveryCount\":1", "\"NetworkRecoveryCount\":0"),
            ("\"LastRecoveryRequestOrdinal\":52", "\"LastRecoveryRequestOrdinal\":50"),
            (
                "\"ResolvedWindowsAppRuntimeName\":\"Microsoft.WindowsAppRuntime.2\"",
                "\"ResolvedWindowsAppRuntimeName\":\"Microsoft.WindowsAppRuntime.3\""),
            (
                "\"ResolvedWindowsAppRuntimeVersion\":\"2.4.0.0\"",
                "\"ResolvedWindowsAppRuntimeVersion\":\"2.3.0.9\""),
            (
                "\"ResolvedWindowsAppRuntimeArchitecture\":\"x64\"",
                "\"ResolvedWindowsAppRuntimeArchitecture\":\"X64\""),
            (
                "\"ResolvedWindowsAppRuntimePublisherId\":\"8wekyb3d8bbwe\"",
                "\"ResolvedWindowsAppRuntimePublisherId\":\"publisher\""),
            ("\"ResolvedWindowsAppRuntimeIsFramework\":true", "\"ResolvedWindowsAppRuntimeIsFramework\":false"),
            ("\"RuntimePackageBaselinePreserved\":true", "\"RuntimePackageBaselinePreserved\":false"),
            (
                "\"RuntimePackageGraphDisposition\":\"ExactRestored\"",
                "\"RuntimePackageGraphDisposition\":\"Unexpected\""),
            ("\"RuntimePackageSharedAdditionCount\":0", "\"RuntimePackageSharedAdditionCount\":-1"),
            ("\"RuntimePackageSharedAdditionCount\":0", "\"RuntimePackageSharedAdditionCount\":1"),
            ("\"RuntimePackageSharedAdditionCount\":0", "\"RuntimePackageSharedAdditionCount\":65"),
            (
                "\"PackageSha256\":\"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\"",
                "\"PackageSha256\":\"DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD\""),
        ];

        foreach ((string original, string replacement) in mutations)
        {
            files.WriteEvidence(ReplaceOnce(evidence, original, replacement));
            _ = Assert.ThrowsExactly<InvalidDataException>(files.Validate);
        }
    }

    [TestMethod]
    public void ValidationErrorsDoNotEchoPathsOrUntrustedValues()
    {
        using ValidationFiles files = ValidationFiles.Create("native-evidence-sanitized-error");
        const string untrustedValue = "SENSITIVE_UNTRUSTED_VALUE";
        string evidence = ReplaceOnce(
            CreateValidEvidence(files.ControllerSha256),
            "\"Stage\":\"M10NativeTierAPlayback\"",
            $"\"Stage\":\"{untrustedValue}\"");
        files.WriteEvidence(evidence);

        InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(files.Validate);

        Assert.IsFalse(exception.Message.Contains(untrustedValue, StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains(files.EvidencePath, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(exception.Message.Contains(files.ControllerPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateValidEvidence(string controllerSha256)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("SchemaVersion", 9);
            writer.WriteString("Stage", "M10NativeTierAPlayback");
            writer.WriteString("Result", "Passed");
            writer.WriteString("RunId", RunId);
            writer.WriteString("CompletedAtUtc", "2026-08-22T00:00:00.0000000Z");
            writer.WriteString("Configuration", "Release");
            writer.WriteString("Platform", "x64");
            writer.WriteString("DotNetSdk", ExpectedSdk);
            writer.WriteBoolean("CleanHeadBound", true);
            writer.WriteString("CommitSha", CommitSha);
            writer.WriteString("ControllerScriptSha256", controllerSha256);
            writer.WriteString("HarnessAssemblySha256", new string('b', 64));
            writer.WriteString("FixtureManifestSha256", new string('c', 64));
            writer.WriteBoolean("FixtureCorpusVerified", true);
            writer.WriteNumber("ProbeEnvelopeSchemaVersion", 1);
            writer.WriteBoolean("ProbeRunIdBound", true);
            writer.WriteNumber("SwitchCount", 100);
            writer.WriteNumber("StartupP95Milliseconds", 250.125);
            writer.WriteNumber("StartupMaximumMilliseconds", 2500.5);
            writer.WriteNumber("HlsStartupP95Milliseconds", 300.25);
            writer.WriteNumber("DirectStartupP95Milliseconds", 200.75);
            writer.WriteNumber("SoakMinutes", 0);
            writer.WriteNumber("ResourceSampleCount", 0);
            writer.WriteNumber("WarmupPrivateBytes", 0);
            writer.WriteNumber("MemoryNetGrowthBytes", 0);
            writer.WriteNumber("MemoryNetGrowthPercent", 0);
            writer.WriteBoolean("MemoryMonotonicIncrease", false);
            writer.WriteNumber("WarmupHandleCount", 0);
            writer.WriteNumber("HandleNetGrowth", 0);
            writer.WriteNumber("SurfaceTransitionCount", 6);
            writer.WriteNumber("DetachedSourceCount", 101);
            writer.WriteNumber("PlaybackRetryCount", 1);
            writer.WriteNumber("SourceDetachP95Milliseconds", 8.25);
            writer.WriteNumber("SourceDetachMaximumMilliseconds", 10.5);
            writer.WriteNumber("NetworkInterruptionCount", 1);
            writer.WriteNumber("NetworkRecoveryCount", 1);
            writer.WriteNumber("LastInjectedRequestOrdinal", 50);
            writer.WriteNumber("LastRecoveryRequestOrdinal", 52);
            writer.WriteNumber("InitialPrivateBytes", 100_000_000);
            writer.WriteNumber("FinalPrivateBytes", 101_000_000);
            writer.WriteNumber("InitialHandleCount", 100);
            writer.WriteNumber("FinalHandleCount", 101);
            writer.WriteNumber("LoopbackRequestCount", 120);
            writer.WriteBoolean("H264DecoderRegistered", true);
            writer.WriteBoolean("AacDecoderRegistered", true);
            writer.WriteString("Transport", "Tls12LoopbackAllowlist");
            writer.WriteStartArray("Fixtures");
            writer.WriteStringValue("DirectH264AacMpegTs");
            writer.WriteStringValue("HlsH264AacMpegTs");
            writer.WriteEndArray();
            writer.WriteString("PackageSha256", new string('d', 64));
            writer.WriteString("PackageSignatureStatus", "Valid");
            writer.WriteString("RuntimeDependencyPackageSha256", new string('e', 64));
            writer.WriteString("RuntimeDependencyPackageSignatureStatus", "Valid");
            writer.WriteString("ResolvedWindowsAppRuntimeName", "Microsoft.WindowsAppRuntime.2");
            writer.WriteString("ResolvedWindowsAppRuntimeVersion", "2.4.0.0");
            writer.WriteString("ResolvedWindowsAppRuntimeArchitecture", "x64");
            writer.WriteString("ResolvedWindowsAppRuntimePublisherId", "8wekyb3d8bbwe");
            writer.WriteBoolean("ResolvedWindowsAppRuntimeIsFramework", true);
            writer.WriteBoolean("NormalCloseVerified", true);
            writer.WriteBoolean("ForcedProcessTerminationUsed", false);
            writer.WriteBoolean("ProcessCleanupPassed", true);
            writer.WriteBoolean("TlsServerDisposed", true);
            writer.WriteBoolean("PackageRemoved", true);
            writer.WriteBoolean("PackageAppDataRemoved", true);
            writer.WriteBoolean("PackageAppDataEmptyRootCleanupUsed", false);
            writer.WriteBoolean("RuntimePackageBaselinePreserved", true);
            writer.WriteString("RuntimePackageGraphDisposition", "ExactRestored");
            writer.WriteNumber("RuntimePackageSharedAdditionCount", 0);
            writer.WriteBoolean("EphemeralCertificatesRemoved", true);
            writer.WriteBoolean("ExportedCertificateFilesRemoved", true);
            writer.WriteBoolean("PackageOutputRemoved", true);
            writer.WriteBoolean("EnvironmentRestored", true);
            writer.WriteBoolean("RepositoryCleanAfterRun", true);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ReplaceOnce(string value, string original, string replacement)
    {
        int index = value.IndexOf(original, StringComparison.Ordinal);
        if (index < 0 || value.IndexOf(original, index + original.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException("The native evidence test mutation is not unique.");
        }

        return string.Concat(value.AsSpan(0, index), replacement, value.AsSpan(index + original.Length));
    }

    private sealed class ValidationFiles : IDisposable
    {
        private readonly TemporaryDirectory _temporary;

        private ValidationFiles(TemporaryDirectory temporary, string controllerPath, string evidencePath)
        {
            _temporary = temporary;
            ControllerPath = controllerPath;
            EvidencePath = evidencePath;
            ControllerSha256 = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(controllerPath))).ToLowerInvariant();
        }

        internal string ControllerPath { get; }

        internal string EvidencePath { get; }

        internal string ControllerSha256 { get; }

        internal static ValidationFiles Create(string scope)
        {
            TemporaryDirectory temporary = TemporaryDirectory.Create(scope);
            string controllerPath = Path.Combine(temporary.FullPath, "controller.ps1");
            string evidencePath = Path.Combine(temporary.FullPath, "evidence.json");
            File.WriteAllText(
                controllerPath,
                "Write-Output 'synthetic controller'",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new ValidationFiles(temporary, controllerPath, evidencePath);
        }

        internal void WriteEvidence(string evidence) =>
            File.WriteAllText(
                EvidencePath,
                evidence,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        internal void Validate() =>
            NativePlaybackEvidenceValidator.Validate(
                EvidencePath,
                ControllerPath,
                CommitSha,
                ExpectedSdk);

        public void Dispose() => _temporary.Dispose();
    }
}
