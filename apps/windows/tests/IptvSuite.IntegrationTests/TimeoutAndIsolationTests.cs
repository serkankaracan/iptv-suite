using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class TimeoutAndIsolationTests
{
    [TestMethod]
    [Timeout(5_000)]
    public async Task TimeoutGuardCancelsCooperativeOperationWithinBound()
    {
        await Assert.ThrowsExactlyAsync<TimeoutException>(async () =>
            await TimeoutGuard.RunAsync(
                cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                TimeSpan.FromMilliseconds(100)));
    }

    [TestMethod]
    public void TemporaryDirectoryCleansOnlyItsDedicatedChild()
    {
        TemporaryDirectory temporary = TemporaryDirectory.Create("cleanup");
        string path = temporary.FullPath;
        File.WriteAllText(Path.Combine(path, "artifact.txt"), "synthetic");

        temporary.Dispose();

        Assert.IsFalse(Directory.Exists(path));
    }
}
