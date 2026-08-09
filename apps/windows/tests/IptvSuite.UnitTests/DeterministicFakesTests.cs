using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class DeterministicFakesTests
{
    private static readonly byte[] ExpectedStoredPayload = [1, 2, 3, 4];
    private static readonly string[] ExpectedSecretJournalKeys =
        ["fixture-secret", "fixture-secret", "fixture-secret", "fixture-secret"];

    [TestMethod]
    [Timeout(5_000)]
    public async Task FakeTimeCompletesDelayOnlyAfterExplicitAdvance()
    {
        DateTimeOffset start = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        Microsoft.Extensions.Time.Testing.FakeTimeProvider time = TestTime.Create(start);

        Task delay = Task.Delay(TimeSpan.FromMinutes(5), time, CancellationToken.None);

        Assert.IsFalse(delay.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.IsFalse(delay.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(1));
        await delay.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        Assert.AreEqual(start.AddMinutes(5), time.GetUtcNow());
    }

    [TestMethod]
    public async Task ScriptedTransportMatchesRequestsAndRejectsUnexpectedCalls()
    {
        ScriptedTransport transport = new();
        transport.Enqueue("GET", "/fixture", new FixtureTransportResponse(200, Encoding.UTF8.GetBytes("synthetic")));

        FixtureTransportResponse response = await transport.SendAsync(
            new FixtureTransportRequest("GET", "/fixture", ReadOnlyMemory<byte>.Empty));

        Assert.AreEqual(200, response.StatusCode);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("synthetic"), response.Body.ToArray());
        Assert.HasCount(1, transport.Requests);
        transport.VerifyComplete();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await transport.SendAsync(
                new FixtureTransportRequest("GET", "/fixture", ReadOnlyMemory<byte>.Empty)));
    }

    [TestMethod]
    public async Task SecretStoreCopiesPayloadAndJournalsOnlyKeys()
    {
        using InMemorySecretStore store = new();
        byte[] source = ExpectedStoredPayload.ToArray();

        await store.WriteAsync("fixture-secret", source);
        source[0] = 99;
        byte[]? stored = await store.ReadAsync("fixture-secret");

        CollectionAssert.AreEqual(ExpectedStoredPayload, stored);
        Assert.IsTrue(await store.DeleteAsync("fixture-secret"));
        Assert.IsNull(await store.ReadAsync("fixture-secret"));
        CollectionAssert.AreEqual(
            ExpectedSecretJournalKeys,
            store.Operations.Select(operation => operation.Key).ToArray());
        Assert.IsFalse(store.Operations.Any(operation =>
            operation.ToString().Contains("1, 2, 3, 4", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PlayerDoubleRecordsCommandsButDoesNotInventStateTransitions()
    {
        FakePlayer player = new();

        await player.OpenAsync("synthetic-fixture-1");
        await player.PlayAsync();
        await player.StopAsync();

        Assert.AreEqual(FakePlayerState.Idle, player.State);
        CollectionAssert.AreEqual(
            new[] { FakePlayerOperation.Open, FakePlayerOperation.Play, FakePlayerOperation.Stop },
            player.Calls.Select(call => call.Operation).ToArray());

        player.SetState(FakePlayerState.Playing);
        Assert.AreEqual(FakePlayerState.Playing, player.State);
    }
}
