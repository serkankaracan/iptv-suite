namespace IptvSuite.Testing;

public sealed class ScriptedTransport
{
    private readonly Queue<ExpectedTransportExchange> _expectations = new();
    private readonly List<FixtureTransportRequest> _requests = [];
    private readonly object _sync = new();

    public IReadOnlyList<FixtureTransportRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return [.. _requests];
            }
        }
    }

    public void Enqueue(string method, string relativePath, FixtureTransportResponse response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ValidateRelativePath(relativePath);
        ArgumentNullException.ThrowIfNull(response);

        lock (_sync)
        {
            _expectations.Enqueue(new ExpectedTransportExchange(
                method,
                relativePath,
                new FixtureTransportResponse(response.StatusCode, response.Body.ToArray())));
        }
    }

    public ValueTask<FixtureTransportResponse> SendAsync(
        FixtureTransportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRelativePath(request.RelativePath);

        lock (_sync)
        {
            if (!_expectations.TryDequeue(out ExpectedTransportExchange? expectation))
            {
                throw new InvalidOperationException("The scripted transport has no remaining response.");
            }

            if (!string.Equals(expectation.Method, request.Method, StringComparison.Ordinal) ||
                !string.Equals(expectation.RelativePath, request.RelativePath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The scripted transport request did not match the next expectation.");
            }

            _requests.Add(new FixtureTransportRequest(request.Method, request.RelativePath, request.Body.ToArray()));
            return ValueTask.FromResult(new FixtureTransportResponse(
                expectation.Response.StatusCode,
                expectation.Response.Body.ToArray()));
        }
    }

    public void VerifyComplete()
    {
        lock (_sync)
        {
            if (_expectations.Count != 0)
            {
                throw new InvalidOperationException($"The scripted transport has {_expectations.Count} unused response(s).");
            }
        }
    }

    private static void ValidateRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!relativePath.StartsWith('/') ||
            Uri.TryCreate(relativePath, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Fixture transport paths must be relative and begin with '/'.", nameof(relativePath));
        }
    }

    private sealed record ExpectedTransportExchange(
        string Method,
        string RelativePath,
        FixtureTransportResponse Response);
}

public sealed record FixtureTransportRequest(string Method, string RelativePath, ReadOnlyMemory<byte> Body);

public sealed record FixtureTransportResponse(int StatusCode, ReadOnlyMemory<byte> Body);
