using System.Diagnostics;
using System.Security.Cryptography;

namespace IptvSuite.DpapiUserBoundaryHarness;

internal sealed class BoundaryWorkspace
{
    internal const string TicketFileName = "boundary-ticket.bin";
    internal const string RawFileName = "primary-raw.dpapi";
    internal const string ResultFileName = "probe-result.bin";
    internal const string ReleaseFileName = "release.signal";
    private const int MaximumEntriesPerDirectory = 8;
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReleasePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly string[] RootDirectoryNames =
        ["input", "primary-store", "result", "secondary-store"];

    private BoundaryWorkspace(string rootPath, Guid runId)
    {
        RootPath = rootPath;
        RunId = runId;
        InputPath = GetContainedPath(rootPath, "input");
        PrimaryStorePath = GetContainedPath(rootPath, "primary-store");
        SecondaryStorePath = GetContainedPath(rootPath, "secondary-store");
        ResultPath = GetContainedPath(rootPath, "result");
    }

    internal string RootPath { get; }

    internal Guid RunId { get; }

    internal string InputPath { get; }

    internal string PrimaryStorePath { get; }

    internal string SecondaryStorePath { get; }

    internal string ResultPath { get; }

    internal string TicketPath => GetContainedPath(InputPath, TicketFileName);

    internal string RawPath => GetContainedPath(InputPath, RawFileName);

    internal string ProbeResultPath => GetContainedPath(ResultPath, ResultFileName);

    internal string ReleasePath => GetContainedPath(ResultPath, ReleaseFileName);

    internal static BoundaryWorkspace OpenForPrepare(string rootPath)
    {
        BoundaryWorkspace workspace = OpenRoot(rootPath);
        EnsureExactEntries(workspace.RootPath, []);

        foreach (string directoryName in RootDirectoryNames)
        {
            string path = GetContainedPath(workspace.RootPath, directoryName);
            Directory.CreateDirectory(path);
            EnsureExistingDirectory(path);
        }

        workspace.ValidateFixedDirectories();
        EnsureExactEntries(workspace.InputPath, []);
        EnsureExactEntries(workspace.PrimaryStorePath, []);
        EnsureExactEntries(workspace.SecondaryStorePath, []);
        EnsureExactEntries(workspace.ResultPath, []);
        return workspace;
    }

    internal static BoundaryWorkspace OpenExisting(string rootPath)
    {
        BoundaryWorkspace workspace = OpenRoot(rootPath);
        workspace.ValidateFixedDirectories();
        return workspace;
    }

    internal void ValidateBeforeProbe(string recordFileName)
    {
        ValidateFixedDirectories();
        EnsureExactRegularFiles(InputPath, TicketFileName, RawFileName);
        EnsureExactRegularFiles(PrimaryStorePath, recordFileName);
        EnsureExactEntries(SecondaryStorePath, []);
        EnsureExactEntries(ResultPath, []);
    }

    internal void ValidateBeforeVerify(string recordFileName)
    {
        ValidateFixedDirectories();
        EnsureExactRegularFiles(InputPath, TicketFileName, RawFileName);
        EnsureExactRegularFiles(PrimaryStorePath, recordFileName);
        EnsureExactEntries(SecondaryStorePath, []);
        EnsureExactRegularFiles(ResultPath, ResultFileName);
    }

    internal void ValidateAfterVerify()
    {
        ValidateFixedDirectories();
        EnsureExactEntries(InputPath, []);
        EnsureExactEntries(PrimaryStorePath, []);
        EnsureExactEntries(SecondaryStorePath, []);
        EnsureExactRegularFiles(ResultPath, ResultFileName, ReleaseFileName);
    }

    internal string GetPrimaryRecordPath(string recordFileName)
    {
        if (!BoundaryTicket.IsValidRecordFileName(recordFileName))
        {
            throw new InvalidDataException("The protected record file name is invalid.");
        }

        return GetContainedPath(PrimaryStorePath, recordFileName);
    }

    internal string GetSinglePrimaryRecordPath()
    {
        string[] entries = EnumerateEntries(PrimaryStorePath);

        if (entries.Length != 1)
        {
            throw new HarnessFailureException(HarnessExitCode.VerificationFailed);
        }

        string path = entries[0];
        EnsureRegularFile(path);

        if (!BoundaryTicket.IsValidRecordFileName(Path.GetFileName(path)))
        {
            throw new HarnessFailureException(HarnessExitCode.VerificationFailed);
        }

        return path;
    }

