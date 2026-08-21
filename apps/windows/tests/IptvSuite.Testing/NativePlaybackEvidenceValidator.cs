using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace IptvSuite.Testing;

public static class NativePlaybackEvidenceValidator
{
    private const int MaximumEvidenceBytes = 64 * 1024;
    private const int MaximumControllerBytes = 1024 * 1024;
    private const string ExpectedStage = "M10NativeTierAPlayback";
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
        "RuntimePackageGraphRestored",
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
            ValidateRoot(document.RootElement, controllerSha256, expectedCommitSha, expectedSdk);
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
        string expectedSdk)
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

        RequireEqual(RequireInt32(root, "SchemaVersion"), 8, "SchemaVersion");
        RequireEqual(RequireString(root, "Stage"), ExpectedStage, "Stage");
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
        RequireEqual(RequireInt32(root, "ProbeEnvelopeSchemaVersion"), 1, "ProbeEnvelopeSchemaVersion");
        RequireEqual(RequireBoolean(root, "ProbeRunIdBound"), true, "ProbeRunIdBound");

        int switchCount = RequireInt32(root, "SwitchCount");
        RequireEqual(switchCount, 100, "SwitchCount");
        double startupP95 = RequireNonNegativeDouble(root, "StartupP95Milliseconds");
        double startupMaximum = RequireNonNegativeDouble(root, "StartupMaximumMilliseconds");
        double hlsStartupP95 = RequireNonNegativeDouble(root, "HlsStartupP95Milliseconds");
        double directStartupP95 = RequireNonNegativeDouble(root, "DirectStartupP95Milliseconds");
        if (startupP95 > 3000 || startupMaximum > 5000 || startupP95 > startupMaximum ||
            hlsStartupP95 > startupMaximum || directStartupP95 > startupMaximum)
        {
            throw InvalidProperty("StartupP95Milliseconds");
        }

        RequireEqual(RequireInt32(root, "SoakMinutes"), 0, "SoakMinutes");
        RequireEqual(RequireInt32(root, "ResourceSampleCount"), 0, "ResourceSampleCount");
        RequireEqual(RequireInt64(root, "WarmupPrivateBytes"), 0L, "WarmupPrivateBytes");
        RequireEqual(RequireInt64(root, "MemoryNetGrowthBytes"), 0L, "MemoryNetGrowthBytes");
        RequireEqual(RequireDouble(root, "MemoryNetGrowthPercent"), 0d, "MemoryNetGrowthPercent");
        RequireEqual(RequireBoolean(root, "MemoryMonotonicIncrease"), false, "MemoryMonotonicIncrease");
        RequireEqual(RequireInt32(root, "WarmupHandleCount"), 0, "WarmupHandleCount");
        RequireEqual(RequireInt32(root, "HandleNetGrowth"), 0, "HandleNetGrowth");
        RequireEqual(RequireInt32(root, "SurfaceTransitionCount"), 6, "SurfaceTransitionCount");

        int playbackRetryCount = RequireInt32(root, "PlaybackRetryCount");
        if (playbackRetryCount is < 0 or > 1)
        {
            throw InvalidProperty("PlaybackRetryCount");
        }

        RequireEqual(
            RequireInt32(root, "DetachedSourceCount"),
            switchCount + playbackRetryCount,
            "DetachedSourceCount");
        double sourceDetachP95 = RequireNonNegativeDouble(root, "SourceDetachP95Milliseconds");
        double sourceDetachMaximum = RequireNonNegativeDouble(root, "SourceDetachMaximumMilliseconds");
        if (sourceDetachP95 > 3000 || sourceDetachMaximum > 5000 ||
            sourceDetachP95 > sourceDetachMaximum)
        {
            throw InvalidProperty("SourceDetachP95Milliseconds");
        }

        RequireEqual(RequireInt32(root, "NetworkInterruptionCount"), 1, "NetworkInterruptionCount");
        RequireEqual(RequireInt32(root, "NetworkRecoveryCount"), 1, "NetworkRecoveryCount");
        int injectedOrdinal = RequireInt32(root, "LastInjectedRequestOrdinal");
        int recoveryOrdinal = RequireInt32(root, "LastRecoveryRequestOrdinal");
        if (injectedOrdinal <= 0 || recoveryOrdinal <= injectedOrdinal)
        {
            throw InvalidProperty("LastRecoveryRequestOrdinal");
        }

        RequireNonNegativeInt64(root, "InitialPrivateBytes");
        RequireNonNegativeInt64(root, "FinalPrivateBytes");
        RequireNonNegativeInt32(root, "InitialHandleCount");
        RequireNonNegativeInt32(root, "FinalHandleCount");
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
            RequireBoolean(root, "RuntimePackageGraphRestored"),
            true,
            "RuntimePackageGraphRestored");
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
            version < new Version(2, 3, 1, 0))
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
