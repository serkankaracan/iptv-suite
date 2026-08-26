using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace IptvSuite.Testing;

public static class NativePlaybackEvidenceValidator
{
    private const int MaximumEvidenceBytes = 64 * 1024;
    private const int MaximumControllerBytes = 1024 * 1024;
    private const int M16MinimumResourceSampleCount = (1440 / 5) - 2;
    private const int M16MaximumResourceSampleCount = 290;
    private const string ExpectedM10Stage = "M10NativeTierAPlayback";
    private const string ExpectedM16Stage = "M16NativeTierAFinalAcceptance";
    private const string ExpectedRuntimeName = "Microsoft.WindowsAppRuntime.2";
    private const string ExpectedRuntimePublisherId = "8wekyb3d8bbwe";

    private static readonly string[] ExpectedPropertyNames =
    [
        "SchemaVersion",
        "Stage",
        "Result",
        "RunId",
        "CompletedAtUtc",
        "Configuration",
        "Platform",
        "DotNetSdk",
        "CleanHeadBound",
        "CommitSha",
        "ControllerScriptSha256",
        "HarnessAssemblySha256",
        "FixtureManifestSha256",
        "FixtureCorpusVerified",
        "ProbeEnvelopeSchemaVersion",
        "ProbeRunIdBound",
        "SwitchCount",
        "StartupP95Milliseconds",
        "StartupMaximumMilliseconds",
        "HlsStartupP95Milliseconds",
        "DirectStartupP95Milliseconds",
        "SoakMinutes",
        "ResourceSampleCount",
        "WarmupPrivateBytes",
        "MemoryNetGrowthBytes",
        "MemoryNetGrowthPercent",
        "MemoryMonotonicIncrease",
        "WarmupHandleCount",
        "HandleNetGrowth",
        "SurfaceTransitionCount",
        "DetachedSourceCount",
        "PlaybackRetryCount",
        "SourceDetachP95Milliseconds",
        "SourceDetachMaximumMilliseconds",
        "NetworkInterruptionCount",
        "NetworkRecoveryCount",
        "LastInjectedRequestOrdinal",
        "LastRecoveryRequestOrdinal",
        "CancellationProbeCount",
        "CancellationObservedCount",
        "CancellationSourceDetachCount",
        "CancellationRecoveryCount",
        "CancellationRecoverySourceDetachCount",
        "CancellationLatencyMilliseconds",
        "CancellationQuiescenceMilliseconds",
        "CancellationObservationMilliseconds",
        "CancellationSourceDetachMilliseconds",
        "CancellationRecoveryStartupMilliseconds",
        "CancellationRecoveryAdvanceMilliseconds",
        "CancellationRecoverySourceDetachMilliseconds",
        "CancellationSourceNullAfterObservation",
        "CancellationRecoveryUsedFreshSource",
        "CancellationNoAutomaticRestart",
        "InitialPrivateBytes",
        "FinalPrivateBytes",
        "InitialHandleCount",
        "FinalHandleCount",
        "LoopbackRequestCount",
        "H264DecoderRegistered",
        "AacDecoderRegistered",
        "Transport",
        "Fixtures",
        "PackageSha256",
        "PackageSignatureStatus",
        "RuntimeDependencyPackageSha256",
        "RuntimeDependencyPackageSignatureStatus",
        "ResolvedWindowsAppRuntimeName",
        "ResolvedWindowsAppRuntimeVersion",
        "ResolvedWindowsAppRuntimeArchitecture",
        "ResolvedWindowsAppRuntimePublisherId",
        "ResolvedWindowsAppRuntimeIsFramework",
        "NormalCloseVerified",
        "ForcedProcessTerminationUsed",
        "ProcessCleanupPassed",
        "TlsServerDisposed",
        "PackageRemoved",
        "PackageAppDataRemoved",
        "PackageAppDataEmptyRootCleanupUsed",
        "RuntimePackageBaselinePreserved",
        "RuntimePackageGraphDisposition",
        "RuntimePackageSharedAdditionCount",
        "EphemeralCertificatesRemoved",
        "ExportedCertificateFilesRemoved",
        "PackageOutputRemoved",
        "EnvironmentRestored",
        "RepositoryCleanAfterRun",
    ];

