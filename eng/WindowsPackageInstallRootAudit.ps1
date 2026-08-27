Set-StrictMode -Version Latest

$script:packageInstallRootAuditErrorPrefix = "PackageInstallRootAudit:"
$script:packageInstallRootAuditUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
$script:packageInstallRootAuditHandles =
    [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)

if ($null -eq ("IptvSuite.PackageInstallRootAudit.MutationCollector" -as [type])) {
    $interopSource = @'
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace IptvSuite.PackageInstallRootAudit
{
    public sealed class MutationState
    {
        public MutationState(long eventCount, bool overflowed)
        {
            EventCount = eventCount;
            Overflowed = overflowed;
        }

        public long EventCount { get; private set; }
        public bool Overflowed { get; private set; }
    }

    public sealed class MutationCollector : IDisposable
    {
        private readonly FileSystemWatcher watcher;
        private readonly SerialSynchronizer synchronizer;
        private long eventCount;
        private int overflowed;
        private int started;
        private int stopping;
        private int disposed;

        public MutationCollector(string rootPath)
        {
            if (String.IsNullOrWhiteSpace(rootPath))
            {
                throw new ArgumentException("RootRequired", "rootPath");
            }

            synchronizer = new SerialSynchronizer();
            try
            {
                watcher = new FileSystemWatcher(rootPath);
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter =
                    NotifyFilters.FileName |
                    NotifyFilters.DirectoryName |
                    NotifyFilters.Size |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Attributes |
                    NotifyFilters.Security;
                watcher.InternalBufferSize = 64 * 1024;
                watcher.SynchronizingObject = synchronizer;
                watcher.Changed += OnMutation;
                watcher.Created += OnMutation;
                watcher.Deleted += OnMutation;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
            }
            catch
            {
                synchronizer.Dispose();
                throw;
            }
        }

        public void Start()
        {
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0 ||
                Volatile.Read(ref disposed) != 0)
            {
                throw new InvalidOperationException("WatcherStateInvalid");
            }

            watcher.EnableRaisingEvents = true;
        }

        public MutationState GetState()
        {
            return new MutationState(
                Interlocked.Read(ref eventCount),
                Volatile.Read(ref overflowed) != 0 || synchronizer.DispatchOverflowed);
        }

        public MutationState GetStateAfterBarrier(int drainMilliseconds)
        {
            if (drainMilliseconds < 0 || drainMilliseconds > 2000)
            {
                throw new ArgumentOutOfRangeException("drainMilliseconds");
            }
            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref started) == 0)
            {
                throw new InvalidOperationException("WatcherStateInvalid");
            }

            if (drainMilliseconds > 0)
            {
                Thread.Sleep(drainMilliseconds);
            }
            synchronizer.Barrier();
            return GetState();
        }

        public MutationState StopAndGetState(int drainMilliseconds)
        {
            if (drainMilliseconds < 0 || drainMilliseconds > 2000)
            {
                throw new ArgumentOutOfRangeException("drainMilliseconds");
            }

            if (Volatile.Read(ref disposed) != 0)
            {
                return GetState();
            }
            if (Interlocked.CompareExchange(ref stopping, 1, 0) != 0)
            {
                throw new InvalidOperationException("WatcherStateInvalid");
            }

            Exception failure = null;
            try
            {
                if (drainMilliseconds > 0)
                {
                    Thread.Sleep(drainMilliseconds);
                }

                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
                synchronizer.CloseAndDrain();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                watcher.Dispose();
            }
            catch (Exception exception)
            {
                if (failure == null)
                {
                    failure = exception;
                }
            }
            try
            {
                synchronizer.Dispose();
            }
            catch (Exception exception)
            {
                if (failure == null)
                {
                    failure = exception;
                }
            }
            if (synchronizer.DispatchOverflowed)
            {
                Interlocked.Exchange(ref overflowed, 1);
            }
            if (failure == null)
            {
                Interlocked.Exchange(ref disposed, 1);
            }
            Interlocked.Exchange(ref stopping, 0);
            if (failure != null)
            {
                throw new InvalidOperationException("WatcherStopFailed", failure);
            }

            return GetState();
        }

        private void OnMutation(object sender, FileSystemEventArgs args)
        {
            Interlocked.Increment(ref eventCount);
        }

        private void OnRenamed(object sender, RenamedEventArgs args)
        {
            Interlocked.Increment(ref eventCount);
        }

        private void OnError(object sender, ErrorEventArgs args)
        {
            Interlocked.Exchange(ref overflowed, 1);
        }

        public void Dispose()
        {
            StopAndGetState(100);
        }
    }

    internal sealed class SerialInvocationResult : IAsyncResult, IDisposable
    {
        private readonly ManualResetEventSlim completed = new ManualResetEventSlim(false);

        public object Result { get; private set; }
        public Exception Error { get; private set; }
        public object AsyncState { get { return null; } }
        public WaitHandle AsyncWaitHandle { get { return completed.WaitHandle; } }
        public bool CompletedSynchronously { get { return false; } }
        public bool IsCompleted { get { return completed.IsSet; } }

        public void Complete(object result, Exception error)
        {
            Result = result;
            Error = error;
            completed.Set();
        }

        public void Wait()
        {
            completed.Wait();
        }

        public void Dispose()
        {
            completed.Dispose();
        }
    }

    internal sealed class SerialInvocation
    {
        public Delegate Method;
        public object[] Arguments;
        public SerialInvocationResult AsyncResult;
    }

    internal sealed class SerialSynchronizer : ISynchronizeInvoke, IDisposable
    {
        private const int MaximumPendingInvocations = 4096;
        private const int WorkerDrainTimeoutMilliseconds = 10000;
        private readonly object gate = new object();
        private readonly BlockingCollection<SerialInvocation> queue =
            new BlockingCollection<SerialInvocation>(MaximumPendingInvocations);
        private readonly Thread worker;
        private int workerThreadId;
        private int accepting = 1;
        private int dispatchOverflowed;
        private int disposed;

        public bool DispatchOverflowed
        {
            get { return Volatile.Read(ref dispatchOverflowed) != 0; }
        }

        public SerialSynchronizer()
        {
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "PackageInstallRootAuditWatcher";
            worker.Start();
        }

        public bool InvokeRequired
        {
            get { return Thread.CurrentThread.ManagedThreadId != Volatile.Read(ref workerThreadId); }
        }

        public IAsyncResult BeginInvoke(Delegate method, object[] args)
        {
            if (method == null)
            {
                throw new ArgumentNullException("method");
            }
            SerialInvocationResult result = new SerialInvocationResult();
            SerialInvocation invocation = new SerialInvocation
            {
                Method = method,
                Arguments = args ?? new object[0],
                AsyncResult = result
            };
            lock (gate)
            {
                if (Volatile.Read(ref accepting) == 0 || !queue.TryAdd(invocation))
                {
                    Interlocked.Exchange(ref dispatchOverflowed, 1);
                    result.Complete(null, null);
                }
            }
            return result;
        }

        public object EndInvoke(IAsyncResult result)
        {
            SerialInvocationResult invocationResult = result as SerialInvocationResult;
            if (invocationResult == null)
            {
                throw new ArgumentException("AsyncResultInvalid", "result");
            }

            invocationResult.Wait();
            try
            {
                if (invocationResult.Error != null)
                {
                    throw invocationResult.Error;
                }
                return invocationResult.Result;
            }
            finally
            {
                invocationResult.Dispose();
            }
        }

        public object Invoke(Delegate method, object[] args)
        {
            if (!InvokeRequired)
            {
                return method.DynamicInvoke(args ?? new object[0]);
            }
            return EndInvoke(BeginInvoke(method, args));
        }

        public void Barrier()
        {
            Invoke(new Action(NoOp), new object[0]);
        }

        public void CloseAndDrain()
        {
            lock (gate)
            {
                if (Interlocked.Exchange(ref accepting, 0) != 0)
                {
                    queue.CompleteAdding();
                }
            }
            if (!worker.Join(WorkerDrainTimeoutMilliseconds))
            {
                throw new InvalidOperationException("WatcherBarrierTimeout");
            }
        }

        private static void NoOp()
        {
        }

        private void Run()
        {
            Volatile.Write(ref workerThreadId, Thread.CurrentThread.ManagedThreadId);
            foreach (SerialInvocation invocation in queue.GetConsumingEnumerable())
            {
                object result = null;
                Exception error = null;
                try
                {
                    result = invocation.Method.DynamicInvoke(invocation.Arguments);
                }
                catch (TargetInvocationException exception)
                {
                    error = exception.InnerException ?? exception;
                }
                catch (Exception exception)
                {
                    error = exception;
                }
                invocation.AsyncResult.Complete(result, error);
            }
        }

        public void Dispose()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }
            CloseAndDrain();
            queue.Dispose();
            Interlocked.Exchange(ref disposed, 1);
        }
    }

    public sealed class PathSnapshot
    {
        public PathSnapshot(
            int attributes,
            long length,
            string sha256,
            uint volumeSerialNumber,
            uint fileIndexHigh,
            uint fileIndexLow)
        {
            Attributes = attributes;
            Length = length;
            Sha256 = sha256;
            Identity = volumeSerialNumber.ToString("x8") + ":" +
                fileIndexHigh.ToString("x8") + fileIndexLow.ToString("x8");
        }

        public int Attributes { get; private set; }
        public long Length { get; private set; }
        public string Sha256 { get; private set; }
        public string Identity { get; private set; }
        public bool IsDirectory
        {
            get { return (Attributes & (int)FileAttributes.Directory) != 0; }
        }
        public bool IsReparsePoint
        {
            get { return (Attributes & (int)FileAttributes.ReparsePoint) != 0; }
        }
    }

    public static class PathInspector
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileShareAll = 0x00000007;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        public static PathSnapshot Inspect(string path, bool hashContent)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("PathRequired", "path");
            }

            uint access = FileReadAttributes | (hashContent ? GenericRead : 0);
            SafeFileHandle handle = CreateFile(
                path,
                access,
                hashContent ? (uint)FileShare.Read : FileShareAll,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new InvalidOperationException("PathOpenFailed");
            }

            try
            {
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    throw new InvalidOperationException("PathInformationFailed");
                }

                StringBuilder finalPathBuilder = new StringBuilder(32768);
                uint finalLength = GetFinalPathNameByHandle(
                    handle,
                    finalPathBuilder,
                    (uint)finalPathBuilder.Capacity,
                    0);
                if (finalLength == 0 || finalLength >= finalPathBuilder.Capacity)
                {
                    throw new InvalidOperationException("FinalPathFailed");
                }

                string finalPath = NormalizeFinalPath(finalPathBuilder.ToString());
                string expectedPath = Path.GetFullPath(path);
                if (!String.Equals(finalPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("FinalPathMismatch");
                }

                long length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
                string sha256 = String.Empty;
                if (hashContent)
                {
                    if ((((FileAttributes)information.FileAttributes) & FileAttributes.Directory) != 0)
                    {
                        throw new InvalidOperationException("EntryTypeChanged");
                    }
                    using (FileStream stream = new FileStream(handle, FileAccess.Read, 65536, false))
                    using (SHA256 algorithm = SHA256.Create())
                    {
                        handle = null;
                        byte[] hash = algorithm.ComputeHash(stream);
                        sha256 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                        if (stream.Length != length)
                        {
                            throw new InvalidOperationException("FileLengthChanged");
                        }
                    }
                }

                return new PathSnapshot(
                    (int)information.FileAttributes,
                    length,
                    sha256,
                    information.VolumeSerialNumber,
                    information.FileIndexHigh,
                    information.FileIndexLow);
            }
            finally
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
            }
        }

        private static string NormalizeFinalPath(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string localPrefix = @"\\?\";
            if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path.Substring(uncPrefix.Length);
            }
            if (path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(localPrefix.Length);
            }
            return path;
        }
    }

    public static class AlternateStreamInspector
    {
        private const int FindStreamInfoStandard = 0;
        private const int ErrorHandleEof = 38;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Win32FindStreamData
        {
            public long StreamSize;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
            public string StreamName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstStreamW(
            string fileName,
            int infoLevel,
            out Win32FindStreamData findStreamData,
            int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextStreamW(
            IntPtr findStream,
            out Win32FindStreamData findStreamData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr findFile);

        public static int CountNamedStreams(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("PathRequired", "path");
            }

            Win32FindStreamData data;
            IntPtr handle = FindFirstStreamW(
                path,
                FindStreamInfoStandard,
                out data,
                0);
            if (handle == InvalidHandleValue)
            {
                if (Marshal.GetLastWin32Error() == ErrorHandleEof)
                {
                    return 0;
                }

                throw new InvalidOperationException("StreamEnumerationFailed");
            }

            int namedStreamCount = 0;
            try
            {
                while (true)
                {
                    if (!String.Equals(data.StreamName, "::$DATA", StringComparison.Ordinal))
                    {
                        namedStreamCount = 1;
                        break;
                    }

                    if (!FindNextStreamW(handle, out data))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != ErrorHandleEof)
                        {
                            throw new InvalidOperationException("StreamEnumerationFailed");
                        }

                        break;
                    }
                }
            }
            finally
            {
                if (!FindClose(handle))
                {
                    throw new InvalidOperationException("StreamHandleCloseFailed");
                }
            }

            return namedStreamCount;
        }
    }
}
'@

    try {
        Add-Type -TypeDefinition $interopSource -Language CSharp -ErrorAction Stop
    }
    catch {
        throw "PackageInstallRootAudit:InteropUnavailable"
    }
}

