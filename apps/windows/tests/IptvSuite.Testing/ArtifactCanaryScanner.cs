using System.Security.Cryptography;
using System.Text;

namespace IptvSuite.Testing;

public static class ArtifactCanaryScanner
{
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
}

public sealed record CanaryFinding(string RelativePath, TestCanaryEncoding Encoding, long ByteOffset);