    public static void Validate(
        string evidencePath,
        string controllerPath,
        string expectedCommitSha,
        string expectedSdk)
    {
        ValidateCore(
            evidencePath,
            controllerPath,
            expectedCommitSha,
            expectedSdk,
            m16FinalAcceptance: false);
    }

    internal static void ValidateM16FinalAcceptance(
        string evidencePath,
        string controllerPath,
        string expectedCommitSha,
        string expectedSdk)
    {
        ValidateCore(
            evidencePath,
            controllerPath,
            expectedCommitSha,
            expectedSdk,
            m16FinalAcceptance: true);
    }

    private static void ValidateCore(
        string evidencePath,
        string controllerPath,
        string expectedCommitSha,
        string expectedSdk,
        bool m16FinalAcceptance)
    {
        if (!IsLowerHex(expectedCommitSha, 40))
        {
            throw Invalid("Expected native playback commit is invalid.");
        }

        if (string.IsNullOrWhiteSpace(expectedSdk) ||
            !string.Equals(expectedSdk, expectedSdk.Trim(), StringComparison.Ordinal))
        {
            throw Invalid("Expected native playback SDK is invalid.");
        }

        byte[] controllerBytes = ReadRegularFile(
            controllerPath,
            MaximumControllerBytes,
            "Native playback controller could not be read.");
        string controllerSha256 = Convert.ToHexString(SHA256.HashData(controllerBytes)).ToLowerInvariant();
        byte[] evidenceBytes = ReadRegularFile(
            evidencePath,
            MaximumEvidenceBytes,
            "Native playback evidence could not be read.");

        ReadOnlyMemory<byte> json = evidenceBytes;
        if (evidenceBytes.Length >= 3 &&
            evidenceBytes[0] == 0xef &&
            evidenceBytes[1] == 0xbb &&
            evidenceBytes[2] == 0xbf)
        {
            json = evidenceBytes.AsMemory(3);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            ValidateRoot(
                document.RootElement,
                controllerSha256,
                expectedCommitSha,
                expectedSdk,
                m16FinalAcceptance);
        }
        catch (JsonException)
        {
            throw Invalid("Native playback evidence JSON is invalid.");
        }
    }

