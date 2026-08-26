#requires -Version 5.1

[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",

    [string]$DotNetPath = "dotnet",

    [ValidateRange(2, 200)]
    [int]$SwitchCount = 25,

    [ValidateRange(0, 1440)]
    [int]$SoakMinutes = 0,

    [ValidateRange(0, 7)]
    [int]$NetworkInterruptionCount = 0,

    [ValidateRange(0, 1)]
    [int]$CancellationProbeCount = 0,

    [ValidateSet("M10", "M16Final")]
    [string]$AcceptanceProfile = "M10"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$isM16FinalAcceptance = $AcceptanceProfile -ceq "M16Final"
$maximumProbeEvidenceBytes = if ($isM16FinalAcceptance) { 131072 } else { 65536 }
$maximumResourceSampleCount = if ($isM16FinalAcceptance) { 290 } else { 128 }

if ($isM16FinalAcceptance) {
    if ($SwitchCount -ne 200 -or
        $SoakMinutes -ne 1440 -or
        $NetworkInterruptionCount -ne 7 -or
        $CancellationProbeCount -ne 0) {
        throw "The M16 final native acceptance profile requires exactly 200 switches, 1440 soak minutes, seven interruptions, and no inline cancellation probe."
    }
}
else {
    if ($SwitchCount -gt 100 -or $SoakMinutes -gt 480) {
        throw "The M10 native playback profile is outside its fixed switch or soak boundary."
    }
    if ($SoakMinutes -gt 0 -and $SwitchCount -ne 100) {
        throw "A native playback soak requires exactly 100 alternating switches."
    }
    if ($NetworkInterruptionCount -gt 0 -and $SwitchCount -ne 100) {
        throw "A native playback network interruption probe requires exactly 100 alternating switches."
    }
    if ($CancellationProbeCount -gt 0 -and
        ($SwitchCount -ne 100 -or $SoakMinutes -ne 0)) {
        throw "A native playback cancellation probe requires exactly 100 alternating switches and no soak."
    }
}

$activationInterop = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace IptvSuite.NativePlaybackSmoke
{
    [Flags]
    internal enum ActivateOptions : uint { NoErrorUi = 0x00000002 }

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);
    }

    public static class PackagedApplicationActivator
    {
        [StructLayout(LayoutKind.Explicit, Size = 8)]
        private struct PackageVersionNative
        {
            [FieldOffset(0)] internal ulong Value;
            [FieldOffset(0)] internal ushort Revision;
            [FieldOffset(2)] internal ushort Build;
            [FieldOffset(4)] internal ushort Minor;
            [FieldOffset(6)] internal ushort Major;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PackageIdNative
        {
            internal uint Reserved;
            internal uint ProcessorArchitecture;
            internal PackageVersionNative Version;
            [MarshalAs(UnmanagedType.LPWStr)] internal string Name;
            [MarshalAs(UnmanagedType.LPWStr)] internal string Publisher;
            [MarshalAs(UnmanagedType.LPWStr)] internal string ResourceId;
            [MarshalAs(UnmanagedType.LPWStr)] internal string PublisherId;
        }

        public static int Activate(string appUserModelId, string arguments)
        {
            Guid classId = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
            Guid interfaceId = new Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D");
            object manager;
            int creationResult = CoCreateInstance(ref classId, IntPtr.Zero, 4, ref interfaceId, out manager);
            if (creationResult < 0) throw new COMException("Packaged activation service creation failed.", creationResult);
            try
            {
                uint processId;
                int result = ((IApplicationActivationManager)manager).ActivateApplication(
                    appUserModelId, arguments, ActivateOptions.NoErrorUi, out processId);
                if (result < 0) throw new COMException("Packaged activation failed.", result);
                if (processId == 0 || processId > Int32.MaxValue) throw new InvalidOperationException("Invalid activation process identifier.");
                return (int)processId;
            }
            finally
            {
                if (Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager);
            }
        }

        public static string GetPackageFamilyName(
            string name,
            string publisher,
            ushort major,
            ushort minor,
            ushort build,
            ushort revision)
        {
            var id = new PackageIdNative
            {
                Reserved = 0,
                ProcessorArchitecture = 9,
                Version = new PackageVersionNative
                {
                    Major = major,
                    Minor = minor,
                    Build = build,
                    Revision = revision
                },
                Name = name,
                Publisher = publisher,
                ResourceId = null,
                PublisherId = null
            };
            uint length = 0;
            int result = PackageFamilyNameFromId(ref id, ref length, null);
            if (result != 122 || length < 18 || length > 65)
                throw new Win32Exception(result, "Package family name sizing failed.");
            var value = new StringBuilder(checked((int)length));
            result = PackageFamilyNameFromId(ref id, ref length, value);
            if (result != 0 || value.Length + 1 != length)
                throw new Win32Exception(result, "Package family name calculation failed.");
            return value.ToString();
        }

        public static bool RemoveExactEmptyDirectory(string path)
        {
            if (RemoveDirectory2(path, 1)) return true;
            int error = Marshal.GetLastWin32Error();
            if (error == 2 || error == 3) return false;
            throw new Win32Exception(error, "Exact empty package-data directory removal failed.");
        }

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int CoCreateInstance(
            [In] ref Guid classId,
            IntPtr outer,
            uint classContext,
            [In] ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object value);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int PackageFamilyNameFromId(
            ref PackageIdNative packageId,
            ref uint packageFamilyNameLength,
            [Out] StringBuilder packageFamilyName);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "RemoveDirectory2W",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveDirectory2(string path, uint flags);
    }
}
'@

$tlsServerSource = @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IptvSuite.NativePlaybackSmoke
{
    public sealed class TierARequestTrace
    {
        private readonly object sync = new object();

        public TierARequestTrace(int acceptOrdinal, long acceptedTimestamp)
        {
            AcceptOrdinal = acceptOrdinal;
            AcceptedTimestamp = acceptedTimestamp;
            Resource = "Unclassified";
            Method = "Pending";
            RangeShape = "Pending";
            Outcome = "InFlight";
        }

        public int AcceptOrdinal { get; private set; }
        public int RequestOrdinal { get; private set; }
        public string Resource { get; private set; }
        public string Method { get; private set; }
        public string RangeShape { get; private set; }
        public int StatusCode { get; private set; }
        public long BodyBytes { get; private set; }
        public long AcceptedTimestamp { get; private set; }
        public long TlsAuthenticatedTimestamp { get; private set; }
        public long RequestHeaderCompletedTimestamp { get; private set; }
        public long ResponseHeaderWrittenTimestamp { get; private set; }
        public long BodyWriteCompletedTimestamp { get; private set; }
        public long FlushCompletedTimestamp { get; private set; }
        public string Outcome { get; private set; }
        public long TerminalTimestamp { get; private set; }

        public void MarkTlsAuthenticated(long timestamp)
        {
            lock (sync) TlsAuthenticatedTimestamp = timestamp;
        }

        public void MarkRequestHeaderCompleted(long timestamp)
        {
            lock (sync) RequestHeaderCompletedTimestamp = timestamp;
        }

        public void MarkRequest(string resource, string method)
        {
            lock (sync)
            {
                Resource = resource;
                Method = method;
            }
        }

        public void MarkUnsupportedMethod()
        {
            lock (sync) Method = "Unsupported";
        }

        public void MarkRangeShape(string rangeShape)
        {
            lock (sync) RangeShape = rangeShape;
        }

        public void MarkRequestOrdinal(int requestOrdinal)
        {
            lock (sync) RequestOrdinal = requestOrdinal;
        }

        public void MarkResponseHeader(int statusCode, long timestamp)
        {
            lock (sync)
            {
                StatusCode = statusCode;
                ResponseHeaderWrittenTimestamp = timestamp;
            }
        }

        public void MarkCompleted(long bodyBytes, long bodyWriteCompletedTimestamp, long flushCompletedTimestamp)
        {
            lock (sync)
            {
                BodyBytes = bodyBytes;
                BodyWriteCompletedTimestamp = bodyWriteCompletedTimestamp;
                FlushCompletedTimestamp = flushCompletedTimestamp;
                Outcome = "Completed";
                TerminalTimestamp = flushCompletedTimestamp;
            }
        }

        public void MarkRejected(int statusCode, long timestamp)
        {
            lock (sync)
            {
                StatusCode = statusCode;
                ResponseHeaderWrittenTimestamp = timestamp;
                Outcome = "Rejected";
                TerminalTimestamp = timestamp;
            }
        }

        public void MarkTerminalFailure(string outcome, long timestamp)
        {
            lock (sync)
            {
                if (Outcome == "Completed" || Outcome == "Rejected") return;
                Outcome = outcome;
                TerminalTimestamp = timestamp;
            }
        }

        public TierARequestTrace Snapshot()
        {
            lock (sync)
            {
                var snapshot = new TierARequestTrace(AcceptOrdinal, AcceptedTimestamp);
                snapshot.RequestOrdinal = RequestOrdinal;
                snapshot.Resource = Resource;
                snapshot.Method = Method;
                snapshot.RangeShape = RangeShape;
                snapshot.StatusCode = StatusCode;
                snapshot.BodyBytes = BodyBytes;
                snapshot.TlsAuthenticatedTimestamp = TlsAuthenticatedTimestamp;
                snapshot.RequestHeaderCompletedTimestamp = RequestHeaderCompletedTimestamp;
                snapshot.ResponseHeaderWrittenTimestamp = ResponseHeaderWrittenTimestamp;
                snapshot.BodyWriteCompletedTimestamp = BodyWriteCompletedTimestamp;
                snapshot.FlushCompletedTimestamp = FlushCompletedTimestamp;
                snapshot.Outcome = Outcome;
                snapshot.TerminalTimestamp = TerminalTimestamp;
                return snapshot;
            }
        }
    }

    public sealed class TierARequestTraceSnapshot
    {
        public TierARequestTraceSnapshot(
            TierARequestTrace[] traces,
            int droppedCount,
            long firstDroppedAcceptedTimestamp)
        {
            Traces = traces;
            DroppedCount = droppedCount;
            FirstDroppedAcceptedTimestamp = firstDroppedAcceptedTimestamp;
        }

        public TierARequestTrace[] Traces { get; private set; }
        public int DroppedCount { get; private set; }
        public long FirstDroppedAcceptedTimestamp { get; private set; }
    }

    public sealed class TierANetworkRecoveryTrace
    {
        private readonly object sync = new object();

        public TierANetworkRecoveryTrace(
            int ordinal,
            int injectedRequestOrdinal,
            long injectedTimestamp)
        {
            Ordinal = ordinal;
            InjectedRequestOrdinal = injectedRequestOrdinal;
            InjectedTimestamp = injectedTimestamp;
        }

        public int Ordinal { get; private set; }
        public int InjectedRequestOrdinal { get; private set; }
        public long InjectedTimestamp { get; private set; }
        public int RecoveryRequestOrdinal { get; private set; }
        public long RecoveryTimestamp { get; private set; }

        public void MarkRecovery(int recoveryRequestOrdinal, long recoveryTimestamp)
        {
            lock (sync)
            {
                if (RecoveryRequestOrdinal != 0 || recoveryRequestOrdinal <= InjectedRequestOrdinal ||
                    recoveryTimestamp < InjectedTimestamp)
                {
                    throw new InvalidOperationException("A network recovery trace transition is inconsistent.");
                }

                RecoveryRequestOrdinal = recoveryRequestOrdinal;
                RecoveryTimestamp = recoveryTimestamp;
            }
        }

        public TierANetworkRecoveryTrace Snapshot()
        {
            lock (sync)
            {
                var snapshot = new TierANetworkRecoveryTrace(
                    Ordinal,
                    InjectedRequestOrdinal,
                    InjectedTimestamp);
                snapshot.RecoveryRequestOrdinal = RecoveryRequestOrdinal;
                snapshot.RecoveryTimestamp = RecoveryTimestamp;
                return snapshot;
            }
        }
    }

    public sealed class TierATlsServer : IDisposable
    {
        private const int RequestTraceCapacity = 32;
        private const int NetworkRecoveryTraceCapacity = 7;
        private readonly string root;
        private readonly X509Certificate2 certificate;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly ConcurrentDictionary<int, TcpClient> activeClients = new ConcurrentDictionary<int, TcpClient>();
        private readonly ConcurrentDictionary<int, Task> activeHandlers = new ConcurrentDictionary<int, Task>();
        private readonly object requestTraceSync = new object();
        private readonly List<TierARequestTrace> requestTraces = new List<TierARequestTrace>();
        private readonly object networkRecoveryTraceSync = new object();
        private readonly List<TierANetworkRecoveryTrace> networkRecoveryTraces =
            new List<TierANetworkRecoveryTrace>();
        private readonly Task acceptLoop;
        private int nextHandlerId;
        private int disposed;
        private int requestCount;
        private int failureCount;
        private int completedResponseCount;
        private int ioAbortCount;
        private int headRequestCount;
        private int rangeRequestCount;
        private int openEndedRangeCount;
        private int suffixRangeCount;
        private int boundedRangeCount;
        private int armedMediaFailure;
        private int pendingRecovery;
        private int injectedFailureCount;
        private int recoveryCount;
        private int lastInjectedRequestOrdinal;
        private int lastRecoveryRequestOrdinal;
        private long completedBodyBytes;
        private int requestTraceDroppedCount;
        private long firstDroppedRequestAcceptedTimestamp;

        public TierATlsServer(string root, X509Certificate2 certificate)
        {
            this.root = Path.GetFullPath(root);
            this.certificate = certificate;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(8);
            acceptLoop = Task.Run((Func<Task>)AcceptLoopAsync);
        }

        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public int RequestCount { get { return Volatile.Read(ref requestCount); } }
        public int FailureCount { get { return Volatile.Read(ref failureCount); } }
        public int CompletedResponseCount { get { return Volatile.Read(ref completedResponseCount); } }
        public int IoAbortCount { get { return Volatile.Read(ref ioAbortCount); } }
        public int HeadRequestCount { get { return Volatile.Read(ref headRequestCount); } }
        public int RangeRequestCount { get { return Volatile.Read(ref rangeRequestCount); } }
        public int OpenEndedRangeCount { get { return Volatile.Read(ref openEndedRangeCount); } }
        public int SuffixRangeCount { get { return Volatile.Read(ref suffixRangeCount); } }
        public int BoundedRangeCount { get { return Volatile.Read(ref boundedRangeCount); } }
        public int InjectedFailureCount { get { return Volatile.Read(ref injectedFailureCount); } }
        public int RecoveryCount { get { return Volatile.Read(ref recoveryCount); } }
        public int LastInjectedRequestOrdinal { get { return Volatile.Read(ref lastInjectedRequestOrdinal); } }
        public int LastRecoveryRequestOrdinal { get { return Volatile.Read(ref lastRecoveryRequestOrdinal); } }
        public long CompletedBodyBytes { get { return Interlocked.Read(ref completedBodyBytes); } }
        public TierARequestTraceSnapshot GetRequestTraceSnapshot()
        {
            lock (requestTraceSync)
            {
                var traces = new TierARequestTrace[requestTraces.Count];
                for (int index = 0; index < requestTraces.Count; index++)
                    traces[index] = requestTraces[index].Snapshot();
                return new TierARequestTraceSnapshot(
                    traces,
                    requestTraceDroppedCount,
                    firstDroppedRequestAcceptedTimestamp);
            }
        }
        public TierANetworkRecoveryTrace[] GetNetworkRecoveryTraceSnapshot()
        {
            lock (networkRecoveryTraceSync)
            {
                var traces = new TierANetworkRecoveryTrace[networkRecoveryTraces.Count];
                for (int index = 0; index < networkRecoveryTraces.Count; index++)
                    traces[index] = networkRecoveryTraces[index].Snapshot();
                return traces;
            }
        }

        public void ArmNextMediaRequestFailure()
        {
            if (Volatile.Read(ref pendingRecovery) != 0 ||
                Interlocked.CompareExchange(ref armedMediaFailure, 1, 0) != 0)
            {
                throw new InvalidOperationException("A media fault is already pending.");
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    long acceptedTimestamp = Stopwatch.GetTimestamp();
                    int handlerId = Interlocked.Increment(ref nextHandlerId);
                    TierARequestTrace requestTrace = ReserveRequestTrace(handlerId, acceptedTimestamp);
                    if (!activeClients.TryAdd(handlerId, client))
                    {
                        if (requestTrace != null)
                            requestTrace.MarkTerminalFailure("TransportFailure", Stopwatch.GetTimestamp());
                        client.Dispose();
                        throw new InvalidOperationException("A loopback handler identity collided.");
                    }
                    Task handler = Task.Run(() => HandleTrackedAsync(handlerId, client, requestTrace));
                    if (!activeHandlers.TryAdd(handlerId, handler))
                    {
                        if (requestTrace != null)
                            requestTrace.MarkTerminalFailure("TransportFailure", Stopwatch.GetTimestamp());
                        TcpClient trackedClient;
                        activeClients.TryRemove(handlerId, out trackedClient);
                        client.Dispose();
                        throw new InvalidOperationException("A loopback handler task could not be tracked.");
                    }
                    Task continuation = handler.ContinueWith(
                        completed =>
                        {
                            Task ignored;
                            activeHandlers.TryRemove(handlerId, out ignored);
                            if (completed.IsFaulted) Interlocked.Increment(ref failureCount);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    GC.KeepAlive(continuation);
                }
                catch (ObjectDisposedException) { if (cancellation.IsCancellationRequested) return; throw; }
                catch (SocketException) { if (cancellation.IsCancellationRequested) return; throw; }
                catch
                {
                    if (cancellation.IsCancellationRequested) return;
                    Interlocked.Increment(ref failureCount);
                }
            }
        }

        private TierARequestTrace ReserveRequestTrace(int acceptOrdinal, long acceptedTimestamp)
        {
            lock (requestTraceSync)
            {
                if (requestTraces.Count >= RequestTraceCapacity)
                {
                    requestTraceDroppedCount++;
                    if (firstDroppedRequestAcceptedTimestamp == 0)
                        firstDroppedRequestAcceptedTimestamp = acceptedTimestamp;
                    return null;
                }

                var trace = new TierARequestTrace(acceptOrdinal, acceptedTimestamp);
                requestTraces.Add(trace);
                return trace;
            }
        }

        private async Task HandleTrackedAsync(
            int handlerId,
            TcpClient client,
            TierARequestTrace requestTrace)
        {
            try
            {
                await HandleAsync(client, requestTrace).ConfigureAwait(false);
            }
            finally
            {
                TcpClient trackedClient;
                if (activeClients.TryRemove(handlerId, out trackedClient)) trackedClient.Dispose();
            }
        }

        private async Task HandleAsync(TcpClient client, TierARequestTrace requestTrace)
        {
            using (client)
            using (var ssl = new SslStream(client.GetStream(), false))
            {
                try
                {
                    await ssl.AuthenticateAsServerAsync(
                        certificate,
                        false,
                        SslProtocols.Tls12,
                        false).ConfigureAwait(false);
                    long tlsAuthenticatedTimestamp = Stopwatch.GetTimestamp();
                    if (requestTrace != null)
                        requestTrace.MarkTlsAuthenticated(tlsAuthenticatedTimestamp);
                    byte[] headerBuffer = new byte[16384];
                    int length = 0;
                    while (length < headerBuffer.Length)
                    {
                        int read = await ssl.ReadAsync(headerBuffer, length, headerBuffer.Length - length).ConfigureAwait(false);
                        if (read == 0)
                        {
                            if (requestTrace != null)
                                requestTrace.MarkTerminalFailure("IoAbort", Stopwatch.GetTimestamp());
                            return;
                        }
                        length += read;
                        if (length >= 4 && FindHeaderEnd(headerBuffer, length) >= 0) break;
                    }
                    if (FindHeaderEnd(headerBuffer, length) < 0)
                    {
                        await WriteRejectedStatusAsync(ssl, 431, requestTrace).ConfigureAwait(false);
                        return;
                    }
                    long requestHeaderCompletedTimestamp = Stopwatch.GetTimestamp();
                    if (requestTrace != null)
                        requestTrace.MarkRequestHeaderCompleted(requestHeaderCompletedTimestamp);

                    string header = Encoding.ASCII.GetString(headerBuffer, 0, length);
                    string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] request = lines[0].Split(' ');
                    if (request.Length != 3 || (request[0] != "GET" && request[0] != "HEAD"))
                    {
                        if (requestTrace != null) requestTrace.MarkUnsupportedMethod();
                        await WriteRejectedStatusAsync(ssl, 405, requestTrace).ConfigureAwait(false);
                        return;
                    }
                    string traceMethod = request[0] == "GET" ? "Get" : "Head";
                    string fileName;
                    string contentType;
                    if (!TryMap(request[1], out fileName, out contentType))
                    {
                        if (requestTrace != null) requestTrace.MarkRequest("Unclassified", traceMethod);
                        await WriteRejectedStatusAsync(ssl, 404, requestTrace).ConfigureAwait(false);
                        return;
                    }
                    if (requestTrace != null)
                        requestTrace.MarkRequest(GetTraceResource(fileName), traceMethod);

                    string filePath = Path.GetFullPath(Path.Combine(root, fileName));
                    if (!filePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
                    {
                        await WriteRejectedStatusAsync(ssl, 404, requestTrace).ConfigureAwait(false);
                        return;
                    }

                    long total = new FileInfo(filePath).Length;
                    long start = 0;
                    long end = total - 1;
                    bool partial = false;
                    int rangeShape = 0;
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            string range = line.Substring(13);
                            string[] bounds = range.Split('-');
                            if (range.IndexOf(',') >= 0 || bounds.Length != 2)
                            {
                                if (requestTrace != null) requestTrace.MarkRangeShape("Invalid");
                                await WriteRejectedStatusAsync(ssl, 416, requestTrace).ConfigureAwait(false);
                                return;
                            }
                            if (bounds[0].Length == 0)
                            {
                                rangeShape = 2;
                                long suffixLength;
                                if (!Int64.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out suffixLength) || suffixLength <= 0)
                                {
                                    if (requestTrace != null) requestTrace.MarkRangeShape("Invalid");
                                    await WriteRejectedStatusAsync(ssl, 416, requestTrace).ConfigureAwait(false);
                                    return;
                                }
                                start = Math.Max(0, total - suffixLength);
                                end = total - 1;
                            }
                            else
                            {
                                rangeShape = bounds[1].Length == 0 ? 1 : 3;
                                if (!Int64.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0 || start >= total)
                                {
                                    if (requestTrace != null) requestTrace.MarkRangeShape("Invalid");
                                    await WriteRejectedStatusAsync(ssl, 416, requestTrace).ConfigureAwait(false);
                                    return;
                                }
                                if (bounds[1].Length > 0 && (!Int64.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start))
                                {
                                    if (requestTrace != null) requestTrace.MarkRangeShape("Invalid");
                                    await WriteRejectedStatusAsync(ssl, 416, requestTrace).ConfigureAwait(false);
                                    return;
                                }
                                end = Math.Min(end, total - 1);
                            }
                            partial = true;
                        }
                    }
                    if (requestTrace != null)
                        requestTrace.MarkRangeShape(GetTraceRangeShape(rangeShape));

                    long contentLength = end - start + 1;
                    int requestOrdinal = Interlocked.Increment(ref requestCount);
                    if (requestTrace != null) requestTrace.MarkRequestOrdinal(requestOrdinal);
                    if (Interlocked.Exchange(ref armedMediaFailure, 0) == 1)
                    {
                        int injectedOrdinal = Volatile.Read(ref injectedFailureCount) + 1;
                        long injectedTimestamp = Stopwatch.GetTimestamp();
                        RecordInjectedFailure(injectedOrdinal, requestOrdinal, injectedTimestamp);
                        Volatile.Write(ref lastInjectedRequestOrdinal, requestOrdinal);
                        Interlocked.Exchange(ref pendingRecovery, 1);
                        Interlocked.Increment(ref injectedFailureCount);
                        await WriteRejectedStatusAsync(ssl, 503, requestTrace).ConfigureAwait(false);
                        return;
                    }
                    if (request[0] == "HEAD") Interlocked.Increment(ref headRequestCount);
                    if (partial)
                    {
                        Interlocked.Increment(ref rangeRequestCount);
                        if (rangeShape == 1) Interlocked.Increment(ref openEndedRangeCount);
                        else if (rangeShape == 2) Interlocked.Increment(ref suffixRangeCount);
                        else if (rangeShape == 3) Interlocked.Increment(ref boundedRangeCount);
                    }
                    var response = new StringBuilder();
                    response.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                    response.Append("Content-Type: ").Append(contentType).Append("\r\n");
                    response.Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                    response.Append("Accept-Ranges: bytes\r\n");
                    if (partial) response.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(total).Append("\r\n");
                    response.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");
                    byte[] responseBytes = Encoding.ASCII.GetBytes(response.ToString());
                    await ssl.WriteAsync(responseBytes, 0, responseBytes.Length).ConfigureAwait(false);
                    long responseHeaderWrittenTimestamp = Stopwatch.GetTimestamp();
                    if (requestTrace != null)
                        requestTrace.MarkResponseHeader(partial ? 206 : 200, responseHeaderWrittenTimestamp);
                    if (request[0] == "GET")
                    {
                        using (var file = File.OpenRead(filePath))
                        {
                            file.Position = start;
                            byte[] buffer = new byte[65536];
                            long remaining = contentLength;
                            while (remaining > 0)
                            {
                                int read = await file.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining)).ConfigureAwait(false);
                                if (read == 0) throw new EndOfStreamException();
                                await ssl.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                                remaining -= read;
                            }
                        }
                    }
                    long bodyWriteCompletedTimestamp = Stopwatch.GetTimestamp();
                    await ssl.FlushAsync().ConfigureAwait(false);
                    long flushCompletedTimestamp = Stopwatch.GetTimestamp();
                    if (request[0] == "GET") Interlocked.Add(ref completedBodyBytes, contentLength);
                    Interlocked.Increment(ref completedResponseCount);
                    if (requestTrace != null)
                        requestTrace.MarkCompleted(
                            request[0] == "GET" ? contentLength : 0,
                            bodyWriteCompletedTimestamp,
                            flushCompletedTimestamp);
                    int injectedRequestOrdinal = Volatile.Read(ref lastInjectedRequestOrdinal);
                    if (requestOrdinal > injectedRequestOrdinal &&
                        Interlocked.CompareExchange(ref pendingRecovery, 0, 1) == 1)
                    {
                        int recoveryOrdinal = Volatile.Read(ref recoveryCount) + 1;
                        long recoveryTimestamp = Stopwatch.GetTimestamp();
                        RecordRecovery(recoveryOrdinal, requestOrdinal, recoveryTimestamp);
                        Volatile.Write(ref lastRecoveryRequestOrdinal, requestOrdinal);
                        Interlocked.Increment(ref recoveryCount);
                    }
                }
                catch (IOException)
                {
                    if (requestTrace != null)
                        requestTrace.MarkTerminalFailure("IoAbort", Stopwatch.GetTimestamp());
                    if (!cancellation.IsCancellationRequested) Interlocked.Increment(ref ioAbortCount);
                }
                catch (AuthenticationException)
                {
                    if (requestTrace != null)
                        requestTrace.MarkTerminalFailure("AuthFailure", Stopwatch.GetTimestamp());
                    if (!cancellation.IsCancellationRequested) Interlocked.Increment(ref failureCount);
                }
                catch (ObjectDisposedException)
                {
                    if (requestTrace != null)
                        requestTrace.MarkTerminalFailure("TransportFailure", Stopwatch.GetTimestamp());
                    if (!cancellation.IsCancellationRequested) Interlocked.Increment(ref failureCount);
                }
                catch
                {
                    if (requestTrace != null)
                        requestTrace.MarkTerminalFailure("TransportFailure", Stopwatch.GetTimestamp());
                    if (!cancellation.IsCancellationRequested) Interlocked.Increment(ref failureCount);
                }
            }
        }

        private void RecordInjectedFailure(
            int ordinal,
            int requestOrdinal,
            long timestamp)
        {
            lock (networkRecoveryTraceSync)
            {
                if (ordinal != networkRecoveryTraces.Count + 1 ||
                    networkRecoveryTraces.Count >= NetworkRecoveryTraceCapacity)
                {
                    throw new InvalidOperationException("The bounded network recovery trace is inconsistent.");
                }

                networkRecoveryTraces.Add(new TierANetworkRecoveryTrace(
                    ordinal,
                    requestOrdinal,
                    timestamp));
            }
        }

        private void RecordRecovery(
            int ordinal,
            int requestOrdinal,
            long timestamp)
        {
            lock (networkRecoveryTraceSync)
            {
                if (ordinal < 1 || ordinal > networkRecoveryTraces.Count)
                {
                    throw new InvalidOperationException("The bounded network recovery trace is incomplete.");
                }

                networkRecoveryTraces[ordinal - 1].MarkRecovery(requestOrdinal, timestamp);
            }
        }

        private static string GetTraceResource(string fileName)
        {
            switch (fileName)
            {
                case "direct-h264-aac.ts": return "Direct";
                case "hls.m3u8": return "Playlist";
                case "hls-000.ts": return "Segment0";
                case "hls-001.ts": return "Segment1";
                case "hls-002.ts": return "Segment2";
                case "hls-003.ts": return "Segment3";
                default: return "Unclassified";
            }
        }

        private static string GetTraceRangeShape(int rangeShape)
        {
            return rangeShape == 1
                ? "OpenEnded"
                : rangeShape == 2
                    ? "Suffix"
                    : rangeShape == 3
                        ? "Bounded"
                        : "None";
        }

        private static bool TryMap(string path, out string fileName, out string contentType)
        {
            contentType = "video/mp2t";
            switch (path)
            {
                case "/direct-h264-aac.ts": fileName = "direct-h264-aac.ts"; return true;
                case "/hls.m3u8": fileName = "hls.m3u8"; contentType = "application/vnd.apple.mpegurl"; return true;
                case "/hls-000.ts": fileName = "hls-000.ts"; return true;
                case "/hls-001.ts": fileName = "hls-001.ts"; return true;
                case "/hls-002.ts": fileName = "hls-002.ts"; return true;
                case "/hls-003.ts": fileName = "hls-003.ts"; return true;
                default: fileName = null; return false;
            }
        }

        private static int FindHeaderEnd(byte[] value, int length)
        {
            for (int i = 3; i < length; i++) if (value[i - 3] == 13 && value[i - 2] == 10 && value[i - 1] == 13 && value[i] == 10) return i - 3;
            return -1;
        }

        private static async Task WriteStatusAsync(Stream stream, int status)
        {
            byte[] value = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Rejected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(value, 0, value.Length).ConfigureAwait(false);
        }

        private static async Task WriteRejectedStatusAsync(
            Stream stream,
            int status,
            TierARequestTrace requestTrace)
        {
            await WriteStatusAsync(stream, status).ConfigureAwait(false);
            if (requestTrace != null)
                requestTrace.MarkRejected(status, Stopwatch.GetTimestamp());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            cancellation.Cancel();
            listener.Stop();
            Exception shutdownFailure = null;
            try
            {
                if (!acceptLoop.Wait(TimeSpan.FromSeconds(5)))
                    shutdownFailure = new InvalidOperationException("The loopback accept task did not stop.");
            }
            catch (AggregateException exception)
            {
                shutdownFailure = exception.Flatten();
            }

            try
            {
                foreach (TcpClient client in activeClients.Values) client.Dispose();
                Task[] handlers = new List<Task>(activeHandlers.Values).ToArray();
                if (handlers.Length > 0 && !Task.WaitAll(handlers, TimeSpan.FromSeconds(5)))
                    shutdownFailure = shutdownFailure ??
                        new InvalidOperationException("The loopback handler tasks did not drain.");

                foreach (KeyValuePair<int, Task> pair in activeHandlers)
                {
                    if (!pair.Value.IsCompleted) continue;
                    Task ignored;
                    activeHandlers.TryRemove(pair.Key, out ignored);
                }

                if (!activeClients.IsEmpty || !activeHandlers.IsEmpty)
                    shutdownFailure = shutdownFailure ??
                        new InvalidOperationException("The loopback handler registry did not drain.");
            }
            catch (AggregateException exception)
            {
                shutdownFailure = shutdownFailure ?? exception.Flatten();
            }
            finally
            {
                cancellation.Dispose();
            }

            if (shutdownFailure != null) throw shutdownFailure;
        }
    }
}
'@

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.NativePlaybackCompatibilitySpike\IptvSuite.NativePlaybackCompatibilitySpike.csproj"
$manifestPath = Join-Path (Split-Path -Parent $projectPath) "Package.appxmanifest"
$lockFilePath = Join-Path (Split-Path -Parent $projectPath) "packages.lock.json"
$inventorySpecificationPath = Join-Path (Split-Path -Parent $projectPath) "package-inventory.json"
$inventoryValidatorPath = Join-Path $repositoryRoot "eng\Test-WindowsNativePlaybackPackageInventory.ps1"
$fixtureRoot = Join-Path $repositoryRoot "apps\windows\tests\fixtures\playback\tier-a"
$fixtureManifestPath = Join-Path $fixtureRoot "fixture-manifest.json"
$artifactRoot = Join-Path $repositoryRoot ".artifacts\native-playback-smoke"
$packagesRoot = Join-Path $artifactRoot "packages"
$localPackagesRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "Packages"
$runId = [Guid]::NewGuid().ToString("N")
$packageOutput = Join-Path $packagesRoot $runId
$signingCertificatePath = Join-Path $artifactRoot "$runId-signing.cer"
$tlsCertificatePath = Join-Path $artifactRoot "$runId-tls.cer"
$evidencePath = Join-Path $artifactRoot "last-success.json"
$failureEvidencePath = Join-Path $artifactRoot "last-failure.json"
$packageInventoryEvidencePath = Join-Path $artifactRoot "package-inventory.json"
$expectedControllerPath = Join-Path $repositoryRoot "eng\Invoke-WindowsNativePlaybackSmoke.ps1"
$expectedName = "NativePlaybackCompatibilitySpike.Local.a47d1387"
$expectedPublisher = "CN=Native Playback Compatibility Spike Local Test"
$expectedApplicationId = "App"
$expectedVersion = "0.0.1.0"
$expectedPackageFamilyName = "NativePlaybackCompatibilitySpike.Local.a47d1387_6cjqrm2wkajhe"
$expectedRuntimeDependencyName = "Microsoft.WindowsAppRuntime.2"
$expectedRuntimeDependencyPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
$expectedRuntimeDependencyPublisherId = "8wekyb3d8bbwe"
$expectedRuntimeDependencyVersion = "2.4.0.0"
$expectedRuntimeDependencyArchitectures = @("X64", "X86")
$expectedRuntimeNuGetVersion = "2.4.0"
$projectAssetsPath = Join-Path (Split-Path -Parent $projectPath) "obj\project.assets.json"
$depsFilePath = Join-Path `
    (Split-Path -Parent $projectPath) `
    "bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\IptvSuite.NativePlaybackCompatibilitySpike.deps.json"