    internal byte[] ReadBoundedFile(string path, int maximumBytes, int? exactLength = null)
    {
        EnsureKnownContainedFile(path);
        EnsureRegularFile(path);
        var options = new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan,
            BufferSize = 4096,
        };

        using var stream = new FileStream(path, options);
        long length = stream.Length;

        if (length is <= 0 or > int.MaxValue || length > maximumBytes ||
            (exactLength is not null && length != exactLength.Value))
        {
            throw new InvalidDataException("A bounded harness file has an invalid length.");
        }

        byte[] value = GC.AllocateUninitializedArray<byte>((int)length);

        try
        {
            stream.ReadExactly(value);

            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException("A bounded harness file grew while it was read.");
            }

            return value;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(value);
            throw;
        }
    }

    internal byte[] ComputeFileDigest(string path, int exactLength)
    {
        byte[] value = ReadBoundedFile(path, BoundaryTicket.MaximumProtectedFileBytes, exactLength);

        try
        {
            return SHA256.HashData(value);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    internal void WriteFile(string path, ReadOnlySpan<byte> value, int maximumBytes)
    {
        if (value.Length is <= 0 || value.Length > maximumBytes)
        {
            throw new InvalidDataException("A bounded harness write has an invalid length.");
        }

        EnsureKnownContainedFile(path);
        string? directory = Path.GetDirectoryName(path);

        if (directory is null)
        {
            throw new InvalidDataException("A harness file has no containing directory.");
        }

        EnsureExistingDirectory(directory);

        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("A harness output already exists.");
        }

        string temporaryPath = path + ".next";

        if (File.Exists(temporaryPath) || Directory.Exists(temporaryPath))
        {
            throw new IOException("A pending harness output already exists.");
        }

        bool temporaryCreated = false;

        try
        {
            var options = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
                BufferSize = 4096,
            };

            using (var stream = new FileStream(temporaryPath, options))
            {
                temporaryCreated = true;
                stream.Write(value);
                stream.Flush(flushToDisk: true);
            }

            EnsureRegularFile(temporaryPath);
            EnsureExistingDirectory(directory);
            File.Move(temporaryPath, path);
            temporaryCreated = false;
            EnsureRegularFile(path);
        }
        finally
        {
            if (temporaryCreated && File.Exists(temporaryPath))
            {
                EnsureRegularFile(temporaryPath);
                File.Delete(temporaryPath);
            }
        }
    }

    internal void DeleteFile(string path)
    {
        EnsureKnownContainedFile(path);
        EnsureRegularFile(path);
        File.Delete(path);

        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("A harness file could not be removed.");
        }
    }

    internal async Task WaitForReleaseAsync(ReadOnlyMemory<byte> expectedTicketDigest)
    {
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < ReleaseTimeout)
        {
            if (File.Exists(ReleasePath))
            {
                byte[] encoded = ReadBoundedFile(
                    ReleasePath,
                    BoundaryRelease.EncodedLength,
                    BoundaryRelease.EncodedLength);

                try
                {
                    if (!BoundaryRelease.IsValid(encoded, RunId, expectedTicketDigest.Span))
                    {
                        throw new InvalidDataException("The release binding is invalid.");
                    }

                    return;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encoded);
                }
            }

            await Task.Delay(ReleasePollInterval).ConfigureAwait(false);
        }

        throw new HarnessFailureException(HarnessExitCode.ReleaseBarrierTimedOut);
    }

    internal void EnsureDirectoryEmpty(string path)
    {
        if (!string.Equals(path, InputPath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, PrimaryStorePath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, SecondaryStorePath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, ResultPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }

        EnsureExactEntries(path, []);
    }

    private static BoundaryWorkspace OpenRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathFullyQualified(rootPath))
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }

        string supplied = Path.TrimEndingDirectorySeparator(rootPath);
        string fullPath;

        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }

        if (!string.Equals(supplied, fullPath, StringComparison.OrdinalIgnoreCase) ||
            fullPath.Length > 240 ||
            !TryParseRunId(Path.GetFileName(fullPath), out Guid runId))
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }

        string? volumeRoot = Path.GetPathRoot(fullPath);

        if (volumeRoot is null || string.Equals(
                Path.TrimEndingDirectorySeparator(volumeRoot),
                fullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }

        EnsureNoExistingReparsePoint(fullPath);
        EnsureExistingDirectory(fullPath);
        return new BoundaryWorkspace(fullPath, runId);
    }

    private void ValidateFixedDirectories()
    {
        EnsureNoExistingReparsePoint(RootPath);
        EnsureExistingDirectory(RootPath);
        EnsureExactDirectoryNames(RootPath, RootDirectoryNames);
        EnsureExistingDirectory(InputPath);
        EnsureExistingDirectory(PrimaryStorePath);
        EnsureExistingDirectory(SecondaryStorePath);
        EnsureExistingDirectory(ResultPath);
    }

    private void EnsureKnownContainedFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        bool inKnownDirectory = directory is not null &&
            (string.Equals(directory, InputPath, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(directory, PrimaryStorePath, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(directory, SecondaryStorePath, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(directory, ResultPath, StringComparison.OrdinalIgnoreCase));

        if (!inKnownDirectory || !string.Equals(fullPath, path, StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }

        EnsureNoExistingReparsePoint(directory!);
    }

    private static void EnsureExactRegularFiles(string directory, params string[] fileNames)
    {
        string[] entries = EnumerateEntries(directory);

        if (entries.Length != fileNames.Length)
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }

        var expected = new HashSet<string>(fileNames, StringComparer.Ordinal);

        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);

            if (!expected.Remove(name))
            {
                throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
            }

            EnsureRegularFile(entry);
        }

        if (expected.Count != 0)
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }
    }

    private static void EnsureExactDirectoryNames(string directory, string[] directoryNames)
    {
        string[] entries = EnumerateEntries(directory);

        if (entries.Length != directoryNames.Length)
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }

        var expected = new HashSet<string>(directoryNames, StringComparer.Ordinal);

        foreach (string entry in entries)
        {
            if (!expected.Remove(Path.GetFileName(entry)))
            {
                throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
            }

            EnsureExistingDirectory(entry);
        }

        if (expected.Count != 0)
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }
    }

    private static void EnsureNoExistingReparsePoint(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
        {
            if (!current.Exists)
            {
                continue;
            }

            FileAttributes attributes = File.GetAttributes(current.FullName);

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
            }
        }
    }

    private static string GetContainedPath(string directory, string leafName)
    {
        if (!string.Equals(Path.GetFileName(leafName), leafName, StringComparison.Ordinal) ||
            leafName is "." or "..")
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }

        string fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string candidate = Path.GetFullPath(Path.Combine(fullDirectory, leafName));
        string prefix = fullDirectory + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }

        return candidate;
    }

    private static void EnsureExistingDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("A harness directory is unavailable.");
        }

        FileAttributes attributes = File.GetAttributes(path);

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }
    }

    private static void EnsureRegularFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }
    }

    private static string[] EnumerateEntries(string directory)
    {
        EnsureExistingDirectory(directory);
        List<string> entries = new(MaximumEntriesPerDirectory);

        foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            if (entries.Count == MaximumEntriesPerDirectory)
            {
                throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
            }

            entries.Add(entry);
        }

        return [.. entries];
    }

    private static void EnsureExactEntries(string directory, string[] expectedNames)
    {
        string[] entries = EnumerateEntries(directory);

        if (entries.Length != expectedNames.Length)
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }

        var expected = new HashSet<string>(expectedNames, StringComparer.Ordinal);

        foreach (string entry in entries)
        {
            if (!expected.Remove(Path.GetFileName(entry)))
            {
                throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
            }
        }

        if (expected.Count != 0)
        {
            throw new HarnessFailureException(HarnessExitCode.WorkspaceRejected);
        }
    }

    private static bool TryParseRunId(string value, out Guid runId)
    {
        runId = Guid.Empty;

        if (value.Length != 32 || !Guid.TryParseExact(value, "N", out runId) || runId == Guid.Empty)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                runId = Guid.Empty;
                return false;
            }
        }

        return true;
    }
}