    private static void ValidateRoot(
        JsonElement root,
        string controllerSha256,
        string expectedCommitSha,
        string expectedSdk,
        bool m16FinalAcceptance)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Native playback evidence root is invalid.");
        }

        JsonProperty[] properties = root.EnumerateObject().ToArray();
        if (properties.Length != ExpectedPropertyNames.Length ||
            !properties.Select(property => property.Name).SequenceEqual(
                ExpectedPropertyNames,
                StringComparer.Ordinal))
        {
            throw Invalid("Native playback evidence property sequence is invalid.");
        }

        RequireEqual(
            RequireInt32(root, "SchemaVersion"),
            m16FinalAcceptance ? 11 : 10,
            "SchemaVersion");
        RequireEqual(
            RequireString(root, "Stage"),
            m16FinalAcceptance ? ExpectedM16Stage : ExpectedM10Stage,
            "Stage");
        RequireEqual(RequireString(root, "Result"), "Passed", "Result");
        RequireLowerHex(root, "RunId", 32);
        RequireUtcRoundTripTimestamp(root, "CompletedAtUtc");
        RequireEqual(RequireString(root, "Configuration"), "Release", "Configuration");
        RequireEqual(RequireString(root, "Platform"), "x64", "Platform");
        RequireEqual(RequireString(root, "DotNetSdk"), expectedSdk, "DotNetSdk");
        RequireEqual(RequireBoolean(root, "CleanHeadBound"), true, "CleanHeadBound");
        RequireEqual(RequireString(root, "CommitSha"), expectedCommitSha, "CommitSha");
        RequireEqual(
            RequireString(root, "ControllerScriptSha256"),
            controllerSha256,
            "ControllerScriptSha256");
        RequireLowerHex(root, "HarnessAssemblySha256", 64);
        RequireLowerHex(root, "FixtureManifestSha256", 64);
        RequireEqual(RequireBoolean(root, "FixtureCorpusVerified"), true, "FixtureCorpusVerified");
        RequireEqual(RequireInt32(root, "ProbeEnvelopeSchemaVersion"), 8, "ProbeEnvelopeSchemaVersion");
        RequireEqual(RequireBoolean(root, "ProbeRunIdBound"), true, "ProbeRunIdBound");

        int switchCount = RequireInt32(root, "SwitchCount");
        RequireEqual(switchCount, m16FinalAcceptance ? 200 : 100, "SwitchCount");
        double startupP95 = RequireNonNegativeDouble(root, "StartupP95Milliseconds");
        double startupMaximum = RequireNonNegativeDouble(root, "StartupMaximumMilliseconds");
        double hlsStartupP95 = RequireNonNegativeDouble(root, "HlsStartupP95Milliseconds");
        double directStartupP95 = RequireNonNegativeDouble(root, "DirectStartupP95Milliseconds");
        if (startupP95 > 3000 || startupMaximum > 5000 || startupP95 > startupMaximum ||
            hlsStartupP95 > startupMaximum || directStartupP95 > startupMaximum)
        {
            throw InvalidProperty("StartupP95Milliseconds");
        }

        int soakMinutes = RequireInt32(root, "SoakMinutes");
        int resourceSampleCount = RequireInt32(root, "ResourceSampleCount");
        long warmupPrivateBytes = RequireInt64(root, "WarmupPrivateBytes");
        long memoryNetGrowthBytes = RequireInt64(root, "MemoryNetGrowthBytes");
        double memoryNetGrowthPercent = RequireDouble(root, "MemoryNetGrowthPercent");
        bool memoryMonotonicIncrease = RequireBoolean(root, "MemoryMonotonicIncrease");
        int warmupHandleCount = RequireInt32(root, "WarmupHandleCount");
        int handleNetGrowth = RequireInt32(root, "HandleNetGrowth");
        if (m16FinalAcceptance)
        {
            RequireEqual(soakMinutes, 1440, "SoakMinutes");
            if (resourceSampleCount is < M16MinimumResourceSampleCount or
                > M16MaximumResourceSampleCount)
            {
                throw InvalidProperty("ResourceSampleCount");
            }

            if (warmupPrivateBytes <= 0)
            {
                throw InvalidProperty("WarmupPrivateBytes");
            }

            if (memoryNetGrowthBytes > 100L * 1024 * 1024)
            {
                throw InvalidProperty("MemoryNetGrowthBytes");
            }

            if (memoryNetGrowthPercent > 10d)
            {
                throw InvalidProperty("MemoryNetGrowthPercent");
            }

            double calculatedMemoryNetGrowthPercent =
                (double)memoryNetGrowthBytes * 100d / warmupPrivateBytes;
            if (Math.Abs(
                    memoryNetGrowthPercent -
                    calculatedMemoryNetGrowthPercent) > 0.0005001d)
            {
                throw InvalidProperty("MemoryNetGrowthPercent");
            }

            if (memoryMonotonicIncrease)
            {
                throw InvalidProperty("MemoryMonotonicIncrease");
            }

            if (warmupHandleCount <= 0)
            {
                throw InvalidProperty("WarmupHandleCount");
            }

            _ = handleNetGrowth;
        }
        else
        {
            RequireEqual(soakMinutes, 0, "SoakMinutes");
            RequireEqual(resourceSampleCount, 0, "ResourceSampleCount");
            RequireEqual(warmupPrivateBytes, 0L, "WarmupPrivateBytes");
            RequireEqual(memoryNetGrowthBytes, 0L, "MemoryNetGrowthBytes");
            RequireEqual(memoryNetGrowthPercent, 0d, "MemoryNetGrowthPercent");
            RequireEqual(memoryMonotonicIncrease, false, "MemoryMonotonicIncrease");
            RequireEqual(warmupHandleCount, 0, "WarmupHandleCount");
            RequireEqual(handleNetGrowth, 0, "HandleNetGrowth");
        }
        RequireEqual(RequireInt32(root, "SurfaceTransitionCount"), 6, "SurfaceTransitionCount");

        int playbackRetryCount = RequireInt32(root, "PlaybackRetryCount");
        int maximumPlaybackRetryCount = m16FinalAcceptance ? 7 : 1;
        if (playbackRetryCount < 0 || playbackRetryCount > maximumPlaybackRetryCount)
        {
            throw InvalidProperty("PlaybackRetryCount");
        }

        double sourceDetachP95 = RequireNonNegativeDouble(root, "SourceDetachP95Milliseconds");
        double sourceDetachMaximum = RequireNonNegativeDouble(root, "SourceDetachMaximumMilliseconds");
        if (sourceDetachP95 > 3000 || sourceDetachMaximum > 5000 ||
            sourceDetachP95 > sourceDetachMaximum)
        {
            throw InvalidProperty("SourceDetachP95Milliseconds");
        }

        int cancellationProbeCount = RequireInt32(root, "CancellationProbeCount");
        if (cancellationProbeCount is < 0 or > 1 ||
            (m16FinalAcceptance && cancellationProbeCount != 0))
        {
            throw InvalidProperty("CancellationProbeCount");
        }

        RequireEqual(
            RequireInt32(root, "DetachedSourceCount"),
            switchCount + playbackRetryCount + (cancellationProbeCount * 2) +
                (m16FinalAcceptance ? 1 : 0),
            "DetachedSourceCount");

        int cancellationObservedCount = RequireInt32(root, "CancellationObservedCount");
        int cancellationSourceDetachCount = RequireInt32(root, "CancellationSourceDetachCount");
        int cancellationRecoveryCount = RequireInt32(root, "CancellationRecoveryCount");
        int cancellationRecoverySourceDetachCount =
            RequireInt32(root, "CancellationRecoverySourceDetachCount");
        double cancellationLatency = RequireDouble(root, "CancellationLatencyMilliseconds");
        double cancellationQuiescence = RequireDouble(root, "CancellationQuiescenceMilliseconds");
        double cancellationObservation = RequireDouble(root, "CancellationObservationMilliseconds");
        double cancellationSourceDetach = RequireDouble(root, "CancellationSourceDetachMilliseconds");
        double cancellationRecoveryStartup =
            RequireDouble(root, "CancellationRecoveryStartupMilliseconds");
        double cancellationRecoveryAdvance =
            RequireDouble(root, "CancellationRecoveryAdvanceMilliseconds");
        double cancellationRecoverySourceDetach =
            RequireDouble(root, "CancellationRecoverySourceDetachMilliseconds");
        bool cancellationSourceNullAfterObservation =
            RequireBoolean(root, "CancellationSourceNullAfterObservation");
        bool cancellationRecoveryUsedFreshSource =
            RequireBoolean(root, "CancellationRecoveryUsedFreshSource");
        bool cancellationNoAutomaticRestart =
            RequireBoolean(root, "CancellationNoAutomaticRestart");

        if (cancellationProbeCount == 0)
        {
            RequireEqual(cancellationObservedCount, 0, "CancellationObservedCount");
            RequireEqual(cancellationSourceDetachCount, 0, "CancellationSourceDetachCount");
            RequireEqual(cancellationRecoveryCount, 0, "CancellationRecoveryCount");
            RequireEqual(
                cancellationRecoverySourceDetachCount,
                0,
                "CancellationRecoverySourceDetachCount");
            RequireEqual(cancellationLatency, 0d, "CancellationLatencyMilliseconds");
            RequireEqual(cancellationQuiescence, 0d, "CancellationQuiescenceMilliseconds");
            RequireEqual(cancellationObservation, 0d, "CancellationObservationMilliseconds");
            RequireEqual(cancellationSourceDetach, 0d, "CancellationSourceDetachMilliseconds");
            RequireEqual(
                cancellationRecoveryStartup,
                0d,
                "CancellationRecoveryStartupMilliseconds");
            RequireEqual(
                cancellationRecoveryAdvance,
                0d,
                "CancellationRecoveryAdvanceMilliseconds");
            RequireEqual(
                cancellationRecoverySourceDetach,
                0d,
                "CancellationRecoverySourceDetachMilliseconds");
            RequireEqual(
                cancellationSourceNullAfterObservation,
                false,
                "CancellationSourceNullAfterObservation");
            RequireEqual(
                cancellationRecoveryUsedFreshSource,
                false,
                "CancellationRecoveryUsedFreshSource");
            RequireEqual(
                cancellationNoAutomaticRestart,
                false,
                "CancellationNoAutomaticRestart");
        }
        else
        {
            RequireEqual(cancellationObservedCount, 1, "CancellationObservedCount");
            RequireEqual(cancellationSourceDetachCount, 1, "CancellationSourceDetachCount");
            RequireEqual(cancellationRecoveryCount, 1, "CancellationRecoveryCount");
            RequireEqual(
                cancellationRecoverySourceDetachCount,
                1,
                "CancellationRecoverySourceDetachCount");
            if (cancellationLatency < 0 || cancellationLatency > 1000)
            {
                throw InvalidProperty("CancellationLatencyMilliseconds");
            }

            if (cancellationQuiescence < 0 || cancellationQuiescence > 1000)
            {
                throw InvalidProperty("CancellationQuiescenceMilliseconds");
            }

            if (cancellationObservation < 1000 || cancellationObservation > 1500)
            {
                throw InvalidProperty("CancellationObservationMilliseconds");
            }

            if (cancellationSourceDetach < 0 || cancellationSourceDetach > 5000)
            {
                throw InvalidProperty("CancellationSourceDetachMilliseconds");
            }

            if (cancellationRecoveryStartup <= 0 || cancellationRecoveryStartup > 5000)
            {
                throw InvalidProperty("CancellationRecoveryStartupMilliseconds");
            }

            if (cancellationRecoveryAdvance <= 0 || cancellationRecoveryAdvance > 3000)
            {
                throw InvalidProperty("CancellationRecoveryAdvanceMilliseconds");
            }

            if (cancellationRecoverySourceDetach < 0 || cancellationRecoverySourceDetach > 5000)
            {
                throw InvalidProperty("CancellationRecoverySourceDetachMilliseconds");
            }

            const double roundingTolerance = 0.002;
            if (cancellationLatency + cancellationSourceDetach >
                cancellationQuiescence + roundingTolerance)
            {
                throw InvalidProperty("CancellationQuiescenceMilliseconds");
            }

            if (cancellationQuiescence + cancellationObservation < 1000 - roundingTolerance)
            {
                throw InvalidProperty("CancellationObservationMilliseconds");
            }

            if (cancellationSourceDetach > sourceDetachMaximum + roundingTolerance ||
                cancellationRecoverySourceDetach > sourceDetachMaximum + roundingTolerance)
            {
                throw InvalidProperty("SourceDetachMaximumMilliseconds");
            }

            RequireEqual(
                cancellationSourceNullAfterObservation,
                true,
                "CancellationSourceNullAfterObservation");
            RequireEqual(
                cancellationRecoveryUsedFreshSource,
                true,
                "CancellationRecoveryUsedFreshSource");
            RequireEqual(
                cancellationNoAutomaticRestart,
                true,
                "CancellationNoAutomaticRestart");
        }

        int expectedNetworkInterruptionCount = m16FinalAcceptance ? 7 : 1;
        RequireEqual(
            RequireInt32(root, "NetworkInterruptionCount"),
            expectedNetworkInterruptionCount,
            "NetworkInterruptionCount");
        RequireEqual(
            RequireInt32(root, "NetworkRecoveryCount"),
            expectedNetworkInterruptionCount,
            "NetworkRecoveryCount");
        int injectedOrdinal = RequireInt32(root, "LastInjectedRequestOrdinal");
        int recoveryOrdinal = RequireInt32(root, "LastRecoveryRequestOrdinal");
        if (injectedOrdinal <= 0 || recoveryOrdinal <= injectedOrdinal)
        {
            throw InvalidProperty("LastRecoveryRequestOrdinal");
        }

        long initialPrivateBytes = RequireInt64(root, "InitialPrivateBytes");
        long finalPrivateBytes = RequireInt64(root, "FinalPrivateBytes");
        int initialHandleCount = RequireInt32(root, "InitialHandleCount");
        int finalHandleCount = RequireInt32(root, "FinalHandleCount");
        if (initialPrivateBytes < 0 || finalPrivateBytes < 0 ||
            initialHandleCount < 0 || finalHandleCount < 0 ||
            (m16FinalAcceptance &&
                (initialPrivateBytes == 0 || finalPrivateBytes == 0 ||
                 initialHandleCount == 0 || finalHandleCount == 0)))
        {
            throw InvalidProperty("InitialPrivateBytes");
        }
        if (RequireInt32(root, "LoopbackRequestCount") < switchCount)
        {
            throw InvalidProperty("LoopbackRequestCount");
        }

        RequireEqual(RequireBoolean(root, "H264DecoderRegistered"), true, "H264DecoderRegistered");
        RequireEqual(RequireBoolean(root, "AacDecoderRegistered"), true, "AacDecoderRegistered");
        RequireEqual(RequireString(root, "Transport"), "Tls12LoopbackAllowlist", "Transport");
        RequireExactFixtures(root);
        RequireLowerHex(root, "PackageSha256", 64);
        RequireEqual(RequireString(root, "PackageSignatureStatus"), "Valid", "PackageSignatureStatus");
        RequireLowerHex(root, "RuntimeDependencyPackageSha256", 64);
        RequireEqual(
            RequireString(root, "RuntimeDependencyPackageSignatureStatus"),
            "Valid",
            "RuntimeDependencyPackageSignatureStatus");
        RequireEqual(
            RequireString(root, "ResolvedWindowsAppRuntimeName"),
            ExpectedRuntimeName,
            "ResolvedWindowsAppRuntimeName");
        RequireRuntimeVersion(root);
        RequireEqual(
            RequireString(root, "ResolvedWindowsAppRuntimeArchitecture"),
            "x64",
            "ResolvedWindowsAppRuntimeArchitecture");
        RequireEqual(
            RequireString(root, "ResolvedWindowsAppRuntimePublisherId"),
            ExpectedRuntimePublisherId,
            "ResolvedWindowsAppRuntimePublisherId");
        RequireEqual(
            RequireBoolean(root, "ResolvedWindowsAppRuntimeIsFramework"),
            true,
            "ResolvedWindowsAppRuntimeIsFramework");

        RequireEqual(RequireBoolean(root, "NormalCloseVerified"), true, "NormalCloseVerified");
        RequireEqual(
            RequireBoolean(root, "ForcedProcessTerminationUsed"),
            false,
            "ForcedProcessTerminationUsed");
        RequireEqual(RequireBoolean(root, "ProcessCleanupPassed"), true, "ProcessCleanupPassed");
        RequireEqual(RequireBoolean(root, "TlsServerDisposed"), true, "TlsServerDisposed");
        RequireEqual(RequireBoolean(root, "PackageRemoved"), true, "PackageRemoved");
        RequireEqual(RequireBoolean(root, "PackageAppDataRemoved"), true, "PackageAppDataRemoved");
        _ = RequireBoolean(root, "PackageAppDataEmptyRootCleanupUsed");
        RequireEqual(
            RequireBoolean(root, "RuntimePackageBaselinePreserved"),
            true,
            "RuntimePackageBaselinePreserved");
        string runtimePackageGraphDisposition = RequireString(root, "RuntimePackageGraphDisposition");
        int runtimePackageSharedAdditionCount =
            RequireInt32(root, "RuntimePackageSharedAdditionCount");
        if (runtimePackageSharedAdditionCount is < 0 or > 64)
        {
            throw InvalidProperty("RuntimePackageSharedAdditionCount");
        }

        if (string.Equals(
                runtimePackageGraphDisposition,
                "ExactRestored",
                StringComparison.Ordinal))
        {
            if (runtimePackageSharedAdditionCount != 0)
            {
                throw InvalidProperty("RuntimePackageSharedAdditionCount");
            }
        }
        else if (string.Equals(
                     runtimePackageGraphDisposition,
                     "SharedAdditionsPreserved",
                     StringComparison.Ordinal))
        {
            if (runtimePackageSharedAdditionCount == 0)
            {
                throw InvalidProperty("RuntimePackageSharedAdditionCount");
            }
        }
        else
        {
            throw InvalidProperty("RuntimePackageGraphDisposition");
        }
        RequireEqual(
            RequireBoolean(root, "EphemeralCertificatesRemoved"),
            true,
            "EphemeralCertificatesRemoved");
        RequireEqual(
            RequireBoolean(root, "ExportedCertificateFilesRemoved"),
            true,
            "ExportedCertificateFilesRemoved");
        RequireEqual(RequireBoolean(root, "PackageOutputRemoved"), true, "PackageOutputRemoved");
        RequireEqual(RequireBoolean(root, "EnvironmentRestored"), true, "EnvironmentRestored");
        RequireEqual(
            RequireBoolean(root, "RepositoryCleanAfterRun"),
            true,
            "RepositoryCleanAfterRun");
    }

    private static byte[] ReadRegularFile(string path, int maximumBytes, string failureMessage)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new IOException();
            }

            string fullPath = Path.GetFullPath(path);
            FileInfo file = new(fullPath);
            if (!file.Exists ||
                (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                file.Length is <= 0 || file.Length > maximumBytes)
            {
                throw new IOException();
            }

            byte[] bytes = File.ReadAllBytes(fullPath);
            if (bytes.Length is 0 || bytes.Length > maximumBytes)
            {
                throw new IOException();
            }

            return bytes;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException)
        {
            throw Invalid(failureMessage);
        }
    }

    private static string RequireString(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not string text)
        {
            throw InvalidProperty(name);
        }

        return text;
    }

    private static bool RequireBoolean(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw InvalidProperty(name);
        }

        return value.GetBoolean();
    }

    private static int RequireInt32(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
        {
            throw InvalidProperty(name);
        }

        return result;
    }

    private static long RequireInt64(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result))
        {
            throw InvalidProperty(name);
        }

        return result;
    }

    private static double RequireDouble(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) ||
            !double.IsFinite(result))
        {
            throw InvalidProperty(name);
        }

        return result;
    }

    private static double RequireNonNegativeDouble(JsonElement root, string name)
    {
        double value = RequireDouble(root, name);
        if (value < 0)
        {
            throw InvalidProperty(name);
        }

        return value;
    }

    private static void RequireNonNegativeInt32(JsonElement root, string name)
    {
        if (RequireInt32(root, name) < 0)
        {
            throw InvalidProperty(name);
        }
    }

    private static void RequireNonNegativeInt64(JsonElement root, string name)
    {
        if (RequireInt64(root, name) < 0)
        {
            throw InvalidProperty(name);
        }
    }

    private static void RequireExactFixtures(JsonElement root)
    {
        JsonElement value = root.GetProperty("Fixtures");
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidProperty("Fixtures");
        }

        JsonElement[] fixtures = value.EnumerateArray().ToArray();
        if (fixtures.Length != 2 ||
            fixtures[0].ValueKind != JsonValueKind.String ||
            fixtures[1].ValueKind != JsonValueKind.String ||
            !string.Equals(fixtures[0].GetString(), "DirectH264AacMpegTs", StringComparison.Ordinal) ||
            !string.Equals(fixtures[1].GetString(), "HlsH264AacMpegTs", StringComparison.Ordinal))
        {
            throw InvalidProperty("Fixtures");
        }
    }

    private static void RequireRuntimeVersion(JsonElement root)
    {
        string text = RequireString(root, "ResolvedWindowsAppRuntimeVersion");
        if (!Version.TryParse(text, out Version? version) ||
            version.Revision < 0 ||
            !string.Equals(version.ToString(4), text, StringComparison.Ordinal) ||
            version.Major != 2 ||
            version < new Version(2, 4, 0, 0))
        {
            throw InvalidProperty("ResolvedWindowsAppRuntimeVersion");
        }
    }

    private static void RequireUtcRoundTripTimestamp(JsonElement root, string name)
    {
        string text = RequireString(root, name);
        if (!DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset timestamp) ||
            timestamp.Offset != TimeSpan.Zero)
        {
            throw InvalidProperty(name);
        }
    }

    private static void RequireLowerHex(JsonElement root, string name, int length)
    {
        if (!IsLowerHex(RequireString(root, name), length))
        {
            throw InvalidProperty(name);
        }
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireEqual<T>(T actual, T expected, string name)
        where T : IEquatable<T>
    {
        if (!actual.Equals(expected))
        {
            throw InvalidProperty(name);
        }
    }

    private static InvalidDataException InvalidProperty(string name) =>
        Invalid($"Native playback evidence property '{name}' is invalid.");

    private static InvalidDataException Invalid(string message) => new(message);
}

/// <summary>
/// Validates the fixed M16 native soak evidence contract. This does not infer
/// OS-level audio-session quiescence from the managed source-detach metrics.
/// </summary>
public static class M16NativePlaybackEvidenceValidator
{
    public static void Validate(
        string evidencePath,
        string controllerPath,
        string expectedCommitSha,
        string expectedSdk) =>
        NativePlaybackEvidenceValidator.ValidateM16FinalAcceptance(
            evidencePath,
            controllerPath,
            expectedCommitSha,
            expectedSdk);
}
