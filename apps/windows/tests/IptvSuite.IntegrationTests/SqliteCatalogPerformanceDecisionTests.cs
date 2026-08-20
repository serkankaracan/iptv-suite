using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class SqliteCatalogPerformanceDecisionTests
{
    private const string OptInVariable = "IPTVSUITE_M8_CATALOG_DECISION";
    private const string EvidenceRootVariable = "IPTVSUITE_M8_CATALOG_EVIDENCE_ROOT";
    private const string CommitVariable = "IPTVSUITE_M8_CATALOG_COMMIT";
    private const int Iterations = 20;
    private static readonly int[] Scales = [5_000, 10_000, 20_000, 50_000];
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    [TestMethod]
    [Timeout(30 * 60 * 1000)]
    public async Task MeasureParserToProtectedSqliteDecisionMatrix()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        Assert.IsTrue(OperatingSystem.IsWindows());
        string evidenceRoot = RequireAbsoluteDirectory(Environment.GetEnvironmentVariable(EvidenceRootVariable));
        string commit = Environment.GetEnvironmentVariable(CommitVariable) ?? string.Empty;
        Assert.IsTrue(commit.Length == 40 && commit.All(char.IsAsciiHexDigit));
        Directory.CreateDirectory(evidenceRoot);
        string evidencePath = Path.Combine(evidenceRoot, "decision-summary.json");
        if (File.Exists(evidencePath))
        {
            File.Delete(evidencePath);
        }

        var scaleResults = new List<object>();
        foreach (int scale in Scales)
        {
            string playlist = BuildPlaylist(scale);
            var samples = new List<Sample>(Iterations);
            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                using TemporaryDirectory temporary = TemporaryDirectory.Create("m8-catalog-decision");
                string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
                var store = new M4InMemorySecretStore();
                ContentSource source = await CreateSourceAsync(store);
                object loader = CreateLoader(store, new DecisionTransport(playlist), databasePath);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                using Process process = Process.GetCurrentProcess();
                process.Refresh();
                long workingSetBefore = process.WorkingSet64;
                var stopwatch = Stopwatch.StartNew();
                bool success = await InvokeLoaderAsync(loader, source);
                stopwatch.Stop();
                long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
                process.Refresh();
                long workingSetAfter = process.WorkingSet64;
                Assert.IsTrue(success);
                Assert.AreEqual(scale, await ReadChannelCountAsync(databasePath));
                Assert.IsFalse(await ContainsLocatorCanaryAsync(databasePath));
                Assert.IsFalse(File.Exists(databasePath + "-wal"));
                Assert.IsFalse(File.Exists(databasePath + "-shm"));
                samples.Add(new Sample(
                    stopwatch.Elapsed.TotalMilliseconds,
                    allocatedAfter - allocatedBefore,
                    Math.Max(0, workingSetAfter - workingSetBefore),
                    new FileInfo(databasePath).Length));
            }

            scaleResults.Add(new
            {
                recordCount = scale,
                iterations = Iterations,
                durationMilliseconds = Summary(samples.Select(sample => sample.DurationMilliseconds)),
                allocatedBytes = Summary(samples.Select(sample => (double)sample.AllocatedBytes)),
                workingSetDeltaBytes = Summary(samples.Select(sample => (double)sample.WorkingSetDeltaBytes)),
                databaseBytes = Summary(samples.Select(sample => (double)sample.DatabaseBytes)),
                rawSamples = samples,
            });
        }

        var evidence = new
        {
            schemaVersion = 1,
            milestone = "M8",
            evidenceKind = "catalog-parser-protected-sqlite-decision",
            configuration = "Release",
            platform = "x64",
            commitSha = commit.ToLowerInvariant(),
            sdkVersion = Environment.Version.ToString(),
            iterations = Iterations,
            scales = scaleResults,
            plaintextLocatorCanaryScan = "passed",
            journalMode = "DELETE",
            note = "Component measurement; parser, normalization, protected persistence and indexes are included; UI and network are excluded.",
        };
        string temporaryEvidence = evidencePath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryEvidence,
            JsonSerializer.Serialize(evidence, EvidenceJsonOptions),
            new UTF8Encoding(false));
        File.Move(temporaryEvidence, evidencePath);
    }

    private static string BuildPlaylist(int count)
    {
        var builder = new StringBuilder(checked(count * 64));
        builder.AppendLine("#EXTM3U");
        for (int index = 0; index < count; index++)
        {
            builder.Append("#EXTINF:-1 tvg-id=\"c").Append(index).Append("\",C").Append(index).AppendLine();
            builder.Append("s/").Append(index).AppendLine();
        }

        return builder.ToString();
    }

    private static async Task<ContentSource> CreateSourceAsync(M4InMemorySecretStore store)
    {
        DomainResult<ValidatedSourceDraft> draft = await new SourceDraftProtectionService(store)
            .ProtectRemotePlaylistAsync(
                SourceId.Generate(),
                "Synthetic M8 decision",
                "https://fixtures.invalid/catalog/list.m3u");
        Assert.IsTrue(draft.IsSuccess);
        DateTimeOffset now = new(2026, 8, 20, 18, 0, 0, TimeSpan.Zero);
        DomainResult<ContentSource> source = ContentSource.Create(
            draft.Value,
            ContentSourceStatus.Testing,
            now,
            now);
        Assert.IsTrue(source.IsSuccess);
        return source.Value!;
    }

    private static object CreateLoader(ISecretStore store, IStreamingHttpTransport transport, string databasePath)
    {
        Assembly assembly = typeof(BoundedHttpTransport).Assembly;
        Type sinkType = assembly.GetType("IptvSuite.Infrastructure.SqliteRemoteM3uImportSink", true)!;
        object sink = Activator.CreateInstance(
            sinkType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [databasePath],
            null)!;
        Type loaderType = assembly.GetType("IptvSuite.Infrastructure.RemotePlaylistCatalogLoader", true)!;
        return Activator.CreateInstance(
            loaderType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [store, transport, sink],
            null)!;
    }

    private static async Task<bool> InvokeLoaderAsync(object loader, ContentSource source)
    {
        MethodInfo method = loader.GetType().GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object valueTask = method.Invoke(loader, [source, CancellationToken.None])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return (bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!;
    }

    private static async Task<long> ReadChannelCountAsync(string databasePath)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM channels;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ContainsLocatorCanaryAsync(string databasePath)
    {
        byte[] bytes = await File.ReadAllBytesAsync(databasePath);
        return bytes.AsSpan().IndexOf("https://fixtures.invalid/catalog/final/s/"u8) >= 0;
    }

    private static object Summary(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        return new
        {
            min = ordered[0],
            median = Percentile(ordered, 0.50),
            p95 = Percentile(ordered, 0.95),
            max = ordered[^1],
            mean = ordered.Average(),
        };
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        int index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static string RequireAbsoluteDirectory(string? path)
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(path));
        string fullPath = Path.GetFullPath(path!);
        Assert.AreEqual(fullPath, path, ignoreCase: true);
        return fullPath;
    }

    private sealed record Sample(
        double DurationMilliseconds,
        long AllocatedBytes,
        long WorkingSetDeltaBytes,
        long DatabaseBytes);

    private sealed class DecisionTransport(string playlist) : IStreamingHttpTransport
    {
        public ValueTask<HttpStreamingResult> GetStreamAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(playlist), writable: false);
            ConstructorInfo constructor = typeof(HttpStreamingResponseLease).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var lease = (HttpStreamingResponseLease)constructor.Invoke(
                [stream, new Uri("https://fixtures.invalid/catalog/final/list.m3u"), new EmptyOwner()]);
            return ValueTask.FromResult(HttpStreamingResult.Success(200, lease));
        }
    }

    private sealed class EmptyOwner : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
