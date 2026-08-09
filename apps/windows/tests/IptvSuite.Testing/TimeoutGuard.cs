namespace IptvSuite.Testing;

public static class TimeoutGuard
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task operationTask = operation(operationCancellation.Token);
        Task timeoutTask = Task.Delay(timeout, cancellationToken);
        Task completed = await Task.WhenAny(operationTask, timeoutTask).ConfigureAwait(false);

        if (completed == operationTask)
        {
            await operationTask.ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await operationCancellation.CancelAsync().ConfigureAwait(false);
        throw new TimeoutException("The test operation exceeded its configured timeout.");
    }
}