function Throw-PackageInstallRootAuditErrorInternal {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('\A[A-Za-z][A-Za-z0-9]+\z')]
        [string]$Code
    )

    throw "$($script:packageInstallRootAuditErrorPrefix)$Code"
}

function Test-PackageInstallRootAuditErrorInternal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    return [System.Text.RegularExpressions.Regex]::IsMatch(
        $Message,
        '\APackageInstallRootAudit:[A-Za-z][A-Za-z0-9]+\z')
}

function Assert-PackageInstallRootAuditNoStreamsInternal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    try {
        $namedStreamCount =
            [IptvSuite.PackageInstallRootAudit.AlternateStreamInspector]::CountNamedStreams($Path)
    }
    catch {
        Throw-PackageInstallRootAuditErrorInternal -Code "StreamEnumerationFailed"
    }

    if ($namedStreamCount -ne 0) {
        Throw-PackageInstallRootAuditErrorInternal -Code "AlternateDataStreamDetected"
    }
}

function Get-PackageInstallRootAuditCanonicalRootInternal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    if ([string]::IsNullOrWhiteSpace($RootPath) -or
        -not [System.IO.Path]::IsPathRooted($RootPath)) {
        Throw-PackageInstallRootAuditErrorInternal -Code "RootInvalid"
    }

    try {
        $pathRoot = [System.IO.Path]::GetPathRoot($RootPath)
        $pathTail = $RootPath.Substring($pathRoot.Length)
        $canonicalRoot = [System.IO.Path]::GetFullPath($RootPath).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        $canonicalInput = $RootPath.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    }
    catch {
        Throw-PackageInstallRootAuditErrorInternal -Code "RootInvalid"
    }

    if ($pathTail.IndexOf(':') -ge 0) {
        Throw-PackageInstallRootAuditErrorInternal -Code "RootAlternateDataStream"
    }

    if (-not [string]::Equals(
            $canonicalRoot,
            $canonicalInput,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Throw-PackageInstallRootAuditErrorInternal -Code "RootNotCanonical"
    }

    if ([string]::Equals(
            $canonicalRoot,
            $pathRoot.TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Throw-PackageInstallRootAuditErrorInternal -Code "RootTooBroad"
    }

    try {
        $attributes = [System.IO.File]::GetAttributes($canonicalRoot)
    }
    catch [System.IO.FileNotFoundException] {
        Throw-PackageInstallRootAuditErrorInternal -Code "RootMissing"
    }
    catch [System.IO.DirectoryNotFoundException] {
        Throw-PackageInstallRootAuditErrorInternal -Code "RootMissing"
    }
    catch {
        Throw-PackageInstallRootAuditErrorInternal -Code "RootInspectionFailed"
    }

    if (($attributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
        Throw-PackageInstallRootAuditErrorInternal -Code "RootNotDirectory"
    }

    if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Throw-PackageInstallRootAuditErrorInternal -Code "RootReparsePoint"
    }

    Assert-PackageInstallRootAuditNoStreamsInternal -Path $canonicalRoot
    return $canonicalRoot
}

function Get-PackageInstallRootAuditPathSnapshotInternal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [bool]$HashContent
    )

    try {
        return [IptvSuite.PackageInstallRootAudit.PathInspector]::Inspect(
            $Path,
            $HashContent)
    }
    catch {
        Throw-PackageInstallRootAuditErrorInternal -Code "PathInspectionFailed"
    }
}

