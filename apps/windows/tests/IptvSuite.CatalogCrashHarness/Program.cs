using System.Reflection;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;

namespace IptvSuite.CatalogCrashHarness;

internal static class Program
{
    private const string EffectiveUri = "https://fixtures.invalid/catalog/final/list.m3u";

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 3)
        {
            return 2;
        }

        string databasePath = Path.GetFullPath(args[0]);
        string storePath = Path.GetFullPath(args[1]);
        string readyPath = Path.GetFullPath(args[2]);
        if (!Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]) ||
            !Path.IsPathFullyQualified(args[2]) || File.Exists(readyPath))
        {
            return 3;
        }

        Directory.CreateDirectory(storePath);
        var store = new DpapiCurrentUserSecretStore(storePath);
        ContentSource source = await CreateSourceAsync(store).ConfigureAwait(false);
        bool baseline = await InvokeLoaderAsync(
            store,
            new FixedTransport("#EXTM3U\n#EXTINF:-1 tvg-id=\"old\",Old channel\nhttps://fixtures.invalid/old.m3u8\n"),
            source,
            databasePath).ConfigureAwait(false);
        if (!baseline)
        {
            return 4;
        }

        const string committedPrefix =
            "#EXTM3U\n#EXTINF:-1 tvg-id=\"new\",New channel\nhttps://fixtures.invalid/new.m3u8\n";
        string replacement = committedPrefix +
            "#EXTINF:-1 tvg-id=\"never\",Never committed\nhttps://fixtures.invalid/never.m3u8\n";
        _ = await InvokeLoaderAsync(
            store,
            new BlockingTransport(replacement, Encoding.UTF8.GetByteCount(committedPrefix), readyPath),
            source,
            databasePath).ConfigureAwait(false);
        return 5;
    }

    private static async Task<ContentSource> CreateSourceAsync(ISecretStore store)
    {
        DomainResult<ValidatedSourceDraft> draft = await new SourceDraftProtectionService(store)
            .ProtectRemotePlaylistAsync(
                SourceId.Generate(),
                "Synthetic crash source",
                "https://fixtures.invalid/catalog/list.m3u").ConfigureAwait(false);
        if (!draft.IsSuccess)
        {
            throw new InvalidOperationException("Synthetic source protection failed.");
        }

        DateTimeOffset now = new(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);
        DomainResult<ContentSource> source = ContentSource.Create(
            draft.Value,
            ContentSourceStatus.Testing,
            now,
            now);
        return source.IsSuccess
            ? source.Value!
            : throw new InvalidOperationException("Synthetic source creation failed.");
    }

    private static async Task<bool> InvokeLoaderAsync(
        ISecretStore store,
        IStreamingHttpTransport transport,
        ContentSource source,
        string databasePath)
    {
        Assembly assembly = typeof(BoundedHttpTransport).Assembly;
        Type sinkType = assembly.GetType("IptvSuite.Infrastructure.SqliteRemoteM3uImportSink", true)!;
        object sink = Activator.CreateInstance(
            sinkType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [databasePath],
            null)!;
        Type loaderType = assembly.GetType("IptvSuite.Infrastructure.RemotePlaylistCatalogLoader", true)!;
        object loader = Activator.CreateInstance(
            loaderType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [store, transport, sink],
            null)!;
        object valueTask = loaderType.GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(loader, [source, CancellationToken.None])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task.ConfigureAwait(false);
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return (bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!;
    }

    private static HttpStreamingResult Response(Stream stream)
    {
        ConstructorInfo constructor = typeof(HttpStreamingResponseLease).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var lease = (HttpStreamingResponseLease)constructor.Invoke(
            [stream, new Uri(EffectiveUri), new EmptyOwner(), null, null]);
        return HttpStreamingResult.Success(200, lease);
    }

    private sealed class FixedTransport(string playlist) : IStreamingHttpTransport
    {
        public ValueTask<HttpStreamingResult> GetStreamAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Response(new MemoryStream(Encoding.UTF8.GetBytes(playlist), writable: false)));
    }

    private sealed class BlockingTransport(string playlist, int blockAfterBytes, string readyPath)
        : IStreamingHttpTransport
    {
        public ValueTask<HttpStreamingResult> GetStreamAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<HttpStreamingResult>(Response(
                new BlockingStream(Encoding.UTF8.GetBytes(playlist), blockAfterBytes, readyPath)));
    }

    private sealed class BlockingStream(byte[] payload, int blockAfterBytes, string readyPath) : Stream
    {
        private int _offset;
        private bool _signaled;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset >= blockAfterBytes)
            {
                if (!_signaled)
                {
                    await File.WriteAllBytesAsync(readyPath, "ready"u8.ToArray(), cancellationToken)
                        .ConfigureAwait(false);
                    _signaled = true;
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            int count = Math.Min(buffer.Length, Math.Min(1, blockAfterBytes - _offset));
            payload.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class EmptyOwner : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
