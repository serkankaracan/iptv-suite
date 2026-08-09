namespace IptvSuite.Testing;

public sealed class TemporaryDirectory : IDisposable
{
    private static readonly string TestRoot = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "iptv-suite-tests"));

    private bool _disposed;

    private TemporaryDirectory(string fullPath)
    {
        FullPath = fullPath;
    }

    public string FullPath { get; }

    public static TemporaryDirectory Create(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (scope.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("Temporary-directory scope must use ASCII letters, digits, '-' or '_'.", nameof(scope));
        }

        Directory.CreateDirectory(TestRoot);
        string path = Path.Combine(TestRoot, $"{scope}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new TemporaryDirectory(path);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        string resolved = Path.GetFullPath(FullPath);
        string expectedPrefix = TestRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove a test directory outside the dedicated temp root.");
        }

        if (Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive: true);
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
