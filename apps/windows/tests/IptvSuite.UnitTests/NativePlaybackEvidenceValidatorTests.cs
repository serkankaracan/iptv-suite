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
            ReplaceOnce(
                evidence,
                "\"CancellationProbeCount\":1",
                "\"CancellationProbeCount\":\"1\""),
            ReplaceOnce(
                evidence,
                "\"CancellationLatencyMilliseconds\":125.25",
                "\"CancellationLatencyMilliseconds\":\"125.25\""),
            ReplaceOnce(
                evidence,
                "\"CancellationNoAutomaticRestart\":true",
                "\"CancellationNoAutomaticRestart\":\"true\""),
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
    public void CancellationRecoverySchemaCountsBoundsAndPostconditionsFailClosed()
    {
        using ValidationFiles files = ValidationFiles.Create("native-evidence-cancellation-recovery");
        string evidence = CreateValidEvidence(files.ControllerSha256);
        (string Original, string Replacement)[] mutations =
        [
            ("\"SchemaVersion\":10", "\"SchemaVersion\":9"),
            ("\"ProbeEnvelopeSchemaVersion\":8", "\"ProbeEnvelopeSchemaVersion\":7"),
            ("\"DetachedSourceCount\":103", "\"DetachedSourceCount\":101"),
            ("\"CancellationProbeCount\":1", "\"CancellationProbeCount\":0"),
            ("\"CancellationObservedCount\":1", "\"CancellationObservedCount\":2"),
            ("\"CancellationSourceDetachCount\":1", "\"CancellationSourceDetachCount\":0"),
            ("\"CancellationRecoveryCount\":1", "\"CancellationRecoveryCount\":0"),
            (
                "\"CancellationRecoverySourceDetachCount\":1",
                "\"CancellationRecoverySourceDetachCount\":0"),
            (
                "\"CancellationLatencyMilliseconds\":125.25",
                "\"CancellationLatencyMilliseconds\":-0.001"),
            (
                "\"CancellationLatencyMilliseconds\":125.25",
                "\"CancellationLatencyMilliseconds\":1000.001"),
            (
                "\"CancellationQuiescenceMilliseconds\":175.5",
                "\"CancellationQuiescenceMilliseconds\":-0.001"),
            (
                "\"CancellationQuiescenceMilliseconds\":175.5",
                "\"CancellationQuiescenceMilliseconds\":1000.001"),
            (
                "\"CancellationObservationMilliseconds\":1025.75",
                "\"CancellationObservationMilliseconds\":999.999"),
            (
                "\"CancellationObservationMilliseconds\":1025.75",
                "\"CancellationObservationMilliseconds\":1500.001"),
            (
                "\"CancellationSourceDetachMilliseconds\":9.5",
                "\"CancellationSourceDetachMilliseconds\":-0.001"),
            (
                "\"CancellationSourceDetachMilliseconds\":9.5",
                "\"CancellationSourceDetachMilliseconds\":5000.001"),
            (
                "\"CancellationQuiescenceMilliseconds\":175.5",
                "\"CancellationQuiescenceMilliseconds\":130"),
            (
                "\"CancellationSourceDetachMilliseconds\":9.5",
                "\"CancellationSourceDetachMilliseconds\":10.51"),
            (
                "\"CancellationRecoveryStartupMilliseconds\":225.25",
                "\"CancellationRecoveryStartupMilliseconds\":0"),
            (
                "\"CancellationRecoveryStartupMilliseconds\":225.25",
                "\"CancellationRecoveryStartupMilliseconds\":5000.001"),
            (
                "\"CancellationRecoveryAdvanceMilliseconds\":275.5",
                "\"CancellationRecoveryAdvanceMilliseconds\":0"),
            (
                "\"CancellationRecoveryAdvanceMilliseconds\":275.5",
                "\"CancellationRecoveryAdvanceMilliseconds\":3000.001"),
            (
                "\"CancellationRecoverySourceDetachMilliseconds\":10.25",
                "\"CancellationRecoverySourceDetachMilliseconds\":-0.001"),
            (
                "\"CancellationRecoverySourceDetachMilliseconds\":10.25",
                "\"CancellationRecoverySourceDetachMilliseconds\":5000.001"),
            (
                "\"CancellationRecoverySourceDetachMilliseconds\":10.25",
                "\"CancellationRecoverySourceDetachMilliseconds\":10.51"),
            (
                "\"CancellationSourceNullAfterObservation\":true",
                "\"CancellationSourceNullAfterObservation\":false"),
            (
                "\"CancellationRecoveryUsedFreshSource\":true",
                "\"CancellationRecoveryUsedFreshSource\":false"),
            (
                "\"CancellationNoAutomaticRestart\":true",
                "\"CancellationNoAutomaticRestart\":false"),
        ];

        foreach ((string original, string replacement) in mutations)
        {
            files.WriteEvidence(ReplaceOnce(evidence, original, replacement));
            _ = Assert.ThrowsExactly<InvalidDataException>(files.Validate);
        }
    }

    [TestMethod]
    public void CancellationRoundedLatencyAndQuiescenceMayBeZero()
    {
        using ValidationFiles files = ValidationFiles.Create("native-evidence-cancellation-rounded-zero");
        string evidence = ReplaceOnce(
            ReplaceOnce(
                ReplaceOnce(
                    CreateValidEvidence(files.ControllerSha256),
                    "\"CancellationLatencyMilliseconds\":125.25",
                    "\"CancellationLatencyMilliseconds\":0"),
                "\"CancellationSourceDetachMilliseconds\":9.5",
                "\"CancellationSourceDetachMilliseconds\":0"),
            "\"CancellationQuiescenceMilliseconds\":175.5",
            "\"CancellationQuiescenceMilliseconds\":0");

        files.WriteEvidence(evidence);
        files.Validate();
    }

    [TestMethod]
    public void CancellationDisabledModeRequiresZeroedEvidenceAndNoAdditionalDetaches()
    {
        using ValidationFiles files = ValidationFiles.Create("native-evidence-cancellation-disabled");
        string evidence = CreateCancellationDisabledEvidence(
            CreateValidEvidence(files.ControllerSha256));

        files.WriteEvidence(evidence);
        files.Validate();

        (string Original, string Replacement)[] mutations =
        [
            ("\"CancellationProbeCount\":0", "\"CancellationProbeCount\":2"),
            ("\"DetachedSourceCount\":101", "\"DetachedSourceCount\":103"),
            ("\"CancellationObservedCount\":0", "\"CancellationObservedCount\":1"),
            ("\"CancellationSourceDetachCount\":0", "\"CancellationSourceDetachCount\":1"),
            ("\"CancellationRecoveryCount\":0", "\"CancellationRecoveryCount\":1"),
            (
                "\"CancellationRecoverySourceDetachCount\":0",
                "\"CancellationRecoverySourceDetachCount\":1"),
            (
                "\"CancellationLatencyMilliseconds\":0",
                "\"CancellationLatencyMilliseconds\":1"),
            (
                "\"CancellationQuiescenceMilliseconds\":0",
                "\"CancellationQuiescenceMilliseconds\":1"),
            (
                "\"CancellationObservationMilliseconds\":0",
                "\"CancellationObservationMilliseconds\":1"),
            (
                "\"CancellationSourceDetachMilliseconds\":0",
                "\"CancellationSourceDetachMilliseconds\":1"),
            (
                "\"CancellationRecoveryStartupMilliseconds\":0",
                "\"CancellationRecoveryStartupMilliseconds\":1"),
            (
                "\"CancellationRecoveryAdvanceMilliseconds\":0",
                "\"CancellationRecoveryAdvanceMilliseconds\":1"),
            (
                "\"CancellationRecoverySourceDetachMilliseconds\":0",
                "\"CancellationRecoverySourceDetachMilliseconds\":1"),
            (
                "\"CancellationSourceNullAfterObservation\":false",
                "\"CancellationSourceNullAfterObservation\":true"),
            (
                "\"CancellationRecoveryUsedFreshSource\":false",
                "\"CancellationRecoveryUsedFreshSource\":true"),
            (
                "\"CancellationNoAutomaticRestart\":false",
                "\"CancellationNoAutomaticRestart\":true"),
        ];

        foreach ((string original, string replacement) in mutations)
        {
            files.WriteEvidence(ReplaceOnce(evidence, original, replacement));
            _ = Assert.ThrowsExactly<InvalidDataException>(files.Validate);
        }
    }

    [TestMethod]
    public void M16FinalProfileAcceptsOnlyItsExactLongSoakShape()
    {
        using ValidationFiles files = ValidationFiles.Create("m16-native-evidence-valid");
        string m16Evidence = CreateValidM16Evidence(files.ControllerSha256);

        files.WriteEvidence(m16Evidence);
        files.ValidateM16();
        _ = Assert.ThrowsExactly<InvalidDataException>(files.Validate);

        files.WriteEvidence(CreateValidEvidence(files.ControllerSha256));
        _ = Assert.ThrowsExactly<InvalidDataException>(files.ValidateM16);
    }

    [TestMethod]
    public void M16FinalProfileKeepsExactCountsAndResourceBudgetsFailClosed()
    {
        using ValidationFiles files = ValidationFiles.Create("m16-native-evidence-bounds");
        string evidence = CreateValidM16Evidence(files.ControllerSha256);
        (string Original, string Replacement)[] mutations =
        [
            ("\"SchemaVersion\":11", "\"SchemaVersion\":10"),
            (
                "\"Stage\":\"M16NativeTierAFinalAcceptance\"",
                "\"Stage\":\"M10NativeTierAPlayback\""),
            ("\"SwitchCount\":200", "\"SwitchCount\":199"),
            ("\"SoakMinutes\":1440", "\"SoakMinutes\":1439"),
            ("\"ResourceSampleCount\":289", "\"ResourceSampleCount\":285"),
            ("\"ResourceSampleCount\":289", "\"ResourceSampleCount\":291"),
            ("\"WarmupPrivateBytes\":200000000", "\"WarmupPrivateBytes\":0"),
            ("\"MemoryNetGrowthBytes\":10000000", "\"MemoryNetGrowthBytes\":104857601"),
            ("\"MemoryNetGrowthPercent\":5", "\"MemoryNetGrowthPercent\":10.001"),
            ("\"MemoryNetGrowthPercent\":5", "\"MemoryNetGrowthPercent\":5.001"),
            ("\"MemoryMonotonicIncrease\":false", "\"MemoryMonotonicIncrease\":true"),
            ("\"WarmupHandleCount\":1000", "\"WarmupHandleCount\":0"),
            ("\"DetachedSourceCount\":202", "\"DetachedSourceCount\":201"),
            ("\"PlaybackRetryCount\":1", "\"PlaybackRetryCount\":8"),
            ("\"NetworkInterruptionCount\":7", "\"NetworkInterruptionCount\":6"),
            ("\"NetworkRecoveryCount\":7", "\"NetworkRecoveryCount\":6"),
            ("\"CancellationProbeCount\":0", "\"CancellationProbeCount\":1"),
        ];

        foreach ((string original, string replacement) in mutations)
        {
            files.WriteEvidence(ReplaceOnce(evidence, original, replacement));
            _ = Assert.ThrowsExactly<InvalidDataException>(files.ValidateM16);
        }
    }

    [TestMethod]
    public void M16FinalProfileAcceptsUnchangedInclusiveResourceLimits()
    {
        using ValidationFiles files = ValidationFiles.Create("m16-native-evidence-exact-limits");
        string evidence = CreateValidM16Evidence(files.ControllerSha256);

        files.WriteEvidence(evidence);
        files.ValidateM16();

        string absoluteLimitEvidence = ReplaceOnce(
            evidence,
            "\"WarmupPrivateBytes\":200000000",
            "\"WarmupPrivateBytes\":2097152000");
        absoluteLimitEvidence = ReplaceOnce(
            absoluteLimitEvidence,
            "\"MemoryNetGrowthBytes\":10000000",
            "\"MemoryNetGrowthBytes\":104857600");
        files.WriteEvidence(absoluteLimitEvidence);
        files.ValidateM16();

        string relativeLimitEvidence = ReplaceOnce(
            evidence,
            "\"MemoryNetGrowthBytes\":10000000",
            "\"MemoryNetGrowthBytes\":20000000");
        relativeLimitEvidence = ReplaceOnce(
            relativeLimitEvidence,
            "\"MemoryNetGrowthPercent\":5",
            "\"MemoryNetGrowthPercent\":10");
        files.WriteEvidence(relativeLimitEvidence);
        files.ValidateM16();

        string roundedPercentEvidence = ReplaceOnce(
            evidence,
            "\"MemoryNetGrowthBytes\":10000000",
            "\"MemoryNetGrowthBytes\":10000999");
        files.WriteEvidence(roundedPercentEvidence);
        files.ValidateM16();

        files.WriteEvidence(ReplaceOnce(
            evidence,
            "\"ResourceSampleCount\":289",
            "\"ResourceSampleCount\":286"));
        files.ValidateM16();

        files.WriteEvidence(ReplaceOnce(
            evidence,
            "\"ResourceSampleCount\":289",
            "\"ResourceSampleCount\":290"));
        files.ValidateM16();
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
            writer.WriteNumber("SchemaVersion", 10);
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
            writer.WriteNumber("ProbeEnvelopeSchemaVersion", 8);
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
            writer.WriteNumber("DetachedSourceCount", 103);
            writer.WriteNumber("PlaybackRetryCount", 1);
            writer.WriteNumber("SourceDetachP95Milliseconds", 8.25);
            writer.WriteNumber("SourceDetachMaximumMilliseconds", 10.5);
            writer.WriteNumber("NetworkInterruptionCount", 1);
            writer.WriteNumber("NetworkRecoveryCount", 1);
            writer.WriteNumber("LastInjectedRequestOrdinal", 50);
            writer.WriteNumber("LastRecoveryRequestOrdinal", 52);
            writer.WriteNumber("CancellationProbeCount", 1);
            writer.WriteNumber("CancellationObservedCount", 1);
            writer.WriteNumber("CancellationSourceDetachCount", 1);
            writer.WriteNumber("CancellationRecoveryCount", 1);
            writer.WriteNumber("CancellationRecoverySourceDetachCount", 1);
            writer.WriteNumber("CancellationLatencyMilliseconds", 125.25);
            writer.WriteNumber("CancellationQuiescenceMilliseconds", 175.5);
            writer.WriteNumber("CancellationObservationMilliseconds", 1025.75);
            writer.WriteNumber("CancellationSourceDetachMilliseconds", 9.5);
            writer.WriteNumber("CancellationRecoveryStartupMilliseconds", 225.25);
            writer.WriteNumber("CancellationRecoveryAdvanceMilliseconds", 275.5);
            writer.WriteNumber("CancellationRecoverySourceDetachMilliseconds", 10.25);
            writer.WriteBoolean("CancellationSourceNullAfterObservation", true);
            writer.WriteBoolean("CancellationRecoveryUsedFreshSource", true);
            writer.WriteBoolean("CancellationNoAutomaticRestart", true);
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

    private static string CreateCancellationDisabledEvidence(string evidence)
    {
        (string Original, string Replacement)[] replacements =
        [
            ("\"DetachedSourceCount\":103", "\"DetachedSourceCount\":101"),
            ("\"CancellationProbeCount\":1", "\"CancellationProbeCount\":0"),
            ("\"CancellationObservedCount\":1", "\"CancellationObservedCount\":0"),
            ("\"CancellationSourceDetachCount\":1", "\"CancellationSourceDetachCount\":0"),
            ("\"CancellationRecoveryCount\":1", "\"CancellationRecoveryCount\":0"),
            (
                "\"CancellationRecoverySourceDetachCount\":1",
                "\"CancellationRecoverySourceDetachCount\":0"),
            (
                "\"CancellationLatencyMilliseconds\":125.25",
                "\"CancellationLatencyMilliseconds\":0"),
            (
                "\"CancellationQuiescenceMilliseconds\":175.5",
                "\"CancellationQuiescenceMilliseconds\":0"),
            (
                "\"CancellationObservationMilliseconds\":1025.75",
                "\"CancellationObservationMilliseconds\":0"),
            (
                "\"CancellationSourceDetachMilliseconds\":9.5",
                "\"CancellationSourceDetachMilliseconds\":0"),
            (
                "\"CancellationRecoveryStartupMilliseconds\":225.25",
                "\"CancellationRecoveryStartupMilliseconds\":0"),
            (
                "\"CancellationRecoveryAdvanceMilliseconds\":275.5",
                "\"CancellationRecoveryAdvanceMilliseconds\":0"),
            (
                "\"CancellationRecoverySourceDetachMilliseconds\":10.25",
                "\"CancellationRecoverySourceDetachMilliseconds\":0"),
            (
                "\"CancellationSourceNullAfterObservation\":true",
                "\"CancellationSourceNullAfterObservation\":false"),
            (
                "\"CancellationRecoveryUsedFreshSource\":true",
                "\"CancellationRecoveryUsedFreshSource\":false"),
            (
                "\"CancellationNoAutomaticRestart\":true",
                "\"CancellationNoAutomaticRestart\":false"),
        ];

        foreach ((string original, string replacement) in replacements)
        {
            evidence = ReplaceOnce(evidence, original, replacement);
        }

        return evidence;
    }

    private static string CreateValidM16Evidence(string controllerSha256)
    {
        string evidence = CreateCancellationDisabledEvidence(CreateValidEvidence(controllerSha256));
        (string Original, string Replacement)[] replacements =
        [
            ("\"SchemaVersion\":10", "\"SchemaVersion\":11"),
            (
                "\"Stage\":\"M10NativeTierAPlayback\"",
                "\"Stage\":\"M16NativeTierAFinalAcceptance\""),
            ("\"SwitchCount\":100", "\"SwitchCount\":200"),
            ("\"SoakMinutes\":0", "\"SoakMinutes\":1440"),
            ("\"ResourceSampleCount\":0", "\"ResourceSampleCount\":289"),
            ("\"WarmupPrivateBytes\":0", "\"WarmupPrivateBytes\":200000000"),
            ("\"MemoryNetGrowthBytes\":0", "\"MemoryNetGrowthBytes\":10000000"),
            ("\"MemoryNetGrowthPercent\":0", "\"MemoryNetGrowthPercent\":5"),
            ("\"WarmupHandleCount\":0", "\"WarmupHandleCount\":1000"),
            ("\"HandleNetGrowth\":0", "\"HandleNetGrowth\":-10"),
            ("\"DetachedSourceCount\":101", "\"DetachedSourceCount\":202"),
            ("\"NetworkInterruptionCount\":1", "\"NetworkInterruptionCount\":7"),
            ("\"NetworkRecoveryCount\":1", "\"NetworkRecoveryCount\":7"),
            ("\"LoopbackRequestCount\":120", "\"LoopbackRequestCount\":240"),
        ];

        foreach ((string original, string replacement) in replacements)
        {
            evidence = ReplaceOnce(evidence, original, replacement);
        }

        return evidence;
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

        internal void ValidateM16() =>
            M16NativePlaybackEvidenceValidator.Validate(
                EvidencePath,
                ControllerPath,
                CommitSha,
                ExpectedSdk);

        public void Dispose() => _temporary.Dispose();
    }
}
