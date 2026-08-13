using System.Diagnostics;
using System.Text.Json;
using Microsoft.Windows.Storage;

namespace IptvSuite.PackageLifecycleHarness;

internal sealed class HarnessFileStore
{
    private const int MaxResultBytes = 2048;
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(30);
    private readonly string _runDirectory;

    private HarnessFileStore(string protectedStoreDirectory, string runDirectory)
    {
        ProtectedStoreDirectory = protectedStoreDirectory;
        _runDirectory = runDirectory;
    }

    internal string ProtectedStoreDirectory { get; }

    internal string TicketPath => Path.Combine(_runDirectory, "control-ticket.dpapi");

    internal static HarnessFileStore Open(HarnessInvocation invocation)
    {
        string localCachePath = Path.GetFullPath(ApplicationData.GetDefault().LocalCachePath);
        EnsureExistingDirectory(localCachePath);

        string protectedStoreRoot = Path.Combine(localCachePath, "ProtectedStore");
        string protectedStoreDirectory = Path.Combine(protectedStoreRoot, "v2");
        string harnessRoot = Path.Combine(localCachePath, "LifecycleHarness");
        string versionRoot = Path.Combine(harnessRoot, "v1");
        string runsRoot = Path.Combine(versionRoot, "runs");
        string runDirectory = Path.Combine(runsRoot, invocation.RunDirectoryName);

        if (invocation.Phase is HarnessPhase.Create)
        {
            EnsureDirectory(protectedStoreRoot);
            EnsureDirectory(protectedStoreDirectory);
            EnsureDirectory(harnessRoot);
            EnsureDirectory(versionRoot);
            EnsureDirectory(runsRoot);
            EnsureDirectory(runDirectory);
        }
        else
        {
            EnsureExistingDirectory(protectedStoreRoot);
            EnsureExistingDirectory(protectedStoreDirectory);
            EnsureExistingDirectory(harnessRoot);
            EnsureExistingDirectory(versionRoot);
            EnsureExistingDirectory(runsRoot);
            EnsureExistingDirectory(runDirectory);
        }

        return new HarnessFileStore(protectedStoreDirectory, runDirectory);
    }

    internal bool TicketExists()
    {
        if (!File.Exists(TicketPath))
        {
            return false;
        }

        EnsureRegularFile(TicketPath);
        return true;
    }

    internal byte[] ReadTicket(int maximumBytes) => ReadBoundedFile(TicketPath, maximumBytes);

    internal void WriteTicket(ReadOnlySpan<byte> protectedTicket, bool replaceExisting) =>
        AtomicWrite(TicketPath, protectedTicket, replaceExisting);

    internal void DeleteTicket()
    {
        EnsureRegularFile(TicketPath);
        File.Delete(TicketPath);

        if (File.Exists(TicketPath))
        {
            throw new IOException("The control ticket could not be removed.");
        }
    }

    internal void WriteResult(HarnessPhaseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(result);

        try
        {
            if (serialized.Length is <= 0 or > MaxResultBytes)
            {
                throw new InvalidDataException("The sanitized result is outside its size bound.");
            }

            AtomicWrite(GetResultPath(result.Phase), serialized, replaceExisting: true);
        }
        finally
        {
            Array.Clear(serialized);
        }
    }

    internal async Task<bool> WaitForReleaseAsync(HarnessPhase phase)
    {
        string releasePath = GetReleasePath(phase);
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < ReleaseTimeout)
        {
            if (File.Exists(releasePath))
            {
                EnsureRegularFile(releasePath);

                if (new FileInfo(releasePath).Length != 0)
                {
                    throw new InvalidDataException("The release marker must be empty.");
                }

                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        return false;
    }

    private string GetResultPath(HarnessPhase phase) => Path.Combine(
        _runDirectory,
        phase is HarnessPhase.Create ? "result-create.json" : "result-verify-delete.json");

    private string GetReleasePath(HarnessPhase phase) => Path.Combine(
        _runDirectory,
        phase is HarnessPhase.Create ? "release-create.ready" : "release-verify-delete.ready");

    private static void AtomicWrite(string targetPath, ReadOnlySpan<byte> value, bool replaceExisting)
    {
        string? directory = Path.GetDirectoryName(targetPath);

        if (directory is null)
        {
            throw new IOException("A contained target directory is required.");
        }

        EnsureExistingDirectory(directory);

        if (File.Exists(targetPath))
        {
            EnsureRegularFile(targetPath);

            if (!replaceExisting)
            {
                throw new IOException("The target already exists.");
            }
        }
        else if (!replaceExisting && Directory.Exists(targetPath))
        {
            throw new IOException("The target is not a regular file.");
        }

        string temporaryPath = targetPath + ".next";

        if (File.Exists(temporaryPath) || Directory.Exists(temporaryPath))
        {
            throw new IOException("A pending atomic write already exists.");
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

            EnsureExistingDirectory(directory);
            File.Move(temporaryPath, targetPath, replaceExisting);
            temporaryCreated = false;
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

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
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

        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("The bounded file length is invalid.");
        }

        byte[] value = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        int offset = 0;

        try
        {
            while (offset < value.Length)
            {
                int read = stream.Read(value, offset, value.Length - offset);

                if (read == 0)
                {
                    throw new EndOfStreamException("The bounded file was truncated.");
                }

                offset += read;
            }

            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException("The bounded file grew while it was read.");
            }

            return value;
        }
        catch
        {
            Array.Clear(value);
            throw;
        }
    }

    private static void EnsureDirectory(string path)
    {
        if (File.Exists(path))
        {
            throw new IOException("A required directory path is occupied by a file.");
        }

        Directory.CreateDirectory(path);
        EnsureExistingDirectory(path);
    }

    private static void EnsureExistingDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("A required directory is unavailable.");
        }

        FileAttributes attributes = File.GetAttributes(path);

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("A required directory is unsafe.");
        }
    }

    private static void EnsureRegularFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("A required file is unsafe.");
        }
    }
}
