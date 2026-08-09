namespace IptvSuite.Testing;

public sealed class FakePlayer
{
    private readonly List<FakePlayerCall> _calls = [];

    public FakePlayerState State { get; private set; } = FakePlayerState.Idle;

    public IReadOnlyList<FakePlayerCall> Calls => _calls;

    public ValueTask OpenAsync(string syntheticFixtureId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(syntheticFixtureId);
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Add(new FakePlayerCall(FakePlayerOperation.Open, syntheticFixtureId));
        return ValueTask.CompletedTask;
    }

    public ValueTask PlayAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Add(new FakePlayerCall(FakePlayerOperation.Play, null));
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Add(new FakePlayerCall(FakePlayerOperation.Stop, null));
        return ValueTask.CompletedTask;
    }

    public void SetState(FakePlayerState state)
    {
        State = state;
    }
}

public enum FakePlayerState
{
    Idle,
    Ready,
    Playing,
    Stopped,
}

public enum FakePlayerOperation
{
    Open,
    Play,
    Stop,
}

public sealed record FakePlayerCall(FakePlayerOperation Operation, string? SyntheticFixtureId);