$h264DecoderClass = "Registry::HKEY_CLASSES_ROOT\CLSID\{62CE7E72-4C71-4D20-B15D-452831A87D9D}\InprocServer32"
$aacDecoderClass = "Registry::HKEY_CLASSES_ROOT\CLSID\{32D186A7-218F-4C75-8876-DD77273A8999}\InprocServer32"
$signingCertificate = $null
$tlsCertificate = $null
$tlsServer = $null
$installedPackage = $null
$installedPackageFullName = $null
$packageFamilyName = $null
$packageAppDataPath = $null
$packageEvidenceRoot = $null
$packageEvidencePath = $null
$installAttempted = $false
$activationAttempted = $false
$launchedProcess = $null
$environmentBackup = @{}
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$successCandidate = $null
$primaryFailure = $null
$primaryFailureStage = $null
$primaryFailureCode = $null
$failureStage = "Initialization"
$failureCode = "UnexpectedFailure"
$repositoryHead = $null
$controllerScriptSha256 = $null
$fixtureManifestSha256 = $null
$harnessAssemblySha256 = $null
$packageSha256 = $null
$runtimeDependencyPackageSha256 = $null
$runtimePackagesBefore = $null
$runtimeDependencyCleanupDiagnostic = "NotStarted"
$probeEnvelopeSchemaVersion = 0
$probeRunIdBound = $false
$resolvedWindowsAppRuntimeName = $null
$resolvedWindowsAppRuntimeVersion = $null
$resolvedWindowsAppRuntimeArchitecture = $null
$resolvedWindowsAppRuntimePublisherId = $null
$resolvedWindowsAppRuntimeIsFramework = $false
$actualSdk = $null
$fixtureCorpusVerified = $false
$processExitedWithoutForce = $false
$forcedProcessTerminationUsed = $false
$processCleanupPassed = $false
$tlsServerDisposed = $false
$packageRemoved = $false
$packageAppDataRemoved = $false
$packageAppDataEmptyRootCleanupUsed = $false
$runtimePackageBaselinePreserved = $false
$runtimePackageGraphDisposition = $null
$runtimePackageSharedAdditionCount = -1
$environmentRestored = $false
$signingCertificateRemoved = $false
$tlsCertificateRemoved = $false
$exportedCertificateFilesRemoved = $false
$runOutputRemoved = $false
$tlsRequestCount = 0
$tlsFailureCount = 0
$tlsCompletedResponseCount = 0
$tlsIoAbortCount = 0
$tlsHeadRequestCount = 0
$tlsRangeRequestCount = 0
$tlsOpenEndedRangeCount = 0
$tlsSuffixRangeCount = 0
$tlsBoundedRangeCount = 0
$tlsInjectedFailureCount = 0
$tlsRecoveryCount = 0
$tlsLastInjectedRequestOrdinal = 0
$tlsLastRecoveryRequestOrdinal = 0
$tlsCompletedBodyBytes = 0L
$firstHlsTransportAttributionObserved = $false
$firstHlsTraceRequestCount = 0
$firstHlsTracePlaylistResponseCount = 0
$firstHlsTraceSegmentResponseCount = 0
$firstHlsTraceBodyBytes = 0L
$firstHlsTraceResponsesBeforeSourceOpen = 0
$firstHlsTraceResponsesBeforeMediaOpened = 0
$firstHlsStartupToFirstAcceptMilliseconds = 0.0
$firstHlsStartupToFirstHeaderMilliseconds = 0.0
$firstHlsMaximumTlsAuthenticationMilliseconds = 0.0
$firstHlsTotalTlsAuthenticationMilliseconds = 0.0
$firstHlsFirstHeaderToLastFlushMilliseconds = 0.0
$firstHlsLastFlushToSourceOpenMilliseconds = 0.0
$firstHlsLastFlushToMediaOpenedMilliseconds = 0.0
$requestTraceDroppedCount = 0
$firstDroppedRequestAcceptedTimestamp = 0L
$msBuildEnvironment = @{
    AppxBundle = "Never"
    AppxPackageDir = "$packageOutput\"
    AppxPackageSigningEnabled = "true"
    AppxSymbolPackageEnabled = "false"
    DebugSymbols = "false"
    DebugType = "None"
    GenerateAppxPackageOnBuild = "true"
    UapAppxPackageBuildMode = "SideloadOnly"
}

function Set-FailurePoint {
    param(
        [Parameter(Mandatory)]
        [string]$Stage,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $script:failureStage = $Stage
    $script:failureCode = $Code
}

function Assert-NoReparsePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $current = [System.IO.DirectoryInfo]::new([System.IO.Path]::GetFullPath($Path))
    while ($null -ne $current) {
        if ($current.Exists -and
            ($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A native playback path contains a reparse point."
        }

        $current = $current.Parent
    }
}

function Assert-RegularDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "A required native playback directory is unavailable."
    }

    Assert-NoReparsePath -Path $Path
    $attributes = [System.IO.File]::GetAttributes([System.IO.Path]::GetFullPath($Path))
    if (($attributes -band [System.IO.FileAttributes]::Directory) -eq 0 -or
        ($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "A required native playback directory is unsafe."
    }
}

function Assert-RegularFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "A required native playback file is unavailable."
    }

    $attributes = [System.IO.File]::GetAttributes([System.IO.Path]::GetFullPath($Path))
    if (($attributes -band ([System.IO.FileAttributes]::Directory -bor [System.IO.FileAttributes]::ReparsePoint)) -ne 0) {
        throw "A required native playback file is unsafe."
    }
}

function New-RegularDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Assert-NoReparsePath -Path $Path
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        throw "A native playback directory path is occupied by a file."
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        New-Item -ItemType Directory -Path $Path -ErrorAction Stop | Out-Null
    }

    Assert-RegularDirectory -Path $Path
}

function Assert-ExactChildPath {
    param(
        [Parameter(Mandatory)]
        [string]$Parent,

        [Parameter(Mandatory)]
        [string]$Child
    )

    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $fullChild = [System.IO.Path]::GetFullPath($Child).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $actualParent = [System.IO.Directory]::GetParent($fullChild)
    if ($null -eq $actualParent -or
        -not $actualParent.FullName.Equals($fullParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "A native playback cleanup path escaped its exact parent."
    }
}

function Remove-ExactOwnedFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ExpectedParent
    )

    Assert-ExactChildPath -Parent $ExpectedParent -Child $Path
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    Assert-RegularFile -Path $Path
    Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $Path) {
        throw "An exact native playback file remains after cleanup."
    }
}

function Remove-ExactOwnedTree {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ExpectedParent
    )

    Assert-ExactChildPath -Parent $ExpectedParent -Child $Path
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    Assert-RegularDirectory -Path $Path
    $pending = [System.Collections.Generic.Queue[System.IO.DirectoryInfo]]::new()
    $pending.Enqueue([System.IO.DirectoryInfo]::new([System.IO.Path]::GetFullPath($Path)))
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($entry in $directory.GetFileSystemInfos()) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "A native playback cleanup tree contains a reparse point."
            }

            if ($entry -is [System.IO.DirectoryInfo]) {
                $pending.Enqueue($entry)
            }
        }
    }

    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $Path) {
        throw "An exact native playback cleanup tree remains."
    }
}

function Get-RepositoryStatus {
    $status = @(& git -C $script:repositoryRoot status --porcelain=v1 --untracked-files=all 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "The repository state is unavailable."
    }

    return @($status)
}

function Get-RepositoryHead {
    $head = @(& git -C $script:repositoryRoot rev-parse --verify HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $head[0] -notmatch '\A[0-9a-fA-F]{40}\z') {
        throw "The repository HEAD is unavailable."
    }

    return $head[0].ToLowerInvariant()
}

function Get-RegularFileSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Assert-RegularFile -Path $Path
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
    if ($hash -notmatch '\A[0-9a-f]{64}\z') {
        throw "A native playback file hash is invalid."
    }

    return $hash
}

