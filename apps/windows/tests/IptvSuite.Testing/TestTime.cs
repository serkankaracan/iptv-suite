using Microsoft.Extensions.Time.Testing;

namespace IptvSuite.Testing;

public static class TestTime
{
    public static FakeTimeProvider Create(DateTimeOffset initialUtcNow)
    {
        return new FakeTimeProvider(initialUtcNow.ToUniversalTime());
    }
}
