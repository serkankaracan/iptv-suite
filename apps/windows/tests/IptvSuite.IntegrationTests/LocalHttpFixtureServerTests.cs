using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
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
    [Timeout(15_000)]
    public async Task HttpsMediaRouteSupportsHeadAndOneByteRangeWithoutDisablingTlsValidation()
    {
        byte[] body = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        Dictionary<string, FixtureHttpResponse> routes = new(StringComparer.Ordinal)
        {
            ["/media.ts"] = new FixtureHttpResponse(
                StatusCodes.Status200OK,
                "video/mp2t",
                body,
                SupportsByteRanges: true),
        };

        await using LocalHttpFixtureServer server = await LocalHttpFixtureServer.StartHttpsAsync(routes);
        Assert.IsNotNull(server.Certificate);
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                CryptographicOperations.FixedTimeEquals(
                    certificate.RawData,
                    server.Certificate.RawData),
        };
        using HttpClient client = new(handler)
        {
            BaseAddress = server.BaseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "media.ts");
        using HttpResponseMessage headResponse = await client.SendAsync(headRequest);
        Assert.AreEqual(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.AreEqual(body.Length, headResponse.Content.Headers.ContentLength);
        Assert.AreEqual("bytes", headResponse.Headers.AcceptRanges.Single());
        Assert.HasCount(0, await headResponse.Content.ReadAsByteArrayAsync());

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "media.ts");
        rangeRequest.Headers.Range = new RangeHeaderValue(10, 19);
        using HttpResponseMessage rangeResponse = await client.SendAsync(rangeRequest);
        byte[] selected = await rangeResponse.Content.ReadAsByteArrayAsync();
        Assert.AreEqual(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.AreEqual("bytes", rangeResponse.Content.Headers.ContentRange?.Unit);
        Assert.AreEqual(10, rangeResponse.Content.Headers.ContentRange?.From);
        Assert.AreEqual(19, rangeResponse.Content.Headers.ContentRange?.To);
        Assert.AreEqual(body.Length, rangeResponse.Content.Headers.ContentRange?.Length);
        CollectionAssert.AreEqual(body[10..20], selected);

        using var multipleRangeRequest = new HttpRequestMessage(HttpMethod.Get, "media.ts");
        multipleRangeRequest.Headers.TryAddWithoutValidation("Range", "bytes=0-1,4-5");
        using HttpResponseMessage multipleRangeResponse = await client.SendAsync(multipleRangeRequest);
        Assert.AreEqual(HttpStatusCode.RequestedRangeNotSatisfiable, multipleRangeResponse.StatusCode);

        Assert.AreEqual(3, server.RequestCount);
        Assert.AreEqual(2, server.CompletedResponseCount);
        Assert.AreEqual(10L, server.CompletedBodyBytes);
        Assert.AreEqual(1, server.FailureCount);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ControlledStreamSupportsExactAbortHoldRejectRestoreAndDisabledFallback()
    {
        byte[] body = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        Dictionary<string, FixtureHttpResponse> routes = new(StringComparer.Ordinal)
        {
            ["/media.ts"] = new FixtureHttpResponse(
                StatusCodes.Status200OK,
                "video/mp2t",
                body,
                SupportsByteRanges: true),
        };

        await using LocalHttpFixtureServer server = await LocalHttpFixtureServer.StartAsync(routes);
        ControlledFixtureStreamControl control = server.EnableControlledStream(
            "/media.ts",
            new ControlledFixtureStreamOptions
            {
                WriteInterval = TimeSpan.FromMilliseconds(2),
                WriteSize = 4,
                MaximumRequestOrdinals = 8,
            });
        using HttpClient client = new()
        {
            BaseAddress = server.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };

        using HttpResponseMessage firstResponse = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "media.ts"),
            HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        await using Stream firstBody = await firstResponse.Content.ReadAsStreamAsync();
        byte[] repeatedBody = new byte[body.Length + 4];
        await firstBody.ReadExactlyAsync(repeatedBody);
        CollectionAssert.AreEqual(body.Concat(body[..4]).ToArray(), repeatedBody);

        ControlledFixtureStreamSnapshot firstActive = await WaitForControlledSnapshotAsync(
            control,
            snapshot => snapshot.ActiveRequestOrdinal == 1);
        control.HoldSubsequentRequests();
        Assert.IsFalse(control.TryAbortActive(2));
        Assert.IsTrue(control.TryAbortActive(firstActive.ActiveRequestOrdinal));
        Assert.IsFalse(control.TryAbortActive(firstActive.ActiveRequestOrdinal));
        await WaitForControlledSnapshotAsync(
            control,
            snapshot => snapshot.ExpectedAbortCount == 1 && snapshot.ActiveRequestOrdinal == 0);

        Task<HttpResponseMessage> heldResponseTask = client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "media.ts"),
            HttpCompletionOption.ResponseHeadersRead);
        await WaitForControlledSnapshotAsync(
            control,
            snapshot => snapshot.CurrentHeldRequestCount == 1);
        Assert.IsFalse(heldResponseTask.IsCompleted);

        control.Restore();
        using HttpResponseMessage restoredResponse = await heldResponseTask;
        Assert.AreEqual(HttpStatusCode.OK, restoredResponse.StatusCode);
        ControlledFixtureStreamSnapshot secondActive = await WaitForControlledSnapshotAsync(
            control,
            snapshot => snapshot.ActiveRequestOrdinal == 2);

        control.RejectNextRequest();
        Assert.IsTrue(control.TryAbortActive(secondActive.ActiveRequestOrdinal));
        await WaitForControlledSnapshotAsync(
            control,
            snapshot => snapshot.ExpectedAbortCount == 2 && snapshot.ActiveRequestOrdinal == 0);
        using HttpResponseMessage rejectedResponse = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "media.ts"),
            HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, rejectedResponse.StatusCode);
        ControlledFixtureStreamSnapshot rejected = await WaitForControlledSnapshotAsync(
            control,
            snapshot => snapshot.ExpectedRejectCount == 1);
        Assert.AreEqual(3L, rejected.LastExpectedRejectOrdinal);
        Assert.AreEqual(ControlledFixtureStreamMode.Holding, rejected.Mode);

        control.Restore();
        using HttpResponseMessage fourthResponse = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "media.ts"),
            HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, fourthResponse.StatusCode);
        await WaitForControlledSnapshotAsync(
            control,
            snapshot => snapshot.ActiveRequestOrdinal == 4);

        control.HoldSubsequentRequests();
        Task<HttpResponseMessage> disabledFallbackTask = client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "media.ts"),
            HttpCompletionOption.ResponseHeadersRead);
        await WaitForControlledSnapshotAsync(
            control,
            snapshot => snapshot.CurrentHeldRequestCount == 1);
        control.Disable();
        ControlledFixtureStreamSnapshot disabling = control.Snapshot;
        if (disabling.ActiveRequestOrdinal != 0)
        {
            Assert.IsFalse(disabledFallbackTask.IsCompleted);
            Assert.AreEqual(0, disabling.DisabledFallbackCount);
        }

        using HttpResponseMessage disabledFallbackResponse = await disabledFallbackTask;
        CollectionAssert.AreEqual(body, await disabledFallbackResponse.Content.ReadAsByteArrayAsync());
        await WaitForControlledSnapshotAsync(
            control,
            snapshot =>
                snapshot.Mode == ControlledFixtureStreamMode.Disabled &&
                snapshot.ExpectedAbortCount == 3 &&
                snapshot.DisabledFallbackCount == 1 &&
                snapshot.ActiveRequestOrdinal == 0);

        using HttpResponseMessage defaultResponse = await client.GetAsync("media.ts");
        CollectionAssert.AreEqual(body, await defaultResponse.Content.ReadAsByteArrayAsync());
        ControlledFixtureStreamSnapshot final = control.Snapshot;
        Assert.AreEqual(5L, final.LastAssignedRequestOrdinal);
        Assert.AreEqual(5L, final.LastDisabledFallbackOrdinal);
        Assert.AreEqual(1, final.PeakActiveRequestCount);
        Assert.AreEqual(1, final.PeakHeldRequestCount);
        Assert.AreEqual(0, final.OverlapViolationCount);
        Assert.AreEqual(0, final.ClientDetachCount);
        Assert.AreEqual(0, final.UnexpectedFailureCount);
        Assert.AreEqual(6, server.RequestCount);
        Assert.AreEqual(2, server.CompletedResponseCount);
        Assert.AreEqual(0, server.FailureCount);
        Assert.ThrowsExactly<InvalidOperationException>(control.Restore);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ControlledStreamCompletesActiveResponseAsCleanEofWithExactAccounting()
    {
        byte[] body = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        Dictionary<string, FixtureHttpResponse> routes = new(StringComparer.Ordinal)
        {
            ["/media.ts"] = new FixtureHttpResponse(
                StatusCodes.Status200OK,
                "video/mp2t",
                body,
                SupportsByteRanges: true),
        };

        await using LocalHttpFixtureServer server = await LocalHttpFixtureServer.StartAsync(routes);
        ControlledFixtureStreamControl control = server.EnableControlledStream(
            "/media.ts",
            new ControlledFixtureStreamOptions
            {
                WriteInterval = TimeSpan.FromMilliseconds(2),
                WriteSize = 4,
                MaximumRequestOrdinals = 3,
            });
        using HttpClient client = new()
        {
            BaseAddress = server.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };

        using HttpResponseMessage response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "media.ts"),
            HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await using Stream responseBody = await response.Content.ReadAsStreamAsync();
        byte[] prefix = new byte[body.Length + 4];
        await responseBody.ReadExactlyAsync(prefix);
        CollectionAssert.AreEqual(body.Concat(body[..4]).ToArray(), prefix);

        ControlledFixtureStreamSnapshot active = await WaitForControlledSnapshotAsync(
            control,
            snapshot => snapshot.ActiveRequestOrdinal == 1);
        Assert.IsFalse(control.TryCompleteActive(2));
        Assert.IsTrue(control.TryCompleteActive(active.ActiveRequestOrdinal));
        Assert.IsFalse(control.TryCompleteActive(active.ActiveRequestOrdinal));

        using var received = new MemoryStream();
        received.Write(prefix);
        await responseBody.CopyToAsync(received);
        byte[] eofProbe = new byte[1];
        Assert.AreEqual(0, await responseBody.ReadAsync(eofProbe));
        ControlledFixtureStreamSnapshot completed = await WaitForControlledSnapshotAsync(
            control,
            snapshot =>
                snapshot.ExpectedCompletionCount == 1 &&
                snapshot.ActiveRequestOrdinal == 0);

        Assert.IsGreaterThan(0, received.Length);
        Assert.AreEqual(ControlledFixtureStreamMode.Enabled, completed.Mode);
        Assert.AreEqual(1L, completed.LastAssignedRequestOrdinal);
        Assert.AreEqual(0L, completed.ActiveRequestOrdinal);
        Assert.AreEqual(0, completed.CurrentHeldRequestCount);
        Assert.AreEqual(0, completed.PeakHeldRequestCount);
        Assert.AreEqual(1, completed.PeakActiveRequestCount);
        Assert.AreEqual(1, completed.ExpectedCompletionCount);
        Assert.AreEqual(1L, completed.LastExpectedCompletionOrdinal);
        Assert.AreEqual(0, completed.ExpectedAbortCount);
        Assert.AreEqual(0L, completed.LastExpectedAbortOrdinal);
        Assert.IsFalse(control.TryAbortActive(1));
        Assert.AreEqual(0, completed.ClientDetachCount);
        Assert.AreEqual(0, completed.ExpectedRejectCount);
        Assert.AreEqual(0, completed.DisabledFallbackCount);
        Assert.AreEqual(0, completed.CapacityRejectCount);
        Assert.AreEqual(0, completed.OverlapViolationCount);
        Assert.AreEqual(0, completed.UnexpectedFailureCount);
        Assert.AreEqual(1, server.RequestCount);
        Assert.AreEqual(1, server.CompletedResponseCount);
        Assert.AreEqual(received.Length, server.CompletedBodyBytes);
        Assert.AreEqual(0, server.FailureCount);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ControlledStreamBoundsOrdinalsAndClassifiesHeldClientDetachWithoutOverlap()
    {
        byte[] body = Enumerable.Range(0, 8).Select(value => (byte)value).ToArray();
        LocalHttpFixtureServer server = await LocalHttpFixtureServer.StartAsync(
            new Dictionary<string, FixtureHttpResponse>(StringComparer.Ordinal)
            {
                ["/media.ts"] = new FixtureHttpResponse(200, "video/mp2t", body),
            });
        try
        {
            ControlledFixtureStreamControl control = server.EnableControlledStream(
                "/media.ts",
                new ControlledFixtureStreamOptions
                {
                    WriteInterval = TimeSpan.FromMilliseconds(2),
                    WriteSize = 4,
                    MaximumRequestOrdinals = 3,
                });
            using HttpClient client = new()
            {
                BaseAddress = server.BaseAddress,
                Timeout = TimeSpan.FromSeconds(10),
            };

            using HttpResponseMessage activeResponse = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "media.ts"),
                HttpCompletionOption.ResponseHeadersRead);
            await WaitForControlledSnapshotAsync(
                control,
                snapshot => snapshot.ActiveRequestOrdinal == 1);

            using CancellationTokenSource secondCancellation = new(TimeSpan.FromSeconds(5));
            using CancellationTokenSource thirdCancellation = new(TimeSpan.FromSeconds(5));
            Task<HttpResponseMessage> secondResponseTask = client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "media.ts"),
                HttpCompletionOption.ResponseHeadersRead,
                secondCancellation.Token);
            await WaitForControlledSnapshotAsync(
                control,
                snapshot => snapshot.CurrentHeldRequestCount == 1);
            Task<HttpResponseMessage> thirdResponseTask = client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "media.ts"),
                HttpCompletionOption.ResponseHeadersRead,
                thirdCancellation.Token);
            await WaitForControlledSnapshotAsync(
                control,
                snapshot => snapshot.CurrentHeldRequestCount == 2);

            using HttpResponseMessage capacityResponse = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "media.ts"),
                HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, capacityResponse.StatusCode);

            secondCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await secondResponseTask);
            await WaitForControlledSnapshotAsync(
                control,
                snapshot =>
                    snapshot.ClientDetachCount == 1 &&
                    snapshot.LastClientDetachOrdinal == 2);
            thirdCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await thirdResponseTask);
            ControlledFixtureStreamSnapshot detached = await WaitForControlledSnapshotAsync(
                control,
                snapshot =>
                    snapshot.ClientDetachCount == 2 &&
                    snapshot.CurrentHeldRequestCount == 0);
            Assert.AreEqual(3L, detached.LastAssignedRequestOrdinal);
            Assert.AreEqual(3L, detached.LastClientDetachOrdinal);
            Assert.AreEqual(1, detached.CapacityRejectCount);
            Assert.AreEqual(2, detached.PeakHeldRequestCount);
            Assert.AreEqual(1, detached.PeakActiveRequestCount);
            Assert.AreEqual(0, detached.OverlapViolationCount);

            Assert.IsFalse(control.TryAbortActive(2));
            Assert.AreEqual(0, control.Snapshot.ExpectedAbortCount);
            Task detachActiveResponse = Task.Run(activeResponse.Dispose);
            bool abortAccepted = control.TryAbortActive(1);
            await detachActiveResponse;
            int expectedAbortCount = abortAccepted ? 1 : 0;
            int expectedClientDetachCount = abortAccepted ? 2 : 3;
            ControlledFixtureStreamSnapshot completed = await WaitForControlledSnapshotAsync(
                control,
                snapshot =>
                    snapshot.ExpectedAbortCount + snapshot.ClientDetachCount == 3 &&
                    snapshot.ActiveRequestOrdinal == 0);
            Assert.AreEqual(expectedAbortCount, completed.ExpectedAbortCount);
            Assert.AreEqual(expectedClientDetachCount, completed.ClientDetachCount);
            Assert.IsFalse(control.TryAbortActive(1));
            Assert.AreEqual(expectedAbortCount, control.Snapshot.ExpectedAbortCount);
            Assert.AreEqual(0, completed.UnexpectedFailureCount);
            Assert.AreEqual(4, server.RequestCount);
            Assert.AreEqual(0, server.CompletedResponseCount);
            Assert.AreEqual(0, server.FailureCount);
        }
        finally
        {
            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
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

    private static async Task<ControlledFixtureStreamSnapshot> WaitForControlledSnapshotAsync(
        ControlledFixtureStreamControl control,
        Func<ControlledFixtureStreamSnapshot, bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (true)
        {
            ControlledFixtureStreamSnapshot snapshot = control.Snapshot;
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}
