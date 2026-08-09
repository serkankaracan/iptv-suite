using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class LocalHttpFixtureServerTests
{
    [TestMethod]
    [Timeout(15_000)]
    public async Task ServerReturnsExactSyntheticResponseOnLoopback()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"kind\":\"synthetic\"}");
        Dictionary<string, FixtureHttpResponse> routes = new(StringComparer.Ordinal)
        {
            ["/fixture"] = new FixtureHttpResponse(200, "application/json", body),
        };

        await using LocalHttpFixtureServer server = await LocalHttpFixtureServer.StartAsync(routes);
        using HttpClient client = new() { BaseAddress = server.BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        using HttpResponseMessage response = await client.GetAsync("fixture");
        byte[] actual = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        CollectionAssert.AreEqual(body, actual);
        Assert.IsTrue(IPAddress.IsLoopback(IPAddress.Parse(server.BaseAddress.Host)));
        Assert.HasCount(1, server.Requests);
        Assert.AreEqual("/fixture", server.Requests[0].Path);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task DisposeReleasesTheOperatingSystemSelectedPort()
    {
        LocalHttpFixtureServer server = await LocalHttpFixtureServer.StartAsync(
            new Dictionary<string, FixtureHttpResponse>(StringComparer.Ordinal));
        int port = server.Port;

        await server.DisposeAsync();

        TcpListener listener = new(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ParallelServersAndTemporaryDirectoriesRemainIsolated()
    {
        List<TemporaryDirectory> directories = [];
        List<LocalHttpFixtureServer> servers = [];

        try
        {
            for (int index = 0; index < 8; index++)
            {
                directories.Add(TemporaryDirectory.Create($"parallel-{index}"));
                byte[] body = Encoding.UTF8.GetBytes($"synthetic-{index}");
                servers.Add(await LocalHttpFixtureServer.StartAsync(
                    new Dictionary<string, FixtureHttpResponse>(StringComparer.Ordinal)
                    {
                        ["/value"] = new FixtureHttpResponse(200, "text/plain", body),
                    }));
            }

            Assert.HasCount(8, servers.Select(server => server.Port).Distinct().ToArray());
            Assert.HasCount(
                8,
                directories.Select(directory => directory.FullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray());

            Task<string>[] requests = servers.Select(async server =>
            {
                using HttpClient client = new() { BaseAddress = server.BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
                return await client.GetStringAsync("value");
            }).ToArray();
            string[] values = await Task.WhenAll(requests);

            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, 8).Select(index => $"synthetic-{index}").ToArray(),
                values);
        }
        finally
        {
            foreach (LocalHttpFixtureServer server in servers)
            {
                await server.DisposeAsync();
            }

            foreach (TemporaryDirectory directory in directories)
            {
                directory.Dispose();
                Assert.IsFalse(Directory.Exists(directory.FullPath));
            }
        }
    }
}
