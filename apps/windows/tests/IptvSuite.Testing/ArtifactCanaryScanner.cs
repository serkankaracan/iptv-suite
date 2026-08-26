using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("IptvSuite.UnitTests")]

namespace IptvSuite.Testing;

public static class ArtifactCanaryScanner
{
    private static readonly ArtifactCanaryScanLimits M16ReleaseCandidateLimits = new(
        MaximumDirectoryDepth: 32,
        MaximumEntryCount: 25_000,
        MaximumSingleFileBytes: 4_294_967_296,
        MaximumTotalFileBytes: 8_589_934_592,
        MaximumFindingCount: 256,
        MaximumRelativePathLength: 4_096);

    public static IReadOnlyList<CanaryFinding> Scan(string rootPath, TestCanary canary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(canary);

        string root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Artifact root does not exist.");
        }

        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Artifact scan refuses a reparse-point root.");
        }

        IReadOnlyList<(TestCanaryEncoding Encoding, byte[] Bytes)> patterns = canary.GetSearchPatterns();
        List<CanaryFinding> findings = [];

        foreach (string path in EnumerateFilesFailClosed(root, findings))
        {
            foreach ((TestCanaryEncoding encoding, byte[] pattern) in patterns)
            {
                try
                {
                    using FileStream stream = File.OpenRead(path);
                    long offset = FindOffset(stream, pattern);
                    if (offset >= 0)
                    {
                        findings.Add(new CanaryFinding(GetSafeRelativePath(root, path), encoding, offset));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"Artifact scan failed for {GetSafeRelativePath(root, path)}.",
                        exception);
                }
            }
        }

        return findings
            .OrderBy(finding => finding.RelativePath, StringComparer.Ordinal)
            .ThenBy(finding => finding.Encoding)
            .ThenBy(finding => finding.ByteOffset)
            .ToArray();
    }

    public static IReadOnlyList<CanaryFinding> Scan(
        string rootPath,
        TestCanary canary,
        ArtifactCanaryScanProfile profile)
    {
        if (profile != ArtifactCanaryScanProfile.M16ReleaseCandidate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile,
                "The artifact canary scan profile is unsupported.");
        }

        return ScanBounded(rootPath, canary, M16ReleaseCandidateLimits);
    }

    internal static IReadOnlyList<CanaryFinding> ScanBounded(
        string rootPath,
        TestCanary canary,
        ArtifactCanaryScanLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(canary);
        limits.Validate();

        string root;
        try
        {
            root = Path.GetFullPath(rootPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or
                UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new ArgumentException(
                "M16ReleaseCandidateArtifactScan:RootInvalid",
                nameof(rootPath),
                exception);
        }

        IReadOnlyList<(TestCanaryEncoding Encoding, byte[] Bytes)> patterns =
            canary.GetSearchPatterns();
        List<CanaryFinding> findings = [];
        List<BoundedArtifactEntry> initialInventory = EnumerateEntriesBounded(
            root,
            limits,
            findings,
            collectPathFindings: true);
        List<BoundedArtifactDigest> initialDigests = ScanAndHashInventory(
            initialInventory,
            patterns,
            findings,
            limits,
            scanForCanaries: true);

        List<BoundedArtifactEntry> verificationInventory = EnumerateEntriesBounded(
            root,
            limits,
            findings: null,
            collectPathFindings: false);
        AssertInventoryEquivalent(initialInventory, verificationInventory);
        List<BoundedArtifactDigest> verificationDigests = ScanAndHashInventory(
            verificationInventory,
            patterns,
            findings: null,
            limits,
            scanForCanaries: false);
        AssertDigestEquivalent(initialDigests, verificationDigests);

        List<BoundedArtifactEntry> finalInventory = EnumerateEntriesBounded(
            root,
            limits,
            findings: null,
            collectPathFindings: false);
        AssertInventoryEquivalent(verificationInventory, finalInventory);

        return findings
            .OrderBy(finding => finding.RelativePath, StringComparer.Ordinal)
            .ThenBy(finding => finding.Encoding)
            .ThenBy(finding => finding.ByteOffset)
            .ToArray();
    }

    private static List<BoundedArtifactEntry> EnumerateEntriesBounded(
        string root,
        ArtifactCanaryScanLimits limits,
        List<CanaryFinding>? findings,
        bool collectPathFindings)
    {
        AssertBoundedRoot(root);
        Queue<(string Path, int Depth)> pending = new();
        pending.Enqueue((root, 0));
        List<BoundedArtifactEntry> inventory = [];
        int entryCount = 0;
        long totalFileBytes = 0;

        while (pending.TryDequeue(out (string Path, int Depth) directory))
        {
            List<string> entries = [];
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    entryCount++;
                    if (entryCount > limits.MaximumEntryCount)
                    {
                        throw new ArtifactCanaryScanLimitException(
                            "M16ReleaseCandidateArtifactScan:EntryLimitExceeded");
                    }

                    entries.Add(entry);
                }
            }
            catch (ArtifactCanaryScanLimitException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    System.Security.SecurityException)
            {
                throw new IOException(
                    "M16ReleaseCandidateArtifactScan:EnumerationFailed",
                    exception);
            }

            entries.Sort(StringComparer.Ordinal);
            foreach (string entry in entries)
            {
                string relativePath;
                FileAttributes attributes;
                try
                {
                    relativePath = Path.GetRelativePath(root, entry);
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or IOException or NotSupportedException or
                        UnauthorizedAccessException or System.Security.SecurityException)
                {
                    throw new IOException(
                        "M16ReleaseCandidateArtifactScan:EntryInspectionFailed",
                        exception);
                }

                if (string.IsNullOrWhiteSpace(relativePath) ||
                    Path.IsPathRooted(relativePath) ||
                    relativePath.Equals("..", StringComparison.Ordinal) ||
                    relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    throw new IOException(
                        "M16ReleaseCandidateArtifactScan:EntryInspectionFailed");
                }

                if (relativePath.Length > limits.MaximumRelativePathLength)
                {
                    throw new ArtifactCanaryScanLimitException(
                        "M16ReleaseCandidateArtifactScan:PathLimitExceeded");
                }

                int pathMarkerOffset = relativePath.IndexOf(
                    TestCanary.Marker,
                    StringComparison.Ordinal);
                if (collectPathFindings && pathMarkerOffset >= 0)
                {
                    AddBoundedFinding(
                        findings!,
                        new CanaryFinding(
                            FingerprintRelativePath(relativePath),
                            TestCanaryEncoding.Path,
                            pathMarkerOffset),
                        limits);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "M16ReleaseCandidateArtifactScan:ReparsePointRefused");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    int childDepth = directory.Depth + 1;
                    if (childDepth > limits.MaximumDirectoryDepth)
                    {
                        throw new ArtifactCanaryScanLimitException(
                            "M16ReleaseCandidateArtifactScan:DepthLimitExceeded");
                    }

                    inventory.Add(new BoundedArtifactEntry(
                        entry,
                        relativePath,
                        IsDirectory: true,
                        Length: 0));
                    pending.Enqueue((entry, childDepth));
                    continue;
                }

                long fileLength;
                try
                {
                    fileLength = new FileInfo(entry).Length;
                }
                catch (Exception exception) when (
                    exception is ArgumentException or IOException or NotSupportedException or
                        UnauthorizedAccessException or System.Security.SecurityException)
                {
                    throw new IOException(
                        "M16ReleaseCandidateArtifactScan:EntryInspectionFailed",
                        exception);
                }

                if (fileLength > limits.MaximumSingleFileBytes)
                {
                    throw new ArtifactCanaryScanLimitException(
                        "M16ReleaseCandidateArtifactScan:FileSizeLimitExceeded");
                }

                if (fileLength > limits.MaximumTotalFileBytes - totalFileBytes)
                {
                    throw new ArtifactCanaryScanLimitException(
                        "M16ReleaseCandidateArtifactScan:TotalSizeLimitExceeded");
                }

                totalFileBytes += fileLength;
                inventory.Add(new BoundedArtifactEntry(
                    entry,
                    relativePath,
                    IsDirectory: false,
                    Length: fileLength));
            }
        }

        inventory.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return inventory;
    }

    private static void AssertBoundedRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                "M16ReleaseCandidateArtifactScan:RootMissing");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(root);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            throw new IOException(
                "M16ReleaseCandidateArtifactScan:RootInspectionFailed",
                exception);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
            FileAttributes.Directory)
        {
            throw new IOException(
                "M16ReleaseCandidateArtifactScan:ReparsePointRefused");
        }
    }

    private static List<BoundedArtifactDigest> ScanAndHashInventory(
        List<BoundedArtifactEntry> inventory,
        IReadOnlyList<(TestCanaryEncoding Encoding, byte[] Bytes)> patterns,
        List<CanaryFinding>? findings,
        ArtifactCanaryScanLimits limits,
        bool scanForCanaries)
    {
        List<BoundedArtifactDigest> digests = [];
        foreach (BoundedArtifactEntry entry in inventory)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            List<BoundedPatternMatcher> matchers = scanForCanaries
                ? patterns.Select(static pattern => new BoundedPatternMatcher(pattern)).ToList()
                : [];
            byte[] digest = ScanAndHashFile(entry, matchers);
            digests.Add(new BoundedArtifactDigest(entry.RelativePath, entry.Length, digest));

            foreach (BoundedPatternMatcher matcher in matchers)
            {
                if (matcher.FirstOffset >= 0)
                {
                    AddBoundedFinding(
                        findings!,
                        new CanaryFinding(
                            FingerprintRelativePath(entry.RelativePath),
                            matcher.Encoding,
                            matcher.FirstOffset),
                        limits);
                }
            }
        }

        return digests;
    }

    private static byte[] ScanAndHashFile(
        BoundedArtifactEntry entry,
        List<BoundedPatternMatcher> matchers)
    {
        try
        {
            var current = new FileInfo(entry.FullPath);
            current.Refresh();
            if (!current.Exists ||
                (current.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                current.Length != entry.Length)
            {
                throw new ArtifactCanaryScanInvariantException(
                    "M16ReleaseCandidateArtifactScan:InventoryChanged");
            }

            using FileStream stream = new(
                entry.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                FileOptions.SequentialScan);
            if (stream.Length != entry.Length)
            {
                throw new ArtifactCanaryScanInvariantException(
                    "M16ReleaseCandidateArtifactScan:InventoryChanged");
            }

            using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[8192];
            long consumed = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (read > entry.Length - consumed)
                {
                    throw new ArtifactCanaryScanInvariantException(
                        "M16ReleaseCandidateArtifactScan:InventoryChanged");
                }

                hasher.AppendData(buffer, 0, read);
                foreach (BoundedPatternMatcher matcher in matchers)
                {
                    matcher.Consume(buffer, read, consumed);
                }

                consumed += read;
            }

            if (consumed != entry.Length || stream.Length != entry.Length)
            {
                throw new ArtifactCanaryScanInvariantException(
                    "M16ReleaseCandidateArtifactScan:InventoryChanged");
            }

            return hasher.GetHashAndReset();
        }
        catch (ArtifactCanaryScanInvariantException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            throw new IOException(
                "M16ReleaseCandidateArtifactScan:FileReadFailed",
                exception);
        }
    }

    private static void AssertInventoryEquivalent(
        List<BoundedArtifactEntry> expected,
        List<BoundedArtifactEntry> actual)
    {
        if (expected.Count != actual.Count)
        {
            throw new ArtifactCanaryScanInvariantException(
                "M16ReleaseCandidateArtifactScan:InventoryChanged");
        }

        for (int index = 0; index < expected.Count; index++)
        {
            BoundedArtifactEntry expectedEntry = expected[index];
            BoundedArtifactEntry actualEntry = actual[index];
            if (!expectedEntry.RelativePath.Equals(
                    actualEntry.RelativePath,
                    StringComparison.Ordinal) ||
                expectedEntry.IsDirectory != actualEntry.IsDirectory ||
                expectedEntry.Length != actualEntry.Length)
            {
                throw new ArtifactCanaryScanInvariantException(
                    "M16ReleaseCandidateArtifactScan:InventoryChanged");
            }
        }
    }

    private static void AssertDigestEquivalent(
        List<BoundedArtifactDigest> expected,
        List<BoundedArtifactDigest> actual)
    {
        if (expected.Count != actual.Count)
        {
            throw new ArtifactCanaryScanInvariantException(
                "M16ReleaseCandidateArtifactScan:ContentChanged");
        }

        for (int index = 0; index < expected.Count; index++)
        {
            BoundedArtifactDigest expectedDigest = expected[index];
            BoundedArtifactDigest actualDigest = actual[index];
            if (!expectedDigest.RelativePath.Equals(
                    actualDigest.RelativePath,
                    StringComparison.Ordinal) ||
                expectedDigest.Length != actualDigest.Length ||
                !CryptographicOperations.FixedTimeEquals(
                    expectedDigest.Sha256,
                    actualDigest.Sha256))
            {
                throw new ArtifactCanaryScanInvariantException(
                    "M16ReleaseCandidateArtifactScan:ContentChanged");
            }
        }
    }

    private static string FingerprintRelativePath(string relativePath)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(relativePath);
        try
        {
            string fingerprint = Convert.ToHexString(SHA256.HashData(pathBytes))[..16];
            return $"[REDACTED-ARTIFACT-PATH:{fingerprint}]";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pathBytes);
        }
    }

    private static void AddBoundedFinding(
        List<CanaryFinding> findings,
        CanaryFinding finding,
        ArtifactCanaryScanLimits limits)
    {
        if (findings.Count >= limits.MaximumFindingCount)
        {
            throw new ArtifactCanaryScanLimitException(
                "M16ReleaseCandidateArtifactScan:FindingLimitExceeded");
        }

        findings.Add(finding);
    }

    private static List<string> EnumerateFilesFailClosed(string root, List<CanaryFinding> findings)
    {
        Queue<string> pending = new([root]);
        List<string> files = [];

        while (pending.TryDequeue(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                string relativePath = Path.GetRelativePath(root, entry);
                int pathMarkerOffset = relativePath.IndexOf(TestCanary.Marker, StringComparison.Ordinal);
                if (pathMarkerOffset >= 0)
                {
                    findings.Add(new CanaryFinding(RedactCanaryPath(relativePath), TestCanaryEncoding.Path, pathMarkerOffset));
                }

                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Artifact scan refuses reparse points: {GetSafeRelativePath(root, entry)}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Enqueue(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        return files;
    }

    private static string GetSafeRelativePath(string root, string path)
    {
        string relativePath = Path.GetRelativePath(root, path);
        return relativePath.Contains(TestCanary.Marker, StringComparison.Ordinal)
            ? RedactCanaryPath(relativePath)
            : relativePath;
    }

    private static string RedactCanaryPath(string relativePath)
    {
        string fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)))[..12];
        return $"[REDACTED-CANARY-PATH:{fingerprint}]";
    }

    private static long FindOffset(Stream stream, byte[] pattern)
    {
        int[] prefixTable = BuildPrefixTable(pattern);
        byte[] buffer = new byte[8192];
        int matched = 0;
        long consumed = 0;
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int index = 0; index < read; index++)
            {
                byte current = buffer[index];

                while (matched > 0 && current != pattern[matched])
                {
                    matched = prefixTable[matched - 1];
                }

                if (current == pattern[matched])
                {
                    matched++;
                    if (matched == pattern.Length)
                    {
                        return consumed + index - pattern.Length + 1;
                    }
                }
            }

            consumed += read;
        }

        return -1;
    }

    private static int[] BuildPrefixTable(byte[] pattern)
    {
        int[] table = new int[pattern.Length];
        int prefixLength = 0;

        for (int index = 1; index < pattern.Length; index++)
        {
            while (prefixLength > 0 && pattern[index] != pattern[prefixLength])
            {
                prefixLength = table[prefixLength - 1];
            }

            if (pattern[index] == pattern[prefixLength])
            {
                prefixLength++;
                table[index] = prefixLength;
            }
        }

        return table;
    }

    private sealed record BoundedArtifactEntry(
        string FullPath,
        string RelativePath,
        bool IsDirectory,
        long Length);

    private sealed record BoundedArtifactDigest(
        string RelativePath,
        long Length,
        byte[] Sha256);

    private sealed class BoundedPatternMatcher
    {
        private readonly byte[] _pattern;
        private readonly int[] _prefixTable;
        private int _matched;

        public BoundedPatternMatcher((TestCanaryEncoding Encoding, byte[] Bytes) pattern)
        {
            Encoding = pattern.Encoding;
            _pattern = pattern.Bytes;
            _prefixTable = BuildPrefixTable(_pattern);
        }

        public TestCanaryEncoding Encoding { get; }

        public long FirstOffset { get; private set; } = -1;

        public void Consume(byte[] buffer, int count, long consumedBeforeBuffer)
        {
            if (FirstOffset >= 0)
            {
                return;
            }

            for (int index = 0; index < count; index++)
            {
                byte current = buffer[index];
                while (_matched > 0 && current != _pattern[_matched])
                {
                    _matched = _prefixTable[_matched - 1];
                }

                if (current == _pattern[_matched])
                {
                    _matched++;
                    if (_matched == _pattern.Length)
                    {
                        FirstOffset = consumedBeforeBuffer + index - _pattern.Length + 1;
                        return;
                    }
                }
            }
        }
    }
}

public enum ArtifactCanaryScanProfile
{
    M16ReleaseCandidate,
}

internal readonly record struct ArtifactCanaryScanLimits(
    int MaximumDirectoryDepth,
    int MaximumEntryCount,
    long MaximumSingleFileBytes,
    long MaximumTotalFileBytes,
    int MaximumFindingCount,
    int MaximumRelativePathLength)
{
    public void Validate()
    {
        if (MaximumDirectoryDepth < 0 ||
            MaximumEntryCount <= 0 ||
            MaximumSingleFileBytes < 0 ||
            MaximumTotalFileBytes < 0 ||
            MaximumFindingCount <= 0 ||
            MaximumRelativePathLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ArtifactCanaryScanLimits),
                "Artifact canary scan limits must be bounded and non-negative.");
        }
    }
}

internal sealed class ArtifactCanaryScanLimitException(string message) : IOException(message);

internal sealed class ArtifactCanaryScanInvariantException(string message) : IOException(message);

public sealed record CanaryFinding(string RelativePath, TestCanaryEncoding Encoding, long ByteOffset);
