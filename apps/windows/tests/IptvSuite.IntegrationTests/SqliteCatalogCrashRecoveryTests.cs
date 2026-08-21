using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class SqliteCatalogCrashRecoveryTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task KilledRefreshRollsBackAndPreservesPreviousActiveSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-process-crash");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        string storePath = Path.Combine(temporary.FullPath, "store");
        string readyPath = Path.Combine(temporary.FullPath, "transaction.ready");
        string harnessPath = Path.Combine(
            FindRepositoryRoot(),
            "apps", "windows", "tests", "IptvSuite.CatalogCrashHarness",
            "bin", "x64", "Release", "net10.0", "IptvSuite.CatalogCrashHarness.dll");
        Assert.IsTrue(File.Exists(harnessPath));
        string dotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? string.Empty;
        Assert.IsTrue(Path.IsPathFullyQualified(dotnetPath));
        Assert.IsTrue(File.Exists(dotnetPath));
        Assert.IsTrue(string.Equals(
            "dotnet",
            Path.GetFileNameWithoutExtension(dotnetPath),
            StringComparison.OrdinalIgnoreCase));

        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = dotnetPath,
            ArgumentList =
            {
                harnessPath,
                databasePath,
                storePath,
                readyPath,
            },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        try
        {
            await WaitForFileAsync(readyPath, process, TimeSpan.FromSeconds(20));
            Assert.IsFalse(process.HasExited);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        await InitializeAsync(databasePath);
        await using (var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT count(*) FROM sources),
                    (SELECT count(*) FROM snapshots),
                    (SELECT count(*) FROM channels),
                    (SELECT count(*) FROM sync_runs),
                    (SELECT display_name FROM channels
                        WHERE snapshot_id = (SELECT active_snapshot_id FROM sources));
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(1L, reader.GetInt64(0));
            Assert.AreEqual(1L, reader.GetInt64(1));
            Assert.AreEqual(1L, reader.GetInt64(2));
            Assert.AreEqual(1L, reader.GetInt64(3));
            Assert.AreEqual("Old channel", reader.GetString(4));
        }

        SqliteConnection.ClearAllPools();
        await WaitForExclusiveFileAccessAsync(databasePath, TimeSpan.FromSeconds(5));
        AssertNoHotRollbackJournal(databasePath + "-journal");
        Assert.IsFalse(File.Exists(databasePath + "-wal"));
        Assert.IsFalse(File.Exists(databasePath + "-shm"));
    }

    private static async Task WaitForExclusiveFileAccessAsync(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return;
            }
            catch (IOException) when (stopwatch.Elapsed < timeout)
            {
                await Task.Delay(50);
            }
        }
    }

    private static void AssertNoHotRollbackJournal(string journalPath)
    {
        if (!File.Exists(journalPath))
        {
            return;
        }

        using FileStream stream = new(
            journalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[8];
        Assert.AreEqual(header.Length, stream.Read(header));
        Assert.IsTrue(header.SequenceEqual(stackalloc byte[8]));
    }

    private static async Task WaitForFileAsync(string path, Process process, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                Assert.Fail($"Crash harness exited before transaction readiness with code {process.ExitCode}.");
            }

            if (stopwatch.Elapsed >= timeout)
            {
                Assert.Fail("Crash harness did not reach transaction readiness.");
            }

            await Task.Delay(25);
        }
    }

    private static async Task InitializeAsync(string databasePath)
    {
        Type type = typeof(IptvSuite.Infrastructure.AssemblyMarker).Assembly.GetType(
            "IptvSuite.Infrastructure.SqliteCatalogDatabase", true)!;
        object database = Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [databasePath],
            null)!;
        object valueTask = type.GetMethod("InitializeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(database, [CancellationToken.None])!;
        await (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "global.json")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
