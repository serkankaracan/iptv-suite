using System.Diagnostics;

namespace IptvSuite.SecretStoreSpike;

internal sealed record PhaseSample(
    int OperationCount,
    double DurationMilliseconds,
    long AllocatedBytes,
    int GenerationZeroCollections,
    int GenerationOneCollections,
    int GenerationTwoCollections,
    long WorkingSetBoundaryMaximumBytes)
{
    internal static async ValueTask<PhaseSample> MeasureAsync(
        int operationCount,
        Func<ValueTask> action)
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
            Stopwatch.GetElapsedTime(0, elapsed).TotalMilliseconds,
            Math.Max(0, allocatedAfter - allocatedBefore),
            generationZeroAfter - generationZeroBefore,
            generationOneAfter - generationOneBefore,
            generationTwoAfter - generationTwoBefore,
            Math.Max(workingSetBefore, workingSetAfter));
    }
}

internal sealed record NumberSummary(
    double Minimum,
    double Median,
    double Percentile95,
    double Maximum,
    double Mean)
{
    internal static NumberSummary From(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("At least one metric value is required.", nameof(values));
        }

        return new NumberSummary(
            Round(ordered[0]),
            Round(Percentile(ordered, 0.50)),
            Round(Percentile(ordered, 0.95)),
            Round(ordered[^1]),
            Round(ordered.Average()));
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        int rank = Math.Max(1, (int)Math.Ceiling(percentile * ordered.Length));
        return ordered[rank - 1];
    }

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
    NumberSummary WorkingSetBoundaryMaximumBytes,
    GarbageCollectionAggregate GarbageCollections)
{
    internal static PhaseAggregate From(IReadOnlyCollection<PhaseSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one phase sample is required.", nameof(samples));
        }

        int operationsPerSample = samples.First().OperationCount;
        if (samples.Any(sample => sample.OperationCount != operationsPerSample))
        {
            throw new InvalidOperationException("Phase samples must have a stable operation count.");
        }

        return new PhaseAggregate(
            samples.Count,
            operationsPerSample,
            NumberSummary.From(samples.Select(sample => sample.DurationMilliseconds)),
            NumberSummary.From(samples.Select(sample =>
                sample.OperationCount / Math.Max(sample.DurationMilliseconds / 1_000, double.Epsilon))),
            NumberSummary.From(samples.Select(sample => (double)sample.AllocatedBytes)),
            NumberSummary.From(samples.Select(sample => (double)sample.AllocatedBytes / sample.OperationCount)),
            NumberSummary.From(samples.Select(sample => (double)sample.WorkingSetBoundaryMaximumBytes)),
            new GarbageCollectionAggregate(
                samples.Sum(sample => sample.GenerationZeroCollections),
                samples.Sum(sample => sample.GenerationOneCollections),
                samples.Sum(sample => sample.GenerationTwoCollections)));
    }
}