function Get-PackageInstallRootAuditInventoryInternal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CanonicalRoot,

        [Parameter(Mandatory = $true)]
        [int]$MaximumEntryCount,

        [Parameter(Mandatory = $true)]
        [int]$MaximumFileCount,

        [Parameter(Mandatory = $true)]
        [int]$MaximumDepth,

        [Parameter(Mandatory = $true)]
        [long]$MaximumTotalBytes,

        [Parameter(Mandatory = $true)]
        [long]$MaximumFileBytes,

        [Parameter(Mandatory = $true)]
        [int]$MaximumRelativePathUtf8Bytes,

        [Parameter(Mandatory = $true)]
        [int]$MaximumManifestBytes,

        [Parameter(Mandatory = $true)]
        [bool]$InspectDirectoryStreams
    )

    $records = [System.Collections.Generic.List[object]]::new()
    $pending = [System.Collections.Generic.Queue[object]]::new()
    $seenPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $pending.Enqueue([pscustomobject]@{
        FullPath = $CanonicalRoot
        RelativePath = "."
        Depth = 0
    })
    $fileCount = 0
    [long]$totalBytes = 0
    $rootPrefix = $CanonicalRoot + [System.IO.Path]::DirectorySeparatorChar
    $discoveredEntryCount = 1

    try {
        while ($pending.Count -gt 0) {
            $candidate = $pending.Dequeue()
            if ($candidate.Depth -gt $MaximumDepth) {
                Throw-PackageInstallRootAuditErrorInternal -Code "DepthExceeded"
            }

            $canonicalCandidate = [System.IO.Path]::GetFullPath($candidate.FullPath)
            if (-not [string]::Equals(
                    $canonicalCandidate,
                    $CanonicalRoot,
                    [System.StringComparison]::OrdinalIgnoreCase) -and
                -not $canonicalCandidate.StartsWith(
                    $rootPrefix,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                Throw-PackageInstallRootAuditErrorInternal -Code "EntryEscapedRoot"
            }

            $relativePath = [string]$candidate.RelativePath
            $relativePathBytes = $script:packageInstallRootAuditUtf8.GetByteCount($relativePath)
            if ($relativePathBytes -gt $MaximumRelativePathUtf8Bytes) {
                Throw-PackageInstallRootAuditErrorInternal -Code "RelativePathTooLong"
            }

            if (-not $seenPaths.Add($relativePath)) {
                Throw-PackageInstallRootAuditErrorInternal -Code "RelativePathCollision"
            }

            $pathSnapshot = Get-PackageInstallRootAuditPathSnapshotInternal `
                -Path $canonicalCandidate `
                -HashContent $false
            $attributes = [int]$pathSnapshot.Attributes
            if ($pathSnapshot.IsReparsePoint) {
                Throw-PackageInstallRootAuditErrorInternal -Code "ReparsePointDetected"
            }

            $isDirectory = [bool]$pathSnapshot.IsDirectory
            if (-not $isDirectory -or $InspectDirectoryStreams) {
                Assert-PackageInstallRootAuditNoStreamsInternal -Path $canonicalCandidate
            }
            if ($isDirectory) {
                $records.Add([pscustomobject]@{
                    Kind = "D"
                    RelativePath = $relativePath.Replace('\', '/')
                    Attributes = [int]$attributes
                    Length = [long]0
                    Sha256 = ""
                    Identity = [string]$pathSnapshot.Identity
                }) | Out-Null

                $children =
                    [System.IO.DirectoryInfo]::new($canonicalCandidate).EnumerateFileSystemInfos()
                foreach ($child in $children) {
                    $discoveredEntryCount++
                    if ($discoveredEntryCount -gt $MaximumEntryCount) {
                        Throw-PackageInstallRootAuditErrorInternal -Code "EntryCountExceeded"
                    }
                    $childRelativePath = if ($relativePath -ceq ".") {
                        $child.Name
                    }
                    else {
                        "$relativePath\$($child.Name)"
                    }
                    $pending.Enqueue([pscustomobject]@{
                        FullPath = $child.FullName
                        RelativePath = $childRelativePath
                        Depth = $candidate.Depth + 1
                    })
                }

                $directoryAfterEnumeration =
                    Get-PackageInstallRootAuditPathSnapshotInternal `
                        -Path $canonicalCandidate `
                        -HashContent $false
                if ($directoryAfterEnumeration.IsReparsePoint -or
                    -not $directoryAfterEnumeration.IsDirectory -or
                    [int]$directoryAfterEnumeration.Attributes -ne $attributes -or
                    $directoryAfterEnumeration.Identity -cne $pathSnapshot.Identity) {
                    Throw-PackageInstallRootAuditErrorInternal `
                        -Code "EntryChangedDuringInventory"
                }
            }
            else {
                if ($pathSnapshot.Length -gt $MaximumFileBytes) {
                    Throw-PackageInstallRootAuditErrorInternal -Code "FileSizeExceeded"
                }

                $fileCount++
                if ($fileCount -gt $MaximumFileCount) {
                    Throw-PackageInstallRootAuditErrorInternal -Code "FileCountExceeded"
                }

                if ($totalBytes -gt ($MaximumTotalBytes - $pathSnapshot.Length)) {
                    Throw-PackageInstallRootAuditErrorInternal -Code "TotalSizeExceeded"
                }
                $preHashLength = [long]$pathSnapshot.Length
                $totalBytes += $preHashLength

                $hashedSnapshot = Get-PackageInstallRootAuditPathSnapshotInternal `
                    -Path $canonicalCandidate `
                    -HashContent $true
                if ($hashedSnapshot.IsReparsePoint -or
                    $hashedSnapshot.IsDirectory -or
                    $hashedSnapshot.Length -ne $preHashLength -or
                    [int]$hashedSnapshot.Attributes -ne $attributes -or
                    $hashedSnapshot.Identity -cne $pathSnapshot.Identity) {
                    Throw-PackageInstallRootAuditErrorInternal -Code "FileChangedDuringInventory"
                }
                $records.Add([pscustomobject]@{
                    Kind = "F"
                    RelativePath = $relativePath.Replace('\', '/')
                    Attributes = [int]$attributes
                    Length = $preHashLength
                    Sha256 = [string]$hashedSnapshot.Sha256
                    Identity = [string]$hashedSnapshot.Identity
                }) | Out-Null
            }
        }
    }
    catch {
        if (Test-PackageInstallRootAuditErrorInternal -Message $_.Exception.Message) {
            throw $_.Exception.Message
        }

        Throw-PackageInstallRootAuditErrorInternal -Code "InventoryFailed"
    }

    $recordByPath = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $orderedPaths = [string[]]@($records | ForEach-Object { [string]$_.RelativePath })
    foreach ($record in $records) {
        $recordByPath.Add([string]$record.RelativePath, $record)
    }
    [System.Array]::Sort($orderedPaths, [System.StringComparer]::OrdinalIgnoreCase)
    $orderedRecords = [System.Collections.Generic.List[object]]::new()
    foreach ($orderedPath in $orderedPaths) {
        $orderedRecords.Add($recordByPath[$orderedPath]) | Out-Null
    }
    $manifestStream = $null
    $identityStream = $null
    $writer = $null
    $identityWriter = $null
    $algorithm = $null
    $identityAlgorithm = $null
    try {
        $manifestStream = [System.IO.MemoryStream]::new()
        $identityStream = [System.IO.MemoryStream]::new()
        $writer = [System.IO.BinaryWriter]::new(
            $manifestStream,
            $script:packageInstallRootAuditUtf8,
            $true)
        $identityWriter = [System.IO.BinaryWriter]::new(
            $identityStream,
            $script:packageInstallRootAuditUtf8,
            $true)
        $writer.Write([int]1)
        $writer.Write([int]$orderedRecords.Count)
        $identityWriter.Write([int]1)
        $identityWriter.Write([int]$orderedRecords.Count)
        foreach ($record in $orderedRecords) {
            $writer.Write([string]$record.Kind)
            $writer.Write([string]$record.RelativePath)
            $writer.Write([int]$record.Attributes)
            $writer.Write([long]$record.Length)
            $writer.Write([string]$record.Sha256)
            $identityWriter.Write([string]$record.RelativePath)
            $identityWriter.Write([string]$record.Identity)
            if ($manifestStream.Length -gt $MaximumManifestBytes -or
                $identityStream.Length -gt $MaximumManifestBytes) {
                Throw-PackageInstallRootAuditErrorInternal -Code "ManifestSizeExceeded"
            }
        }
        $writer.Flush()
        $identityWriter.Flush()

        if ($manifestStream.Length -gt $MaximumManifestBytes -or
            $identityStream.Length -gt $MaximumManifestBytes) {
            Throw-PackageInstallRootAuditErrorInternal -Code "ManifestSizeExceeded"
        }

        $manifestStream.Position = 0
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        $manifestHash = $algorithm.ComputeHash($manifestStream)
        $manifestSha256 = [System.BitConverter]::ToString($manifestHash).
            Replace("-", "").ToLowerInvariant()
        $identityStream.Position = 0
        $identityAlgorithm = [System.Security.Cryptography.SHA256]::Create()
        $identityHash = $identityAlgorithm.ComputeHash($identityStream)
        $identityManifestSha256 = [System.BitConverter]::ToString($identityHash).
            Replace("-", "").ToLowerInvariant()
    }
    catch {
        if (Test-PackageInstallRootAuditErrorInternal -Message $_.Exception.Message) {
            throw $_.Exception.Message
        }

        Throw-PackageInstallRootAuditErrorInternal -Code "ManifestCompositionFailed"
    }
    finally {
        if ($null -ne $algorithm) {
            $algorithm.Dispose()
        }
        if ($null -ne $identityAlgorithm) {
            $identityAlgorithm.Dispose()
        }
        if ($null -ne $writer) {
            $writer.Dispose()
        }
        if ($null -ne $identityWriter) {
            $identityWriter.Dispose()
        }
        if ($null -ne $manifestStream) {
            $manifestStream.Dispose()
        }
        if ($null -ne $identityStream) {
            $identityStream.Dispose()
        }
    }

    return [pscustomobject]@{
        EntryCount = [int]$orderedRecords.Count
        FileCount = [int]$fileCount
        TotalBytes = [long]$totalBytes
        ManifestSha256 = [string]$manifestSha256
        IdentityManifestSha256 = [string]$identityManifestSha256
    }
}

function Test-PackageInstallRootAuditInventoriesEqualInternal {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Left,

        [Parameter(Mandatory = $true)]
        [object]$Right
    )

    return (
        $Left.EntryCount -eq $Right.EntryCount -and
        $Left.FileCount -eq $Right.FileCount -and
        $Left.TotalBytes -eq $Right.TotalBytes -and
        $Left.ManifestSha256 -ceq $Right.ManifestSha256 -and
        $Left.IdentityManifestSha256 -ceq $Right.IdentityManifestSha256)
}

function Get-PackageInstallRootAuditStateInternal {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Audit,

        [switch]$AllowMissing
    )

    if ($null -eq $Audit -or
        $Audit.PSObject.TypeNames -cnotcontains
            "IptvSuite.PackageInstallRootAudit.Handle" -or
        @($Audit.PSObject.Properties.Name).Count -ne 1 -or
        $Audit.PSObject.Properties.Name -cnotcontains "Token" -or
        $Audit.Token -isnot [string] -or
        $Audit.Token -cnotmatch '\A[0-9a-f]{32}\z') {
        Throw-PackageInstallRootAuditErrorInternal -Code "AuditStateInvalid"
    }

    if (-not $script:packageInstallRootAuditHandles.ContainsKey($Audit.Token)) {
        if ($AllowMissing) {
            return $null
        }
        Throw-PackageInstallRootAuditErrorInternal -Code "AuditStateInvalid"
    }

    return $script:packageInstallRootAuditHandles[$Audit.Token]
}

function Start-WindowsPackageInstallRootAudit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [ValidateRange(1, 8192)]
        [int]$MaximumEntryCount = 8192,

        [ValidateRange(1, 4096)]
        [int]$MaximumFileCount = 4096,

        [ValidateRange(1, 32)]
        [int]$MaximumDepth = 32,

        [ValidateRange(1, 536870912)]
        [long]$MaximumTotalBytes = 536870912,

        [ValidateRange(1, 268435456)]
        [long]$MaximumFileBytes = 268435456,

        [ValidateRange(1, 1024)]
        [int]$MaximumRelativePathUtf8Bytes = 1024,

        [ValidateRange(64, 4194304)]
        [int]$MaximumManifestBytes = 4194304
    )

    $collector = $null
    try {
        $canonicalRoot = Get-PackageInstallRootAuditCanonicalRootInternal -RootPath $RootPath
        $inventoryArguments = @{
            CanonicalRoot = $canonicalRoot
            MaximumEntryCount = $MaximumEntryCount
            MaximumFileCount = $MaximumFileCount
            MaximumDepth = $MaximumDepth
            MaximumTotalBytes = $MaximumTotalBytes
            MaximumFileBytes = $MaximumFileBytes
            MaximumRelativePathUtf8Bytes = $MaximumRelativePathUtf8Bytes
            MaximumManifestBytes = $MaximumManifestBytes
            InspectDirectoryStreams = $false
        }
        $fullInventoryArguments = $inventoryArguments.Clone()
        $fullInventoryArguments.InspectDirectoryStreams = $true
        $preflight = Get-PackageInstallRootAuditInventoryInternal `
            @fullInventoryArguments

        try {
            $collector =
                [IptvSuite.PackageInstallRootAudit.MutationCollector]::new($canonicalRoot)
            $collector.Start()
        }
        catch {
            Throw-PackageInstallRootAuditErrorInternal -Code "WatcherStartFailed"
        }

        $baselineFirst = Get-PackageInstallRootAuditInventoryInternal @inventoryArguments
        Start-Sleep -Milliseconds 50
        $baselineSecond = Get-PackageInstallRootAuditInventoryInternal @inventoryArguments
        Start-Sleep -Milliseconds 50

        if (-not (Test-PackageInstallRootAuditInventoriesEqualInternal `
                -Left $preflight `
                -Right $baselineFirst) -or
            -not (Test-PackageInstallRootAuditInventoriesEqualInternal `
                -Left $baselineFirst `
                -Right $baselineSecond)) {
            Throw-PackageInstallRootAuditErrorInternal -Code "BaselineSnapshotUnstable"
        }
        try {
            $baselineWatcherState = $collector.GetStateAfterBarrier(100)
        }
        catch {
            Throw-PackageInstallRootAuditErrorInternal -Code "WatcherBaselineFault"
        }
        if ($baselineWatcherState.Overflowed -or
            $baselineWatcherState.EventCount -ne 0) {
            Throw-PackageInstallRootAuditErrorInternal -Code "WatcherBaselineFault"
        }

        $token = [Guid]::NewGuid().ToString("N")
        $internalState = [pscustomobject]@{
            Collector = $collector
            Baseline = $baselineSecond
            InventoryArguments = $inventoryArguments
            FullInventoryArguments = $fullInventoryArguments
        }
        $publicHandle = [pscustomobject]@{
            PSTypeName = "IptvSuite.PackageInstallRootAudit.Handle"
            Token = $token
        }
        $script:packageInstallRootAuditHandles.Add($token, $internalState)
        $collector = $null
        return $publicHandle
    }
    catch {
        if ($null -ne $collector) {
            try {
                $collector.Dispose()
            }
            catch {
                # Preserve the stable primary audit failure.
            }
        }

        if (Test-PackageInstallRootAuditErrorInternal -Message $_.Exception.Message) {
            throw $_.Exception.Message
        }

        Throw-PackageInstallRootAuditErrorInternal -Code "StartFailed"
    }
}

function Complete-WindowsPackageInstallRootAudit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Audit
    )

    $completionError = $null
    $watcherState = $null
    $auditState = $null
    $cleanupSucceeded = $false
    $cleanupFailed = $false
    $result = $null
    try {
        $auditState = Get-PackageInstallRootAuditStateInternal -Audit $Audit
        $inventoryArguments = $auditState.InventoryArguments
        $postFirst = Get-PackageInstallRootAuditInventoryInternal @inventoryArguments
        Start-Sleep -Milliseconds 50
        $postSecond = Get-PackageInstallRootAuditInventoryInternal @inventoryArguments

        if (-not (Test-PackageInstallRootAuditInventoriesEqualInternal `
                -Left $postFirst `
                -Right $postSecond)) {
            Throw-PackageInstallRootAuditErrorInternal -Code "PostSnapshotUnstable"
        }

        try {
            $watcherState = $auditState.Collector.StopAndGetState(100)
        }
        catch {
            Throw-PackageInstallRootAuditErrorInternal -Code "WatcherStopFailed"
        }
        $fullInventoryArguments = $auditState.FullInventoryArguments
        $postFull = Get-PackageInstallRootAuditInventoryInternal `
            @fullInventoryArguments
        if (-not (Test-PackageInstallRootAuditInventoriesEqualInternal `
                -Left $postSecond `
                -Right $postFull)) {
            Throw-PackageInstallRootAuditErrorInternal -Code "PostSnapshotUnstable"
        }

        $snapshotEquivalent = Test-PackageInstallRootAuditInventoriesEqualInternal `
            -Left $auditState.Baseline `
            -Right $postFull
        if (-not $snapshotEquivalent) {
            Throw-PackageInstallRootAuditErrorInternal -Code "SnapshotMismatch"
        }
        if ($watcherState.Overflowed) {
            Throw-PackageInstallRootAuditErrorInternal -Code "WatcherOverflow"
        }
        if ($watcherState.EventCount -ne 0) {
            Throw-PackageInstallRootAuditErrorInternal -Code "MutationObserved"
        }

        $result = [pscustomobject][ordered]@{
            SchemaVersion = 1
            Scope = "ExactRegisteredProductPackageInstallLocation"
            ExcludedEntryCount = 0
            BaselineEntryCount = [int]$auditState.Baseline.EntryCount
            BaselineFileCount = [int]$auditState.Baseline.FileCount
            BaselineTotalBytes = [long]$auditState.Baseline.TotalBytes
            BaselineManifestSha256 = [string]$auditState.Baseline.ManifestSha256
            FinalEntryCount = [int]$postFull.EntryCount
            FinalFileCount = [int]$postFull.FileCount
            FinalTotalBytes = [long]$postFull.TotalBytes
            FinalManifestSha256 = [string]$postFull.ManifestSha256
            MutationEventCount = [long]$watcherState.EventCount
            WatcherOverflow = [bool]$watcherState.Overflowed
            SnapshotEquivalent = [bool]$snapshotEquivalent
            RuntimeWriteAuditPassed = $true
        }
    }
    catch {
        $completionError = $_
    }
    finally {
        if ($null -ne $auditState -and $null -ne $auditState.Collector) {
            try {
                $auditState.Collector.Dispose()
                $cleanupSucceeded = $true
            }
            catch {
                $cleanupFailed = $true
            }
        }
        if ($cleanupSucceeded -and
            $script:packageInstallRootAuditHandles.ContainsKey($Audit.Token) -and
            [object]::ReferenceEquals(
                $script:packageInstallRootAuditHandles[$Audit.Token],
                $auditState)) {
            $script:packageInstallRootAuditHandles.Remove($Audit.Token) | Out-Null
        }
    }

    if ($cleanupFailed) {
        Throw-PackageInstallRootAuditErrorInternal -Code "WatcherDisposeFailed"
    }

    if ($null -ne $completionError) {
        if (Test-PackageInstallRootAuditErrorInternal `
                -Message $completionError.Exception.Message) {
            throw $completionError.Exception.Message
        }

        Throw-PackageInstallRootAuditErrorInternal -Code "CompleteFailed"
    }

    return $result
}

function Stop-WindowsPackageInstallRootAudit {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$Audit
    )

    if ($null -eq $Audit) {
        return
    }

    try {
        $auditState = Get-PackageInstallRootAuditStateInternal `
            -Audit $Audit `
            -AllowMissing
        if ($null -eq $auditState) {
            return
        }
        if ($null -ne $auditState.Collector) {
            $auditState.Collector.Dispose()
        }
        if ($script:packageInstallRootAuditHandles.ContainsKey($Audit.Token) -and
            [object]::ReferenceEquals(
                $script:packageInstallRootAuditHandles[$Audit.Token],
                $auditState)) {
            $script:packageInstallRootAuditHandles.Remove($Audit.Token) | Out-Null
        }
    }
    catch {
        if (Test-PackageInstallRootAuditErrorInternal -Message $_.Exception.Message) {
            throw $_.Exception.Message
        }

        Throw-PackageInstallRootAuditErrorInternal -Code "WatcherDisposeFailed"
    }
}