function Get-PackageEntrySha256 {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$EntryName
    )

    Assert-RegularFile -Path $PackagePath
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entries = @($archive.Entries | Where-Object {
            $_.FullName.Equals($EntryName, [System.StringComparison]::OrdinalIgnoreCase)
        })
        if ($entries.Count -ne 1) {
            throw "The native playback package harness assembly is ambiguous."
        }

        $stream = $entries[0].Open()
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $bytes = $sha256.ComputeHash($stream)
            return [System.BitConverter]::ToString($bytes).Replace("-", "").ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-PackageManifest {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    Assert-RegularFile -Path $PackagePath
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entries = @($archive.Entries | Where-Object {
            $_.FullName.Equals("AppxManifest.xml", [System.StringComparison]::OrdinalIgnoreCase)
        })
        if ($entries.Count -ne 1 -or $entries[0].Length -le 0 -or $entries[0].Length -gt 4194304) {
            throw "The native playback package manifest is ambiguous or outside its size bound."
        }

        $stream = $entries[0].Open()
        $reader = [System.IO.StreamReader]::new(
            $stream,
            [System.Text.UTF8Encoding]::new($false, $true),
            $true,
            4096,
            $false)
        try {
            [xml]$manifest = $reader.ReadToEnd()
            return ,$manifest
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-BuiltNativePackageManifest {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    $manifest = Get-PackageManifest -PackagePath $PackagePath
    $identities = @($manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Identity']"))
    $applications = @($manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']"))
    $dependencies = @($manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']"))
    if ($identities.Count -ne 1 -or
        $identities[0].GetAttribute("Name") -ne $script:expectedName -or
        $identities[0].GetAttribute("Publisher") -cne $script:expectedPublisher -or
        $identities[0].GetAttribute("Version") -ne $script:expectedVersion -or
        $identities[0].GetAttribute("ProcessorArchitecture") -ne "x64" -or
        $applications.Count -ne 1 -or
        $applications[0].GetAttribute("Id") -ne $script:expectedApplicationId -or
        $dependencies.Count -ne 1 -or
        $dependencies[0].GetAttribute("Name") -ne $script:expectedRuntimeDependencyName -or
        $dependencies[0].GetAttribute("MinVersion") -ne $script:expectedRuntimeDependencyVersion -or
        $dependencies[0].GetAttribute("Publisher") -cne $script:expectedRuntimeDependencyPublisher) {
        throw "The built native playback package manifest is outside policy."
    }
}

function Assert-RuntimeDependencyPackageManifest {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    $manifest = Get-PackageManifest -PackagePath $PackagePath
    $identities = @($manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Identity']"))
    $frameworks = @($manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='Framework']"))
    if ($identities.Count -ne 1 -or
        $identities[0].GetAttribute("Name") -ne $script:expectedRuntimeDependencyName -or
        $identities[0].GetAttribute("Publisher") -cne $script:expectedRuntimeDependencyPublisher -or
        $identities[0].GetAttribute("Version") -ne $script:expectedRuntimeDependencyVersion -or
        $identities[0].GetAttribute("ProcessorArchitecture") -ne "x64" -or
        $frameworks.Count -ne 1 -or
        $frameworks[0].InnerText -cne "true") {
        throw "The supplied Windows App Runtime package manifest is outside policy."
    }
}

function Get-LockedRuntimeDependencyPackagePath {
    Assert-RegularFile -Path $script:projectAssetsPath
    $assets = Get-Content -LiteralPath $script:projectAssetsPath -Raw | ConvertFrom-Json
    $libraryName = "Microsoft.WindowsAppSDK.Runtime/$($script:expectedRuntimeNuGetVersion)"
    $libraries = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -ceq $libraryName })
    if ($libraries.Count -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$libraries[0].Value.path)) {
        throw "The locked Windows App Runtime library is unavailable in project assets."
    }

    $relativePackagePath = Join-Path `
        ([string]$libraries[0].Value.path) `
        "tools\MSIX\win10-x64\Microsoft.WindowsAppRuntime.2.msix"
    $candidates = @($assets.packageFolders.PSObject.Properties | ForEach-Object {
        $candidate = Join-Path $_.Name $relativePackagePath
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            [System.IO.Path]::GetFullPath($candidate)
        }
    })
    if ($candidates.Count -ne 1) {
        throw "The exact locked Windows App Runtime package input is ambiguous."
    }
    Assert-RegularFile -Path $candidates[0]
    return $candidates[0]
}

function Assert-FixtureCorpus {
    Assert-RegularDirectory -Path $script:fixtureRoot
    Assert-RegularFile -Path $script:fixtureManifestPath
    $expectedNames = @(
        "fixture-manifest.json",
        "direct-h264-aac.ts",
        "hls.m3u8",
        "hls-000.ts",
        "hls-001.ts",
        "hls-002.ts",
        "hls-003.ts"
    )
    $entries = @([System.IO.DirectoryInfo]::new([System.IO.Path]::GetFullPath($script:fixtureRoot)).GetFileSystemInfos())
    if ($entries.Count -ne $expectedNames.Count) {
        throw "The native playback fixture corpus contains an unexpected entry."
    }
    foreach ($entry in $entries) {
        if ($entry -isnot [System.IO.FileInfo] -or
            ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $expectedNames -notcontains $entry.Name) {
            throw "The native playback fixture corpus contains an unsafe entry."
        }
    }

    $manifest = Get-Content -LiteralPath $script:fixtureManifestPath -Raw | ConvertFrom-Json
    if ([int]$manifest.SchemaVersion -ne 1 -or
        [string]$manifest.FixtureId -ne "iptvsuite-tier-a-synthetic-v1" -or
        [string]$manifest.Rights.License -ne "CC0-1.0") {
        throw "The native playback fixture manifest contract changed."
    }

    $expectedPayloadNames = @($expectedNames | Where-Object { $_ -ne "fixture-manifest.json" })
    $manifestFiles = @($manifest.Files)
    if ($manifestFiles.Count -ne $expectedPayloadNames.Count) {
        throw "The native playback fixture manifest file set changed."
    }
    foreach ($file in $manifestFiles) {
        $relativePath = [string]$file.Path
        if ($expectedPayloadNames -notcontains $relativePath -or
            [System.IO.Path]::GetFileName($relativePath) -ne $relativePath) {
            throw "The native playback fixture manifest contains an unsafe path."
        }

        $payloadPath = Join-Path $script:fixtureRoot $relativePath
        Assert-RegularFile -Path $payloadPath
        $payload = Get-Item -LiteralPath $payloadPath -ErrorAction Stop
        if ([long]$file.SizeBytes -ne $payload.Length -or
            [string]$file.Sha256 -cne (Get-RegularFileSha256 -Path $payloadPath)) {
            throw "The native playback fixture corpus does not match its manifest."
        }
    }

    $actualPayloadNames = @($manifestFiles | ForEach-Object { [string]$_.Path } | Sort-Object)
    $sortedExpectedPayloadNames = @($expectedPayloadNames | Sort-Object)
    if ([string]::Join("`n", $actualPayloadNames) -cne [string]::Join("`n", $sortedExpectedPayloadNames)) {
        throw "The native playback fixture manifest file set is ambiguous."
    }

    $playlistPath = Join-Path $script:fixtureRoot "hls.m3u8"
    $playlistLines = @(Get-Content -LiteralPath $playlistPath -Encoding UTF8)
    $playlistVersionLines = @($playlistLines | Where-Object {
        ([string]$_).StartsWith("#EXT-X-VERSION:", [StringComparison]::Ordinal)
    })
    $independentSegmentLines = @($playlistLines | Where-Object {
        ([string]$_).StartsWith("#EXT-X-INDEPENDENT-SEGMENTS", [StringComparison]::Ordinal)
    })
    $playlistUris = @($playlistLines | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_) -and
        -not ([string]$_).StartsWith("#", [StringComparison]::Ordinal)
    })
    $expectedPlaylistUris = @("hls-000.ts", "hls-001.ts", "hls-002.ts", "hls-003.ts")
    $independentSegmentIndex = -1
    $firstExtInfIndex = -1
    for ($lineIndex = 0; $lineIndex -lt $playlistLines.Count; $lineIndex++) {
        if ($independentSegmentIndex -lt 0 -and
            [string]$playlistLines[$lineIndex] -ceq "#EXT-X-INDEPENDENT-SEGMENTS") {
            $independentSegmentIndex = $lineIndex
        }
        if ($firstExtInfIndex -lt 0 -and
            ([string]$playlistLines[$lineIndex]).StartsWith("#EXTINF:", [StringComparison]::Ordinal)) {
            $firstExtInfIndex = $lineIndex
        }
    }
    if ($playlistVersionLines.Count -ne 1 -or
        [string]$playlistVersionLines[0] -cne "#EXT-X-VERSION:6") {
        throw "The native playback HLS fixture must declare exact version 6 once."
    }
    if ($independentSegmentLines.Count -ne 1 -or
        [string]$independentSegmentLines[0] -cne "#EXT-X-INDEPENDENT-SEGMENTS" -or
        $independentSegmentIndex -lt 0 -or
        $firstExtInfIndex -lt 0 -or
        $independentSegmentIndex -ge $firstExtInfIndex) {
        throw "The native playback HLS fixture independent-segments contract changed."
    }
    if ([string]::Join("`n", $playlistUris) -cne
        [string]::Join("`n", $expectedPlaylistUris)) {
        throw "The native playback HLS fixture segment order changed."
    }
}

function Remove-ExactCertificate {
    param(
        [Parameter(Mandatory)]
        [string]$StorePath,

        [Parameter(Mandatory)]
        [string]$Thumbprint,

        [Parameter(Mandatory)]
        [string]$ExpectedSubject
    )

    if (-not (Test-Path -LiteralPath $StorePath)) {
        return
    }

    $certificate = Get-Item -LiteralPath $StorePath -ErrorAction Stop
    if (-not [string]::Equals($certificate.Thumbprint, $Thumbprint, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($certificate.Subject, $ExpectedSubject, [System.StringComparison]::Ordinal)) {
        throw "An exact native playback certificate identity is unexpected."
    }
    Remove-Item -LiteralPath $StorePath -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $StorePath) {
        throw "An exact native playback certificate remains after cleanup."
    }
}

function Write-JsonAtomically {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    $directory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($DestinationPath))
    Assert-RegularDirectory -Path $directory
    Assert-ExactChildPath -Parent $directory -Child $DestinationPath
    if (Test-Path -LiteralPath $DestinationPath) {
        throw "Refusing to overwrite an existing native playback evidence file."
    }

    $temporaryPath = Join-Path $directory ("staging-" + [Guid]::NewGuid().ToString("N") + ".json")
    try {
        $json = $Value | ConvertTo-Json -Depth 6
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
        $stream = [System.IO.File]::Open(
            $temporaryPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }

        Assert-RegularFile -Path $temporaryPath
        [System.IO.File]::Move($temporaryPath, $DestinationPath)
        Assert-RegularFile -Path $DestinationPath
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-ExactOwnedFile -Path $temporaryPath -ExpectedParent $directory
        }
    }
}

function Assert-ExactJsonProperties {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string[]]$ExpectedNames
    )

    $properties = @($Value.PSObject.Properties)
    if ($properties.Count -ne $ExpectedNames.Count) {
        throw "A native playback JSON object has an unexpected property count."
    }
    for ($index = 0; $index -lt $ExpectedNames.Count; $index++) {
        if (-not [string]::Equals(
                $properties[$index].Name,
                $ExpectedNames[$index],
                [System.StringComparison]::Ordinal)) {
            throw "A native playback JSON object has an unexpected property contract."
        }
    }
}

function Test-JsonInteger {
    param([AllowNull()][object]$Value)

    return $Value -is [int] -or $Value -is [long]
}

function Test-JsonNumber {
    param([AllowNull()][object]$Value)

    if (Test-JsonInteger -Value $Value) { return $true }
    if ($Value -is [decimal]) { return $true }
    if ($Value -is [double]) {
        return -not [double]::IsNaN($Value) -and -not [double]::IsInfinity($Value)
    }

    return $false
}

function Get-QpcDeltaMilliseconds {
    param(
        [Parameter(Mandatory)]
        [long]$StartTimestamp,

        [Parameter(Mandatory)]
        [long]$EndTimestamp,

        [Parameter(Mandatory)]
        [long]$Frequency
    )

    if ($Frequency -le 0) {
        throw "The native playback QPC frequency is invalid."
    }

    return (([double]$EndTimestamp - [double]$StartTimestamp) * 1000.0) / [double]$Frequency
}

