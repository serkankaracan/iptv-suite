namespace IptvSuite.SecretStoreSpike;

internal sealed class SafeSpikeWorkspace
{
    private const string ArtifactsDirectoryName = ".artifacts";
    private const string SpikeDirectoryName = "m4-secret-store-spike";
    private const string WorkDirectoryName = "work";
    private const string EvidenceDirectoryName = "evidence";
    private const string RunLockFileName = "run.lock";

    private readonly string _repositoryRoot;
    private readonly string _artifactsRoot;
    private readonly string _spikeRoot;

    private SafeSpikeWorkspace(string repositoryRoot)
    {
        _repositoryRoot = repositoryRoot;
        _artifactsRoot = GetExactChild(_repositoryRoot, ArtifactsDirectoryName);
        _spikeRoot = GetExactChild(_artifactsRoot, SpikeDirectoryName);
        WorkRoot = GetExactChild(_spikeRoot, WorkDirectoryName);
        EvidenceRoot = GetExactChild(_spikeRoot, EvidenceDirectoryName);
        SpecificationPath = GetExactChild(
            _repositoryRoot,
            Path.Combine("apps", "windows", "testdata", "m4", "secret-store-spike-spec.json"));
        LicensePath = GetExactChild(
            _repositoryRoot,
            Path.Combine(
                "apps",
                "windows",
                "testdata",
                "LICENSES",
                "LicenseRef-IPTVSuite-Synthetic-Test-Only.txt"));
        GlobalJsonPath = GetExactChild(_repositoryRoot, "global.json");
    }

    internal string RepositoryRoot => _repositoryRoot;

    internal string WorkRoot { get; }

    internal string EvidenceRoot { get; }

    internal string SpecificationPath { get; }

    internal string LicensePath { get; }

    internal string GlobalJsonPath { get; }

    internal static SafeSpikeWorkspace OpenFromCurrentDirectory()
    {
        string repositoryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Environment.CurrentDirectory));
        var workspace = new SafeSpikeWorkspace(repositoryRoot);
        workspace.AssertRepositoryRoot();
        workspace.AssertSafeAncestors();
        return workspace;
    }

    internal FileStream AcquireExclusiveRunLock()
    {
        EnsureRegularDirectory(_artifactsRoot);
        EnsureRegularDirectory(_spikeRoot);
        string lockPath = GetExactChild(_spikeRoot, RunLockFileName);
        EnsureNotReparsePointIfPresent(lockPath);

        FileStream stream = new(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);

        try
        {
            if ((File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The spike run lock is a reparse point.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal void Prepare()
    {
        if (Directory.Exists(WorkRoot))
        {
            DeleteOwnedWorkTree(WorkRoot);
        }

        EnsureRegularDirectory(_artifactsRoot);
        EnsureRegularDirectory(_spikeRoot);
        EnsureRegularDirectory(WorkRoot);
        EnsureRegularDirectory(EvidenceRoot);
    }

    internal string CreateStoreDirectory(int recordCount, int iteration)
    {
        string directoryName = $"scale-{recordCount}-iteration-{iteration}";
        string path = GetExactChild(WorkRoot, directoryName);
        EnsureRegularDirectory(path);
        return path;
    }

    internal string CreateCancellationStoreDirectory(int sample)
    {
        string directoryName = $"cancellation-sample-{sample}";
        string path = GetExactChild(WorkRoot, directoryName);
        EnsureRegularDirectory(path);
        return path;
    }

    internal string CreateWarmupStoreDirectory()
    {
        string path = GetExactChild(WorkRoot, "warmup");
        EnsureRegularDirectory(path);
        return path;
    }

    internal void DeleteStoreDirectory(string storeDirectory)
    {
        string expectedParent = Path.GetFullPath(WorkRoot);
        string candidate = Path.GetFullPath(storeDirectory);
        string? parent = Directory.GetParent(candidate)?.FullName;
        if (!string.Equals(parent, expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Refusing to clean an unexpected spike store directory.");
        }

        DeleteOwnedWorkTree(candidate);
    }

    internal void Complete()
    {
        if (Directory.Exists(WorkRoot))
        {
            DeleteOwnedWorkTree(WorkRoot);
        }
    }

    private void AssertRepositoryRoot()
    {
        string applicationProjectPath = GetExactChild(
            _repositoryRoot,
            Path.Combine("apps", "windows", "src", "IptvSuite.Application", "IptvSuite.Application.csproj"));

        if (!File.Exists(GlobalJsonPath) ||
            !File.Exists(applicationProjectPath) ||
            !File.Exists(SpecificationPath) ||
            !File.Exists(LicensePath))
        {
            throw new InvalidOperationException("The current directory is not the repository root.");
        }
    }

    private void AssertSafeAncestors()
    {
        EnsureNotReparsePointIfPresent(_repositoryRoot);
        EnsureNotReparsePointIfPresent(_artifactsRoot);
        EnsureNotReparsePointIfPresent(_spikeRoot);
        EnsureNotReparsePointIfPresent(WorkRoot);
        EnsureNotReparsePointIfPresent(EvidenceRoot);
    }

    private static void EnsureRegularDirectory(string path)
    {
        EnsureNotReparsePointIfPresent(path);
        Directory.CreateDirectory(path);
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The spike workspace contains an unsafe directory.");
        }
    }

    private static void EnsureNotReparsePointIfPresent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The spike workspace contains a reparse point.");
            }
        }
    }

    private void DeleteOwnedWorkTree(string path)
    {
        string candidate = Path.GetFullPath(path);
        string workRoot = Path.GetFullPath(WorkRoot);
        string? parent = Directory.GetParent(candidate)?.FullName;
        bool isWorkRoot = string.Equals(candidate, workRoot, StringComparison.OrdinalIgnoreCase);
        bool isDirectOwnedChild = string.Equals(parent, workRoot, StringComparison.OrdinalIgnoreCase);
        if (!isWorkRoot && !isDirectOwnedChild)
        {
            throw new IOException("Refusing to clean a path outside the fixed spike work root.");
        }

        if (!Directory.Exists(candidate))
        {
            return;
        }

        var pending = new Queue<DirectoryInfo>();
        var root = new DirectoryInfo(candidate);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to clean a reparse-point spike directory.");
        }

        pending.Enqueue(root);
        while (pending.TryDequeue(out DirectoryInfo? directory))
        {
            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Refusing to clean a spike tree containing a reparse point.");
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Enqueue(child);
                }
            }
        }

        Directory.Delete(candidate, recursive: true);
    }

    private static string GetExactChild(string parent, string relativePath)
    {
        string fullParent = Path.GetFullPath(parent);
        string candidate = Path.GetFullPath(Path.Combine(fullParent, relativePath));
        string requiredPrefix = Path.EndsInDirectorySeparator(fullParent)
            ? fullParent
            : fullParent + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A spike workspace path escaped its expected root.");
        }

        return candidate;
    }
}
