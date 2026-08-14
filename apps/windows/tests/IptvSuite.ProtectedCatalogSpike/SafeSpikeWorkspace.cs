namespace IptvSuite.ProtectedCatalogSpike;

internal sealed class SafeSpikeWorkspace
{
    private readonly string _repositoryRoot;
    private readonly string _artifactsRoot;
    private readonly string _spikeRoot;

    private SafeSpikeWorkspace(string repositoryRoot)
    {
        _repositoryRoot = repositoryRoot;
        _artifactsRoot = GetContainedPath(repositoryRoot, ".artifacts");
        _spikeRoot = GetContainedPath(_artifactsRoot, "m4-protected-catalog-spike");
        WorkRoot = GetContainedPath(_spikeRoot, "work");
        EvidenceRoot = GetContainedPath(_spikeRoot, "evidence");
        SpecificationPath = GetContainedPath(
            repositoryRoot,
            Path.Combine("apps", "windows", "testdata", "m4", "protected-catalog-spike-spec.json"));
        LicensePath = GetContainedPath(
            repositoryRoot,
            Path.Combine(
                "apps",
                "windows",
                "testdata",
                "LICENSES",
                "LicenseRef-IPTVSuite-Synthetic-Test-Only.txt"));
        GlobalJsonPath = GetContainedPath(repositoryRoot, "global.json");
        PackageLockPath = GetContainedPath(
            repositoryRoot,
            Path.Combine(
                "apps",
                "windows",
                "tests",
                "IptvSuite.ProtectedCatalogSpike",
                "packages.lock.json"));
    }

    internal string RepositoryRoot => _repositoryRoot;

    internal string WorkRoot { get; }

    internal string EvidenceRoot { get; }

    internal string SpecificationPath { get; }

    internal string LicensePath { get; }

    internal string GlobalJsonPath { get; }

    internal string PackageLockPath { get; }

    internal static SafeSpikeWorkspace OpenFromCurrentDirectory()
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.CurrentDirectory));
        var workspace = new SafeSpikeWorkspace(root);
        workspace.AssertRepositoryRoot();
        workspace.AssertSafeAncestors();
        return workspace;
    }

    internal FileStream AcquireExclusiveRunLock()
    {
        EnsureRegularDirectory(_artifactsRoot);
        EnsureRegularDirectory(_spikeRoot);
        string path = GetContainedPath(_spikeRoot, "run.lock");
        EnsureNotReparsePointIfPresent(path);
        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);

        try
        {
            EnsureNotReparsePointIfPresent(path);
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
            DeleteOwnedTree(WorkRoot);
        }

        EnsureRegularDirectory(_artifactsRoot);
        EnsureRegularDirectory(_spikeRoot);
        EnsureRegularDirectory(WorkRoot);
        EnsureRegularDirectory(EvidenceRoot);
    }

    internal void DeleteExactModeEvidenceSummary(SpikeMode mode)
    {
        string fileName = mode is SpikeMode.Smoke ? "smoke-summary.json" : "decision-summary.json";
        string path = GetContainedPath(EvidenceRoot, fileName);
        EnsureNotReparsePointIfPresent(path);
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    internal string CreateCaseDirectory(string caseName)
    {
        if (string.IsNullOrWhiteSpace(caseName) ||
            caseName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-'))
        {
            throw new ArgumentException("The case name is invalid.", nameof(caseName));
        }

        string path = GetContainedPath(WorkRoot, caseName);
        EnsureRegularDirectory(path);
        return path;
    }

    internal void DeleteCaseDirectory(string path)
    {
        string candidate = Path.GetFullPath(path);
        string? parent = Directory.GetParent(candidate)?.FullName;
        if (!string.Equals(parent, Path.GetFullPath(WorkRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Refusing to clean an unexpected protected-catalog directory.");
        }

        DeleteOwnedTree(candidate);
    }

    internal void Complete()
    {
        if (Directory.Exists(WorkRoot))
        {
            DeleteOwnedTree(WorkRoot);
        }
    }

    private void AssertRepositoryRoot()
    {
        if (!File.Exists(GlobalJsonPath) ||
            !File.Exists(SpecificationPath) ||
            !File.Exists(LicensePath) ||
            !File.Exists(PackageLockPath))
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
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The protected-catalog workspace contains an unsafe directory.");
        }
    }

    private static void EnsureNotReparsePointIfPresent(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The protected-catalog workspace contains a reparse point.");
        }
    }

    private void DeleteOwnedTree(string path)
    {
        string candidate = Path.GetFullPath(path);
        string workRoot = Path.GetFullPath(WorkRoot);
        string? parent = Directory.GetParent(candidate)?.FullName;
        if (!string.Equals(candidate, workRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parent, workRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Refusing to clean outside the protected-catalog work root.");
        }

        if (!Directory.Exists(candidate))
        {
            return;
        }

        var pending = new Queue<DirectoryInfo>();
        pending.Enqueue(new DirectoryInfo(candidate));
        while (pending.TryDequeue(out DirectoryInfo? directory))
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Refusing to clean a reparse-point workspace.");
            }

            foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Refusing to clean a workspace containing a reparse point.");
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Enqueue(child);
                }
            }
        }

        Directory.Delete(candidate, recursive: true);
    }

    private static string GetContainedPath(string parent, string relativePath)
    {
        string fullParent = Path.GetFullPath(parent);
        string candidate = Path.GetFullPath(Path.Combine(fullParent, relativePath));
        string prefix = Path.EndsInDirectorySeparator(fullParent)
            ? fullParent
            : fullParent + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A protected-catalog path escaped its fixed root.");
        }

        return candidate;
    }
}