function Invoke-CleanupStep {
    param(
        [Parameter(Mandatory)]
        [string]$Code,

        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    try {
        & $Action
    }
    catch {
        if ([string]::Equals(
                $Code,
                "RuntimeDependencyValidationFailed",
                [System.StringComparison]::Ordinal)) {
            Write-Host "Native runtime cleanup diagnostic: $($script:runtimeDependencyCleanupDiagnostic)."
        }
        $script:cleanupFailures.Add($Code)
    }
}

function Get-ExactPackages {
    return @(Get-AppxPackage -Name $script:expectedName -ErrorAction Stop |
        Where-Object {
            [string]::Equals(
                $_.Name,
                $script:expectedName,
                [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals(
                $_.Publisher,
                $script:expectedPublisher,
                [System.StringComparison]::Ordinal)
        })
}

function Get-RuntimeDependencyPackages {
    return @(Get-AppxPackage -Name $script:expectedRuntimeDependencyName -ErrorAction Stop |
        Where-Object {
            [string]::Equals(
                $_.Name,
                $script:expectedRuntimeDependencyName,
                [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals(
                $_.Publisher,
                $script:expectedRuntimeDependencyPublisher,
                [System.StringComparison]::Ordinal)
        })
}

function Assert-ExactPackageIdentity {
    param(
        [Parameter(Mandatory)]
        [object]$Package
    )

    if (-not [string]::Equals(
            $Package.Name,
            $script:expectedName,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $Package.Publisher,
            $script:expectedPublisher,
            [System.StringComparison]::Ordinal) -or
        $Package.Architecture.ToString() -ne "X64" -or
        $Package.Version.ToString() -ne $script:expectedVersion -or
        [string]::IsNullOrWhiteSpace($Package.PackageFullName) -or
        [string]::IsNullOrWhiteSpace($Package.PackageFamilyName) -or
        -not [string]::Equals(
            $Package.PackageFamilyName,
            $script:packageFamilyName,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The exact native playback package identity is outside policy."
    }
}

function Remove-ExactPackage {
    $packages = @(Get-ExactPackages)
    if ($packages.Count -gt 1) {
        throw "The exact native playback package registration is ambiguous."
    }
    if ($packages.Count -eq 1) {
        Assert-ExactPackageIdentity -Package $packages[0]
        if (-not [string]::IsNullOrWhiteSpace($script:installedPackageFullName) -and
            -not [string]::Equals(
                $packages[0].PackageFullName,
                $script:installedPackageFullName,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The installed native playback package identity changed during cleanup."
        }

        Remove-AppxPackage -Package $packages[0].PackageFullName -ErrorAction Stop
    }

    $deadline = (Get-Date).AddSeconds(15)
    while (@(Get-ExactPackages).Count -ne 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (@(Get-ExactPackages).Count -ne 0) {
        throw "The exact native playback package registration remains after cleanup."
    }
}

function Wait-PackageAppDataRemoval {
    if ([string]::IsNullOrWhiteSpace($script:packageAppDataPath)) {
        throw "The exact native playback package data path is unavailable."
    }
    Assert-ExactChildPath -Parent $script:localPackagesRoot -Child $script:packageAppDataPath
    if (@(Get-ExactPackages).Count -ne 0) {
        throw "Refusing package app-data cleanup while the package remains registered."
    }

    $deadline = (Get-Date).AddSeconds(15)
    while ((Test-Path -LiteralPath $script:packageAppDataPath) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Test-Path -LiteralPath $script:packageAppDataPath) {
        if ([IptvSuite.NativePlaybackSmoke.PackagedApplicationActivator]::RemoveExactEmptyDirectory(
                [System.IO.Path]::GetFullPath($script:packageAppDataPath))) {
            $script:packageAppDataEmptyRootCleanupUsed = $true
        }
    }
    if (Test-Path -LiteralPath $script:packageAppDataPath) {
        throw "The exact native playback package data remains after deployment cleanup."
    }
}

function Test-RuntimeDependencyPackageGraph {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$ExpectedPackageFullNames
    )

    $actualNames = @(Get-RuntimeDependencyPackages |
        ForEach-Object { $_.PackageFullName } |
        Sort-Object)
    if ($actualNames.Count -ne $ExpectedPackageFullNames.Count) {
        return $false
    }

    for ($index = 0; $index -lt $ExpectedPackageFullNames.Count; $index++) {
        if (-not [string]::Equals(
                $ExpectedPackageFullNames[$index],
                $actualNames[$index],
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Validate-RuntimeDependencyPackageState {
    $script:runtimeDependencyCleanupDiagnostic = "SnapshotValidation"
    if ($null -eq $script:runtimePackagesBefore) {
        if ($script:installAttempted) {
            throw "The Windows App Runtime package graph snapshot is unavailable."
        }

        $script:runtimePackageBaselinePreserved = $true
        $script:runtimePackageGraphDisposition = "ExactRestored"
        $script:runtimePackageSharedAdditionCount = 0
        return
    }

    $beforeNames = @($script:runtimePackagesBefore)
    if ($beforeNames.Count -gt 64) {
        throw "The Windows App Runtime baseline exceeds its validation bound."
    }
    $baselineNameSet = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($baselineFullName in $beforeNames) {
        if ([string]::IsNullOrWhiteSpace($baselineFullName) -or
            -not $baselineNameSet.Add($baselineFullName)) {
            throw "The Windows App Runtime baseline snapshot is invalid."
        }
    }

    $passiveDeadline = (Get-Date).AddSeconds(5)
    do {
        $script:runtimeDependencyCleanupDiagnostic = "PassiveGraphConvergence"
        if (Test-RuntimeDependencyPackageGraph -ExpectedPackageFullNames $beforeNames) {
            $script:runtimeDependencyCleanupDiagnostic = "Restored"
            $script:runtimePackageBaselinePreserved = $true
            $script:runtimePackageGraphDisposition = "ExactRestored"
            $script:runtimePackageSharedAdditionCount = 0
            return
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $passiveDeadline)

    $script:runtimeDependencyCleanupDiagnostic = "GraphComparison"
    if (Test-RuntimeDependencyPackageGraph -ExpectedPackageFullNames $beforeNames) {
        $script:runtimeDependencyCleanupDiagnostic = "Restored"
        $script:runtimePackageBaselinePreserved = $true
        $script:runtimePackageGraphDisposition = "ExactRestored"
        $script:runtimePackageSharedAdditionCount = 0
        return
    }

    $script:runtimeDependencyCleanupDiagnostic = "CurrentGraphRead"
    $currentPackages = @(Get-RuntimeDependencyPackages)
    if ($currentPackages.Count -gt 64) {
        throw "The Windows App Runtime package graph exceeds its validation bound."
    }
    $currentNameSet = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($currentPackage in $currentPackages) {
        if ($null -eq $currentPackage -or
            [string]::IsNullOrWhiteSpace([string]$currentPackage.PackageFullName) -or
            -not $currentNameSet.Add([string]$currentPackage.PackageFullName)) {
            throw "The Windows App Runtime package graph is invalid."
        }
    }

    $missingBaselineNames = @($beforeNames | Where-Object {
        $baselineFullName = $_
        @($currentPackages | Where-Object {
            [string]::Equals(
                $_.PackageFullName,
                $baselineFullName,
                [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -eq 0
    })
    if ($missingBaselineNames.Count -ne 0) {
        $script:runtimeDependencyCleanupDiagnostic =
            "MissingBaseline(count=$($missingBaselineNames.Count))"
        throw "A baseline Windows App Runtime registration disappeared during the transaction."
    }

    $addedPackages = @($currentPackages | Where-Object {
        $currentFullName = $_.PackageFullName
        @($beforeNames | Where-Object {
            [string]::Equals($_, $currentFullName, [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -eq 0
    })
    $validatedAddedPackages = @()
    foreach ($package in $addedPackages) {
            $versionText = if ($null -eq $package.Version) { "null" } else { $package.Version.ToString() }
            $architectureText = if ($null -eq $package.Architecture) { "null" } else { $package.Architecture.ToString() }
            $frameworkText = if ($null -eq $package.IsFramework) { "null" } else { $package.IsFramework.ToString() }
            $familyMatches = [string]::Equals(
                $package.PackageFamilyName,
                "$($script:expectedRuntimeDependencyName)_$($script:expectedRuntimeDependencyPublisherId)",
                [System.StringComparison]::OrdinalIgnoreCase)
            $architectureAllowed = @($script:expectedRuntimeDependencyArchitectures | Where-Object {
                [string]::Equals($_, $architectureText, [System.StringComparison]::Ordinal)
            }).Count -eq 1
            $x64SiblingMatches = $true
            if ([string]::Equals($architectureText, "X86", [System.StringComparison]::Ordinal)) {
                $x64SiblingMatches = @($currentPackages | Where-Object {
                    -not [string]::IsNullOrWhiteSpace([string]$_.PackageFullName) -and
                    [string]::Equals($_.Name, $package.Name, [System.StringComparison]::OrdinalIgnoreCase) -and
                    [string]::Equals($_.Publisher, $package.Publisher, [System.StringComparison]::Ordinal) -and
                    [string]::Equals($_.PackageFamilyName, $package.PackageFamilyName, [System.StringComparison]::OrdinalIgnoreCase) -and
                    [string]::Equals($_.Version.ToString(), $versionText, [System.StringComparison]::Ordinal) -and
                    [string]::Equals($_.Architecture.ToString(), "X64", [System.StringComparison]::Ordinal) -and
                    $_.IsFramework -eq $true
                }).Count -eq 1
            }
            $script:runtimeDependencyCleanupDiagnostic =
                "AddedPackage(version=$versionText;architecture=$architectureText;framework=$frameworkText;familyMatch=$familyMatches;x64Sibling=$x64SiblingMatches)"
            $runtimeVersion = [version]::new()
            $runtimeVersionValid = [version]::TryParse(
                $versionText,
                [ref]$runtimeVersion)
            if ([string]::IsNullOrWhiteSpace([string]$package.PackageFullName) -or
                -not [string]::Equals(
                    $package.Name,
                    $script:expectedRuntimeDependencyName,
                    [System.StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    $package.Publisher,
                    $script:expectedRuntimeDependencyPublisher,
                    [System.StringComparison]::Ordinal) -or
                -not $architectureAllowed -or
                $package.IsFramework -ne $true -or
                -not $runtimeVersionValid -or
                $runtimeVersion.Major -ne 2 -or
                $runtimeVersion -lt [version]$script:expectedRuntimeDependencyVersion -or
                -not $x64SiblingMatches -or
                -not [string]::Equals(
                    $package.PackageFamilyName,
                    "$($script:expectedRuntimeDependencyName)_$($script:expectedRuntimeDependencyPublisherId)",
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "An unexpected Windows App Runtime registration appeared during the transaction."
            }

        $validatedAddedPackages += $package
    }

    $script:runtimePackageBaselinePreserved = $true
    $script:runtimePackageSharedAdditionCount = $validatedAddedPackages.Count
    $script:runtimePackageGraphDisposition = if ($validatedAddedPackages.Count -eq 0) {
        "ExactRestored"
    }
    else {
        "SharedAdditionsPreserved"
    }
    $script:runtimeDependencyCleanupDiagnostic =
        "Validated(disposition=$($script:runtimePackageGraphDisposition);baselineCount=$($beforeNames.Count);currentCount=$($currentPackages.Count);sharedAdditionCount=$($validatedAddedPackages.Count))"
}

function Close-TrackedProcessNormally {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    $Process.Refresh()
    if ($Process.HasExited) {
        if ($Process.ExitCode -ne 0) {
            throw "The native playback probe exited with a nonzero code."
        }
        return
    }

    if (-not $Process.CloseMainWindow()) {
        throw "The native playback probe did not accept a normal window close."
    }
    if (-not $Process.WaitForExit(10000)) {
        throw "The native playback probe did not exit after a normal window close."
    }
    if ($Process.ExitCode -ne 0) {
        throw "The native playback probe exited with a nonzero code."
    }
}

function Assert-PackagePayload([string]$PackagePath) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $forbidden = @($archive.Entries | Where-Object {
            $_.FullName -match '(?i)(libvlc|videolan|libx264|direct-h264-aac|hls-00[0-3]\.ts|hls\.m3u8)'
        })
        if ($forbidden.Count -ne 0) { throw "The disposable native package contains forbidden candidate or fixture payload." }
    }
    finally { $archive.Dispose() }
}

try {
    Set-FailurePoint -Stage "WorkspaceValidation" -Code "ArtifactWorkspaceRejected"
    Assert-RegularDirectory -Path $repositoryRoot
    Assert-RegularFile -Path $PSCommandPath
    if (-not [System.IO.Path]::GetFullPath($PSCommandPath).Equals(
            [System.IO.Path]::GetFullPath($expectedControllerPath),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The native playback controller path is unexpected."
    }
    foreach ($trackedInventoryInput in @(
            $manifestPath,
            $lockFilePath,
            $inventorySpecificationPath,
            $inventoryValidatorPath)) {
        Assert-RegularFile -Path $trackedInventoryInput
    }
    New-RegularDirectory -Path (Join-Path $repositoryRoot ".artifacts")
    New-RegularDirectory -Path $artifactRoot
    New-RegularDirectory -Path $packagesRoot
    foreach ($staleEvidence in @($evidencePath, $failureEvidencePath, $packageInventoryEvidencePath)) {
        if (Test-Path -LiteralPath $staleEvidence) {
            Remove-ExactOwnedFile -Path $staleEvidence -ExpectedParent $artifactRoot
        }
    }

    Set-FailurePoint -Stage "ControllerCompilation" -Code "EmbeddedControllerCompilationFailed"
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
    Add-Type -TypeDefinition $activationInterop -Language CSharp -ErrorAction Stop
    Add-Type -TypeDefinition $tlsServerSource -Language CSharp -ErrorAction Stop

    Set-FailurePoint -Stage "RepositoryBinding" -Code "RepositoryDirty"
    if (@(Get-RepositoryStatus).Count -ne 0) {
        throw "The repository worktree is not clean."
    }
    $repositoryHead = Get-RepositoryHead
    $githubSha = [Environment]::GetEnvironmentVariable("GITHUB_SHA", "Process")
    if (-not [string]::IsNullOrWhiteSpace($githubSha) -and
        ($githubSha -notmatch '\A[0-9a-fA-F]{40}\z' -or
            -not $repositoryHead.Equals($githubSha, [System.StringComparison]::OrdinalIgnoreCase))) {
        throw "The GitHub workflow commit does not match repository HEAD."
    }
    $controllerScriptSha256 = Get-RegularFileSha256 -Path $PSCommandPath
    Assert-FixtureCorpus
    $fixtureManifestSha256 = Get-RegularFileSha256 -Path $fixtureManifestPath
    $fixtureCorpusVerified = $true

    Set-FailurePoint -Stage "HostValidation" -Code "ElevationRequired"
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Native packaged playback smoke requires an elevated Windows PowerShell session."
    }
    $enableLua = Get-ItemPropertyValue "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name EnableLUA
    if ($enableLua -ne 1) { throw "Package activation requires the Windows app-model UAC service." }

    Set-FailurePoint -Stage "SdkValidation" -Code "ExactSdkMismatch"
    $expectedSdk = (Get-Content -Raw (Join-Path $repositoryRoot "global.json") | ConvertFrom-Json).sdk.version
    $actualSdk = (& $DotNetPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) { throw "Expected .NET SDK $expectedSdk, received '$actualSdk'." }

    Set-FailurePoint -Stage "HostInspection" -Code "HostInspectionFailed"
    $h264DecoderRegistered = Test-Path -LiteralPath $h264DecoderClass -PathType Container
    $aacDecoderRegistered = Test-Path -LiteralPath $aacDecoderClass -PathType Container
    $audioServiceRunning = (Get-Service -Name Audiosrv -ErrorAction SilentlyContinue).Status -eq "Running"
    $audioEndpointServiceRunning = (Get-Service -Name AudioEndpointBuilder -ErrorAction SilentlyContinue).Status -eq "Running"
    $userInteractive = [Environment]::UserInteractive
    $installationType = Get-ItemPropertyValue `
        -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" `
        -Name InstallationType `
        -ErrorAction SilentlyContinue

    Set-FailurePoint -Stage "ManifestValidation" -Code "ManifestIdentityChanged"
    [xml]$manifest = Get-Content -Raw $manifestPath
    $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    $application = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']")
    if ($null -eq $identity -or
        $null -eq $application -or
        $identity.GetAttribute("Name") -ne $expectedName -or
        $identity.GetAttribute("Publisher") -cne $expectedPublisher -or
        $identity.GetAttribute("Version") -ne $expectedVersion -or
        $identity.GetAttribute("ProcessorArchitecture") -ne "x64" -or
        $application.GetAttribute("Id") -ne $expectedApplicationId) {
        throw "The disposable native playback manifest identity changed."
    }
    $manifestVersion = [version]$identity.GetAttribute("Version")
    $packageFamilyName = [IptvSuite.NativePlaybackSmoke.PackagedApplicationActivator]::GetPackageFamilyName(
        $expectedName,
        $expectedPublisher,
        [uint16]$manifestVersion.Major,
        [uint16]$manifestVersion.Minor,
        [uint16]$manifestVersion.Build,
        [uint16]$manifestVersion.Revision)
    if (-not [string]::Equals(
            $packageFamilyName,
            $expectedPackageFamilyName,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The disposable native playback package family identity changed."
    }
    $packageAppDataPath = Join-Path $localPackagesRoot $packageFamilyName
    Assert-ExactChildPath -Parent $localPackagesRoot -Child $packageAppDataPath
    $packageEvidenceRoot = Join-Path $packageAppDataPath "LocalCache\M10NativePlayback"
    $packageEvidencePath = Join-Path $packageEvidenceRoot "result-$runId.json"
    Assert-NoReparsePath -Path $packageEvidencePath

    Set-FailurePoint -Stage "CertificatePreparation" -Code "CertificatePreparationFailed"
    New-RegularDirectory -Path $packageOutput
    $signingCertificate = New-SelfSignedCertificate -Type Custom -Subject $expectedPublisher `
        -CertStoreLocation "Cert:\CurrentUser\My" -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable -KeyUsage DigitalSignature -NotAfter (Get-Date).AddDays(2) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    Export-Certificate -Cert $signingCertificate -FilePath $signingCertificatePath | Out-Null
    Import-Certificate -FilePath $signingCertificatePath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null

    $tlsCertificate = New-SelfSignedCertificate -DnsName "localhost" -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddDays(2) -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1")
    Export-Certificate -Cert $tlsCertificate -FilePath $tlsCertificatePath | Out-Null
    Import-Certificate -FilePath $tlsCertificatePath -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
    Write-Host "Ephemeral package-signing and loopback TLS certificates are prepared."

    Set-FailurePoint -Stage "PackageBuild" -Code "PackageBuildFailed"
    $msBuildEnvironment.PackageCertificateThumbprint = $signingCertificate.Thumbprint
    foreach ($entry in $msBuildEnvironment.GetEnumerator()) {
        $environmentBackup[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }
    & $DotNetPath restore $projectPath --locked-mode --configfile (Join-Path $repositoryRoot "NuGet.config") -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw "Locked native playback restore failed." }
    & $DotNetPath build $projectPath -c $Configuration -p:Platform=x64 --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "Signed native playback package build failed." }
    Write-Host "Disposable native playback package build completed."

    Set-FailurePoint -Stage "PackageInspection" -Code "PackageOutputInvalid"
    $packages = @(Get-ChildItem $packageOutput -Filter "IptvSuite.NativePlaybackCompatibilitySpike_*.msix" -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/]Dependencies[\\/]' })
    $dependencies = @(Get-ChildItem $packageOutput -Filter "Microsoft.WindowsAppRuntime.2.msix" -Recurse -File |
        Where-Object { $_.Directory.Name -eq "x64" })
    if ($packages.Count -ne 1 -or $dependencies.Count -ne 1) { throw "Expected one native playback MSIX and one x64 runtime dependency." }
    Assert-RegularFile -Path $packages[0].FullName
    Assert-RegularFile -Path $dependencies[0].FullName
    $signature = Get-AuthenticodeSignature $packages[0].FullName
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $signingCertificate.Thumbprint -or
        $signature.Status.ToString() -ne "Valid") {
        throw "Native playback MSIX signature validation failed."
    }
    $dependencySignature = Get-AuthenticodeSignature $dependencies[0].FullName
    if ($null -eq $dependencySignature.SignerCertificate -or
        $dependencySignature.SignerCertificate.Subject -cne $expectedRuntimeDependencyPublisher -or
        $dependencySignature.Status.ToString() -ne "Valid") {
        throw "Native playback runtime dependency signature validation failed."
    }
    Assert-PackagePayload $packages[0].FullName
    Assert-BuiltNativePackageManifest -PackagePath $packages[0].FullName
    Assert-RuntimeDependencyPackageManifest -PackagePath $dependencies[0].FullName
    $packageSha256 = Get-RegularFileSha256 -Path $packages[0].FullName
    $runtimeDependencyPackageSha256 = Get-RegularFileSha256 -Path $dependencies[0].FullName
    $lockedRuntimeDependencyPath = Get-LockedRuntimeDependencyPackagePath
    if ((Get-RegularFileSha256 -Path $lockedRuntimeDependencyPath) -cne $runtimeDependencyPackageSha256) {
        throw "The supplied Windows App Runtime package differs from the locked restore input."
    }
    Set-FailurePoint -Stage "PackageInventory" -Code "PackageInventoryMismatch"
    & $inventoryValidatorPath `
        -PackagePath $packages[0].FullName `
        -RuntimePackagePath $dependencies[0].FullName `
        -LockFilePath $lockFilePath `
        -AssetsFilePath $projectAssetsPath `
        -DepsFilePath $depsFilePath `
        -ManifestPath $manifestPath `
        -SpecificationPath $inventorySpecificationPath `
        -EvidencePath $packageInventoryEvidencePath
    Assert-RegularFile -Path $packageInventoryEvidencePath
    $harnessAssemblySha256 = Get-PackageEntrySha256 `
        -PackagePath $packages[0].FullName `
        -EntryName "IptvSuite.NativePlaybackCompatibilitySpike.dll"

    Set-FailurePoint -Stage "PackageInstall" -Code "PackageInstallFailed"
    $runtimeDependencyPackagesBefore = @(Get-RuntimeDependencyPackages)
    $runtimePackagesBefore = @($runtimeDependencyPackagesBefore |
        ForEach-Object { $_.PackageFullName } |
        Sort-Object)
    $compatibleRuntimeDependencyRegistered = @($runtimeDependencyPackagesBefore |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_.PackageFullName) -and
            [string]::Equals(
                [string]$_.PackageFamilyName,
                "$($expectedRuntimeDependencyName)_$expectedRuntimeDependencyPublisherId",
                [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals(
                [string]$_.Architecture,
                "X64",
                [System.StringComparison]::Ordinal) -and
            $_.IsFramework -eq $true -and
            [version]$_.Version -ge [version]$expectedRuntimeDependencyVersion
        }).Count -gt 0
    $installAttempted = $true
    Remove-ExactPackage
    Wait-PackageAppDataRemoval
    if ((Get-RegularFileSha256 -Path $packages[0].FullName) -cne $packageSha256 -or
        (Get-RegularFileSha256 -Path $dependencies[0].FullName) -cne $runtimeDependencyPackageSha256) {
        throw "A native playback package input changed after inspection."
    }
    if ($compatibleRuntimeDependencyRegistered) {
        Write-Host "Compatible Windows App Runtime dependency is already registered; package install will reuse it."
        Add-AppxPackage -Path $packages[0].FullName
    }
    else {
        Add-AppxPackage -Path $packages[0].FullName -DependencyPath $dependencies[0].FullName
    }
    $installed = @(Get-ExactPackages)
    if ($installed.Count -ne 1) { throw "Disposable native playback package installation is ambiguous." }
    $installedPackage = $installed[0]
    Assert-ExactPackageIdentity -Package $installedPackage
    $installedPackageFullName = $installedPackage.PackageFullName
    if (Test-Path -LiteralPath $packageEvidenceRoot) {
        Assert-RegularDirectory -Path $packageEvidenceRoot
        if (@([System.IO.DirectoryInfo]::new(
                    [System.IO.Path]::GetFullPath($packageEvidenceRoot)).GetFileSystemInfos()).Count -ne 0) {
            throw "Unexpected native playback package evidence exists before activation."
        }
    }
    Write-Host "Disposable native playback package installation completed."

    Set-FailurePoint -Stage "ProbeActivation" -Code "ProbeActivationFailed"
    $tlsServer = [IptvSuite.NativePlaybackSmoke.TierATlsServer]::new($fixtureRoot, $tlsCertificate)
    $authority = "https://localhost:$($tlsServer.Port)"
    $arguments = "probe $runId $authority/direct-h264-aac.ts $authority/hls.m3u8 $SwitchCount $SoakMinutes $CancellationProbeCount"
    $aumid = "$packageFamilyName!$expectedApplicationId"
    $activationAttempted = $true
    $processId = [IptvSuite.NativePlaybackSmoke.PackagedApplicationActivator]::Activate($aumid, $arguments)
    $launchedProcess = Get-Process -Id $processId -ErrorAction Stop
    $null = $launchedProcess.Handle
    if ($launchedProcess.ProcessName -ne "IptvSuite.NativePlaybackCompatibilitySpike") { throw "Package activation returned an unexpected process." }
    Write-Host "Native playback probe activation completed."

    Set-FailurePoint -Stage "ProbeExecution" -Code "ProbeEvidenceUnavailable"
    $deadline = (Get-Date).AddMinutes([Math]::Max(15, $SoakMinutes + 15))
    $probeStarted = Get-Date
    $scheduledInterruptionCount = 0
    while (-not (Test-Path -LiteralPath $packageEvidencePath -PathType Leaf) -and (Get-Date) -lt $deadline) {
        $launchedProcess.Refresh()
        if ($launchedProcess.HasExited) { throw "Native playback probe exited before writing evidence." }
        if ($scheduledInterruptionCount -lt $NetworkInterruptionCount -and
            $scheduledInterruptionCount -eq $tlsServer.InjectedFailureCount -and
            $tlsServer.InjectedFailureCount -eq $tlsServer.RecoveryCount) {
            $nextInterruption = $scheduledInterruptionCount + 1
            $interruptionDue = if ($SoakMinutes -gt 0) {
                ((Get-Date) - $probeStarted).TotalSeconds -ge
                    (($SoakMinutes * 60.0 / ($NetworkInterruptionCount + 1)) * $nextInterruption)
            }
            else {
                $tlsServer.RequestCount -ge
                    [Math]::Ceiling(($SwitchCount * 1.0 / ($NetworkInterruptionCount + 1)) * $nextInterruption)
            }
            if ($interruptionDue) {
                $tlsServer.ArmNextMediaRequestFailure()
                $scheduledInterruptionCount++
            }
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path -LiteralPath $packageEvidencePath -PathType Leaf)) { throw "Native playback probe evidence deadline expired." }
    Assert-RegularFile -Path $packageEvidencePath

    $packageEvidenceEntries = @([System.IO.DirectoryInfo]::new(
            [System.IO.Path]::GetFullPath($packageEvidenceRoot)).GetFileSystemInfos())
    if ($packageEvidenceEntries.Count -ne 1 -or
        $packageEvidenceEntries[0] -isnot [System.IO.FileInfo] -or
        -not $packageEvidenceEntries[0].Name.Equals(
            [System.IO.Path]::GetFileName($packageEvidencePath),
            [System.StringComparison]::Ordinal) -or
        ($packageEvidenceEntries[0].Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $packageEvidenceEntries[0].Length -le 0 -or
        $packageEvidenceEntries[0].Length -gt $maximumProbeEvidenceBytes) {
        throw "The native playback probe evidence file set is outside policy."
    }

    Set-FailurePoint -Stage "ProbeEnvelopeValidation" -Code "ProbeRunBindingMismatch"
    $probeEnvelope = Get-Content -LiteralPath $packageEvidencePath -Raw | ConvertFrom-Json
    Assert-ExactJsonProperties -Value $probeEnvelope -ExpectedNames @(
        "SchemaVersion",
        "RunId",
        "RuntimeDependency",
        "Probe"
    )
    if (-not (Test-JsonInteger -Value $probeEnvelope.SchemaVersion) -or
        [int]$probeEnvelope.SchemaVersion -ne 8 -or
        $probeEnvelope.RunId -isnot [string] -or
        -not [string]::Equals(
            $probeEnvelope.RunId,
            $runId,
            [System.StringComparison]::Ordinal) -or
        $null -eq $probeEnvelope.RuntimeDependency -or
        $null -eq $probeEnvelope.Probe) {
        throw "The native playback probe evidence is not bound to this controller run."
    }
    $probeEnvelopeSchemaVersion = 8
    $probeRunIdBound = $true

    Assert-ExactJsonProperties -Value $probeEnvelope.RuntimeDependency -ExpectedNames @(
        "Name",
        "Version",
        "Architecture",
        "PublisherId",
        "IsFramework"
    )
    $resolvedRuntimeVersionValue = [version]::new()
    $resolvedRuntimeVersionValid =
        $probeEnvelope.RuntimeDependency.Version -is [string] -and
        [version]::TryParse(
            [string]$probeEnvelope.RuntimeDependency.Version,
            [ref]$resolvedRuntimeVersionValue)
    if ($probeEnvelope.RuntimeDependency.Name -isnot [string] -or
        $probeEnvelope.RuntimeDependency.Architecture -isnot [string] -or
        $probeEnvelope.RuntimeDependency.PublisherId -isnot [string] -or
        $probeEnvelope.RuntimeDependency.IsFramework -isnot [bool] -or
        -not [string]::Equals(
            $probeEnvelope.RuntimeDependency.Name,
            $expectedRuntimeDependencyName,
            [System.StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $probeEnvelope.RuntimeDependency.Architecture,
            "X64",
            [System.StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $probeEnvelope.RuntimeDependency.PublisherId,
            $expectedRuntimeDependencyPublisherId,
            [System.StringComparison]::Ordinal) -or
        $probeEnvelope.RuntimeDependency.IsFramework -ne $true -or
        -not $resolvedRuntimeVersionValid -or
        $resolvedRuntimeVersionValue.Major -ne 2 -or
        $resolvedRuntimeVersionValue -lt [version]$expectedRuntimeDependencyVersion) {
        throw "The resolved Windows App Runtime dependency is outside policy."
    }
    $resolvedWindowsAppRuntimeName = [string]$probeEnvelope.RuntimeDependency.Name
    $resolvedWindowsAppRuntimeVersion = $resolvedRuntimeVersionValue.ToString(4)
    $resolvedWindowsAppRuntimeArchitecture = "x64"
    $resolvedWindowsAppRuntimePublisherId = [string]$probeEnvelope.RuntimeDependency.PublisherId
    $resolvedWindowsAppRuntimeIsFramework = [bool]$probeEnvelope.RuntimeDependency.IsFramework

    Set-FailurePoint -Stage "ProbeValidation" -Code "ProbeInvariantFailed"
    $probe = $probeEnvelope.Probe
    Assert-ExactJsonProperties -Value $probe -ExpectedNames @(
        "Success",
        "Failure",
        "SwitchCount",
        "StartupP95Milliseconds",
        "StartupMaximumMilliseconds",
        "HlsStartupP95Milliseconds",
        "DirectStartupP95Milliseconds",
        "StartupMaximumSwitchOrdinal",
        "StartupMaximumFixture",
        "StartupMaximumAttemptCount",
        "StartupMaximumSurfaceTransitionCount",
        "StartupMaximumPreWaitMilliseconds",
        "StartupMaximumMediaOpenWaitMilliseconds",
        "StartupMaximumSourceOpen",
        "HlsStartupMaximumMilliseconds",
        "DirectStartupMaximumMilliseconds",
        "StartupFailureStage",
        "StartupFailureSwitchOrdinal",
        "StartupFailureFixture",
        "StartupFailureAttemptCount",
        "StartupFailureSurfaceTransitionCount",
        "StartupFailureTotalMilliseconds",
        "StartupFailureSourceCreationMilliseconds",
        "StartupFailureSourceAssignmentMilliseconds",
        "StartupFailurePlayInvocationMilliseconds",
        "StartupFailureSourceOpen",
        "StartupFailureMediaOpenedCompletionObserved",
        "StartupFailureMediaOpenedCompletionMilliseconds",
        "StartupFailureMediaOpenedWithinWaitDeadline",
        "StartupFailureMediaOpenedWithinStartupBudget",
        "StartupFailureActiveStageElapsedMilliseconds",
        "SoakMinutes",
        "ResourceSampleCount",
        "WarmupPrivateBytes",
        "MemoryNetGrowthBytes",
        "MemoryNetGrowthPercent",
        "MemoryMonotonicIncrease",
        "WarmupHandleCount",
        "HandleNetGrowth",
        "SurfaceTransitionCount",
        "DetachedSourceCount",
        "PlaybackRetryCount",
        "SourceDetachP95Milliseconds",
        "SourceDetachMaximumMilliseconds",
        "CancellationProbeCount",
        "CancellationObservedCount",
        "CancellationSourceDetachCount",
        "CancellationRecoveryCount",
        "CancellationRecoverySourceDetachCount",
        "CancellationLatencyMilliseconds",
        "CancellationQuiescenceMilliseconds",
        "CancellationObservationMilliseconds",
        "CancellationSourceDetachMilliseconds",
        "CancellationRecoveryStartupMilliseconds",
        "CancellationRecoveryAdvanceMilliseconds",
        "CancellationRecoverySourceDetachMilliseconds",
        "CancellationSourceNullAfterObservation",
        "CancellationRecoveryUsedFreshSource",
        "CancellationNoAutomaticRestart",
        "PlaybackStateBeforeDetach",
        "SourceDetached",
        "CanPauseBeforeDetach",
        "CanSeekBeforeDetach",
        "TeardownStage",
        "ExceptionCategory",
        "ExceptionHResult",
        "InitialPrivateBytes",
        "FinalPrivateBytes",
        "InitialHandleCount",
        "FinalHandleCount",
        "FirstHlsStartupClock",
        "ResourceSamples"
    )
    foreach ($sourceOpenPropertyName in @(
            "StartupMaximumSourceOpen",
            "StartupFailureSourceOpen")) {
        $sourceOpenDiagnostic = $probe.PSObject.Properties[$sourceOpenPropertyName].Value
        Assert-ExactJsonProperties -Value $sourceOpenDiagnostic -ExpectedNames @(
            "CompletionObserved",
            "ErrorPresent",
            "CompletionMilliseconds",
            "PostCompletionElapsedMilliseconds"
        )
        if ($sourceOpenDiagnostic.CompletionObserved -isnot [bool] -or
            $sourceOpenDiagnostic.ErrorPresent -isnot [bool] -or
            -not (Test-JsonNumber -Value $sourceOpenDiagnostic.CompletionMilliseconds) -or
            -not (Test-JsonNumber -Value $sourceOpenDiagnostic.PostCompletionElapsedMilliseconds)) {
            throw "A native playback source-open diagnostic has an invalid JSON type."
        }
        $sourceOpenCompletionMilliseconds = [double]$sourceOpenDiagnostic.CompletionMilliseconds
        $postSourceOpenElapsedMilliseconds =
            [double]$sourceOpenDiagnostic.PostCompletionElapsedMilliseconds
        if ($sourceOpenCompletionMilliseconds -lt 0 -or
            $postSourceOpenElapsedMilliseconds -lt 0 -or
            (-not [bool]$sourceOpenDiagnostic.CompletionObserved -and
                ([bool]$sourceOpenDiagnostic.ErrorPresent -or
                    $sourceOpenCompletionMilliseconds -ne 0 -or
                    $postSourceOpenElapsedMilliseconds -ne 0))) {
            throw "A native playback source-open diagnostic is inconsistent."
        }
    }
    $firstHlsStartupClock = $probe.FirstHlsStartupClock
    Assert-ExactJsonProperties -Value $firstHlsStartupClock -ExpectedNames @(
        "HighResolution",
        "Frequency",
        "StartupStartedTimestamp",
        "SourceOpenCompletedTimestamp",
        "MediaOpenedTimestamp",
        "WindowCompletedTimestamp"
    )
    if ($firstHlsStartupClock.HighResolution -isnot [bool]) {
        throw "The first-HLS QPC diagnostic has an invalid Boolean type."
    }
    foreach ($propertyName in @(
            "Frequency",
            "StartupStartedTimestamp",
            "SourceOpenCompletedTimestamp",
            "MediaOpenedTimestamp",
            "WindowCompletedTimestamp")) {
        if (-not (Test-JsonInteger -Value $firstHlsStartupClock.PSObject.Properties[$propertyName].Value)) {
            throw "The first-HLS QPC diagnostic has an invalid integer type."
        }
    }
    $firstHlsClockHighResolution = [bool]$firstHlsStartupClock.HighResolution
    $firstHlsClockFrequency = [long]$firstHlsStartupClock.Frequency
    $firstHlsStartupStartedTimestamp = [long]$firstHlsStartupClock.StartupStartedTimestamp
    $firstHlsSourceOpenCompletedTimestamp =
        [long]$firstHlsStartupClock.SourceOpenCompletedTimestamp
    $firstHlsMediaOpenedTimestamp = [long]$firstHlsStartupClock.MediaOpenedTimestamp
    $firstHlsWindowCompletedTimestamp = [long]$firstHlsStartupClock.WindowCompletedTimestamp
    if ($firstHlsStartupStartedTimestamp -eq 0) {
        if ($firstHlsClockHighResolution -or
            $firstHlsClockFrequency -ne 0 -or
            $firstHlsSourceOpenCompletedTimestamp -ne 0 -or
            $firstHlsMediaOpenedTimestamp -ne 0 -or
            $firstHlsWindowCompletedTimestamp -ne 0) {
            throw "The inactive first-HLS QPC diagnostic is not empty."
        }
    }
    elseif (-not $firstHlsClockHighResolution -or
        -not [System.Diagnostics.Stopwatch]::IsHighResolution -or
        $firstHlsClockFrequency -ne [System.Diagnostics.Stopwatch]::Frequency -or
        $firstHlsClockFrequency -le 0 -or
        $firstHlsStartupStartedTimestamp -lt 1 -or
        $firstHlsWindowCompletedTimestamp -lt $firstHlsStartupStartedTimestamp -or
        ($firstHlsSourceOpenCompletedTimestamp -ne 0 -and
            ($firstHlsSourceOpenCompletedTimestamp -lt $firstHlsStartupStartedTimestamp -or
                $firstHlsSourceOpenCompletedTimestamp -gt $firstHlsWindowCompletedTimestamp)) -or
        ($firstHlsMediaOpenedTimestamp -ne 0 -and
            ($firstHlsMediaOpenedTimestamp -lt $firstHlsStartupStartedTimestamp -or
                $firstHlsMediaOpenedTimestamp -gt $firstHlsWindowCompletedTimestamp)) -or
        ($firstHlsSourceOpenCompletedTimestamp -ne 0 -and
            $firstHlsMediaOpenedTimestamp -ne 0 -and
            $firstHlsSourceOpenCompletedTimestamp -gt $firstHlsMediaOpenedTimestamp)) {
        throw "The active first-HLS QPC diagnostic is inconsistent."
    }
    $resourceSampleTrace = @($probe.ResourceSamples)
    if ($resourceSampleTrace.Count -gt $maximumResourceSampleCount) {
        throw "The native playback resource sample trace exceeded its fixed capacity."
    }
    $previousResourceSampleTimestamp = 0L
    $previousResourceSampleElapsedMilliseconds = -1.0
    for ($resourceSampleIndex = 0; $resourceSampleIndex -lt $resourceSampleTrace.Count; $resourceSampleIndex++) {
        $resourceSample = $resourceSampleTrace[$resourceSampleIndex]
        Assert-ExactJsonProperties -Value $resourceSample -ExpectedNames @(
            "Ordinal",
            "CapturedTimestamp",
            "ElapsedMilliseconds",
            "PrivateBytes",
            "HandleCount",
            "Phase",
            "SwitchOrdinal"
        )
        foreach ($propertyName in @(
                "Ordinal", "CapturedTimestamp", "PrivateBytes", "HandleCount", "SwitchOrdinal")) {
            if (-not (Test-JsonInteger -Value $resourceSample.PSObject.Properties[$propertyName].Value)) {
                throw "A native playback resource sample integer field has an invalid JSON type."
            }
        }
        if (-not (Test-JsonNumber -Value $resourceSample.ElapsedMilliseconds) -or
            $resourceSample.Phase -isnot [string]) {
            throw "A native playback resource sample has an invalid JSON type."
        }
        $resourceSampleOrdinal = [int]$resourceSample.Ordinal
        $resourceSampleTimestamp = [long]$resourceSample.CapturedTimestamp
        $resourceSampleElapsedMilliseconds = [double]$resourceSample.ElapsedMilliseconds
        $resourceSamplePrivateBytes = [long]$resourceSample.PrivateBytes
        $resourceSampleHandleCount = [int]$resourceSample.HandleCount
        $resourceSamplePhase = [string]$resourceSample.Phase
        $resourceSampleSwitchOrdinal = [int]$resourceSample.SwitchOrdinal
        if ($resourceSampleOrdinal -ne ($resourceSampleIndex + 1) -or
            $resourceSampleTimestamp -lt 1 -or
            $resourceSampleTimestamp -le $previousResourceSampleTimestamp -or
            $resourceSampleElapsedMilliseconds -lt 0 -or
            $resourceSampleElapsedMilliseconds -lt $previousResourceSampleElapsedMilliseconds -or
            $resourceSamplePrivateBytes -le 0 -or
            $resourceSampleHandleCount -le 0 -or
            $resourceSamplePhase -notin @("ProbeStart", "SwitchesCompleted", "Soak") -or
            $resourceSampleSwitchOrdinal -lt 0 -or
            $resourceSampleSwitchOrdinal -gt $SwitchCount) {
            throw "A native playback resource sample is inconsistent."
        }
        $previousResourceSampleTimestamp = $resourceSampleTimestamp
        $previousResourceSampleElapsedMilliseconds = $resourceSampleElapsedMilliseconds
    }
    foreach ($propertyName in @(
            "SwitchCount", "SoakMinutes", "ResourceSampleCount", "WarmupPrivateBytes",
            "MemoryNetGrowthBytes", "WarmupHandleCount", "HandleNetGrowth",
            "SurfaceTransitionCount", "DetachedSourceCount", "PlaybackRetryCount",
            "CancellationProbeCount", "CancellationObservedCount",
            "CancellationSourceDetachCount", "CancellationRecoveryCount",
            "CancellationRecoverySourceDetachCount",
            "StartupMaximumSwitchOrdinal", "StartupMaximumAttemptCount",
            "StartupMaximumSurfaceTransitionCount",
            "StartupFailureSwitchOrdinal", "StartupFailureAttemptCount",
            "StartupFailureSurfaceTransitionCount",
            "ExceptionHResult", "InitialPrivateBytes", "FinalPrivateBytes",
            "InitialHandleCount", "FinalHandleCount")) {
        if (-not (Test-JsonInteger -Value $probe.PSObject.Properties[$propertyName].Value)) {
            throw "A native playback probe integer field has an invalid JSON type."
        }
    }
    foreach ($propertyName in @(
            "StartupP95Milliseconds", "StartupMaximumMilliseconds",
            "HlsStartupP95Milliseconds", "DirectStartupP95Milliseconds",
            "StartupMaximumPreWaitMilliseconds", "StartupMaximumMediaOpenWaitMilliseconds",
            "HlsStartupMaximumMilliseconds", "DirectStartupMaximumMilliseconds",
            "StartupFailureTotalMilliseconds", "StartupFailureSourceCreationMilliseconds",
            "StartupFailureSourceAssignmentMilliseconds", "StartupFailurePlayInvocationMilliseconds",
            "StartupFailureMediaOpenedCompletionMilliseconds",
            "StartupFailureActiveStageElapsedMilliseconds",
            "MemoryNetGrowthPercent", "SourceDetachP95Milliseconds",
            "SourceDetachMaximumMilliseconds", "CancellationLatencyMilliseconds",
            "CancellationQuiescenceMilliseconds", "CancellationObservationMilliseconds",
            "CancellationSourceDetachMilliseconds", "CancellationRecoveryStartupMilliseconds",
            "CancellationRecoveryAdvanceMilliseconds",
            "CancellationRecoverySourceDetachMilliseconds")) {
        if (-not (Test-JsonNumber -Value $probe.PSObject.Properties[$propertyName].Value)) {
            throw "A native playback probe metric has an invalid JSON type."
        }
    }
    foreach ($propertyName in @(
            "Success", "MemoryMonotonicIncrease", "SourceDetached",
            "CanPauseBeforeDetach", "CanSeekBeforeDetach",
            "StartupFailureMediaOpenedCompletionObserved",
            "StartupFailureMediaOpenedWithinWaitDeadline",
            "StartupFailureMediaOpenedWithinStartupBudget",
            "CancellationSourceNullAfterObservation",
            "CancellationRecoveryUsedFreshSource",
            "CancellationNoAutomaticRestart")) {
        if ($probe.PSObject.Properties[$propertyName].Value -isnot [bool]) {
            throw "A native playback probe Boolean field has an invalid JSON type."
        }
    }
    foreach ($propertyName in @(
            "Failure", "StartupMaximumFixture", "StartupFailureStage",
            "StartupFailureFixture", "PlaybackStateBeforeDetach",
            "TeardownStage", "ExceptionCategory")) {
        if ($probe.PSObject.Properties[$propertyName].Value -isnot [string]) {
            throw "A native playback probe enum field has an invalid JSON type."
        }
    }
    $cancellationProbeCount = [int]$probe.CancellationProbeCount
    $cancellationObservedCount = [int]$probe.CancellationObservedCount
    $cancellationSourceDetachCount = [int]$probe.CancellationSourceDetachCount
    $cancellationRecoveryCount = [int]$probe.CancellationRecoveryCount
    $cancellationRecoverySourceDetachCount =
        [int]$probe.CancellationRecoverySourceDetachCount
    $cancellationLatencyMilliseconds = [double]$probe.CancellationLatencyMilliseconds
    $cancellationQuiescenceMilliseconds = [double]$probe.CancellationQuiescenceMilliseconds
    $cancellationObservationMilliseconds = [double]$probe.CancellationObservationMilliseconds
    $cancellationSourceDetachMilliseconds = [double]$probe.CancellationSourceDetachMilliseconds
    $cancellationRecoveryStartupMilliseconds =
        [double]$probe.CancellationRecoveryStartupMilliseconds
    $cancellationRecoveryAdvanceMilliseconds =
        [double]$probe.CancellationRecoveryAdvanceMilliseconds
    $cancellationRecoverySourceDetachMilliseconds =
        [double]$probe.CancellationRecoverySourceDetachMilliseconds
    $cancellationSourceNullAfterObservation =
        [bool]$probe.CancellationSourceNullAfterObservation
    $cancellationRecoveryUsedFreshSource =
        [bool]$probe.CancellationRecoveryUsedFreshSource
    $cancellationNoAutomaticRestart = [bool]$probe.CancellationNoAutomaticRestart
    $isCompletedResourceFailure =
        $probe.Success -eq $false -and
        [string]::Equals(
            [string]$probe.Failure,
            "ResourceBudgetExceeded",
            [System.StringComparison]::Ordinal)
    $isCompletedProbeResult = $probe.Success -eq $true -or $isCompletedResourceFailure
    $postWarmupResourceSamples = @()
    $postWarmupMinimumSample = $null
    $postWarmupMaximumSample = $null
    $postWarmupFinalSample = $null
    $networkRecoveryTrace = @($tlsServer.GetNetworkRecoveryTraceSnapshot())
    if ($networkRecoveryTrace.Count -gt 7) {
        throw "The native playback network recovery trace exceeded its fixed capacity."
    }
    $previousInjectedTimestamp = 0L
    $previousRecoveryTimestamp = 0L
    for ($networkTraceIndex = 0; $networkTraceIndex -lt $networkRecoveryTrace.Count; $networkTraceIndex++) {
        $networkTrace = $networkRecoveryTrace[$networkTraceIndex]
        $networkTraceOrdinal = [int]$networkTrace.Ordinal
        $networkInjectedRequestOrdinal = [int]$networkTrace.InjectedRequestOrdinal
        $networkInjectedTimestamp = [long]$networkTrace.InjectedTimestamp
        $networkRecoveryRequestOrdinal = [int]$networkTrace.RecoveryRequestOrdinal
        $networkRecoveryTimestamp = [long]$networkTrace.RecoveryTimestamp
        if ($networkTraceOrdinal -ne ($networkTraceIndex + 1) -or
            $networkInjectedRequestOrdinal -lt 1 -or
            $networkInjectedTimestamp -lt 1 -or
            $networkRecoveryRequestOrdinal -le $networkInjectedRequestOrdinal -or
            $networkRecoveryTimestamp -lt $networkInjectedTimestamp -or
            $networkInjectedTimestamp -le $previousInjectedTimestamp -or
            $networkRecoveryTimestamp -le $previousRecoveryTimestamp -or
            ($networkTraceIndex -gt 0 -and
                $networkInjectedTimestamp -le $previousRecoveryTimestamp)) {
            throw "A native playback network recovery trace is inconsistent."
        }
        $previousInjectedTimestamp = $networkInjectedTimestamp
        $previousRecoveryTimestamp = $networkRecoveryTimestamp
    }
    if ($isCompletedProbeResult) {
        if ($resourceSampleTrace.Count -lt 2 -or
            [string]$resourceSampleTrace[0].Phase -ne "ProbeStart" -or
            [int]$resourceSampleTrace[0].SwitchOrdinal -ne 0 -or
            [string]$resourceSampleTrace[1].Phase -ne "SwitchesCompleted" -or
            [int]$resourceSampleTrace[1].SwitchOrdinal -ne $SwitchCount -or
            [long]$resourceSampleTrace[0].CapturedTimestamp -gt $firstHlsStartupStartedTimestamp -or
            [long]$resourceSampleTrace[1].CapturedTimestamp -lt $firstHlsWindowCompletedTimestamp) {
            throw "The completed native playback resource sample phase trace is inconsistent."
        }
        if ($SoakMinutes -eq 0) {
            if ($resourceSampleTrace.Count -ne 2 -or [int]$probe.ResourceSampleCount -ne 0) {
                throw "The non-soak native playback resource sample trace is inconsistent."
            }
        }
        else {
            if ($resourceSampleTrace.Count -lt 3 -or
                [int]$probe.ResourceSampleCount -ne $resourceSampleTrace.Count) {
                throw "The soak native playback resource sample trace count is inconsistent."
            }
            foreach ($soakResourceSample in @($resourceSampleTrace | Select-Object -Skip 2)) {
                if ([string]$soakResourceSample.Phase -ne "Soak" -or
                    [int]$soakResourceSample.SwitchOrdinal -ne $SwitchCount) {
                    throw "A soak resource sample is not bound to the completed switch phase."
                }
            }
            $postWarmupResourceSamples = @($resourceSampleTrace | Where-Object {
                [double]$_.ElapsedMilliseconds -ge 1800000
            })
            if ($postWarmupResourceSamples.Count -ge 2) {
                $postWarmupMinimumSample = $postWarmupResourceSamples |
                    Sort-Object -Property @{ Expression = { [long]$_.PrivateBytes } },
                        @{ Expression = { [int]$_.Ordinal } } |
                    Select-Object -First 1
                $postWarmupMaximumSample = $postWarmupResourceSamples |
                    Sort-Object -Property @{ Expression = { [long]$_.PrivateBytes }; Descending = $true },
                        @{ Expression = { [int]$_.Ordinal } } |
                    Select-Object -First 1
                $postWarmupFinalSample = $postWarmupResourceSamples[-1]
                $traceMemoryNetGrowthBytes =
                    [long]$postWarmupFinalSample.PrivateBytes -
                    [long]$postWarmupResourceSamples[0].PrivateBytes
                $traceMemoryNetGrowthPercent = if ([long]$postWarmupResourceSamples[0].PrivateBytes -eq 0) {
                    [double]::PositiveInfinity
                }
                else {
                    $traceMemoryNetGrowthBytes * 100.0 /
                        [long]$postWarmupResourceSamples[0].PrivateBytes
                }
                $traceMemoryMonotonicIncrease = $true
                for ($postWarmupIndex = 1;
                    $postWarmupIndex -lt $postWarmupResourceSamples.Count;
                    $postWarmupIndex++) {
                    if ([long]$postWarmupResourceSamples[$postWarmupIndex].PrivateBytes -le
                        [long]$postWarmupResourceSamples[$postWarmupIndex - 1].PrivateBytes) {
                        $traceMemoryMonotonicIncrease = $false
                        break
                    }
                }
                if ([long]$probe.WarmupPrivateBytes -ne
                        [long]$postWarmupResourceSamples[0].PrivateBytes -or
                    [long]$probe.MemoryNetGrowthBytes -ne $traceMemoryNetGrowthBytes -or
                    [Math]::Abs(
                        [double]$probe.MemoryNetGrowthPercent -
                        $traceMemoryNetGrowthPercent) -gt 0.000000001 -or
                    [bool]$probe.MemoryMonotonicIncrease -ne $traceMemoryMonotonicIncrease -or
                    [int]$probe.WarmupHandleCount -ne
                        [int]$postWarmupResourceSamples[0].HandleCount -or
                    [int]$probe.HandleNetGrowth -ne
                        ([int]$postWarmupFinalSample.HandleCount -
                            [int]$postWarmupResourceSamples[0].HandleCount)) {
                    throw "The native playback resource aggregates are not derived from the bounded sample trace."
                }
            }
        }
        if ($networkRecoveryTrace.Count -ne $NetworkInterruptionCount -or
            $networkRecoveryTrace.Count -ne $tlsServer.InjectedFailureCount -or
            $networkRecoveryTrace.Count -ne $tlsServer.RecoveryCount) {
            throw "The native playback network recovery trace is not bound to the completed probe."
        }
        if ($cancellationProbeCount -ne $CancellationProbeCount) {
            throw "The native playback cancellation probe count is not bound to the controller request."
        }
        if ($CancellationProbeCount -eq 0) {
            if ($cancellationObservedCount -ne 0 -or
                $cancellationSourceDetachCount -ne 0 -or
                $cancellationRecoveryCount -ne 0 -or
                $cancellationRecoverySourceDetachCount -ne 0 -or
                $cancellationLatencyMilliseconds -ne 0 -or
                $cancellationQuiescenceMilliseconds -ne 0 -or
                $cancellationObservationMilliseconds -ne 0 -or
                $cancellationSourceDetachMilliseconds -ne 0 -or
                $cancellationRecoveryStartupMilliseconds -ne 0 -or
                $cancellationRecoveryAdvanceMilliseconds -ne 0 -or
                $cancellationRecoverySourceDetachMilliseconds -ne 0 -or
                $cancellationSourceNullAfterObservation -or
                $cancellationRecoveryUsedFreshSource -or
                $cancellationNoAutomaticRestart) {
                throw "The inactive native playback cancellation probe evidence is not empty."
            }
        }
        elseif ($cancellationProbeCount -ne 1 -or
            $cancellationObservedCount -ne 1 -or
            $cancellationSourceDetachCount -ne 1 -or
            $cancellationRecoveryCount -ne 1 -or
            $cancellationRecoverySourceDetachCount -ne 1 -or
            $cancellationLatencyMilliseconds -lt 0 -or
            $cancellationLatencyMilliseconds -gt 1000 -or
            $cancellationQuiescenceMilliseconds -lt 0 -or
            $cancellationQuiescenceMilliseconds -gt 1000 -or
            $cancellationObservationMilliseconds -lt 1000 -or
            $cancellationObservationMilliseconds -gt 1500 -or
            $cancellationSourceDetachMilliseconds -lt 0 -or
            $cancellationSourceDetachMilliseconds -gt 5000 -or
            $cancellationRecoveryStartupMilliseconds -le 0 -or
            $cancellationRecoveryStartupMilliseconds -gt 5000 -or
            $cancellationRecoveryAdvanceMilliseconds -le 0 -or
            $cancellationRecoveryAdvanceMilliseconds -gt 3000 -or
            $cancellationRecoverySourceDetachMilliseconds -lt 0 -or
            $cancellationRecoverySourceDetachMilliseconds -gt 5000 -or
            ($cancellationLatencyMilliseconds + $cancellationSourceDetachMilliseconds) -gt
                ($cancellationQuiescenceMilliseconds + 0.002) -or
            ($cancellationQuiescenceMilliseconds + $cancellationObservationMilliseconds) -lt
                999.998 -or
            $cancellationSourceDetachMilliseconds -gt
                ([double]$probe.SourceDetachMaximumMilliseconds + 0.002) -or
            $cancellationRecoverySourceDetachMilliseconds -gt
                ([double]$probe.SourceDetachMaximumMilliseconds + 0.002) -or
            -not $cancellationSourceNullAfterObservation -or
            -not $cancellationRecoveryUsedFreshSource -or
            -not $cancellationNoAutomaticRestart) {
            throw "The active native playback cancellation probe evidence is outside policy."
        }
    }
    $startupFailureStage = [string]$probe.StartupFailureStage
    $startupFailureSwitchOrdinal = [int]$probe.StartupFailureSwitchOrdinal
    $startupFailureFixture = [string]$probe.StartupFailureFixture
    $startupFailureAttemptCount = [int]$probe.StartupFailureAttemptCount
    $startupFailureSurfaceTransitionCount = [int]$probe.StartupFailureSurfaceTransitionCount
    $startupFailureTotalMilliseconds = [double]$probe.StartupFailureTotalMilliseconds
    $startupFailureSourceCreationMilliseconds = [double]$probe.StartupFailureSourceCreationMilliseconds
    $startupFailureSourceAssignmentMilliseconds = [double]$probe.StartupFailureSourceAssignmentMilliseconds
    $startupFailurePlayInvocationMilliseconds = [double]$probe.StartupFailurePlayInvocationMilliseconds
    $startupFailureSourceOpenObserved =
        [bool]$probe.StartupFailureSourceOpen.CompletionObserved
    $startupFailureSourceOpenError = [bool]$probe.StartupFailureSourceOpen.ErrorPresent
    $startupFailureSourceOpenCompletionMilliseconds =
        [double]$probe.StartupFailureSourceOpen.CompletionMilliseconds
    $startupFailurePostSourceOpenElapsedMilliseconds =
        [double]$probe.StartupFailureSourceOpen.PostCompletionElapsedMilliseconds
    $startupFailureMediaOpenedCompletionObserved =
        [bool]$probe.StartupFailureMediaOpenedCompletionObserved
    $startupFailureMediaOpenedCompletionMilliseconds =
        [double]$probe.StartupFailureMediaOpenedCompletionMilliseconds
    $startupFailureMediaOpenedWithinWaitDeadline =
        [bool]$probe.StartupFailureMediaOpenedWithinWaitDeadline
    $startupFailureMediaOpenedWithinStartupBudget =
        [bool]$probe.StartupFailureMediaOpenedWithinStartupBudget
    $startupFailureActiveStageElapsedMilliseconds =
        [double]$probe.StartupFailureActiveStageElapsedMilliseconds
    $allowedStartupFailureStages = @(
        "None",
        "SurfaceReadiness",
        "SourceCreation",
        "SourceAssignment",
        "PlayInvocation",
        "MediaSourceOpenWait",
        "MediaOpenWait",
        "PlaybackAdvanceWait"
    )
    if ($startupFailureStage -notin $allowedStartupFailureStages -or
        $startupFailureFixture -notin @("None", "HlsH264AacMpegTs", "DirectH264AacMpegTs") -or
        $startupFailureSwitchOrdinal -lt 0 -or
        $startupFailureAttemptCount -lt 0 -or
        $startupFailureSurfaceTransitionCount -lt 0 -or
        $startupFailureTotalMilliseconds -lt 0 -or
        $startupFailureSourceCreationMilliseconds -lt 0 -or
        $startupFailureSourceAssignmentMilliseconds -lt 0 -or
        $startupFailurePlayInvocationMilliseconds -lt 0 -or
        ($startupFailureSourceOpenObserved -and
            ($startupFailureSourceOpenCompletionMilliseconds -le 0 -or
                [Math]::Abs(
                    ($startupFailureSourceOpenCompletionMilliseconds +
                        $startupFailurePostSourceOpenElapsedMilliseconds) -
                    $startupFailureTotalMilliseconds) -gt 0.002)) -or
        $startupFailureMediaOpenedCompletionMilliseconds -lt 0 -or
        ($startupFailureMediaOpenedCompletionObserved -and
            ($startupFailureMediaOpenedCompletionMilliseconds -le 0 -or
                $startupFailureMediaOpenedCompletionMilliseconds -gt
                    ($startupFailureTotalMilliseconds + 0.002))) -or
        (-not $startupFailureMediaOpenedCompletionObserved -and
            ($startupFailureMediaOpenedCompletionMilliseconds -ne 0 -or
                $startupFailureMediaOpenedWithinWaitDeadline -or
                $startupFailureMediaOpenedWithinStartupBudget)) -or
        ($startupFailureMediaOpenedWithinStartupBudget -and
            -not $startupFailureMediaOpenedWithinWaitDeadline) -or
        ($startupFailureMediaOpenedWithinStartupBudget -and
            $startupFailureMediaOpenedCompletionMilliseconds -gt 5000.002) -or
        (-not $startupFailureMediaOpenedWithinStartupBudget -and
            $startupFailureMediaOpenedCompletionObserved -and
            $startupFailureMediaOpenedCompletionMilliseconds -lt 4999.998) -or
        $startupFailureActiveStageElapsedMilliseconds -lt 0 -or
        $startupFailureActiveStageElapsedMilliseconds -gt ($startupFailureTotalMilliseconds + 0.002) -or
        ($startupFailureSourceCreationMilliseconds +
            $startupFailureSourceAssignmentMilliseconds +
            $startupFailurePlayInvocationMilliseconds +
            $startupFailureActiveStageElapsedMilliseconds) -gt
            ($startupFailureTotalMilliseconds + 0.002)) {
        throw "The native playback startup failure diagnostic is invalid."
    }
    if ($startupFailureStage -eq "None") {
        if ($startupFailureSwitchOrdinal -ne 0 -or
            $startupFailureFixture -ne "None" -or
            $startupFailureAttemptCount -ne 0 -or
            $startupFailureSurfaceTransitionCount -ne 0 -or
            $startupFailureTotalMilliseconds -ne 0 -or
            $startupFailureSourceCreationMilliseconds -ne 0 -or
            $startupFailureSourceAssignmentMilliseconds -ne 0 -or
            $startupFailurePlayInvocationMilliseconds -ne 0 -or
            $startupFailureSourceOpenObserved -or
            $startupFailureSourceOpenError -or
            $startupFailureSourceOpenCompletionMilliseconds -ne 0 -or
            $startupFailurePostSourceOpenElapsedMilliseconds -ne 0 -or
            $startupFailureMediaOpenedCompletionObserved -or
            $startupFailureMediaOpenedCompletionMilliseconds -ne 0 -or
            $startupFailureMediaOpenedWithinWaitDeadline -or
            $startupFailureMediaOpenedWithinStartupBudget -or
            $startupFailureActiveStageElapsedMilliseconds -ne 0) {
            throw "The inactive native playback startup failure diagnostic is not empty."
        }
    }
    else {
        if ($isCompletedProbeResult -or
            $startupFailureTotalMilliseconds -le 0 -or
            $startupFailureActiveStageElapsedMilliseconds -le 0) {
            throw "The active native playback startup failure diagnostic is inconsistent."
        }
        if ($startupFailureStage -eq "SurfaceReadiness") {
            if ($startupFailureSwitchOrdinal -ne 0 -or
                $startupFailureFixture -ne "None" -or
                $startupFailureAttemptCount -ne 0 -or
                $startupFailureSurfaceTransitionCount -ne 0) {
                throw "The surface-readiness startup failure diagnostic is inconsistent."
            }
        }
        else {
            $expectedFailureSwitchOrdinal = [int]$probe.SwitchCount + 1
            $expectedFailureFixture = if (($expectedFailureSwitchOrdinal % 2) -eq 1) {
                "HlsH264AacMpegTs"
            }
            else {
                "DirectH264AacMpegTs"
            }
            $failureSwitchIndex = $expectedFailureSwitchOrdinal - 1
            $expectedFailureSurfaceTransitionCount = 0
            if ($SwitchCount -ge 25) {
                if ($failureSwitchIndex -eq [Math]::Floor($SwitchCount / 5.0) -or
                    $failureSwitchIndex -eq [Math]::Floor(($SwitchCount * 3.0) / 5.0)) {
                    $expectedFailureSurfaceTransitionCount = 1
                }
                elseif ($failureSwitchIndex -eq [Math]::Floor(($SwitchCount * 2.0) / 5.0) -or
                    $failureSwitchIndex -eq [Math]::Floor(($SwitchCount * 4.0) / 5.0)) {
                    $expectedFailureSurfaceTransitionCount = 2
                }
            }
            if ($startupFailureSwitchOrdinal -ne $expectedFailureSwitchOrdinal -or
                $startupFailureSwitchOrdinal -gt $SwitchCount -or
                -not [string]::Equals(
                    $startupFailureFixture,
                    $expectedFailureFixture,
                    [System.StringComparison]::Ordinal) -or
                $startupFailureAttemptCount -lt 1 -or
                $startupFailureAttemptCount -gt 2 -or
                $startupFailureAttemptCount -gt (1 + [int]$probe.PlaybackRetryCount) -or
                $startupFailureSurfaceTransitionCount -ne $expectedFailureSurfaceTransitionCount) {
                throw "The active native playback startup failure binding is invalid."
            }
        }
    }
    $allowedMediaOpenFailureStages = @(
        "SourceCreation",
        "SourceAssignment",
        "PlayInvocation",
        "MediaSourceOpenWait",
        "MediaOpenWait"
    )
    if (($startupFailureStage -eq "MediaSourceOpenWait" -and
            $startupFailureSourceOpenObserved) -or
        ($startupFailureStage -eq "MediaOpenWait" -and
            -not $startupFailureSourceOpenObserved)) {
        throw "The native playback source-open failure stage is inconsistent."
    }
    if ($probe.Failure -ne "MediaOpenTimeout" -and
        ($startupFailureMediaOpenedCompletionObserved -or
            $startupFailureMediaOpenedCompletionMilliseconds -ne 0 -or
            $startupFailureMediaOpenedWithinWaitDeadline -or
            $startupFailureMediaOpenedWithinStartupBudget)) {
        throw "The native playback MediaOpened timeout diagnostic is active outside its failure domain."
    }
    if (($probe.Failure -eq "SurfaceReadinessTimeout" -and
            $startupFailureStage -ne "SurfaceReadiness") -or
        ($probe.Failure -eq "MediaOpenTimeout" -and
            $startupFailureStage -notin $allowedMediaOpenFailureStages) -or
        ($probe.Failure -eq "PlaybackAdvanceTimeout" -and
            $startupFailureStage -ne "PlaybackAdvanceWait")) {
        throw "The native playback timeout failure is not bound to its active startup stage."
    }
    if ($isCompletedProbeResult -and
        ($firstHlsStartupStartedTimestamp -eq 0 -or
            $firstHlsMediaOpenedTimestamp -eq 0 -or
            $firstHlsWindowCompletedTimestamp -ne $firstHlsMediaOpenedTimestamp)) {
        throw "The completed native playback probe omitted first-HLS QPC attribution."
    }
    if ($firstHlsStartupStartedTimestamp -ne 0) {
        $requestTraceSnapshot = $tlsServer.GetRequestTraceSnapshot()
        $requestTraces = @($requestTraceSnapshot.Traces)
        $requestTraceDroppedCount = [int]$requestTraceSnapshot.DroppedCount
        $firstDroppedRequestAcceptedTimestamp =
            [long]$requestTraceSnapshot.FirstDroppedAcceptedTimestamp
        if ($requestTraces.Count -gt 32 -or
            $requestTraceDroppedCount -lt 0 -or
            ($requestTraceDroppedCount -eq 0 -and
                $firstDroppedRequestAcceptedTimestamp -ne 0) -or
            ($requestTraceDroppedCount -gt 0 -and
                ($requestTraces.Count -ne 32 -or
                    $firstDroppedRequestAcceptedTimestamp -le 0))) {
            throw "The bounded request trace snapshot is inconsistent."
        }

        $seenAcceptOrdinals = [System.Collections.Generic.HashSet[int]]::new()
        $seenRequestOrdinals = [System.Collections.Generic.HashSet[int]]::new()
        $previousAcceptOrdinal = 0
        $previousAcceptedTimestamp = 0L
        foreach ($trace in $requestTraces) {
            $traceAcceptOrdinal = [int]$trace.AcceptOrdinal
            $traceRequestOrdinal = [int]$trace.RequestOrdinal
            $traceResource = [string]$trace.Resource
            $traceMethod = [string]$trace.Method
            $traceRangeShape = [string]$trace.RangeShape
            $traceStatusCode = [int]$trace.StatusCode
            $traceBodyBytes = [long]$trace.BodyBytes
            $traceAcceptedTimestamp = [long]$trace.AcceptedTimestamp
            $traceTlsAuthenticatedTimestamp = [long]$trace.TlsAuthenticatedTimestamp
            $traceRequestHeaderCompletedTimestamp =
                [long]$trace.RequestHeaderCompletedTimestamp
            $traceResponseHeaderWrittenTimestamp =
                [long]$trace.ResponseHeaderWrittenTimestamp
            $traceBodyWriteCompletedTimestamp =
                [long]$trace.BodyWriteCompletedTimestamp
            $traceFlushCompletedTimestamp = [long]$trace.FlushCompletedTimestamp
            $traceOutcome = [string]$trace.Outcome
            $traceTerminalTimestamp = [long]$trace.TerminalTimestamp
            if ($traceAcceptOrdinal -le $previousAcceptOrdinal -or
                -not $seenAcceptOrdinals.Add($traceAcceptOrdinal) -or
                $traceRequestOrdinal -lt 0 -or
                ($traceRequestOrdinal -gt 0 -and
                    -not $seenRequestOrdinals.Add($traceRequestOrdinal)) -or
                $traceResource -notin @(
                    "Unclassified", "Direct", "Playlist",
                    "Segment0", "Segment1", "Segment2", "Segment3") -or
                $traceMethod -notin @("Pending", "Unsupported", "Get", "Head") -or
                $traceRangeShape -notin @(
                    "Pending", "Invalid", "None", "OpenEnded", "Suffix", "Bounded") -or
                $traceStatusCode -notin @(0, 200, 206, 404, 405, 416, 431, 503) -or
                $traceBodyBytes -lt 0 -or
                $traceAcceptedTimestamp -le 0 -or
                ($previousAcceptedTimestamp -ne 0 -and
                    $traceAcceptedTimestamp -lt $previousAcceptedTimestamp) -or
                ($traceTlsAuthenticatedTimestamp -ne 0 -and
                    $traceTlsAuthenticatedTimestamp -lt $traceAcceptedTimestamp) -or
                ($traceRequestHeaderCompletedTimestamp -ne 0 -and
                    ($traceTlsAuthenticatedTimestamp -eq 0 -or
                        $traceRequestHeaderCompletedTimestamp -lt
                            $traceTlsAuthenticatedTimestamp)) -or
                ($traceResponseHeaderWrittenTimestamp -ne 0 -and
                    ($traceTlsAuthenticatedTimestamp -eq 0 -or
                        ($traceRequestHeaderCompletedTimestamp -ne 0 -and
                            $traceResponseHeaderWrittenTimestamp -lt
                                $traceRequestHeaderCompletedTimestamp) -or
                        ($traceRequestHeaderCompletedTimestamp -eq 0 -and
                            $traceResponseHeaderWrittenTimestamp -lt
                                $traceTlsAuthenticatedTimestamp))) -or
                ($traceBodyWriteCompletedTimestamp -ne 0 -and
                    ($traceResponseHeaderWrittenTimestamp -eq 0 -or
                        $traceBodyWriteCompletedTimestamp -lt
                            $traceResponseHeaderWrittenTimestamp)) -or
                ($traceFlushCompletedTimestamp -ne 0 -and
                    ($traceBodyWriteCompletedTimestamp -eq 0 -or
                        $traceFlushCompletedTimestamp -lt
                            $traceBodyWriteCompletedTimestamp)) -or
                $traceOutcome -notin @(
                    "InFlight", "Completed", "IoAbort", "AuthFailure",
                    "Rejected", "TransportFailure") -or
                (($traceOutcome -eq "InFlight") -ne ($traceTerminalTimestamp -eq 0)) -or
                ($traceTerminalTimestamp -ne 0 -and
                    ($traceTerminalTimestamp -lt $traceAcceptedTimestamp -or
                        ($traceTlsAuthenticatedTimestamp -ne 0 -and
                            $traceTerminalTimestamp -lt $traceTlsAuthenticatedTimestamp) -or
                        ($traceRequestHeaderCompletedTimestamp -ne 0 -and
                            $traceTerminalTimestamp -lt $traceRequestHeaderCompletedTimestamp) -or
                        ($traceResponseHeaderWrittenTimestamp -ne 0 -and
                            $traceTerminalTimestamp -lt $traceResponseHeaderWrittenTimestamp) -or
                        ($traceBodyWriteCompletedTimestamp -ne 0 -and
                            $traceTerminalTimestamp -lt $traceBodyWriteCompletedTimestamp) -or
                        ($traceFlushCompletedTimestamp -ne 0 -and
                            $traceTerminalTimestamp -lt $traceFlushCompletedTimestamp)))) {
                throw "A bounded request lifecycle trace is outside policy."
            }
            if ($traceOutcome -eq "Completed") {
                if ($traceRequestOrdinal -le 0 -or
                    $traceResource -notin @(
                        "Direct", "Playlist", "Segment0", "Segment1", "Segment2", "Segment3") -or
                    $traceMethod -notin @("Get", "Head") -or
                    $traceRangeShape -notin @("None", "OpenEnded", "Suffix", "Bounded") -or
                    $traceStatusCode -notin @(200, 206) -or
                    (($traceStatusCode -eq 200) -ne ($traceRangeShape -eq "None")) -or
                    ($traceMethod -eq "Get" -and $traceBodyBytes -le 0) -or
                    ($traceMethod -eq "Head" -and $traceBodyBytes -ne 0) -or
                    $traceTlsAuthenticatedTimestamp -eq 0 -or
                    $traceRequestHeaderCompletedTimestamp -eq 0 -or
                    $traceResponseHeaderWrittenTimestamp -eq 0 -or
                    $traceBodyWriteCompletedTimestamp -eq 0 -or
                    $traceFlushCompletedTimestamp -eq 0 -or
                    $traceTerminalTimestamp -ne $traceFlushCompletedTimestamp) {
                    throw "A completed bounded request lifecycle trace is inconsistent."
                }
            }
            elseif ($traceOutcome -eq "Rejected") {
                if ($traceStatusCode -notin @(404, 405, 416, 431, 503) -or
                    $traceResponseHeaderWrittenTimestamp -eq 0 -or
                    $traceBodyBytes -ne 0 -or
                    $traceBodyWriteCompletedTimestamp -ne 0 -or
                    $traceFlushCompletedTimestamp -ne 0 -or
                    $traceTerminalTimestamp -ne $traceResponseHeaderWrittenTimestamp) {
                    throw "A rejected bounded request lifecycle trace is inconsistent."
                }
            }
            elseif ($traceBodyBytes -ne 0) {
                throw "A non-completed bounded request lifecycle trace reports body bytes."
            }
            $previousAcceptOrdinal = $traceAcceptOrdinal
            $previousAcceptedTimestamp = $traceAcceptedTimestamp
        }
        if ($firstDroppedRequestAcceptedTimestamp -ne 0 -and
            $previousAcceptedTimestamp -ne 0 -and
            $firstDroppedRequestAcceptedTimestamp -lt $previousAcceptedTimestamp) {
            throw "The bounded request lifecycle trace is not accept-ordered."
        }

        $firstHlsWindowStartFloor = $firstHlsStartupStartedTimestamp - 1
        $firstHlsWindowEndCeiling = $firstHlsWindowCompletedTimestamp + 1
        if ($firstDroppedRequestAcceptedTimestamp -ne 0 -and
            $firstDroppedRequestAcceptedTimestamp -le $firstHlsWindowEndCeiling) {
            throw "The bounded request lifecycle trace was truncated during the first-HLS window."
        }
        $firstHlsWindowTraces = @($requestTraces | Where-Object {
            [long]$_.AcceptedTimestamp -ge $firstHlsWindowStartFloor -and
            [long]$_.AcceptedTimestamp -le $firstHlsWindowEndCeiling
        })
        if (($firstHlsSourceOpenCompletedTimestamp -ne 0 -or
                $firstHlsMediaOpenedTimestamp -ne 0) -and
            $firstHlsWindowTraces.Count -eq 0) {
            throw "The first-HLS QPC window has no bounded transport trace."
        }
        $completedFirstHlsTraces = @()
        if ($firstHlsWindowTraces.Count -gt 0) {
            $nonCompletedFirstHlsWindowTraces = @($firstHlsWindowTraces | Where-Object {
                [string]$_.Outcome -ne "Completed"
            })
            if ($nonCompletedFirstHlsWindowTraces.Count -gt 0) {
                $nonCompletedOutcomes = @($nonCompletedFirstHlsWindowTraces |
                    Group-Object -Property Outcome |
                    Sort-Object -Property Name |
                    ForEach-Object { "$($_.Name)=$($_.Count)" })
                throw "The first-HLS QPC window contains non-completed transport lifecycle traces: $($nonCompletedOutcomes -join ', ')."
            }
            $unexpectedCompletedFirstHlsWindowTraces = @($firstHlsWindowTraces | Where-Object {
                [string]$_.Resource -notin @("Playlist", "Segment0", "Segment1", "Segment2", "Segment3")
            })
            if ($unexpectedCompletedFirstHlsWindowTraces.Count -gt 0) {
                throw "The first-HLS QPC window contains a completed non-HLS transport trace."
            }
            $completedFirstHlsTraces = @($firstHlsWindowTraces)
            if (($firstHlsSourceOpenCompletedTimestamp -ne 0 -or
                    $firstHlsMediaOpenedTimestamp -ne 0) -and
                $completedFirstHlsTraces.Count -eq 0) {
                throw "The first-HLS QPC window has no completed HLS response trace."
            }
        }
        if ($completedFirstHlsTraces.Count -gt 0) {
            $firstHlsTransportAttributionObserved = $true
            $firstHlsTraceRequestCount = $completedFirstHlsTraces.Count
            $firstAcceptedTimestamp = [long](
                $completedFirstHlsTraces |
                    Measure-Object -Property AcceptedTimestamp -Minimum).Minimum
            $firstHeaderTimestamp = [long](
                $completedFirstHlsTraces |
                    Measure-Object -Property RequestHeaderCompletedTimestamp -Minimum).Minimum
            $lastFlushTimestamp = [long](
                $completedFirstHlsTraces |
                    Measure-Object -Property FlushCompletedTimestamp -Maximum).Maximum
            foreach ($trace in $completedFirstHlsTraces) {
                $firstHlsTraceBodyBytes += [long]$trace.BodyBytes
                if ([string]$trace.Resource -eq "Playlist") {
                    $firstHlsTracePlaylistResponseCount++
                }
                else {
                    $firstHlsTraceSegmentResponseCount++
                }
                if ($firstHlsSourceOpenCompletedTimestamp -ne 0 -and
                    [long]$trace.FlushCompletedTimestamp -le
                        ($firstHlsSourceOpenCompletedTimestamp + 1)) {
                    $firstHlsTraceResponsesBeforeSourceOpen++
                }
                if ($firstHlsMediaOpenedTimestamp -ne 0 -and
                    [long]$trace.FlushCompletedTimestamp -le ($firstHlsMediaOpenedTimestamp + 1)) {
                    $firstHlsTraceResponsesBeforeMediaOpened++
                }
                $tlsAuthenticationMilliseconds = Get-QpcDeltaMilliseconds `
                    -StartTimestamp ([long]$trace.AcceptedTimestamp) `
                    -EndTimestamp ([long]$trace.TlsAuthenticatedTimestamp) `
                    -Frequency $firstHlsClockFrequency
                $firstHlsTotalTlsAuthenticationMilliseconds += $tlsAuthenticationMilliseconds
                $firstHlsMaximumTlsAuthenticationMilliseconds = [Math]::Max(
                    $firstHlsMaximumTlsAuthenticationMilliseconds,
                    $tlsAuthenticationMilliseconds)
            }
            $firstHlsStartupToFirstAcceptMilliseconds = Get-QpcDeltaMilliseconds `
                -StartTimestamp $firstHlsStartupStartedTimestamp `
                -EndTimestamp $firstAcceptedTimestamp `
                -Frequency $firstHlsClockFrequency
            $firstHlsStartupToFirstHeaderMilliseconds = Get-QpcDeltaMilliseconds `
                -StartTimestamp $firstHlsStartupStartedTimestamp `
                -EndTimestamp $firstHeaderTimestamp `
                -Frequency $firstHlsClockFrequency
            $firstHlsFirstHeaderToLastFlushMilliseconds = Get-QpcDeltaMilliseconds `
                -StartTimestamp $firstHeaderTimestamp `
                -EndTimestamp $lastFlushTimestamp `
                -Frequency $firstHlsClockFrequency
            if ($firstHlsSourceOpenCompletedTimestamp -ne 0) {
                $firstHlsLastFlushToSourceOpenMilliseconds = Get-QpcDeltaMilliseconds `
                    -StartTimestamp $lastFlushTimestamp `
                    -EndTimestamp $firstHlsSourceOpenCompletedTimestamp `
                    -Frequency $firstHlsClockFrequency
            }
            if ($firstHlsMediaOpenedTimestamp -ne 0) {
                $firstHlsLastFlushToMediaOpenedMilliseconds = Get-QpcDeltaMilliseconds `
                    -StartTimestamp $lastFlushTimestamp `
                    -EndTimestamp $firstHlsMediaOpenedTimestamp `
                    -Frequency $firstHlsClockFrequency
            }
            Write-Host "First-HLS transport attribution: requests=$firstHlsTraceRequestCount, playlistResponses=$firstHlsTracePlaylistResponseCount, segmentResponses=$firstHlsTraceSegmentResponseCount, bodyBytes=$firstHlsTraceBodyBytes, responsesBeforeSourceOpen=$firstHlsTraceResponsesBeforeSourceOpen, responsesBeforeMediaOpened=$firstHlsTraceResponsesBeforeMediaOpened, startupToFirstAccept=$firstHlsStartupToFirstAcceptMilliseconds, startupToFirstHeader=$firstHlsStartupToFirstHeaderMilliseconds, maxTlsAuthentication=$firstHlsMaximumTlsAuthenticationMilliseconds, totalTlsAuthentication=$firstHlsTotalTlsAuthenticationMilliseconds, firstHeaderToLastFlush=$firstHlsFirstHeaderToLastFlushMilliseconds, lastFlushToSourceOpen=$firstHlsLastFlushToSourceOpenMilliseconds, lastFlushToMediaOpened=$firstHlsLastFlushToMediaOpenedMilliseconds, traceRecordsOmittedAfterCapacity=$requestTraceDroppedCount."
        }
    }
    if ($SoakMinutes -gt 0 -and $isCompletedProbeResult) {
        foreach ($networkTrace in $networkRecoveryTrace) {
            $injectedElapsedMilliseconds = Get-QpcDeltaMilliseconds `
                -StartTimestamp ([long]$resourceSampleTrace[0].CapturedTimestamp) `
                -EndTimestamp ([long]$networkTrace.InjectedTimestamp) `
                -Frequency $firstHlsClockFrequency
            $recoveryElapsedMilliseconds = Get-QpcDeltaMilliseconds `
                -StartTimestamp ([long]$resourceSampleTrace[0].CapturedTimestamp) `
                -EndTimestamp ([long]$networkTrace.RecoveryTimestamp) `
                -Frequency $firstHlsClockFrequency
            Write-Host "Native playback network recovery trace: ordinal=$($networkTrace.Ordinal), injectedRequestOrdinal=$($networkTrace.InjectedRequestOrdinal), injectedElapsedMilliseconds=$injectedElapsedMilliseconds, recoveryRequestOrdinal=$($networkTrace.RecoveryRequestOrdinal), recoveryElapsedMilliseconds=$recoveryElapsedMilliseconds."
        }
        foreach ($resourceSample in $resourceSampleTrace) {
            $recoveryPhase = "BeforeFirstRecovery"
            $relatedRecoveryOrdinal = 0
            $relatedRequestOrdinal = 0
            foreach ($networkTrace in $networkRecoveryTrace) {
                if ([long]$resourceSample.CapturedTimestamp -lt [long]$networkTrace.InjectedTimestamp) {
                    break
                }
                $relatedRecoveryOrdinal = [int]$networkTrace.Ordinal
                if ([long]$resourceSample.CapturedTimestamp -lt [long]$networkTrace.RecoveryTimestamp) {
                    $recoveryPhase = "RecoveryPending"
                    $relatedRequestOrdinal = [int]$networkTrace.InjectedRequestOrdinal
                    break
                }
                $recoveryPhase = "AfterRecovery"
                $relatedRequestOrdinal = [int]$networkTrace.RecoveryRequestOrdinal
            }
            Write-Host "Native playback resource sample: ordinal=$($resourceSample.Ordinal), elapsedMilliseconds=$($resourceSample.ElapsedMilliseconds), privateBytes=$($resourceSample.PrivateBytes), handleCount=$($resourceSample.HandleCount), phase=$($resourceSample.Phase), switchOrdinal=$($resourceSample.SwitchOrdinal), recoveryPhase=$recoveryPhase, recoveryOrdinal=$relatedRecoveryOrdinal, relatedRequestOrdinal=$relatedRequestOrdinal."
        }
        if ($postWarmupResourceSamples.Count -ge 2) {
            Write-Host "Native playback post-warm resource summary: count=$($postWarmupResourceSamples.Count), warmupOrdinal=$($postWarmupResourceSamples[0].Ordinal), warmupElapsedMilliseconds=$($postWarmupResourceSamples[0].ElapsedMilliseconds), warmupPrivateBytes=$($postWarmupResourceSamples[0].PrivateBytes), minimumOrdinal=$($postWarmupMinimumSample.Ordinal), minimumElapsedMilliseconds=$($postWarmupMinimumSample.ElapsedMilliseconds), minimumPrivateBytes=$($postWarmupMinimumSample.PrivateBytes), maximumOrdinal=$($postWarmupMaximumSample.Ordinal), maximumElapsedMilliseconds=$($postWarmupMaximumSample.ElapsedMilliseconds), maximumPrivateBytes=$($postWarmupMaximumSample.PrivateBytes), peakOrdinal=$($postWarmupMaximumSample.Ordinal), finalOrdinal=$($postWarmupFinalSample.Ordinal), finalElapsedMilliseconds=$($postWarmupFinalSample.ElapsedMilliseconds), finalPrivateBytes=$($postWarmupFinalSample.PrivateBytes), finalHandleCount=$($postWarmupFinalSample.HandleCount)."
        }
    }
    $expectedSurfaceTransitions = if ($SwitchCount -ge 25) { 6 } else { 0 }
    $expectedDetachedSourceCount =
        $SwitchCount +
        [int]$probe.PlaybackRetryCount +
        $cancellationSourceDetachCount +
        $cancellationRecoverySourceDetachCount
    if ($SoakMinutes -gt 0) { $expectedDetachedSourceCount++ }
    $completedLifecycleInvariantPassed =
        [int]$probe.SwitchCount -eq $SwitchCount -and
        [int]$probe.SurfaceTransitionCount -eq $expectedSurfaceTransitions -and
        [int]$probe.DetachedSourceCount -eq $expectedDetachedSourceCount -and
        [int]$probe.PlaybackRetryCount -le $NetworkInterruptionCount
    $minimumResourceSampleCount = [Math]::Max(2, [Math]::Floor($SoakMinutes / 5) - 2)
    $resourceBudgetPredicateFailed =
        $SoakMinutes -gt 0 -and (
            [int]$probe.ResourceSampleCount -lt $minimumResourceSampleCount -or
            [bool]$probe.MemoryMonotonicIncrease -or
            [long]$probe.MemoryNetGrowthBytes -gt 104857600 -or
            [double]$probe.MemoryNetGrowthPercent -gt 10)
    if ($isCompletedResourceFailure) {
        if (-not $completedLifecycleInvariantPassed -or
            [int]$probe.SoakMinutes -ne $SoakMinutes) {
            Set-FailurePoint -Stage "ProbeValidation" -Code "ProbeInvariantFailed"
            throw "The native playback resource failure did not preserve completed probe invariants."
        }
        if (-not $resourceBudgetPredicateFailed) {
            Set-FailurePoint -Stage "ProbeValidation" -Code "ProbeInvariantFailed"
            throw "The native playback resource failure did not identify a failing resource budget predicate."
        }
    }
    if ((-not $isCompletedResourceFailure -and
            ($probe.Success -ne $true -or $probe.Failure -ne "None")) -or
        -not $completedLifecycleInvariantPassed) {
        throw "Native playback probe failed with category '$($probe.Failure)': completedSwitches=$($probe.SwitchCount), detachedSources=$($probe.DetachedSourceCount), playbackRetries=$($probe.PlaybackRetryCount), startupFailureStage=$startupFailureStage, startupFailureOrdinal=$startupFailureSwitchOrdinal, startupFailureFixture=$startupFailureFixture, startupFailureAttempts=$startupFailureAttemptCount, startupFailureTransitions=$startupFailureSurfaceTransitionCount, startupFailureTotal=$startupFailureTotalMilliseconds, startupFailureSourceCreation=$startupFailureSourceCreationMilliseconds, startupFailureSourceAssignment=$startupFailureSourceAssignmentMilliseconds, startupFailurePlayInvocation=$startupFailurePlayInvocationMilliseconds, startupFailureSourceOpenObserved=$startupFailureSourceOpenObserved, startupFailureSourceOpenError=$startupFailureSourceOpenError, startupFailureSourceOpenCompletion=$startupFailureSourceOpenCompletionMilliseconds, startupFailurePostSourceOpenElapsed=$startupFailurePostSourceOpenElapsedMilliseconds, startupFailureMediaOpenedObserved=$startupFailureMediaOpenedCompletionObserved, startupFailureMediaOpenedCompletion=$startupFailureMediaOpenedCompletionMilliseconds, startupFailureMediaOpenedWithinWaitDeadline=$startupFailureMediaOpenedWithinWaitDeadline, startupFailureMediaOpenedWithinStartupBudget=$startupFailureMediaOpenedWithinStartupBudget, startupFailureActiveStageElapsed=$startupFailureActiveStageElapsedMilliseconds, startupMaximumOrdinal=$($probe.StartupMaximumSwitchOrdinal), startupMaximumFixture=$($probe.StartupMaximumFixture), startupMaximumAttempts=$($probe.StartupMaximumAttemptCount), startupMaximumTransitions=$($probe.StartupMaximumSurfaceTransitionCount), startupMaximumPreWait=$($probe.StartupMaximumPreWaitMilliseconds), startupMaximumMediaOpenWait=$($probe.StartupMaximumMediaOpenWaitMilliseconds), startupMaximumSourceOpenObserved=$($probe.StartupMaximumSourceOpen.CompletionObserved), startupMaximumSourceOpenError=$($probe.StartupMaximumSourceOpen.ErrorPresent), startupMaximumSourceOpenCompletion=$($probe.StartupMaximumSourceOpen.CompletionMilliseconds), startupMaximumPostSourceOpenMediaOpened=$($probe.StartupMaximumSourceOpen.PostCompletionElapsedMilliseconds), hlsMaximum=$($probe.HlsStartupMaximumMilliseconds), directMaximum=$($probe.DirectStartupMaximumMilliseconds), playbackStateBeforeDetach=$($probe.PlaybackStateBeforeDetach), sourceDetached=$($probe.SourceDetached), canPauseBeforeDetach=$($probe.CanPauseBeforeDetach), canSeekBeforeDetach=$($probe.CanSeekBeforeDetach), teardownStage=$($probe.TeardownStage), exceptionCategory=$($probe.ExceptionCategory), exceptionHResult=$($probe.ExceptionHResult), surfaceTransitions=$($probe.SurfaceTransitionCount), injectedInterruptions=$($tlsServer.InjectedFailureCount), recoveries=$($tlsServer.RecoveryCount), injectedRequestOrdinal=$($tlsServer.LastInjectedRequestOrdinal), recoveryRequestOrdinal=$($tlsServer.LastRecoveryRequestOrdinal), cancellationProbes=$cancellationProbeCount, cancellationsObserved=$cancellationObservedCount, cancellationDetaches=$cancellationSourceDetachCount, cancellationRecoveries=$cancellationRecoveryCount, cancellationRecoveryDetaches=$cancellationRecoverySourceDetachCount, cancellationLatency=$cancellationLatencyMilliseconds, cancellationQuiescence=$cancellationQuiescenceMilliseconds, cancellationObservation=$cancellationObservationMilliseconds, cancellationSourceNull=$cancellationSourceNullAfterObservation, cancellationFreshRecovery=$cancellationRecoveryUsedFreshSource, cancellationNoAutomaticRestart=$cancellationNoAutomaticRestart, firstHlsTransportObserved=$firstHlsTransportAttributionObserved, firstHlsRequests=$firstHlsTraceRequestCount, firstHlsBodyBytes=$firstHlsTraceBodyBytes, firstHlsResponsesBeforeSourceOpen=$firstHlsTraceResponsesBeforeSourceOpen, firstHlsResponsesBeforeMediaOpened=$firstHlsTraceResponsesBeforeMediaOpened, firstHlsLastFlushToSourceOpen=$firstHlsLastFlushToSourceOpenMilliseconds, firstHlsLastFlushToMediaOpened=$firstHlsLastFlushToMediaOpenedMilliseconds, h264Decoder=$h264DecoderRegistered, aacDecoder=$aacDecoderRegistered, audioService=$audioServiceRunning, audioEndpointService=$audioEndpointServiceRunning, userInteractive=$userInteractive, installationType=$installationType, accepted=$($tlsServer.RequestCount), completed=$($tlsServer.CompletedResponseCount), head=$($tlsServer.HeadRequestCount), range=$($tlsServer.RangeRequestCount), openEnded=$($tlsServer.OpenEndedRangeCount), suffix=$($tlsServer.SuffixRangeCount), bounded=$($tlsServer.BoundedRangeCount), bodyBytes=$($tlsServer.CompletedBodyBytes), ioAbort=$($tlsServer.IoAbortCount), transportFailure=$($tlsServer.FailureCount)."
    }
    $startupMaximumSwitchOrdinal = [int]$probe.StartupMaximumSwitchOrdinal
    $startupMaximumFixture = [string]$probe.StartupMaximumFixture
    $startupMaximumAttemptCount = [int]$probe.StartupMaximumAttemptCount
    $startupMaximumSurfaceTransitionCount = [int]$probe.StartupMaximumSurfaceTransitionCount
    $startupMaximumPreWaitMilliseconds = [double]$probe.StartupMaximumPreWaitMilliseconds
    $startupMaximumMediaOpenWaitMilliseconds = [double]$probe.StartupMaximumMediaOpenWaitMilliseconds
    $startupMaximumSourceOpenObserved =
        [bool]$probe.StartupMaximumSourceOpen.CompletionObserved
    $startupMaximumSourceOpenError = [bool]$probe.StartupMaximumSourceOpen.ErrorPresent
    $startupMaximumSourceOpenCompletionMilliseconds =
        [double]$probe.StartupMaximumSourceOpen.CompletionMilliseconds
    $startupMaximumPostSourceOpenMediaOpenedMilliseconds =
        [double]$probe.StartupMaximumSourceOpen.PostCompletionElapsedMilliseconds
    $startupMaximumMilliseconds = [double]$probe.StartupMaximumMilliseconds
    $hlsStartupP95Milliseconds = [double]$probe.HlsStartupP95Milliseconds
    $directStartupP95Milliseconds = [double]$probe.DirectStartupP95Milliseconds
    $hlsStartupMaximumMilliseconds = [double]$probe.HlsStartupMaximumMilliseconds
    $directStartupMaximumMilliseconds = [double]$probe.DirectStartupMaximumMilliseconds
    $maximumSwitchIndex = $startupMaximumSwitchOrdinal - 1
    $expectedMaximumFixture = if (($startupMaximumSwitchOrdinal % 2) -eq 1) {
        "HlsH264AacMpegTs"
    }
    else {
        "DirectH264AacMpegTs"
    }
    $expectedMaximumSurfaceTransitionCount = 0
    if ($SwitchCount -ge 25) {
        if ($maximumSwitchIndex -eq [Math]::Floor($SwitchCount / 5.0) -or
            $maximumSwitchIndex -eq [Math]::Floor(($SwitchCount * 3.0) / 5.0)) {
            $expectedMaximumSurfaceTransitionCount = 1
        }
        elseif ($maximumSwitchIndex -eq [Math]::Floor(($SwitchCount * 2.0) / 5.0) -or
                $maximumSwitchIndex -eq [Math]::Floor(($SwitchCount * 4.0) / 5.0)) {
            $expectedMaximumSurfaceTransitionCount = 2
        }
    }
    if ($startupMaximumSwitchOrdinal -lt 1 -or
        $startupMaximumSwitchOrdinal -gt $SwitchCount -or
        -not [string]::Equals(
            $startupMaximumFixture,
            $expectedMaximumFixture,
            [System.StringComparison]::Ordinal) -or
        $startupMaximumAttemptCount -lt 1 -or
        $startupMaximumAttemptCount -gt 2 -or
        $startupMaximumAttemptCount -gt (1 + [int]$probe.PlaybackRetryCount) -or
        $startupMaximumSurfaceTransitionCount -ne $expectedMaximumSurfaceTransitionCount -or
        $startupMaximumPreWaitMilliseconds -lt 0 -or
        $startupMaximumMediaOpenWaitMilliseconds -lt 0 -or
        ($startupMaximumSourceOpenObserved -and
            ($startupMaximumSourceOpenError -or
                $startupMaximumSourceOpenCompletionMilliseconds -le 0 -or
                [Math]::Abs(
                    ($startupMaximumSourceOpenCompletionMilliseconds +
                        $startupMaximumPostSourceOpenMediaOpenedMilliseconds) -
                    $startupMaximumMilliseconds) -gt 0.002)) -or
        [Math]::Abs(
            ($startupMaximumPreWaitMilliseconds + $startupMaximumMediaOpenWaitMilliseconds) -
            $startupMaximumMilliseconds) -gt 0.002 -or
        $hlsStartupP95Milliseconds -lt 0 -or
        $directStartupP95Milliseconds -lt 0 -or
        $hlsStartupMaximumMilliseconds -lt $hlsStartupP95Milliseconds -or
        $directStartupMaximumMilliseconds -lt $directStartupP95Milliseconds -or
        $hlsStartupMaximumMilliseconds -gt $startupMaximumMilliseconds -or
        $directStartupMaximumMilliseconds -gt $startupMaximumMilliseconds -or
        [Math]::Abs(
            [Math]::Max($hlsStartupMaximumMilliseconds, $directStartupMaximumMilliseconds) -
            $startupMaximumMilliseconds) -gt 0.001 -or
        ($startupMaximumFixture -eq "HlsH264AacMpegTs" -and
            [Math]::Abs($hlsStartupMaximumMilliseconds - $startupMaximumMilliseconds) -gt 0.001) -or
        ($startupMaximumFixture -eq "DirectH264AacMpegTs" -and
            [Math]::Abs($directStartupMaximumMilliseconds - $startupMaximumMilliseconds) -gt 0.001)) {
        throw "Native playback startup diagnostic invariant failed."
    }
    Write-Host "Native playback startup diagnostic: maximum=$($probe.StartupMaximumMilliseconds), ordinal=$startupMaximumSwitchOrdinal, fixture=$startupMaximumFixture, attempts=$startupMaximumAttemptCount, surfaceTransitions=$startupMaximumSurfaceTransitionCount, preWait=$startupMaximumPreWaitMilliseconds, mediaOpenWait=$startupMaximumMediaOpenWaitMilliseconds, sourceOpenObserved=$startupMaximumSourceOpenObserved, sourceOpenError=$startupMaximumSourceOpenError, sourceOpenCompletion=$startupMaximumSourceOpenCompletionMilliseconds, postSourceOpenMediaOpened=$startupMaximumPostSourceOpenMediaOpenedMilliseconds, hlsMaximum=$hlsStartupMaximumMilliseconds, directMaximum=$directStartupMaximumMilliseconds, playbackRetries=$($probe.PlaybackRetryCount)."
    if ([double]$probe.StartupP95Milliseconds -gt 3000 -or [double]$probe.StartupMaximumMilliseconds -gt 5000) {
        throw "Native playback startup budget failed: p95=$($probe.StartupP95Milliseconds), maximum=$($probe.StartupMaximumMilliseconds), maximumOrdinal=$($probe.StartupMaximumSwitchOrdinal), maximumFixture=$($probe.StartupMaximumFixture), maximumAttempts=$($probe.StartupMaximumAttemptCount), maximumSurfaceTransitions=$($probe.StartupMaximumSurfaceTransitionCount), maximumPreWait=$($probe.StartupMaximumPreWaitMilliseconds), maximumMediaOpenWait=$($probe.StartupMaximumMediaOpenWaitMilliseconds), maximumSourceOpenObserved=$startupMaximumSourceOpenObserved, maximumSourceOpenError=$startupMaximumSourceOpenError, maximumSourceOpenCompletion=$startupMaximumSourceOpenCompletionMilliseconds, maximumPostSourceOpenMediaOpened=$startupMaximumPostSourceOpenMediaOpenedMilliseconds, hlsP95=$($probe.HlsStartupP95Milliseconds), hlsMaximum=$($probe.HlsStartupMaximumMilliseconds), directP95=$($probe.DirectStartupP95Milliseconds), directMaximum=$($probe.DirectStartupMaximumMilliseconds), playbackRetries=$($probe.PlaybackRetryCount), firstHlsTransportObserved=$firstHlsTransportAttributionObserved, firstHlsRequests=$firstHlsTraceRequestCount, firstHlsPlaylistResponses=$firstHlsTracePlaylistResponseCount, firstHlsSegmentResponses=$firstHlsTraceSegmentResponseCount, firstHlsBodyBytes=$firstHlsTraceBodyBytes, firstHlsResponsesBeforeSourceOpen=$firstHlsTraceResponsesBeforeSourceOpen, firstHlsResponsesBeforeMediaOpened=$firstHlsTraceResponsesBeforeMediaOpened, firstHlsStartupToFirstAccept=$firstHlsStartupToFirstAcceptMilliseconds, firstHlsStartupToFirstHeader=$firstHlsStartupToFirstHeaderMilliseconds, firstHlsMaxTlsAuthentication=$firstHlsMaximumTlsAuthenticationMilliseconds, firstHlsTotalTlsAuthentication=$firstHlsTotalTlsAuthenticationMilliseconds, firstHlsFirstHeaderToLastFlush=$firstHlsFirstHeaderToLastFlushMilliseconds, firstHlsLastFlushToSourceOpen=$firstHlsLastFlushToSourceOpenMilliseconds, firstHlsLastFlushToMediaOpened=$firstHlsLastFlushToMediaOpenedMilliseconds."
    }
    if ([double]$probe.SourceDetachP95Milliseconds -gt 3000 -or [double]$probe.SourceDetachMaximumMilliseconds -gt 5000) {
        throw "Native playback source-detachment budget failed: p95=$($probe.SourceDetachP95Milliseconds), maximum=$($probe.SourceDetachMaximumMilliseconds)."
    }
    if ($SoakMinutes -gt 0 -and (
        [int]$probe.SoakMinutes -ne $SoakMinutes -or
        $resourceBudgetPredicateFailed)) {
        Set-FailurePoint -Stage "SoakValidation" -Code "ResourceBudgetExceeded"
        Write-Host "Native playback soak resource diagnostic: completedSwitches=$($probe.SwitchCount), detachedSources=$($probe.DetachedSourceCount), surfaceTransitions=$($probe.SurfaceTransitionCount), playbackRetries=$($probe.PlaybackRetryCount), soakMinutes=$($probe.SoakMinutes), resourceSamples=$($probe.ResourceSampleCount), warmupPrivateBytes=$($probe.WarmupPrivateBytes), memoryNetGrowthBytes=$($probe.MemoryNetGrowthBytes), memoryNetGrowthPercent=$($probe.MemoryNetGrowthPercent), memoryMonotonicIncrease=$($probe.MemoryMonotonicIncrease), warmupHandleCount=$($probe.WarmupHandleCount), handleNetGrowth=$($probe.HandleNetGrowth), initialPrivateBytes=$($probe.InitialPrivateBytes), finalPrivateBytes=$($probe.FinalPrivateBytes), initialHandleCount=$($probe.InitialHandleCount), finalHandleCount=$($probe.FinalHandleCount)."
        throw "Native playback soak resource budget failed: soakMinutes=$($probe.SoakMinutes), resourceSamples=$($probe.ResourceSampleCount), warmupPrivateBytes=$($probe.WarmupPrivateBytes), memoryNetGrowthBytes=$($probe.MemoryNetGrowthBytes), memoryNetGrowthPercent=$($probe.MemoryNetGrowthPercent), memoryMonotonicIncrease=$($probe.MemoryMonotonicIncrease), warmupHandleCount=$($probe.WarmupHandleCount), handleNetGrowth=$($probe.HandleNetGrowth), initialPrivateBytes=$($probe.InitialPrivateBytes), finalPrivateBytes=$($probe.FinalPrivateBytes), initialHandleCount=$($probe.InitialHandleCount), finalHandleCount=$($probe.FinalHandleCount)."
    }
    Set-FailurePoint -Stage "ProcessExit" -Code "NormalCloseFailed"
    $launchedProcess.Refresh()
    if ($launchedProcess.HasExited) {
        throw "The native playback probe exited before the required normal close."
    }
    Close-TrackedProcessNormally -Process $launchedProcess
    $processExitedWithoutForce = $true

    Set-FailurePoint -Stage "TlsShutdown" -Code "TlsServerDrainFailed"
    $tlsServer.Dispose()
    $tlsRequestCount = $tlsServer.RequestCount
    $tlsFailureCount = $tlsServer.FailureCount
    $tlsCompletedResponseCount = $tlsServer.CompletedResponseCount
    $tlsIoAbortCount = $tlsServer.IoAbortCount
    $tlsHeadRequestCount = $tlsServer.HeadRequestCount
    $tlsRangeRequestCount = $tlsServer.RangeRequestCount
    $tlsOpenEndedRangeCount = $tlsServer.OpenEndedRangeCount
    $tlsSuffixRangeCount = $tlsServer.SuffixRangeCount
    $tlsBoundedRangeCount = $tlsServer.BoundedRangeCount
    $tlsInjectedFailureCount = $tlsServer.InjectedFailureCount
    $tlsRecoveryCount = $tlsServer.RecoveryCount
    $tlsLastInjectedRequestOrdinal = $tlsServer.LastInjectedRequestOrdinal
    $tlsLastRecoveryRequestOrdinal = $tlsServer.LastRecoveryRequestOrdinal
    $tlsCompletedBodyBytes = $tlsServer.CompletedBodyBytes
    $tlsServer = $null
    $tlsServerDisposed = $true

    Set-FailurePoint -Stage "NetworkValidation" -Code "NetworkInvariantFailed"
    if ($tlsFailureCount -ne 0 -or $tlsRequestCount -lt $SwitchCount) {
        throw "Loopback media request invariant failed."
    }
    if ($scheduledInterruptionCount -ne $NetworkInterruptionCount -or
        $tlsInjectedFailureCount -ne $NetworkInterruptionCount -or
        $tlsRecoveryCount -ne $NetworkInterruptionCount -or
        ($NetworkInterruptionCount -gt 0 -and
            $tlsLastRecoveryRequestOrdinal -le $tlsLastInjectedRequestOrdinal)) {
        throw "Native playback network interruption/recovery invariant failed."
    }

    Set-FailurePoint -Stage "EvidencePreparation" -Code "EvidencePreparationFailed"
    $successCandidate = [ordered]@{
        SchemaVersion = 10
        Stage = "M10NativeTierAPlayback"
        Result = "Passed"
        RunId = $runId
        CompletedAtUtc = $null
        Configuration = $Configuration
        Platform = "x64"
        DotNetSdk = $actualSdk
        CleanHeadBound = $true
        CommitSha = $repositoryHead
        ControllerScriptSha256 = $controllerScriptSha256
        HarnessAssemblySha256 = $harnessAssemblySha256
        FixtureManifestSha256 = $fixtureManifestSha256
        FixtureCorpusVerified = $fixtureCorpusVerified
        ProbeEnvelopeSchemaVersion = $probeEnvelopeSchemaVersion
        ProbeRunIdBound = $probeRunIdBound
        SwitchCount = $SwitchCount
        StartupP95Milliseconds = [Math]::Round([double]$probe.StartupP95Milliseconds, 3)
        StartupMaximumMilliseconds = [Math]::Round([double]$probe.StartupMaximumMilliseconds, 3)
        HlsStartupP95Milliseconds = [Math]::Round([double]$probe.HlsStartupP95Milliseconds, 3)
        DirectStartupP95Milliseconds = [Math]::Round([double]$probe.DirectStartupP95Milliseconds, 3)
        SoakMinutes = [int]$probe.SoakMinutes
        ResourceSampleCount = [int]$probe.ResourceSampleCount
        WarmupPrivateBytes = [long]$probe.WarmupPrivateBytes
        MemoryNetGrowthBytes = [long]$probe.MemoryNetGrowthBytes
        MemoryNetGrowthPercent = [Math]::Round([double]$probe.MemoryNetGrowthPercent, 3)
        MemoryMonotonicIncrease = [bool]$probe.MemoryMonotonicIncrease
        WarmupHandleCount = [int]$probe.WarmupHandleCount
        HandleNetGrowth = [int]$probe.HandleNetGrowth
        SurfaceTransitionCount = [int]$probe.SurfaceTransitionCount
        DetachedSourceCount = [int]$probe.DetachedSourceCount
        PlaybackRetryCount = [int]$probe.PlaybackRetryCount
        SourceDetachP95Milliseconds = [Math]::Round([double]$probe.SourceDetachP95Milliseconds, 3)
        SourceDetachMaximumMilliseconds = [Math]::Round([double]$probe.SourceDetachMaximumMilliseconds, 3)
        NetworkInterruptionCount = $tlsInjectedFailureCount
        NetworkRecoveryCount = $tlsRecoveryCount
        LastInjectedRequestOrdinal = $tlsLastInjectedRequestOrdinal
        LastRecoveryRequestOrdinal = $tlsLastRecoveryRequestOrdinal
        CancellationProbeCount = $cancellationProbeCount
        CancellationObservedCount = $cancellationObservedCount
        CancellationSourceDetachCount = $cancellationSourceDetachCount
        CancellationRecoveryCount = $cancellationRecoveryCount
        CancellationRecoverySourceDetachCount = $cancellationRecoverySourceDetachCount
        CancellationLatencyMilliseconds = [Math]::Round($cancellationLatencyMilliseconds, 3)
        CancellationQuiescenceMilliseconds = [Math]::Round($cancellationQuiescenceMilliseconds, 3)
        CancellationObservationMilliseconds = [Math]::Round($cancellationObservationMilliseconds, 3)
        CancellationSourceDetachMilliseconds = [Math]::Round($cancellationSourceDetachMilliseconds, 3)
        CancellationRecoveryStartupMilliseconds = [Math]::Round($cancellationRecoveryStartupMilliseconds, 3)
        CancellationRecoveryAdvanceMilliseconds = [Math]::Round($cancellationRecoveryAdvanceMilliseconds, 3)
        CancellationRecoverySourceDetachMilliseconds = [Math]::Round($cancellationRecoverySourceDetachMilliseconds, 3)
        CancellationSourceNullAfterObservation = $cancellationSourceNullAfterObservation
        CancellationRecoveryUsedFreshSource = $cancellationRecoveryUsedFreshSource
        CancellationNoAutomaticRestart = $cancellationNoAutomaticRestart
        InitialPrivateBytes = [long]$probe.InitialPrivateBytes
        FinalPrivateBytes = [long]$probe.FinalPrivateBytes
        InitialHandleCount = [int]$probe.InitialHandleCount
        FinalHandleCount = [int]$probe.FinalHandleCount
        LoopbackRequestCount = $tlsRequestCount
        H264DecoderRegistered = $h264DecoderRegistered
        AacDecoderRegistered = $aacDecoderRegistered
        Transport = "Tls12LoopbackAllowlist"
        Fixtures = @("DirectH264AacMpegTs", "HlsH264AacMpegTs")
        PackageSha256 = $packageSha256
        PackageSignatureStatus = "Valid"
        RuntimeDependencyPackageSha256 = $runtimeDependencyPackageSha256
        RuntimeDependencyPackageSignatureStatus = "Valid"
        ResolvedWindowsAppRuntimeName = $resolvedWindowsAppRuntimeName
        ResolvedWindowsAppRuntimeVersion = $resolvedWindowsAppRuntimeVersion
        ResolvedWindowsAppRuntimeArchitecture = $resolvedWindowsAppRuntimeArchitecture
        ResolvedWindowsAppRuntimePublisherId = $resolvedWindowsAppRuntimePublisherId
        ResolvedWindowsAppRuntimeIsFramework = $resolvedWindowsAppRuntimeIsFramework
        NormalCloseVerified = $false
        ForcedProcessTerminationUsed = $false
        ProcessCleanupPassed = $false
        TlsServerDisposed = $false
        PackageRemoved = $false
        PackageAppDataRemoved = $false
        PackageAppDataEmptyRootCleanupUsed = $false
        RuntimePackageBaselinePreserved = $false
        RuntimePackageGraphDisposition = "NotValidated"
        RuntimePackageSharedAdditionCount = -1
        EphemeralCertificatesRemoved = $false
        ExportedCertificateFilesRemoved = $false
        PackageOutputRemoved = $false
        EnvironmentRestored = $false
        RepositoryCleanAfterRun = $false
    }
    if ($isM16FinalAcceptance) {
        $successCandidate["SchemaVersion"] = 11
        $successCandidate["Stage"] = "M16NativeTierAFinalAcceptance"
    }
}
catch {
    $primaryFailure = $_
    $primaryFailureStage = $failureStage
    $primaryFailureCode = $failureCode
}
finally {
    Invoke-CleanupStep -Code "ProcessCleanupFailed" -Action {
        if ($null -eq $script:launchedProcess) {
            $script:processCleanupPassed = $true
            return
        }

        try {
            $script:launchedProcess.Refresh()
            if (-not $script:launchedProcess.HasExited) {
                try {
                    Close-TrackedProcessNormally -Process $script:launchedProcess
                    $script:processExitedWithoutForce = $true
                }
                catch {
                    $normalCloseFailure = $_
                    $script:launchedProcess.Refresh()
                    if (-not $script:launchedProcess.HasExited) {
                        $script:forcedProcessTerminationUsed = $true
                        $script:launchedProcess.Kill()
                        if (-not $script:launchedProcess.WaitForExit(10000)) {
                            throw "The exact native playback probe did not exit after bounded failure cleanup."
                        }
                    }
                    throw $normalCloseFailure
                }
            }
            if ($script:forcedProcessTerminationUsed) {
                throw "The native playback probe required forced termination."
            }
            $script:processCleanupPassed = $true
        }
        finally {
            $script:launchedProcess.Dispose()
            $script:launchedProcess = $null
        }
    }

    Invoke-CleanupStep -Code "TlsServerCleanupFailed" -Action {
        if ($null -ne $script:tlsServer) {
            $script:tlsServer.Dispose()
            $script:tlsServer = $null
        }
        $script:tlsServerDisposed = $true
    }

    Invoke-CleanupStep -Code "PackageCleanupFailed" -Action {
        if ($script:installAttempted) {
            Remove-ExactPackage
        }
        $script:packageRemoved = @(Get-ExactPackages).Count -eq 0
        if (-not $script:packageRemoved) {
            throw "The exact native playback package registration remains."
        }
    }

    Invoke-CleanupStep -Code "PackageAppDataCleanupFailed" -Action {
        if ($script:installAttempted -and $null -eq $script:packageAppDataPath) {
            throw "The exact native playback package data path is unavailable."
        }
        if ($script:installAttempted) {
            if (-not $script:packageRemoved) {
                throw "Refusing package app-data cleanup while the package remains registered."
            }
            Wait-PackageAppDataRemoval
        }
        $script:packageAppDataRemoved = $true
    }

    Invoke-CleanupStep -Code "RuntimeDependencyValidationFailed" -Action {
        Validate-RuntimeDependencyPackageState
    }

    Invoke-CleanupStep -Code "EnvironmentCleanupFailed" -Action {
        foreach ($entry in $script:environmentBackup.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
            if (-not [object]::Equals(
                    [Environment]::GetEnvironmentVariable($entry.Key, "Process"),
                    $entry.Value)) {
                throw "A native playback build environment value was not restored."
            }
        }
        $script:environmentRestored = $true
    }

    Invoke-CleanupStep -Code "TlsCertificateCleanupFailed" -Action {
        if ($null -ne $script:tlsCertificate) {
            foreach ($path in @(
                    "Cert:\LocalMachine\Root\$($script:tlsCertificate.Thumbprint)",
                    "Cert:\CurrentUser\My\$($script:tlsCertificate.Thumbprint)")) {
                Remove-ExactCertificate `
                    -StorePath $path `
                    -Thumbprint $script:tlsCertificate.Thumbprint `
                    -ExpectedSubject $script:tlsCertificate.Subject
            }
        }
        $script:tlsCertificateRemoved = $true
    }

    Invoke-CleanupStep -Code "SigningCertificateCleanupFailed" -Action {
        if ($null -ne $script:signingCertificate) {
            foreach ($path in @(
                    "Cert:\LocalMachine\TrustedPeople\$($script:signingCertificate.Thumbprint)",
                    "Cert:\CurrentUser\My\$($script:signingCertificate.Thumbprint)")) {
                Remove-ExactCertificate `
                    -StorePath $path `
                    -Thumbprint $script:signingCertificate.Thumbprint `
                    -ExpectedSubject $script:signingCertificate.Subject
            }
        }
        $script:signingCertificateRemoved = $true
    }

    Invoke-CleanupStep -Code "ExportedCertificateCleanupFailed" -Action {
        Remove-ExactOwnedFile -Path $script:signingCertificatePath -ExpectedParent $script:artifactRoot
        Remove-ExactOwnedFile -Path $script:tlsCertificatePath -ExpectedParent $script:artifactRoot
        if ((Test-Path -LiteralPath $script:signingCertificatePath) -or
            (Test-Path -LiteralPath $script:tlsCertificatePath)) {
            throw "An exported native playback certificate remains."
        }
        $script:exportedCertificateFilesRemoved = $true
    }

    Invoke-CleanupStep -Code "PackageOutputCleanupFailed" -Action {
        Remove-ExactOwnedTree -Path $script:packageOutput -ExpectedParent $script:packagesRoot
        if (Test-Path -LiteralPath $script:packageOutput) {
            throw "The exact native playback package output remains."
        }
        $script:runOutputRemoved = $true
    }
}

if ($cleanupFailures.Count -eq 0) {
    try {
        Set-FailurePoint -Stage "RepositoryBinding" -Code "RepositoryChanged"
        if (@(Get-RepositoryStatus).Count -ne 0 -or (Get-RepositoryHead) -ne $repositoryHead) {
            throw "The repository changed during the native playback smoke."
        }
    }
    catch {
        if ($null -eq $primaryFailure) {
            $primaryFailure = $_
            $primaryFailureStage = $failureStage
            $primaryFailureCode = $failureCode
        }
    }
}

if ($null -eq $primaryFailure -and $cleanupFailures.Count -eq 0) {
    try {
        Set-FailurePoint -Stage "CleanupVerification" -Code "CleanupEvidenceIncomplete"
        if ($null -eq $successCandidate -or
            -not $processExitedWithoutForce -or
            $forcedProcessTerminationUsed -or
            -not $processCleanupPassed -or
            -not $tlsServerDisposed -or
            -not $packageRemoved -or
            -not $packageAppDataRemoved -or
            -not $runtimePackageBaselinePreserved -or
            $runtimePackageSharedAdditionCount -lt 0 -or
            $runtimePackageSharedAdditionCount -gt 64 -or
            ([string]::Equals(
                    $runtimePackageGraphDisposition,
                    "ExactRestored",
                    [System.StringComparison]::Ordinal) -and
                $runtimePackageSharedAdditionCount -ne 0) -or
            ([string]::Equals(
                    $runtimePackageGraphDisposition,
                    "SharedAdditionsPreserved",
                    [System.StringComparison]::Ordinal) -and
                $runtimePackageSharedAdditionCount -eq 0) -or
            (-not [string]::Equals(
                    $runtimePackageGraphDisposition,
                    "ExactRestored",
                    [System.StringComparison]::Ordinal) -and
                -not [string]::Equals(
                    $runtimePackageGraphDisposition,
                    "SharedAdditionsPreserved",
                    [System.StringComparison]::Ordinal)) -or
            -not $tlsCertificateRemoved -or
            -not $signingCertificateRemoved -or
            -not $exportedCertificateFilesRemoved -or
            -not $runOutputRemoved -or
            -not $environmentRestored) {
            throw "The native playback cleanup evidence is incomplete."
        }

        $successCandidate["CompletedAtUtc"] = [DateTime]::UtcNow.ToString("O")
        $successCandidate["NormalCloseVerified"] = $true
        $successCandidate["ForcedProcessTerminationUsed"] = $false
        $successCandidate["ProcessCleanupPassed"] = $true
        $successCandidate["TlsServerDisposed"] = $true
        $successCandidate["PackageRemoved"] = $true
        $successCandidate["PackageAppDataRemoved"] = $true
        $successCandidate["PackageAppDataEmptyRootCleanupUsed"] = $packageAppDataEmptyRootCleanupUsed
        $successCandidate["RuntimePackageBaselinePreserved"] = $true
        $successCandidate["RuntimePackageGraphDisposition"] = $runtimePackageGraphDisposition
        $successCandidate["RuntimePackageSharedAdditionCount"] = $runtimePackageSharedAdditionCount
        $successCandidate["EphemeralCertificatesRemoved"] = $true
        $successCandidate["ExportedCertificateFilesRemoved"] = $true
        $successCandidate["PackageOutputRemoved"] = $true
        $successCandidate["EnvironmentRestored"] = $true
        $successCandidate["RepositoryCleanAfterRun"] = $true

        Set-FailurePoint -Stage "EvidencePublication" -Code "EvidencePublicationFailed"
        Write-JsonAtomically -Value $successCandidate -DestinationPath $evidencePath
    }
    catch {
        $primaryFailure = $_
        $primaryFailureStage = $failureStage
        $primaryFailureCode = $failureCode
    }
}

if ($null -ne $primaryFailure -or $cleanupFailures.Count -ne 0) {
    $effectiveStage = $primaryFailureStage
    $effectiveCode = $primaryFailureCode
    if ($cleanupFailures.Count -ne 0) {
        $effectiveStage = "Cleanup"
        $effectiveCode = if ($cleanupFailures.Count -eq 1) {
            $cleanupFailures[0]
        }
        else {
            "MultipleCleanupFailures"
        }
    }
    if ([string]::IsNullOrWhiteSpace($effectiveStage)) { $effectiveStage = "Unknown" }
    if ([string]::IsNullOrWhiteSpace($effectiveCode)) { $effectiveCode = "UnexpectedFailure" }

    $failureEvidence = [ordered]@{
        Stage = $effectiveStage
        Code = $effectiveCode
    }
    try {
        New-RegularDirectory -Path (Join-Path $repositoryRoot ".artifacts")
        New-RegularDirectory -Path $artifactRoot
        if (Test-Path -LiteralPath $evidencePath) {
            Remove-ExactOwnedFile -Path $evidencePath -ExpectedParent $artifactRoot
        }
        Write-JsonAtomically -Value $failureEvidence -DestinationPath $failureEvidencePath
    }
    catch {
        throw "Native playback smoke failed and stable failure evidence could not be written."
    }

    if ($cleanupFailures.Count -ne 0) {
        throw "Native playback smoke failed at $effectiveStage ($effectiveCode)."
    }
    throw $primaryFailure
}

Write-Host "Native packaged Tier A playback smoke passed: $SwitchCount alternating switches."
