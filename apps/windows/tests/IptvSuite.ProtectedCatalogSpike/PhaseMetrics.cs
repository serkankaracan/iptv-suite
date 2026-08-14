using System.Diagnostics;

namespace IptvSuite.ProtectedCatalogSpike;

internal sealed record PhaseSample(
    int OperationCount,
    double DurationMilliseconds,
    long AllocatedBytes,
    int GenerationZeroCollections,
    int GenerationOneCollections,
    int GenerationTwoCollections,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long WorkingSetBoundaryMaximumBytes,
    long WorkingSetBoundaryDeltaBytes)
{
    internal static async ValueTask<PhaseSample> MeasureAsync(int operationCount, Func<ValueTask> action)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationCount);
        ArgumentNullException.ThrowIfNull(action);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int generationZeroBefore = GC.CollectionCount(0);
        int generationOneBefore = GC.CollectionCount(1);
        int generationTwoBefore = GC.CollectionCount(2);
        long started = Stopwatch.GetTimestamp();

        await action().ConfigureAwait(false);

        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        int generationZeroAfter = GC.CollectionCount(0);
        int generationOneAfter = GC.CollectionCount(1);
        int generationTwoAfter = GC.CollectionCount(2);
        process.Refresh();
        long workingSetAfter = process.WorkingSet64;
        return new PhaseSample(
            operationCount,
            Math.Round(Stopwatch.GetElapsedTime(0, elapsed).TotalMilliseconds, 3),
            Math.Max(0, allocatedAfter - allocatedBefore),
            generationZeroAfter - generationZeroBefore,
            generationOneAfter - generationOneBefore,
            generationTwoAfter - generationTwoBefore,
            workingSetBefore,
            workingSetAfter,
            Math.Max(workingSetBefore, workingSetAfter),
            workingSetAfter - workingSetBefore);
    }
}

internal sealed record NumberSummary(
    double Minimum,
    double Median,
    double Percentile90,
    double Percentile95,
    double Maximum,
    double Mean,
    double CoefficientOfVariation)
{
    internal static NumberSummary From(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("At least one metric is required.", nameof(values));
        }

        double mean = ordered.Average();
        double standardDeviation = Math.Sqrt(
            ordered.Sum(value => Math.Pow(value - mean, 2)) / ordered.Length);
        return new NumberSummary(
            Round(ordered[0]),
            Round(Percentile(ordered, 0.5)),
            Round(Percentile(ordered, 0.9)),
            Round(Percentile(ordered, 0.95)),
            Round(ordered[^1]),
            Round(mean),
            Round(Math.Abs(mean) <= double.Epsilon ? 0 : standardDeviation / Math.Abs(mean)));
    }

    private static double Percentile(double[] ordered, double percentile) =>
        ordered[Math.Max(1, (int)Math.Ceiling(percentile * ordered.Length)) - 1];

    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}

internal sealed record GarbageCollectionAggregate(int GenerationZero, int GenerationOne, int GenerationTwo);

internal sealed record PhaseAggregate(
    int Samples,
    int OperationsPerSample,
    NumberSummary DurationMilliseconds,
    NumberSummary OperationsPerSecond,
    NumberSummary AllocatedBytes,
    NumberSummary AllocatedBytesPerOperation,
    NumberSummary WorkingSetBeforeBytes,
    NumberSummary WorkingSetAfterBytes,
    NumberSummary WorkingSetBoundaryMaximumBytes,
    NumberSummary WorkingSetBoundaryDeltaBytes,
    GarbageCollectionAggregate GarbageCollections)
{
    internal static PhaseAggregate From(IReadOnlyCollection<PhaseSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0 || samples.Any(sample => sample.OperationCount != samples.First().OperationCount))
        {
            throw new InvalidOperationException("Phase samples require one stable non-empty operation count.");
        }

        int operations = samples.First().OperationCount;
        return new PhaseAggregate(
            samples.Count,
            operations,
            NumberSummary.From(samples.Select(sample => sample.DurationMilliseconds)),
            NumberSummary.From(samples.Select(sample =>
                operations / Math.Max(sample.DurationMilliseconds / 1_000, double.Epsilon))),
            NumberSummary.From(samples.Select(sample => (double)sample.AllocatedBytes)),
            NumberSummary.From(samples.Select(sample => (double)sample.AllocatedBytes / operations)),
            NumberSummary.From(samples.Select(sample => (double)sample.WorkingSetBeforeBytes)),
            NumberSummary.From(samples.Select(sample => (double)sample.WorkingSetAfterBytes)),
            NumberSummary.From(samples.Select(sample => (double)sample.WorkingSetBoundaryMaximumBytes)),
            NumberSummary.From(samples.Select(sample => (double)sample.WorkingSetBoundaryDeltaBytes)),
            new GarbageCollectionAggregate(
                samples.Sum(sample => sample.GenerationZeroCollections),
                samples.Sum(sample => sample.GenerationOneCollections),
                samples.Sum(sample => sample.GenerationTwoCollections)));
    }
}

internal sealed record PhaseEvidence(IReadOnlyList<PhaseSample> RawSamples, PhaseAggregate Aggregate)
{
    internal static PhaseEvidence From(List<PhaseSample> samples) => new(samples, PhaseAggregate.From(samples));
}
