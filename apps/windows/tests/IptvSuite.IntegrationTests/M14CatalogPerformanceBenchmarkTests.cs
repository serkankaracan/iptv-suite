using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class M14CatalogPerformanceBenchmarkTests
{
    private const string OptInVariable = "IPTVSUITE_M14_CATALOG_BENCHMARK";
    private const string EvidenceRootVariable = "IPTVSUITE_M14_CATALOG_EVIDENCE_ROOT";
    private const string CommitVariable = "IPTVSUITE_M14_CATALOG_COMMIT";
    private const string ValidatedSdkVariable = "IPTVSUITE_M14_CATALOG_VALIDATED_SDK";
    private const string ReferenceModeVariable = "IPTVSUITE_M14_CATALOG_REFERENCE_MODE";
    private const string CacheConditionVariable = "IPTVSUITE_M14_CATALOG_CACHE_CONDITION";
    private const string PowerConditionVariable = "IPTVSUITE_M14_CATALOG_POWER_CONDITION";
    private const string ThermalConditionVariable = "IPTVSUITE_M14_CATALOG_THERMAL_CONDITION";
    private const string BackgroundConditionVariable = "IPTVSUITE_M14_CATALOG_BACKGROUND_CONDITION";
    private const int Iterations = 20;
    private const int MinimumAuthoritativeWarmIterations = 20;
    private const int ColdObservationsPerStage = 1;
    private const int PeakWorkingSetSampleLimit = 512;
    private const double ParserBudgetMilliseconds = 2_000;
    private const double NormalizeProtectPersistIndexBudgetMilliseconds = 3_000;
    private const double CombinedImportBudgetMilliseconds = 5_000;
    private const double ImportAllocationBudgetBytes = 150 * 1024 * 1024;
    private const long PeakWorkingSetBudgetBytes = 250L * 1024 * 1024;
    private const double CancellationBudgetMilliseconds = 250;
    private const double QueryBudgetMilliseconds = 100;
    private const double ReopenBudgetMilliseconds = 500;
    private static readonly int[] Scales = [100, 5_000, 10_000, 20_000, 50_000];
    private static readonly Uri CorpusUri = new("https://fixtures.invalid/catalog/list.m3u");
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [TestMethod]
    [Timeout(2 * 60 * 60 * 1000)]
    public async Task MeasureM14CatalogBenchmarkMatrix()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        Assert.IsTrue(OperatingSystem.IsWindows());
        Assert.IsTrue(Environment.Is64BitProcess);
        Assert.AreEqual(
            "Release",
            typeof(M14CatalogPerformanceBenchmarkTests).Assembly
                .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration);

        string repositoryRoot = FindRepositoryRoot();
        string evidenceRoot = RequireAbsoluteDirectory(Environment.GetEnvironmentVariable(EvidenceRootVariable));
        string commit = RequireCommit(Environment.GetEnvironmentVariable(CommitVariable));
        string sdkVersion = RequireValidatedSdk(
            repositoryRoot,
            Environment.GetEnvironmentVariable(ValidatedSdkVariable));
        bool referenceModeRequested = ReadReferenceMode();
        MetadataValue cacheCondition = ReadCondition(CacheConditionVariable, "Warm");
        MetadataValue powerCondition = ReadCondition(PowerConditionVariable, "AcStable");
        MetadataValue thermalCondition = ReadCondition(ThermalConditionVariable, "Nominal");
        MetadataValue backgroundCondition = ReadCondition(BackgroundConditionVariable, "Controlled");
        bool conditionDeclarationsComplete =
            cacheCondition.IsDeclared &&
            powerCondition.IsDeclared &&
            thermalCondition.IsDeclared &&
            backgroundCondition.IsDeclared;
        if (referenceModeRequested && !conditionDeclarationsComplete)
        {
            throw new InvalidDataException(
                "M14 reference mode requires all exact closed condition declarations.");
        }

        Directory.CreateDirectory(evidenceRoot);
        string evidencePath = Path.Combine(evidenceRoot, "benchmark-summary.json");
        string temporaryEvidencePath = evidencePath + ".tmp";
        File.Delete(temporaryEvidencePath);
        File.Delete(evidencePath);

        using TemporaryDirectory corpusOutput = TemporaryDirectory.Create("m14-catalog-corpora");
        string specificationPath = Path.Combine(
            repositoryRoot,
            "apps",
            "windows",
            "testdata",
            "m14",
            "catalog-corpus-spec.json");
        M14CatalogCorpusSpecification specification =
            M14CatalogCorpusGenerator.LoadSpecification(specificationPath);
        GeneratedM14CatalogCorpusSet generated = await M14CatalogCorpusGenerator.GenerateAsync(
            specification,
            corpusOutput.FullPath,
            CancellationToken.None);
        await ValidateCorporaAsync(corpusOutput.FullPath, generated.Corpora);

        (long manifestByteLength, string manifestSha256) = await MeasureFileAsync(generated.ManifestPath);
        (long specificationByteLength, string specificationSha256) = await MeasureFileAsync(specificationPath);
        string repositoryPrefix = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string licensePath = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            specification.License.File.Replace('/', Path.DirectorySeparatorChar)));
        Assert.IsTrue(licensePath.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase));
        (long licenseByteLength, string licenseSha256) = await MeasureFileAsync(licensePath);

        var scaleResults = new List<ScaleBenchmark>(Scales.Length);
        foreach (int scale in Scales)
        {
            GeneratedM14CatalogCorpus corpus = generated.Corpora.Single(candidate =>
                candidate.ChannelCount == scale &&
                candidate.ExpectedOutcome == M14CatalogCorpusExpectedOutcome.Success);

            MeasurementSample parserColdObservation = await MeasureParserAsync(corpus, iteration: 0);
            await WarmParserAsync(corpus);
            var parserSamples = new List<MeasurementSample>(Iterations);
            for (int iteration = 1; iteration <= Iterations; iteration++)
            {
                parserSamples.Add(await MeasureParserAsync(corpus, iteration));
            }

            MeasurementSample combinedColdObservation = await MeasureCombinedImportAsync(corpus, iteration: 0);
            await WarmCombinedImportAsync(corpus);
            var combinedSamples = new List<MeasurementSample>(Iterations);
            for (int iteration = 1; iteration <= Iterations; iteration++)
            {
                combinedSamples.Add(await MeasureCombinedImportAsync(corpus, iteration));
            }

            PeakWorkingSetPass workingSet = await MeasurePeakWorkingSetAsync(corpus);
            scaleResults.Add(new ScaleBenchmark(
                corpus,
                parserColdObservation,
                parserSamples,
                combinedColdObservation,
                combinedSamples,
                workingSet));
        }

        GeneratedM14CatalogCorpus fiftyThousand = generated.Corpora.Single(corpus =>
            corpus.ChannelCount == 50_000 &&
            corpus.ExpectedOutcome == M14CatalogCorpusExpectedOutcome.Success);
        QueryBenchmark query = await MeasureQueryMatrixAsync(fiftyThousand);
        CancellationBenchmark cancellation = await MeasureCancellationAsync(fiftyThousand);
        GeneratedM14CatalogCorpus stress = generated.Corpora.Single(corpus =>
            corpus.ChannelCount == 100_000 &&
            corpus.ExpectedOutcome == M14CatalogCorpusExpectedOutcome.EntryLimitFailClosed);
        EntryLimitProbe entryLimitProbe = await MeasureEntryLimitProbeAsync(stress);

        bool authoritativeWarmSampleCountVerified = scaleResults.All(result =>
            result.ParserSamples.Count >= MinimumAuthoritativeWarmIterations &&
            result.CombinedSamples.Count >= MinimumAuthoritativeWarmIterations);
        bool measurementIntegrityVerified = authoritativeWarmSampleCountVerified && scaleResults.All(result =>
            result.ParserColdObservation.ProcessIo.IsAvailable &&
            result.ParserSamples.All(sample => sample.ProcessIo.IsAvailable) &&
            result.CombinedColdObservation.ProcessIo.IsAvailable &&
            result.CombinedSamples.All(sample => sample.ProcessIo.IsAvailable) &&
            !result.PeakWorkingSet.SampleCapacityReached);
        const bool referenceEligible = false;
        BudgetEvaluation budgetEvaluation = EvaluateBudgets(scaleResults, query, cancellation);
        bool effectiveReferenceEligible = referenceModeRequested
            ? conditionDeclarationsComplete && measurementIntegrityVerified && budgetEvaluation.AllPassed
            : referenceEligible;

        var evidence = new
        {
            schemaVersion = 1,
            milestone = "M14",
            evidenceKind = "catalog-performance-benchmark",
            configuration = "Release",
            platform = "x64",
            commitSha = commit,
            sdkVersion,
            runtime = RuntimeInformation.FrameworkDescription,
            operatingSystemBuild = Environment.OSVersion.Version.ToString(),
            operatingSystemArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processor = ReadProcessorMetadata(),
            logicalProcessorCount = Environment.ProcessorCount,
            iterations = Iterations,
            authoritativeWarmIterations = Iterations,
            minimumAuthoritativeWarmIterations = MinimumAuthoritativeWarmIterations,
            coldObservationsPerStage = ColdObservationsPerStage,
            result = budgetEvaluation.AllPassed ? "passed" : "failed",
            measurementIntegrityVerified,
            authoritativeWarmSampleCountVerified,
            conditionDeclarationsComplete,
            referenceModeRequested,
            referenceEligibilityRequirements = new
            {
                exactConditionDeclarations = conditionDeclarationsComplete,
                measurementIntegrity = measurementIntegrityVerified,
                passingBenchmarkResult = budgetEvaluation.AllPassed,
            },
            referenceEligible = effectiveReferenceEligible,
            conditions = new
            {
                cache = cacheCondition,
                power = powerCondition,
                thermal = thermalCondition,
                background = backgroundCondition,
            },
            corpusManifest = new
            {
                retained = false,
                byteLength = manifestByteLength,
                sha256 = manifestSha256,
                generator = M14CatalogCorpusGenerator.GeneratorName,
                generatorVersion = M14CatalogCorpusGenerator.GeneratorVersion,
            },
            corpusSpecification = new
            {
                repositoryRelativePath = "apps/windows/testdata/m14/catalog-corpus-spec.json",
                byteLength = specificationByteLength,
                sha256 = specificationSha256,
            },
            syntheticLicense = new
            {
                expression = specification.License.Expression,
                status = specification.License.Status,
                repositoryRelativePath = specification.License.File,
                byteLength = licenseByteLength,
                sha256 = licenseSha256,
            },
            corpora = generated.Corpora.Select(CorpusEvidence).ToArray(),
            stageScope = new
            {
                parserDiagnostic = "Production local-byte parser only; transport, persistence, query and UI are excluded.",
                combinedImport = "Production loader plus local-file stream reads, parser, normalization, protected SQLite persistence and indexes; network download is excluded.",
                coldObservation = "Exactly one bounded observation per stage and scale before that stage's explicit warm-up; no operating-system cache flush or verified cold-cache state is claimed.",
                authoritativeTiming = "Budget predicates use at least 20 post-warm-up samples per stage and scale; cold observations are excluded.",
                resourcePass = "Separate bounded working-set sampling pass; samples do not perturb authoritative timing iterations.",
            },
            scales = scaleResults.Select(result => new
            {
                recordCount = result.Corpus.ChannelCount,
                corpusId = result.Corpus.Id,
                parserDiagnostic = StageEvidence(
                    result.ParserColdObservation,
                    result.ParserSamples,
                    includeDatabase: false),
                combinedImport = StageEvidence(
                    result.CombinedColdObservation,
                    result.CombinedSamples,
                    includeDatabase: true),
                peakWorkingSet = result.PeakWorkingSet,
            }).ToArray(),
            query50k = new
            {
                recordCount = 50_000,
                catalogSchemaVersion = query.CatalogSchemaVersion,
                iterations = Iterations,
                firstPageMilliseconds = Summary(query.RawSamples.Select(sample => sample.FirstPageMilliseconds)),
                categoryPageMilliseconds = Summary(query.RawSamples.Select(sample => sample.CategoryPageMilliseconds)),
                searchMilliseconds = Summary(query.RawSamples.Select(sample => sample.SearchMilliseconds)),
                reopenFirstVisibleMilliseconds = Summary(query.RawSamples.Select(sample => sample.ReopenFirstVisibleMilliseconds)),
                rawSamples = query.RawSamples,
            },
            cancellation = new
            {
                recordCount = 50_000,
                iterations = Iterations,
                expectedErrorCode = DomainErrorCode.OperationCancelled.ToString(),
                completionLatencyMilliseconds = Summary(cancellation.RawSamples.Select(sample => sample.DurationMilliseconds)),
                rawSamples = cancellation.RawSamples,
            },
            entryLimitProbe,
            plaintextLocatorCanaryScan = "passed",
            budgets = new
            {
                parserP95Milliseconds = ParserBudgetMilliseconds,
                normalizeProtectPersistIndexP95Milliseconds = NormalizeProtectPersistIndexBudgetMilliseconds,
                combinedImportP95Milliseconds = CombinedImportBudgetMilliseconds,
                importAllocationMaximumBytes = ImportAllocationBudgetBytes,
                peakWorkingSetDeltaBytes = PeakWorkingSetBudgetBytes,
                cancellationP95Milliseconds = CancellationBudgetMilliseconds,
                queryP95Milliseconds = QueryBudgetMilliseconds,
                reopenP95Milliseconds = ReopenBudgetMilliseconds,
            },
            budgetEvaluation,
            nonClaims = new[]
            {
                "The full combined-import p95 is used only as a conservative upper bound for the normalize/protect/persist/index predicate; no exact stage split is measured or claimed.",
                "Cold observations mean only pre-warm-up invocation order; no operating-system cache flush or verified cold-cache state is claimed.",
                "File-backed synthetic corpora exclude network download time and real provider behavior.",
                "This component benchmark does not claim WinUI input, frame, image-cache or physical-device acceptance.",
                "Generated corpora and their aggregate manifest are transient and reproducible from the commit-bound specification; only bounded aggregate hashes are retained.",
                "Machine-condition values are closed caller declarations, not dedicated-runner verification.",
                "Foundation mode is not reference eligible; reference mode requires exact declarations, measurement integrity and a passing result.",
                "Reference eligibility does not perform a baseline comparison or establish a performance baseline.",
            },
        };

        await File.WriteAllTextAsync(
            temporaryEvidencePath,
            JsonSerializer.Serialize(evidence, EvidenceJsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryEvidencePath, evidencePath, overwrite: true);

        AssertBudgets(scaleResults, query, cancellation);
        if (referenceModeRequested)
        {
            Assert.IsTrue(effectiveReferenceEligible);
        }
    }

    private static async Task WarmParserAsync(GeneratedM14CatalogCorpus corpus)
    {
        ParserOutcome outcome = await InvokeParserAsync(corpus.Path, CancellationToken.None);
        RequireSuccessfulCorpus(outcome, corpus.ChannelCount);
    }

    private static async Task<MeasurementSample> MeasureParserAsync(
        GeneratedM14CatalogCorpus corpus,
        int iteration)
    {
        ForceCollection();
        using Process process = Process.GetCurrentProcess();
        MeasurementStart start = StartMeasurement(process);
        ParserOutcome outcome = await InvokeParserAsync(corpus.Path, CancellationToken.None);
        MeasurementSample sample = CompleteMeasurement(process, start, iteration, databaseBytes: 0);
        RequireSuccessfulCorpus(outcome, corpus.ChannelCount);
        return sample;
    }

    private static async Task WarmCombinedImportAsync(GeneratedM14CatalogCorpus corpus)
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m14-import-warmup");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await using LoaderFixture fixture = await LoaderFixture.CreateAsync(corpus.Path, databasePath);
        LoaderOutcome outcome = await InvokeLoaderAsync(fixture.Loader, fixture.Source, CancellationToken.None);
        RequireSuccessfulCorpus(outcome, corpus.ChannelCount);
        await fixture.DisposeSinkAsync();
        Assert.AreEqual(corpus.ChannelCount, await ReadChannelCountAsync(databasePath));
        Assert.AreEqual(corpus.CategoryCount, await ReadCategoryCountAsync(databasePath));
        await AssertNoPlaintextLocatorCanaryAsync(databasePath);
    }

    private static async Task<MeasurementSample> MeasureCombinedImportAsync(
        GeneratedM14CatalogCorpus corpus,
        int iteration)
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m14-import-sample");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await using LoaderFixture fixture = await LoaderFixture.CreateAsync(corpus.Path, databasePath);
        ForceCollection();
        using Process process = Process.GetCurrentProcess();
        MeasurementStart start = StartMeasurement(process);
        LoaderOutcome outcome = await InvokeLoaderAsync(fixture.Loader, fixture.Source, CancellationToken.None);
        MeasurementSample measured = CompleteMeasurement(process, start, iteration, databaseBytes: 0);
        RequireSuccessfulCorpus(outcome, corpus.ChannelCount);
        await fixture.DisposeSinkAsync();
        Assert.AreEqual(corpus.ChannelCount, await ReadChannelCountAsync(databasePath));
        Assert.AreEqual(corpus.CategoryCount, await ReadCategoryCountAsync(databasePath));
        await AssertNoPlaintextLocatorCanaryAsync(databasePath);
        Assert.IsFalse(File.Exists(databasePath + "-wal"));
        Assert.IsFalse(File.Exists(databasePath + "-shm"));
        return measured with { DatabaseBytes = new FileInfo(databasePath).Length };
    }

    private static async Task<PeakWorkingSetPass> MeasurePeakWorkingSetAsync(
        GeneratedM14CatalogCorpus corpus)
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m14-import-resource");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await using LoaderFixture fixture = await LoaderFixture.CreateAsync(corpus.Path, databasePath);
        ForceCollection();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long baseline = process.WorkingSet64;
        long started = Stopwatch.GetTimestamp();
        var samples = new List<WorkingSetSample>(PeakWorkingSetSampleLimit);
        Task<LoaderOutcome> load = InvokeLoaderAsync(
            fixture.Loader,
            fixture.Source,
            CancellationToken.None);
        int ordinal = 0;
        while (!load.IsCompleted && samples.Count < PeakWorkingSetSampleLimit)
        {
            process.Refresh();
            samples.Add(new WorkingSetSample(
                ++ordinal,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                process.WorkingSet64));
            await Task.WhenAny(load, Task.Delay(10));
        }

        bool sampleCapacityReached = !load.IsCompleted && samples.Count == PeakWorkingSetSampleLimit;
        LoaderOutcome outcome = await load;
        RequireSuccessfulCorpus(outcome, corpus.ChannelCount);
        process.Refresh();
        long final = process.WorkingSet64;
        long peak = Math.Max(
            Math.Max(baseline, final),
            samples.Count == 0 ? baseline : samples.Max(sample => sample.WorkingSetBytes));
        await fixture.DisposeSinkAsync();
        Assert.AreEqual(corpus.ChannelCount, await ReadChannelCountAsync(databasePath));
        Assert.AreEqual(corpus.CategoryCount, await ReadCategoryCountAsync(databasePath));
        await AssertNoPlaintextLocatorCanaryAsync(databasePath);
        return new PeakWorkingSetPass(
            baseline,
            peak,
            final,
            Math.Max(0, peak - baseline),
            samples.Count,
            sampleCapacityReached,
            samples);
    }

    private static async Task<QueryBenchmark> MeasureQueryMatrixAsync(
        GeneratedM14CatalogCorpus corpus)
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m14-query-matrix");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await using LoaderFixture fixture = await LoaderFixture.CreateAsync(corpus.Path, databasePath);
        LoaderOutcome outcome = await InvokeLoaderAsync(fixture.Loader, fixture.Source, CancellationToken.None);
        RequireSuccessfulCorpus(outcome, corpus.ChannelCount);
        await fixture.DisposeSinkAsync();

        var browser = new SqliteCatalogQuery(databasePath);
        IReadOnlyList<CatalogCategoryItem> categories = await browser.ReadCategoriesAsync(fixture.Source.Id);
        Assert.HasCount(corpus.CategoryCount, categories);
        CatalogChannelPage seedPage = await browser.ReadChannelsAsync(
            fixture.Source.Id,
            null,
            null,
            0,
            SqliteCatalogQuery.MaximumPageSize);
        Assert.IsNotEmpty(seedPage.Items);
        string searchText = seedPage.Items[^1].Name;

        await RunQuerySetAsync(browser, databasePath, fixture.Source.Id, categories[0].CategoryId, searchText);
        var samples = new List<QuerySample>(Iterations);
        for (int iteration = 1; iteration <= Iterations; iteration++)
        {
            double firstPage = await MeasureDurationAsync(async () =>
            {
                CatalogChannelPage page = await browser.ReadChannelsAsync(
                    fixture.Source.Id, null, null, 0, SqliteCatalogQuery.MaximumPageSize);
                Assert.IsNotEmpty(page.Items);
            });
            double categoryPage = await MeasureDurationAsync(async () =>
            {
                CatalogChannelPage page = await browser.ReadChannelsAsync(
                    fixture.Source.Id,
                    categories[0].CategoryId,
                    null,
                    0,
                    SqliteCatalogQuery.MaximumPageSize);
                Assert.IsNotEmpty(page.Items);
            });
            double search = await MeasureDurationAsync(async () =>
            {
                CatalogChannelPage page = await browser.ReadChannelsAsync(
                    fixture.Source.Id,
                    null,
                    searchText,
                    0,
                    SqliteCatalogQuery.MaximumPageSize);
                Assert.IsNotEmpty(page.Items);
            });
            double reopen = await MeasureDurationAsync(async () =>
            {
                var reopened = new SqliteCatalogQuery(databasePath);
                CatalogChannelPage page = await reopened.ReadChannelsAsync(
                    fixture.Source.Id, null, null, 0, SqliteCatalogQuery.MaximumPageSize);
                Assert.IsNotEmpty(page.Items);
            });
            samples.Add(new QuerySample(iteration, firstPage, categoryPage, search, reopen));
        }

        await AssertNoPlaintextLocatorCanaryAsync(databasePath);
        return new QueryBenchmark(await ReadSchemaVersionAsync(databasePath), samples);
    }

    private static async Task RunQuerySetAsync(
        SqliteCatalogQuery browser,
        string databasePath,
        SourceId sourceId,
        CategoryId categoryId,
        string searchText)
    {
        _ = await browser.ReadChannelsAsync(sourceId, null, null, 0, SqliteCatalogQuery.MaximumPageSize);
        _ = await browser.ReadChannelsAsync(sourceId, categoryId, null, 0, SqliteCatalogQuery.MaximumPageSize);
        _ = await browser.ReadChannelsAsync(sourceId, null, searchText, 0, SqliteCatalogQuery.MaximumPageSize);
        var reopened = new SqliteCatalogQuery(databasePath);
        _ = await reopened.ReadChannelsAsync(sourceId, null, null, 0, SqliteCatalogQuery.MaximumPageSize);
    }

    private static async Task<CancellationBenchmark> MeasureCancellationAsync(
        GeneratedM14CatalogCorpus corpus)
    {
        await RunCancellationAsync(corpus, iteration: 0, measure: false);
        var samples = new List<CancellationSample>(Iterations);
        for (int iteration = 1; iteration <= Iterations; iteration++)
        {
            samples.Add(await RunCancellationAsync(corpus, iteration, measure: true));
        }

        return new CancellationBenchmark(samples);
    }

    private static async Task<CancellationSample> RunCancellationAsync(
        GeneratedM14CatalogCorpus corpus,
        int iteration,
        bool measure)
    {
        using TemporaryDirectory temporary = TemporaryDirectory.Create("m14-import-cancel");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        using var cancellation = new CancellationTokenSource();
        await using LoaderFixture fixture = await LoaderFixture.CreateAsync(
            corpus.Path,
            databasePath,
            cancellation);
        long started = measure ? Stopwatch.GetTimestamp() : 0;
        LoaderOutcome outcome = await InvokeLoaderAsync(fixture.Loader, fixture.Source, cancellation.Token);
        double elapsed = measure ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0;
        Assert.IsFalse(outcome.IsSuccess);
        Assert.AreEqual(DomainErrorCode.OperationCancelled, outcome.ErrorCode);
        Assert.IsTrue(cancellation.IsCancellationRequested);
        await fixture.DisposeSinkAsync();
        Assert.AreEqual(0L, await ReadChannelCountAsync(databasePath));
        await AssertNoPlaintextLocatorCanaryAsync(databasePath);
        Assert.IsFalse(File.Exists(databasePath + "-wal"));
        Assert.IsFalse(File.Exists(databasePath + "-shm"));
        return new CancellationSample(iteration, elapsed);
    }

    private static async Task<EntryLimitProbe> MeasureEntryLimitProbeAsync(
        GeneratedM14CatalogCorpus corpus)
    {
        Assert.AreEqual(M14CatalogCorpusExpectedOutcome.EntryLimitFailClosed, corpus.ExpectedOutcome);
        ParserOutcome parser = await InvokeParserAsync(corpus.Path, CancellationToken.None);
        Assert.IsFalse(parser.IsSuccess);
        Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, parser.ErrorCode);

        using TemporaryDirectory temporary = TemporaryDirectory.Create("m14-entry-limit");
        string databasePath = Path.Combine(temporary.FullPath, "catalog.db");
        await using LoaderFixture fixture = await LoaderFixture.CreateAsync(corpus.Path, databasePath);
        LoaderOutcome combined = await InvokeLoaderAsync(fixture.Loader, fixture.Source, CancellationToken.None);
        Assert.IsFalse(combined.IsSuccess);
        Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, combined.ErrorCode);
        await fixture.DisposeSinkAsync();
        long persisted = await ReadChannelCountAsync(databasePath);
        Assert.AreEqual(0L, persisted);
        await AssertNoPlaintextLocatorCanaryAsync(databasePath);
        return new EntryLimitProbe(
            corpus.ChannelCount,
            corpus.ExpectedOutcome.ToString(),
            parser.ErrorCode!.Value.ToString(),
            combined.ErrorCode!.Value.ToString(),
            persisted);
    }

    private static async Task<ParserOutcome> InvokeParserAsync(
        string corpusPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            corpusPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        Type parserType = typeof(BoundedHttpTransport).Assembly.GetType(
            "IptvSuite.Infrastructure.RemoteM3uPlaylistParser",
            throwOnError: true)!;
        MethodInfo method = parserType.GetMethod(
            "ParseAsync",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            [typeof(Stream), typeof(Uri), typeof(CancellationToken)],
            modifiers: null)!;
        object valueTask = method.Invoke(null, [stream, CorpusUri, cancellationToken])!;
        object result = await AwaitValueTaskResultAsync(valueTask);
        return ReadParserOutcome(result);
    }

    private static async Task<LoaderOutcome> InvokeLoaderAsync(
        object loader,
        ContentSource source,
        CancellationToken cancellationToken)
    {
        MethodInfo method = loader.GetType().GetMethod(
            "LoadAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(ContentSource), typeof(CancellationToken)],
            modifiers: null)!;
        object valueTask = method.Invoke(loader, [source, cancellationToken])!;
        object result = await AwaitValueTaskResultAsync(valueTask);
        bool success = Read<bool>(result, "IsSuccess");
        if (!success)
        {
            object error = Read<object>(result, "Error");
            return new LoaderOutcome(false, Read<DomainErrorCode>(error, "Code"), 0, 0);
        }

        object value = Read<object>(result, "Value");
        return new LoaderOutcome(
            true,
            null,
            Read<int>(value, "ProcessedEntryCount"),
            Read<int>(value, "SkippedEntryCount"));
    }

    private static async Task<object> AwaitValueTaskResultAsync(object valueTask)
    {
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static ParserOutcome ReadParserOutcome(object result)
    {
        bool success = Read<bool>(result, "IsSuccess");
        if (!success)
        {
            object error = Read<object>(result, "Error");
            return new ParserOutcome(false, Read<DomainErrorCode>(error, "Code"), 0, 0);
        }

        object value = Read<object>(result, "Value");
        return new ParserOutcome(
            true,
            null,
            Read<int>(value, "ProcessedEntryCount"),
            Read<int>(value, "SkippedEntryCount"));
    }

    private static T Read<T>(object instance, string propertyName) =>
        (T)instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void RequireSuccessfulCorpus(ParserOutcome outcome, int expectedCount)
    {
        Assert.IsTrue(outcome.IsSuccess);
        Assert.AreEqual(expectedCount, outcome.ProcessedEntryCount);
        Assert.AreEqual(1, outcome.SkippedEntryCount);
    }

    private static void RequireSuccessfulCorpus(LoaderOutcome outcome, int expectedCount)
    {
        Assert.IsTrue(outcome.IsSuccess);
        Assert.AreEqual(expectedCount, outcome.ProcessedEntryCount);
        Assert.AreEqual(1, outcome.SkippedEntryCount);
    }

    private static MeasurementStart StartMeasurement(Process process)
    {
        process.Refresh();
        return new MeasurementStart(
            Stopwatch.GetTimestamp(),
            GC.GetTotalAllocatedBytes(precise: true),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            process.TotalProcessorTime,
            process.WorkingSet64,
            NativeProcessIo.TryRead(process));
    }

    private static MeasurementSample CompleteMeasurement(
        Process process,
        MeasurementStart start,
        int iteration,
        long databaseBytes)
    {
        double elapsed = Stopwatch.GetElapsedTime(start.StartedTimestamp).TotalMilliseconds;
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - start.AllocatedBytes;
        var garbageCollections = new GarbageCollectionDelta(
            GC.CollectionCount(0) - start.Generation0Collections,
            GC.CollectionCount(1) - start.Generation1Collections,
            GC.CollectionCount(2) - start.Generation2Collections);
        process.Refresh();
        long workingSetAfter = process.WorkingSet64;
        double processorMilliseconds = (process.TotalProcessorTime - start.ProcessorTime).TotalMilliseconds;
        ProcessIoSnapshot afterIo = NativeProcessIo.TryRead(process);
        Assert.IsTrue(start.ProcessIo.IsAvailable);
        Assert.IsTrue(afterIo.IsAvailable);
        return new MeasurementSample(
            iteration,
            elapsed,
            Math.Max(0, allocated),
            processorMilliseconds,
            garbageCollections,
            new WorkingSetDelta(
                start.WorkingSetBytes,
                workingSetAfter,
                workingSetAfter - start.WorkingSetBytes),
            ProcessIoDelta.Create(start.ProcessIo, afterIo),
            databaseBytes);
    }

    private static object StageEvidence(
        MeasurementSample coldObservation,
        IReadOnlyList<MeasurementSample> samples,
        bool includeDatabase)
    {
        return new
        {
            iterations = Iterations,
            sampleRole = "authoritative-warm",
            minimumAuthoritativeWarmSamples = MinimumAuthoritativeWarmIterations,
            coldObservation = new
            {
                sampleCount = ColdObservationsPerStage,
                bounded = true,
                authoritative = false,
                capturedBeforeExplicitWarmUp = true,
                operatingSystemCacheFlushPerformed = false,
                rawSample = coldObservation,
            },
            durationMilliseconds = Summary(samples.Select(sample => sample.DurationMilliseconds)),
            allocatedBytes = Summary(samples.Select(sample => (double)sample.AllocatedBytes)),
            processCpuMilliseconds = Summary(samples.Select(sample => sample.ProcessCpuMilliseconds)),
            garbageCollections = new
            {
                generation0 = Summary(samples.Select(sample => (double)sample.GarbageCollections.Generation0)),
                generation1 = Summary(samples.Select(sample => (double)sample.GarbageCollections.Generation1)),
                generation2 = Summary(samples.Select(sample => (double)sample.GarbageCollections.Generation2)),
            },
            workingSet = new
            {
                beforeBytes = Summary(samples.Select(sample => (double)sample.WorkingSet.BeforeBytes)),
                afterBytes = Summary(samples.Select(sample => (double)sample.WorkingSet.AfterBytes)),
                deltaBytes = Summary(samples.Select(sample => (double)sample.WorkingSet.DeltaBytes)),
            },
            processIo = new
            {
                availableForAllSamples = samples.All(sample => sample.ProcessIo.IsAvailable),
                readOperationCount = Summary(samples.Select(sample => (double)sample.ProcessIo.ReadOperationCount)),
                writeOperationCount = Summary(samples.Select(sample => (double)sample.ProcessIo.WriteOperationCount)),
                otherOperationCount = Summary(samples.Select(sample => (double)sample.ProcessIo.OtherOperationCount)),
                readTransferBytes = Summary(samples.Select(sample => (double)sample.ProcessIo.ReadTransferBytes)),
                writeTransferBytes = Summary(samples.Select(sample => (double)sample.ProcessIo.WriteTransferBytes)),
                otherTransferBytes = Summary(samples.Select(sample => (double)sample.ProcessIo.OtherTransferBytes)),
            },
            databaseBytes = includeDatabase
                ? Summary(samples.Select(sample => (double)sample.DatabaseBytes))
                : null,
            rawSamples = samples,
        };
    }

    private static MetricSummary Summary(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        Assert.HasCount(Iterations, ordered);
        double mean = ordered.Average();
        double variance = ordered.Sum(value => (value - mean) * (value - mean)) / ordered.Length;
        double standardDeviation = Math.Sqrt(variance);
        return new MetricSummary(
            ordered[0],
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.90),
            Percentile(ordered, 0.95),
            ordered[^1],
            mean,
            standardDeviation,
            Math.Abs(mean) <= double.Epsilon ? 0 : standardDeviation / Math.Abs(mean));
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        int index = Math.Clamp(
            (int)Math.Ceiling(percentile * ordered.Length) - 1,
            0,
            ordered.Length - 1);
        return ordered[index];
    }

    private static async Task<double> MeasureDurationAsync(Func<Task> action)
    {
        long started = Stopwatch.GetTimestamp();
        await action();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void AssertBudgets(
        IReadOnlyList<ScaleBenchmark> scaleResults,
        QueryBenchmark query,
        CancellationBenchmark cancellation)
    {
        ScaleBenchmark gate = scaleResults.Single(result => result.Corpus.ChannelCount == 50_000);
        double combinedImportP95 = Summary(
            gate.CombinedSamples.Select(sample => sample.DurationMilliseconds)).Percentile95;
        Assert.IsFalse(gate.PeakWorkingSet.SampleCapacityReached);
        Assert.IsLessThanOrEqualTo(
            ParserBudgetMilliseconds,
            Summary(gate.ParserSamples.Select(sample => sample.DurationMilliseconds)).Percentile95);
        Assert.IsLessThanOrEqualTo(
            NormalizeProtectPersistIndexBudgetMilliseconds,
            combinedImportP95);
        Assert.IsLessThanOrEqualTo(
            CombinedImportBudgetMilliseconds,
            combinedImportP95);
        Assert.IsLessThanOrEqualTo(
            ImportAllocationBudgetBytes,
            Summary(gate.CombinedSamples.Select(sample => (double)sample.AllocatedBytes)).Maximum);
        Assert.IsLessThanOrEqualTo(
            PeakWorkingSetBudgetBytes,
            gate.PeakWorkingSet.PeakDeltaBytes);
        Assert.IsLessThanOrEqualTo(
            CancellationBudgetMilliseconds,
            Summary(cancellation.RawSamples.Select(sample => sample.DurationMilliseconds)).Percentile95);
        Assert.IsLessThanOrEqualTo(
            QueryBudgetMilliseconds,
            Summary(query.RawSamples.Select(sample => sample.FirstPageMilliseconds)).Percentile95);
        Assert.IsLessThanOrEqualTo(
            QueryBudgetMilliseconds,
            Summary(query.RawSamples.Select(sample => sample.CategoryPageMilliseconds)).Percentile95);
        Assert.IsLessThanOrEqualTo(
            QueryBudgetMilliseconds,
            Summary(query.RawSamples.Select(sample => sample.SearchMilliseconds)).Percentile95);
        Assert.IsLessThanOrEqualTo(
            ReopenBudgetMilliseconds,
            Summary(query.RawSamples.Select(sample => sample.ReopenFirstVisibleMilliseconds)).Percentile95);
    }

    private static BudgetEvaluation EvaluateBudgets(
        IReadOnlyList<ScaleBenchmark> scaleResults,
        QueryBenchmark query,
        CancellationBenchmark cancellation)
    {
        ScaleBenchmark gate = scaleResults.Single(result => result.Corpus.ChannelCount == 50_000);
        double parserP95 = Summary(
            gate.ParserSamples.Select(sample => sample.DurationMilliseconds)).Percentile95;
        double combinedImportP95 = Summary(
            gate.CombinedSamples.Select(sample => sample.DurationMilliseconds)).Percentile95;
        double normalizeProtectPersistIndexConservativeUpperBoundP95 = combinedImportP95;
        double allocationMaximum = Summary(
            gate.CombinedSamples.Select(sample => (double)sample.AllocatedBytes)).Maximum;
        double cancellationP95 = Summary(
            cancellation.RawSamples.Select(sample => sample.DurationMilliseconds)).Percentile95;
        double firstPageP95 = Summary(
            query.RawSamples.Select(sample => sample.FirstPageMilliseconds)).Percentile95;
        double categoryPageP95 = Summary(
            query.RawSamples.Select(sample => sample.CategoryPageMilliseconds)).Percentile95;
        double searchP95 = Summary(
            query.RawSamples.Select(sample => sample.SearchMilliseconds)).Percentile95;
        double reopenP95 = Summary(
            query.RawSamples.Select(sample => sample.ReopenFirstVisibleMilliseconds)).Percentile95;
        bool peakSamplingComplete = !gate.PeakWorkingSet.SampleCapacityReached;
        bool normalizeProtectPersistIndexPassed =
            normalizeProtectPersistIndexConservativeUpperBoundP95 <=
            NormalizeProtectPersistIndexBudgetMilliseconds;
        bool allPassed =
            parserP95 <= ParserBudgetMilliseconds &&
            normalizeProtectPersistIndexPassed &&
            combinedImportP95 <= CombinedImportBudgetMilliseconds &&
            allocationMaximum <= ImportAllocationBudgetBytes &&
            gate.PeakWorkingSet.PeakDeltaBytes <= PeakWorkingSetBudgetBytes &&
            peakSamplingComplete &&
            cancellationP95 <= CancellationBudgetMilliseconds &&
            firstPageP95 <= QueryBudgetMilliseconds &&
            categoryPageP95 <= QueryBudgetMilliseconds &&
            searchP95 <= QueryBudgetMilliseconds &&
            reopenP95 <= ReopenBudgetMilliseconds;
        return new BudgetEvaluation(
            parserP95,
            normalizeProtectPersistIndexConservativeUpperBoundP95,
            normalizeProtectPersistIndexPassed,
            combinedImportP95,
            allocationMaximum,
            gate.PeakWorkingSet.PeakDeltaBytes,
            peakSamplingComplete,
            cancellationP95,
            firstPageP95,
            categoryPageP95,
            searchP95,
            reopenP95,
            allPassed);
    }

    private static async Task ValidateCorporaAsync(
        string outputRoot,
        IReadOnlyList<GeneratedM14CatalogCorpus> corpora)
    {
        Assert.HasCount(6, corpora);
        string root = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        foreach (GeneratedM14CatalogCorpus corpus in corpora)
        {
            string path = Path.GetFullPath(corpus.Path);
            Assert.IsTrue(path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(IsSafeMetadata(corpus.Id, 64));
            (long byteLength, string sha256) = await MeasureFileAsync(path);
            Assert.AreEqual(corpus.ByteLength, byteLength);
            Assert.AreEqual(corpus.Sha256, sha256);
        }
    }

    private static object CorpusEvidence(GeneratedM14CatalogCorpus corpus) => new
    {
        id = corpus.Id,
        sha256 = corpus.Sha256,
        byteLength = corpus.ByteLength,
        channelCount = corpus.ChannelCount,
        categoryCount = corpus.CategoryCount,
        logoReferenceCount = corpus.LogoReferenceCount,
        expectedOutcome = corpus.ExpectedOutcome.ToString(),
    };

    private static async Task<(long ByteLength, string Sha256)> MeasureFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        byte[] digest = await SHA256.HashDataAsync(stream);
        return (stream.Length, Convert.ToHexString(digest).ToLowerInvariant());
    }

    private static async Task<long> ReadChannelCountAsync(string databasePath)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM channels;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadCategoryCountAsync(string databasePath)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM categories;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<int> ReadSchemaVersionAsync(string databasePath)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task AssertNoPlaintextLocatorCanaryAsync(string databasePath)
    {
        byte[] bytes = await File.ReadAllBytesAsync(databasePath);
        try
        {
            Assert.IsTrue(bytes.AsSpan().IndexOf("access_token=synthetic-test-only-"u8) < 0);
            Assert.IsTrue(bytes.AsSpan().IndexOf("streams/stable-collision.ts"u8) < 0);
            Assert.IsTrue(bytes.AsSpan().IndexOf("fixtures.invalid/logos"u8) < 0);
            Assert.IsTrue(bytes.AsSpan().IndexOf("fixtures.invalid/catalog/list.m3u"u8) < 0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string RequireAbsoluteDirectory(string? path)
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(path));
        string fullPath = Path.GetFullPath(path!);
        Assert.AreEqual(fullPath, path, ignoreCase: true);
        return fullPath;
    }

    private static string RequireCommit(string? commit)
    {
        Assert.IsNotNull(commit);
        Assert.AreEqual(40, commit.Length);
        Assert.IsTrue(commit.All(char.IsAsciiHexDigit));
        return commit.ToLowerInvariant();
    }

    private static string RequireValidatedSdk(string repositoryRoot, string? sdkVersion)
    {
        Assert.IsNotNull(sdkVersion);
        Assert.IsTrue(Version.TryParse(sdkVersion, out _));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "global.json")));
        string configured = document.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
        Assert.AreEqual(configured, sdkVersion);
        return sdkVersion;
    }

    private static bool ReadReferenceMode()
    {
        string? value = Environment.GetEnvironmentVariable(ReferenceModeVariable);
        return value switch
        {
            null or "0" => false,
            "1" => true,
            _ => throw new InvalidDataException(
                "M14 benchmark reference mode is outside the closed vocabulary."),
        };
    }

    private static MetadataValue ReadCondition(string variable, string expectedValue)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return new MetadataValue("Unverified", "Unverified");
        }

        if (!string.Equals(value, expectedValue, StringComparison.Ordinal))
        {
            throw new InvalidDataException("M14 benchmark condition declaration is outside the closed vocabulary.");
        }

        return new MetadataValue("Declared", value);
    }

    private static MetadataValue ReadProcessorMetadata()
    {
        string? value = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        return !string.IsNullOrWhiteSpace(value) && IsSafeMetadata(value, 128)
            ? new MetadataValue("Observed", value)
            : new MetadataValue("Unverified", "Unverified");
    }

    private static bool IsSafeMetadata(string value, int maximumLength) =>
        value.Length is > 0 && value.Length <= maximumLength && value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is ' ' or '-' or '_' or '.' or '(' or ')' or '@' or ',');

    private sealed class LoaderFixture : IAsyncDisposable
    {
        private readonly M4InMemorySecretStore _store;
        private readonly object _sink;
        private bool _sinkDisposed;

        private LoaderFixture(
            M4InMemorySecretStore store,
            object sink,
            object loader,
            ContentSource source)
        {
            _store = store;
            _sink = sink;
            Loader = loader;
            Source = source;
        }

        internal object Loader { get; }
        internal ContentSource Source { get; }

        internal static async Task<LoaderFixture> CreateAsync(
            string corpusPath,
            string databasePath,
            CancellationTokenSource? cancellation = null)
        {
            var store = new M4InMemorySecretStore();
            try
            {
                ContentSource source = await CreateSourceAsync(store);
                Assembly assembly = typeof(BoundedHttpTransport).Assembly;
                Type sinkType = assembly.GetType(
                    "IptvSuite.Infrastructure.SqliteRemoteM3uImportSink",
                    throwOnError: true)!;
                object sink = Activator.CreateInstance(
                    sinkType,
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    [databasePath],
                    culture: null)!;
                Type loaderType = assembly.GetType(
                    "IptvSuite.Infrastructure.RemotePlaylistCatalogLoader",
                    throwOnError: true)!;
                object loader = Activator.CreateInstance(
                    loaderType,
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    [store, new FileCorpusTransport(corpusPath, cancellation), sink],
                    culture: null)!;
                return new LoaderFixture(store, sink, loader, source);
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }

        internal async ValueTask DisposeSinkAsync()
        {
            if (_sinkDisposed)
            {
                return;
            }

            await ((IAsyncDisposable)_sink).DisposeAsync();
            _sinkDisposed = true;
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeSinkAsync();
            _store.Dispose();
        }

        private static async Task<ContentSource> CreateSourceAsync(M4InMemorySecretStore store)
        {
            DomainResult<ValidatedSourceDraft> draft = await new SourceDraftProtectionService(store)
                .ProtectRemotePlaylistAsync(
                    SourceId.Generate(),
                    "Synthetic M14 benchmark",
                    CorpusUri.AbsoluteUri);
            Assert.IsTrue(draft.IsSuccess);
            DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            DomainResult<ContentSource> source = ContentSource.Create(
                draft.Value,
                ContentSourceStatus.Testing,
                now,
                now);
            Assert.IsTrue(source.IsSuccess);
            return source.Value!;
        }
    }

    private sealed class FileCorpusTransport(
        string corpusPath,
        CancellationTokenSource? cancellation) : IStreamingHttpTransport
    {
        public ValueTask<HttpStreamingResult> GetStreamAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            Stream stream = new FileStream(
                corpusPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
            if (cancellation is not null)
            {
                stream = new CancellingReadStream(stream, cancellation, cancelAfterReads: 3);
            }

            ConstructorInfo constructor = typeof(HttpStreamingResponseLease).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var lease = (HttpStreamingResponseLease)constructor.Invoke(
                [stream, CorpusUri, new EmptyOwner(), null, null]);
            return ValueTask.FromResult(HttpStreamingResult.Success(200, lease));
        }
    }

    private sealed class CancellingReadStream(
        Stream inner,
        CancellationTokenSource cancellation,
        int cancelAfterReads) : Stream
    {
        private int _readCount;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            Signal();
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            Signal();
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Signal()
        {
            if (Interlocked.Increment(ref _readCount) == cancelAfterReads)
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class EmptyOwner : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static class NativeProcessIo
    {
        private static readonly Lazy<NativeIoBinding?> Binding = new(CreateBinding);

        internal static ProcessIoSnapshot TryRead(Process process)
        {
            NativeIoBinding? binding = Binding.Value;
            if (binding is null || !binding.Read(process.Handle, out IoCounters counters))
            {
                return ProcessIoSnapshot.Unavailable;
            }

            return new ProcessIoSnapshot(
                true,
                counters.ReadOperationCount,
                counters.WriteOperationCount,
                counters.OtherOperationCount,
                counters.ReadTransferCount,
                counters.WriteTransferCount,
                counters.OtherTransferCount);
        }

        private static NativeIoBinding? CreateBinding()
        {
            if (!NativeLibrary.TryLoad(
                    "kernel32.dll",
                    typeof(M14CatalogPerformanceBenchmarkTests).Assembly,
                    DllImportSearchPath.System32,
                    out nint library) ||
                !NativeLibrary.TryGetExport(library, "GetProcessIoCounters", out nint address))
            {
                if (library != 0)
                {
                    NativeLibrary.Free(library);
                }

                return null;
            }

            try
            {
                return new NativeIoBinding(
                    library,
                    Marshal.GetDelegateForFunctionPointer<GetProcessIoCountersDelegate>(address));
            }
            catch (ArgumentException)
            {
                NativeLibrary.Free(library);
                return null;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool GetProcessIoCountersDelegate(nint process, out IoCounters counters);

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        private sealed class NativeIoBinding(
            nint library,
            GetProcessIoCountersDelegate read)
        {
            private readonly nint _library = library;

            internal bool Read(nint process, out IoCounters counters)
            {
                bool success = read(process, out counters);
                GC.KeepAlive(_library);
                return success;
            }
        }
    }

    private sealed record ScaleBenchmark(
        GeneratedM14CatalogCorpus Corpus,
        MeasurementSample ParserColdObservation,
        IReadOnlyList<MeasurementSample> ParserSamples,
        MeasurementSample CombinedColdObservation,
        IReadOnlyList<MeasurementSample> CombinedSamples,
        PeakWorkingSetPass PeakWorkingSet);

    private sealed record ParserOutcome(
        bool IsSuccess,
        DomainErrorCode? ErrorCode,
        int ProcessedEntryCount,
        int SkippedEntryCount);

    private sealed record LoaderOutcome(
        bool IsSuccess,
        DomainErrorCode? ErrorCode,
        int ProcessedEntryCount,
        int SkippedEntryCount);

    private sealed record MeasurementStart(
        long StartedTimestamp,
        long AllocatedBytes,
        int Generation0Collections,
        int Generation1Collections,
        int Generation2Collections,
        TimeSpan ProcessorTime,
        long WorkingSetBytes,
        ProcessIoSnapshot ProcessIo);

    private sealed record MeasurementSample(
        int Iteration,
        double DurationMilliseconds,
        long AllocatedBytes,
        double ProcessCpuMilliseconds,
        GarbageCollectionDelta GarbageCollections,
        WorkingSetDelta WorkingSet,
        ProcessIoDelta ProcessIo,
        long DatabaseBytes);

    private sealed record GarbageCollectionDelta(int Generation0, int Generation1, int Generation2);
    private sealed record WorkingSetDelta(long BeforeBytes, long AfterBytes, long DeltaBytes);
    private sealed record WorkingSetSample(int Ordinal, double ElapsedMilliseconds, long WorkingSetBytes);

    private sealed record PeakWorkingSetPass(
        long BaselineBytes,
        long PeakBytes,
        long FinalBytes,
        long PeakDeltaBytes,
        int SampleCount,
        bool SampleCapacityReached,
        IReadOnlyList<WorkingSetSample> RawSamples);

    private sealed record BudgetEvaluation(
        double ParserP95Milliseconds,
        double NormalizeProtectPersistIndexConservativeUpperBoundP95Milliseconds,
        bool NormalizeProtectPersistIndexPassed,
        double CombinedImportP95Milliseconds,
        double ImportAllocationMaximumBytes,
        long PeakWorkingSetDeltaBytes,
        bool PeakWorkingSetSamplingComplete,
        double CancellationP95Milliseconds,
        double FirstPageP95Milliseconds,
        double CategoryPageP95Milliseconds,
        double SearchP95Milliseconds,
        double ReopenP95Milliseconds,
        bool AllPassed);

    private sealed record ProcessIoSnapshot(
        bool IsAvailable,
        ulong ReadOperationCount,
        ulong WriteOperationCount,
        ulong OtherOperationCount,
        ulong ReadTransferBytes,
        ulong WriteTransferBytes,
        ulong OtherTransferBytes)
    {
        internal static ProcessIoSnapshot Unavailable { get; } = new(false, 0, 0, 0, 0, 0, 0);
    }

    private sealed record ProcessIoDelta(
        bool IsAvailable,
        ulong ReadOperationCount,
        ulong WriteOperationCount,
        ulong OtherOperationCount,
        ulong ReadTransferBytes,
        ulong WriteTransferBytes,
        ulong OtherTransferBytes)
    {
        internal static ProcessIoDelta Create(ProcessIoSnapshot before, ProcessIoSnapshot after)
        {
            if (!before.IsAvailable || !after.IsAvailable)
            {
                return new ProcessIoDelta(false, 0, 0, 0, 0, 0, 0);
            }

            return new ProcessIoDelta(
                true,
                Subtract(after.ReadOperationCount, before.ReadOperationCount),
                Subtract(after.WriteOperationCount, before.WriteOperationCount),
                Subtract(after.OtherOperationCount, before.OtherOperationCount),
                Subtract(after.ReadTransferBytes, before.ReadTransferBytes),
                Subtract(after.WriteTransferBytes, before.WriteTransferBytes),
                Subtract(after.OtherTransferBytes, before.OtherTransferBytes));
        }

        private static ulong Subtract(ulong after, ulong before) => after >= before ? after - before : 0;
    }

    private sealed record MetricSummary(
        double Minimum,
        double Median,
        double Percentile90,
        double Percentile95,
        double Maximum,
        double Mean,
        double StandardDeviation,
        double CoefficientOfVariation);

    private sealed record QueryBenchmark(int CatalogSchemaVersion, IReadOnlyList<QuerySample> RawSamples);
    private sealed record QuerySample(
        int Iteration,
        double FirstPageMilliseconds,
        double CategoryPageMilliseconds,
        double SearchMilliseconds,
        double ReopenFirstVisibleMilliseconds);
    private sealed record CancellationBenchmark(IReadOnlyList<CancellationSample> RawSamples);
    private sealed record CancellationSample(int Iteration, double DurationMilliseconds);
    private sealed record EntryLimitProbe(
        int RecordCount,
        string ExpectedOutcome,
        string ParserErrorCode,
        string CombinedImportErrorCode,
        long PersistedRowsAfterFailure);

    private sealed record MetadataValue(string Verification, string Value)
    {
        [JsonIgnore]
        internal bool IsDeclared => string.Equals(Verification, "Declared", StringComparison.Ordinal);
    }
}
