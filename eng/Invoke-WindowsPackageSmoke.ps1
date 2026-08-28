[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$DotNetPath = "dotnet",

    [switch]$EmitM14TraceMarkers,

    [switch]$RunWack,

    [switch]$EmitM16FinalArtifactSurfaces,

    [ValidatePattern('\A[0-9a-f]{32}\z')]
    [string]$M16RunToken
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop

$activationInterop = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace IptvSuite.PackageSmoke
{
    [Flags]
    internal enum ActivateOptions : uint
    {
        NoErrorUi = 0x00000002,
    }

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
        private const uint LocalServer = 0x00000004;
        private const int ErrorInsufficientBuffer = 122;
        private static readonly Guid ClassId =
            new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
        private static readonly Guid InterfaceId =
            new Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D");

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

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int CoCreateInstance(
            [In] ref Guid classId,
            IntPtr outer,
            uint classContext,
            [In] ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object value);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int PackageFullNameFromId(
            ref PackageIdNative packageId,
            ref uint packageFullNameLength,
            [Out] StringBuilder packageFullName);

        public static string GetPackageFullName(
            string name,
            string publisher,
            ushort major,
            ushort minor,
            ushort build,
            ushort revision)
        {
            if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(publisher))
            {
                throw new ArgumentException("An exact package identity is required.");
            }

            var id = new PackageIdNative
            {
                Reserved = 0,
                ProcessorArchitecture = 9,
                Version = new PackageVersionNative
                {
                    Major = major,
                    Minor = minor,
                    Build = build,
                    Revision = revision,
                },
                Name = name,
                Publisher = publisher,
                ResourceId = null,
                PublisherId = null,
            };
            uint length = 0;
            int result = PackageFullNameFromId(ref id, ref length, null);
            if (result != ErrorInsufficientBuffer || length < 18 || length > 256)
            {
                throw new InvalidOperationException("Package full-name sizing failed.");
            }

            var value = new StringBuilder(checked((int)length));
            result = PackageFullNameFromId(ref id, ref length, value);
            if (result != 0 || value.Length + 1 != length)
            {
                throw new InvalidOperationException("Package full-name calculation failed.");
            }

            return value.ToString();
        }

        public static int Activate(string appUserModelId)
        {
            if (String.IsNullOrWhiteSpace(appUserModelId))
            {
                throw new ArgumentException(
                    "The application user model ID is required.",
                    "appUserModelId");
            }

            Guid classId = ClassId;
            Guid interfaceId = InterfaceId;
            object activationManager;
            int creationResult = CoCreateInstance(
                ref classId,
                IntPtr.Zero,
                LocalServer,
                ref interfaceId,
                out activationManager);
            if (creationResult < 0)
            {
                throw new COMException(
                    String.Format(
                        "Packaged application activation service creation failed (HRESULT 0x{0:X8}).",
                        unchecked((uint)creationResult)),
                    creationResult);
            }

            try
            {
                uint processId;
                int result = ((IApplicationActivationManager)activationManager).ActivateApplication(
                    appUserModelId,
                    null,
                    ActivateOptions.NoErrorUi,
                    out processId);
                if (result < 0)
                {
                    throw new COMException(
                        String.Format(
                            "Packaged application activation failed (HRESULT 0x{0:X8}).",
                            unchecked((uint)result)),
                        result);
                }

                if (processId == 0 || processId > Int32.MaxValue)
                {
                    throw new InvalidOperationException(
                        "Package activation returned an invalid process identifier.");
                }

                return (int)processId;
            }
            finally
            {
                if (Marshal.IsComObject(activationManager))
                {
                    Marshal.FinalReleaseComObject(activationManager);
                }
            }
        }
    }

    public static class WindowInspector
    {
        private const uint NoMove = 0x0002;
        private const uint NoZOrder = 0x0004;
        private const uint NoActivate = 0x0010;
        private const uint AsyncWindowPosition = 0x4000;
        private const uint WindowMessageNull = 0x0000;
        private const uint SendMessageTimeoutBlock = 0x0001;
        private const uint SendMessageTimeoutAbortIfHung = 0x0002;
        private const uint SendMessageTimeoutErrorOnExit = 0x0020;
        private const uint ExactUiThreadTimeoutMilliseconds = 200;
        private const int ErrorTimeout = 1460;
        private const int ShowMinimized = 2;
        private const int ShowRestore = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRectangle
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        public sealed class WindowBounds
        {
            internal WindowBounds(int left, int top, int right, int bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }

            public int Left { get; private set; }
            public int Top { get; private set; }
            public int Right { get; private set; }
            public int Bottom { get; private set; }
            public int Width { get { return checked(Right - Left); } }
            public int Height { get { return checked(Bottom - Top); } }
        }

        public sealed class UiThreadResponsivenessProbeResult
        {
            public bool TimedOut { get; internal set; }
            public double ElapsedMilliseconds { get; internal set; }
            public uint TimeoutMilliseconds { get; internal set; }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr windowHandle);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(
            IntPtr windowHandle,
            out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRectangle(
            IntPtr windowHandle,
            out WindowRectangle rectangle);

        [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPosition(
            IntPtr windowHandle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr windowHandle);

        [DllImport("user32.dll", EntryPoint = "ShowWindowAsync", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RequestWindowState(IntPtr windowHandle, int command);

        [DllImport("kernel32.dll", EntryPoint = "SetLastError", ExactSpelling = true)]
        private static extern void ResetLastError(uint errorCode);

        [DllImport(
            "user32.dll",
            EntryPoint = "SendMessageTimeoutW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern IntPtr SendWindowMessageWithTimeout(
            IntPtr windowHandle,
            uint message,
            UIntPtr wordParameter,
            IntPtr longParameter,
            uint flags,
            uint timeoutMilliseconds,
            out UIntPtr messageResult);

        public static WindowBounds GetWindowBounds(IntPtr windowHandle)
        {
            ValidateWindowHandle(windowHandle);
            WindowRectangle rectangle;
            if (!GetWindowRectangle(windowHandle, out rectangle) ||
                rectangle.Right <= rectangle.Left ||
                rectangle.Bottom <= rectangle.Top)
            {
                throw new InvalidOperationException("The packaged window bounds are unavailable.");
            }

            return new WindowBounds(
                rectangle.Left,
                rectangle.Top,
                rectangle.Right,
                rectangle.Bottom);
        }

        public static void ResizeWindow(IntPtr windowHandle, int width, int height)
        {
            ValidateWindowHandle(windowHandle);
            if (width < 640 || width > 1920 || height < 480 || height > 1080)
            {
                throw new ArgumentOutOfRangeException("The packaged window size is outside the bounded test range.");
            }
            if (!SetWindowPosition(
                    windowHandle,
                    IntPtr.Zero,
                    0,
                    0,
                    width,
                    height,
                    NoMove | NoZOrder | NoActivate | AsyncWindowPosition))
            {
                throw new InvalidOperationException("The packaged window resize request failed.");
            }
        }

        public static bool IsWindowMinimized(IntPtr windowHandle)
        {
            ValidateWindowHandle(windowHandle);
            return IsIconic(windowHandle);
        }

        public static void MinimizeWindow(IntPtr windowHandle)
        {
            RequestWindowStateChange(windowHandle, ShowMinimized);
        }

        public static void RestoreWindow(IntPtr windowHandle)
        {
            RequestWindowStateChange(windowHandle, ShowRestore);
        }

        public static UiThreadResponsivenessProbeResult ProbeUiThreadResponsiveness(
            IntPtr windowHandle)
        {
            ValidateWindowHandle(windowHandle);
            UIntPtr messageResult;
            ResetLastError(0);
            System.Diagnostics.Stopwatch timer =
                System.Diagnostics.Stopwatch.StartNew();
            IntPtr callResult = SendWindowMessageWithTimeout(
                windowHandle,
                WindowMessageNull,
                UIntPtr.Zero,
                IntPtr.Zero,
                SendMessageTimeoutBlock |
                    SendMessageTimeoutAbortIfHung |
                    SendMessageTimeoutErrorOnExit,
                ExactUiThreadTimeoutMilliseconds,
                out messageResult);
            int callError = callResult == IntPtr.Zero
                ? Marshal.GetLastWin32Error()
                : 0;
            timer.Stop();

            bool timedOut = callResult == IntPtr.Zero;
            if (timedOut && callError != 0 && callError != ErrorTimeout)
            {
                throw new InvalidOperationException(
                    "The packaged UI-thread responsiveness proxy failed.");
            }

            return new UiThreadResponsivenessProbeResult
            {
                TimedOut = timedOut,
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                TimeoutMilliseconds = ExactUiThreadTimeoutMilliseconds,
            };
        }

        private static void RequestWindowStateChange(IntPtr windowHandle, int command)
        {
            ValidateWindowHandle(windowHandle);
            if (!RequestWindowState(windowHandle, command))
            {
                throw new InvalidOperationException("The packaged window state request failed.");
            }
        }

        private static void ValidateWindowHandle(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException("A packaged window handle is required.", "windowHandle");
            }
        }
    }

    public static class KeyboardInspector
    {
        private const ushort VirtualKeyTab = 0x09;
        private const ushort VirtualKeyEnter = 0x0D;
        private const ushort VirtualKeyPageUp = 0x21;
        private const ushort VirtualKeyPageDown = 0x22;
        private const ushort VirtualKeyHome = 0x24;
        private const uint InputKeyboard = 1;
        private const uint KeyEventKeyUp = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            internal uint Type;
            internal InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            internal KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint inputCount,
            [In] Input[] inputs,
            int inputSize);

        public static void PressTab()
        {
            Press(VirtualKeyTab);
        }

        public static void PressEnter()
        {
            Press(VirtualKeyEnter);
        }

        public static void PressHome()
        {
            Press(VirtualKeyHome);
        }

        public static void PressPageUp()
        {
            Press(VirtualKeyPageUp);
        }

        public static void PressPageDown()
        {
            Press(VirtualKeyPageDown);
        }

        private static void Press(ushort virtualKey)
        {
            Input[] inputs =
            {
                CreateKeyboardInput(virtualKey, 0),
                CreateKeyboardInput(virtualKey, KeyEventKeyUp),
            };

            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != (uint)inputs.Length)
            {
                throw new InvalidOperationException("The packaged keyboard input pair was not delivered atomically.");
            }
        }

        private static Input CreateKeyboardInput(ushort virtualKey, uint flags)
        {
            return new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = virtualKey,
                        Flags = flags,
                    },
                },
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnsignedRatio
    {
        internal uint Numerator;
        internal uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct DwmTimingInfo
    {
        internal uint Size;
        internal UnsignedRatio RateRefresh;
        internal ulong QpcRefreshPeriod;
        internal UnsignedRatio RateCompose;
        internal ulong QpcVBlank;
        internal ulong Refresh;
        internal uint DxRefresh;
        internal ulong QpcCompose;
        internal ulong Frame;
        internal uint DxPresent;
        internal ulong RefreshFrame;
        internal ulong FrameSubmitted;
        internal uint DxPresentSubmitted;
        internal ulong FrameConfirmed;
        internal uint DxPresentConfirmed;
        internal ulong RefreshConfirmed;
        internal uint DxRefreshConfirmed;
        internal ulong FramesLate;
        internal uint FramesOutstanding;
        internal ulong FrameDisplayed;
        internal ulong QpcFrameDisplayed;
        internal ulong RefreshFrameDisplayed;
        internal ulong FrameComplete;
        internal ulong QpcFrameComplete;
        internal ulong FramePending;
        internal ulong QpcFramePending;
        internal ulong FramesDisplayed;
        internal ulong FramesComplete;
        internal ulong FramesPending;
        internal ulong FramesAvailable;
        internal ulong FramesDropped;
        internal ulong FramesMissed;
        internal ulong RefreshNextDisplayed;
        internal ulong RefreshNextPresented;
        internal ulong RefreshesDisplayed;
        internal ulong RefreshesPresented;
        internal ulong RefreshStarted;
        internal ulong PixelsReceived;
        internal ulong PixelsDrawn;
        internal ulong BuffersEmpty;
    }

    public sealed class DwmFrameSampleResult
    {
        public double P95Milliseconds { get; internal set; }
        public double MaximumMilliseconds { get; internal set; }
        public double DroppedPercent { get; internal set; }
        public int IntervalCount { get; internal set; }
        public int ExactIntervalCount { get; internal set; }
        public int MultiRefreshSegmentCount { get; internal set; }
    }

    public static class DwmFrameSampler
    {
        private static readonly object Sync = new object();
        private static System.Threading.Thread worker;
        private static bool running;
        private static Exception failure;
        private static readonly System.Collections.Generic.List<double> IntervalsMilliseconds =
            new System.Collections.Generic.List<double>();
        private static readonly System.Collections.Generic.List<double> ExactIntervalsMilliseconds =
            new System.Collections.Generic.List<double>();
        private static ulong displayed;
        private static ulong dropped;
        private static int multiRefreshSegmentCount;
        private static int discontinuityCount;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmGetCompositionTimingInfo(
            IntPtr windowHandle,
            ref DwmTimingInfo timingInfo);

        public static void Start()
        {
            lock (Sync)
            {
                if (Marshal.SizeOf(typeof(DwmTimingInfo)) != 292)
                {
                    throw new InvalidOperationException("The DWM timing ABI size is invalid.");
                }
                if (running || worker != null)
                {
                    throw new InvalidOperationException("The DWM frame sampler is already active.");
                }
                IntervalsMilliseconds.Clear();
                ExactIntervalsMilliseconds.Clear();
                displayed = 0;
                dropped = 0;
                multiRefreshSegmentCount = 0;
                discontinuityCount = 0;
                failure = null;
                running = true;
                worker = new System.Threading.Thread(SampleLoop);
                worker.IsBackground = true;
                worker.Name = "IptvSuite M9 DWM sampler";
                worker.Start();
            }
        }

        public static bool HasMinimumExactIntervalSample(int minimumCount)
        {
            if (minimumCount < 1)
            {
                throw new ArgumentOutOfRangeException("minimumCount");
            }
            lock (Sync)
            {
                if (worker == null)
                {
                    throw new InvalidOperationException("The DWM frame sampler is not active.");
                }
                return ExactIntervalsMilliseconds.Count >= minimumCount;
            }
        }

        public static DwmFrameSampleResult Stop()
        {
            System.Threading.Thread thread;
            lock (Sync)
            {
                if (worker == null)
                {
                    throw new InvalidOperationException("The DWM frame sampler is not active.");
                }
                running = false;
                thread = worker;
            }
            if (!thread.Join(5000))
            {
                throw new TimeoutException("The DWM frame sampler did not stop.");
            }
            lock (Sync)
            {
                worker = null;
                if (failure != null)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "The DWM frame sampler failed ({0}, HRESULT 0x{1:X8}).",
                            failure.GetType().Name,
                            unchecked((uint)failure.HResult)),
                        failure);
                }
                if (discontinuityCount != 0)
                {
                    throw new InvalidOperationException("The DWM timing counters were discontinuous.");
                }
                if (IntervalsMilliseconds.Count < 30)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "The DWM frame interval sample is too small (intervals={0}, exactIntervals={1}, multiRefreshSegments={2}).",
                            IntervalsMilliseconds.Count,
                            ExactIntervalsMilliseconds.Count,
                            multiRefreshSegmentCount));
                }
                if (ExactIntervalsMilliseconds.Count < 30)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "The exact DWM frame interval sample is too small (intervals={0}, exactIntervals={1}, multiRefreshSegments={2}).",
                            IntervalsMilliseconds.Count,
                            ExactIntervalsMilliseconds.Count,
                            multiRefreshSegmentCount));
                }
                var intervals = new System.Collections.Generic.List<double>(IntervalsMilliseconds);
                var exactIntervals =
                    new System.Collections.Generic.List<double>(ExactIntervalsMilliseconds);
                intervals.Sort();
                exactIntervals.Sort();
                int percentileIndex = Math.Max(0, (int)Math.Ceiling(intervals.Count * 0.95) - 1);
                ulong denominator = displayed + dropped;
                if (denominator == 0)
                {
                    throw new InvalidOperationException("The DWM frame counters are unavailable.");
                }
                return new DwmFrameSampleResult
                {
                    P95Milliseconds = intervals[percentileIndex],
                    MaximumMilliseconds = exactIntervals[exactIntervals.Count - 1],
                    DroppedPercent = dropped * 100.0 / denominator,
                    IntervalCount = intervals.Count,
                    ExactIntervalCount = exactIntervals.Count,
                    MultiRefreshSegmentCount = multiRefreshSegmentCount,
                };
            }
        }

        private static void SampleLoop()
        {
            try
            {
                ulong previousTimestamp = 0;
                ulong previousRefresh = 0;
                ulong previousLate = 0;
                while (true)
                {
                    lock (Sync)
                    {
                        if (!running) return;
                    }
                    DwmTimingInfo timing = new DwmTimingInfo();
                    timing.Size = (uint)Marshal.SizeOf(typeof(DwmTimingInfo));
                    int result = DwmGetCompositionTimingInfo(IntPtr.Zero, ref timing);
                    if (result < 0)
                    {
                        throw new COMException("DWM composition timing is unavailable.", result);
                    }
                    if (timing.QpcVBlank != 0 && timing.QpcVBlank != previousTimestamp)
                    {
                        lock (Sync)
                        {
                            if (previousTimestamp != 0)
                            {
                                if (timing.QpcVBlank <= previousTimestamp ||
                                    timing.Refresh < previousRefresh ||
                                    timing.FramesLate < previousLate)
                                {
                                    discontinuityCount++;
                                }
                                else
                                {
                                    ulong refreshDelta = timing.Refresh - previousRefresh;
                                    if (refreshDelta > 0)
                                    {
                                        double intervalMilliseconds =
                                            (timing.QpcVBlank - previousTimestamp) * 1000.0 /
                                            System.Diagnostics.Stopwatch.Frequency /
                                            refreshDelta;
                                        IntervalsMilliseconds.Add(intervalMilliseconds);
                                        if (refreshDelta == 1)
                                        {
                                            ExactIntervalsMilliseconds.Add(intervalMilliseconds);
                                        }
                                        else
                                        {
                                            multiRefreshSegmentCount++;
                                        }
                                        ulong lateDelta = Math.Min(
                                            timing.FramesLate - previousLate,
                                            refreshDelta);
                                        displayed += refreshDelta - lateDelta;
                                        dropped += lateDelta;
                                    }
                                }
                            }
                        }
                        previousTimestamp = timing.QpcVBlank;
                        previousRefresh = timing.Refresh;
                        previousLate = timing.FramesLate;
                    }
                    System.Threading.Thread.Sleep(1);
                }
            }
            catch (Exception exception)
            {
                lock (Sync)
                {
                    failure = exception;
                    running = false;
                }
            }
        }
    }
}
'@
Add-Type -TypeDefinition $activationInterop -Language CSharp -ErrorAction Stop
. (Join-Path $PSScriptRoot "WindowsPackageInstallRootAudit.ps1")
. (Join-Path $PSScriptRoot "WindowsWack.ps1")
if ($EmitM16FinalArtifactSurfaces) {
    . (Join-Path $PSScriptRoot "WindowsM16FinalArtifactEvidence.ps1")
    . (Join-Path $PSScriptRoot "WindowsBoundedProcess.ps1")
}

$m16RunTokenProvided = -not [string]::IsNullOrEmpty($M16RunToken)
if ([bool]$EmitM16FinalArtifactSurfaces -ne $m16RunTokenProvided) {
    throw "The M16 final-artifact mode requires one exact controller-issued run token."
}

$expectedName = "IptvSuite.LocalDev.6f0d9a64"
$expectedPublisher = "CN=IptvSuite Local Development"
$packageIdentityMutexName =
    "Global\IptvSuite.PackageSmoke.IptvSuite.LocalDev.6f0d9a64"
$expectedApplicationId = "App"
$testCanaryMarker = "IPTVSUITE_TEST_ONLY_CANARY_V1"
$expectedRuntimeDependencyName = "Microsoft.WindowsAppRuntime.2"
$expectedRuntimeDependencyPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
$expectedRuntimeDependencyPublisherId = "8wekyb3d8bbwe"
$expectedRuntimeDependencyVersion = "2.4.0.0"
$expectedCatalogSourceName = "Synthetic 50k source"
$expectedPlaybackSourceName = "00 Synthetic protected playback source"
$expectedPlaybackChannelAName = "Synthetic protected Tier A channel A"
$expectedPlaybackChannelBName = "Synthetic protected Tier A channel B"
$expectedOnboardingEmptyStatus = "No imported Live TV catalog is available."
$expectedOnboardingCatalogStatus = "Showing 1$([char]0x2013)2 of 2 channels."
$expectedOnboardingPlaylistPath = if ($EmitM16FinalArtifactSurfaces) {
    "/$testCanaryMarker/synthetic-onboarding.m3u"
}
else {
    "/synthetic-onboarding.m3u"
}
$expectedPlaybackCertificateSubject = "CN=IPTVSuite Synthetic Loopback"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "apps\windows\src\IptvSuite.Windows\IptvSuite.Windows.csproj"
$catalogUiHarnessProjectPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.CatalogUiAcceptanceHarness\IptvSuite.CatalogUiAcceptanceHarness.csproj"
$catalogUiHarnessAssemblyPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.CatalogUiAcceptanceHarness\bin\x64\$Configuration\net10.0\IptvSuite.CatalogUiAcceptanceHarness.dll"
$playbackUiHarnessProjectPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.PlaybackUiAcceptanceHarness\IptvSuite.PlaybackUiAcceptanceHarness.csproj"
$playbackUiHarnessAssemblyPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.PlaybackUiAcceptanceHarness\bin\x64\$Configuration\net10.0\IptvSuite.PlaybackUiAcceptanceHarness.dll"
$testingProjectPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.Testing\IptvSuite.Testing.csproj"
$testingAssemblyPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.Testing\bin\x64\$Configuration\net10.0\IptvSuite.Testing.dll"
$playbackFixtureRoot = Join-Path $repositoryRoot "apps\windows\tests\fixtures\playback\tier-a"
$sourceManifestPath = Join-Path $repositoryRoot "apps\windows\src\IptvSuite.Windows\Package.appxmanifest"
$artifactRoot = Join-Path $repositoryRoot ".artifacts\msix-smoke"
$runId = if ($EmitM16FinalArtifactSurfaces) {
    $M16RunToken
}
else {
    [Guid]::NewGuid().ToString("N")
}
$packageOutput = Join-Path $artifactRoot "packages\$runId"
$playbackControlRoot = Join-Path $artifactRoot "playback-ui"
$playbackControlDirectory = Join-Path $playbackControlRoot $runId
$playbackReadyPath = Join-Path $playbackControlDirectory "ready.json"
$playbackResultPath = Join-Path $playbackControlDirectory "result.json"
$playbackStopSignalPath = Join-Path $playbackControlDirectory "stop.signal"
$playbackCancelVerificationSignalPath = Join-Path $playbackControlDirectory "verify-cancel.signal"
$playbackCancelVerificationTicketPath = Join-Path $playbackControlDirectory "cancel-result.json"
$playbackDialogCloseVerificationSignalPath = Join-Path $playbackControlDirectory "verify-dialog-close.signal"
$playbackDialogCloseVerificationTicketPath = Join-Path $playbackControlDirectory "dialog-close-result.json"
$playbackDeletionFaultArmSignalPath = Join-Path $playbackControlDirectory "arm-delete-failure.signal"
$playbackDeletionFaultReadyTicketPath = Join-Path $playbackControlDirectory "delete-failure-ready.json"
$playbackPendingVerificationSignalPath = Join-Path $playbackControlDirectory "verify-pending.signal"
$playbackPendingVerificationTicketPath = Join-Path $playbackControlDirectory "pending-result.json"
$playbackStreamFaultArmSignalPath = Join-Path $playbackControlDirectory "arm-stream-fault.signal"
$playbackStreamFaultReadyTicketPath = Join-Path $playbackControlDirectory "stream-fault-ready.json"
$playbackStreamEndSignalPath = Join-Path $playbackControlDirectory "end-stream.signal"
$playbackStreamEndResultTicketPath = Join-Path $playbackControlDirectory "stream-end-result.json"
$playbackStreamRestoreSignalPath = Join-Path $playbackControlDirectory "restore-stream.signal"
$playbackStreamRestoreResultTicketPath = Join-Path $playbackControlDirectory "stream-restore-result.json"
$playbackStreamEndForCancelSignalPath = Join-Path $playbackControlDirectory "end-stream-for-cancel.signal"
$playbackStreamCancelReadyTicketPath = Join-Path $playbackControlDirectory "stream-cancel-ready.json"
$playbackStreamCancelVerificationSignalPath = Join-Path $playbackControlDirectory "verify-stream-cancel.signal"
$playbackStreamCancelResultTicketPath = Join-Path $playbackControlDirectory "stream-cancel-result.json"
$playbackStreamProtocolControlNames = @(
    "arm-stream-fault.signal",
    "stream-fault-ready.json",
    "end-stream.signal",
    "stream-end-result.json",
    "restore-stream.signal",
    "stream-restore-result.json",
    "end-stream-for-cancel.signal",
    "stream-cancel-ready.json",
    "verify-stream-cancel.signal",
    "stream-cancel-result.json")
$playbackPublicCertificatePath = Join-Path $playbackControlDirectory "loopback.cer"
$onboardingControlRoot = Join-Path $artifactRoot "onboarding-ui"
$onboardingControlDirectory = Join-Path $onboardingControlRoot $runId
$onboardingReadyPath = Join-Path $onboardingControlDirectory "ready.json"
$onboardingResultPath = Join-Path $onboardingControlDirectory "result.json"
$onboardingStopSignalPath = Join-Path $onboardingControlDirectory "stop.signal"
$onboardingPublicCertificatePath = Join-Path $onboardingControlDirectory "loopback.cer"
$onboardingPipeName = "iptvsuite-onboarding-$runId"
$publicCertificatePath = Join-Path $artifactRoot "$runId.cer"
$evidencePath = Join-Path $artifactRoot "last-success.json"
$failureEvidencePath = Join-Path $artifactRoot "last-failure.json"
$packageSbomPath = Join-Path $artifactRoot "package-sbom.spdx.json"
$packageSbomSummaryPath = Join-Path $artifactRoot "package-sbom-summary.json"
$wackEvidencePath = Join-Path $artifactRoot "wack-development-preflight-summary.json"
$m16SurfaceEvidencePath = Join-Path $artifactRoot "m16-final-artifact-surfaces.json"
$m16BindingEvidencePath = Join-Path $artifactRoot "m16-final-artifact-binding.json"
$m16CaptureParent = Join-Path $artifactRoot "m16-final-artifact-capture"
$m16CaptureRoot = Join-Path $m16CaptureParent $runId
$m16OwnershipParent = Join-Path $artifactRoot "m16-final-artifact-ownership"
$m16OwnershipRoot = Join-Path $m16OwnershipParent $runId
$m16SigningThumbprintPath = Join-Path `
    $m16OwnershipRoot `
    "signing-certificate.thumbprint"
$m16PackageRegistrationIntentPath = Join-Path `
    $m16OwnershipRoot `
    "package-registration.intent"
$m16OnboardingThumbprintPath = Join-Path `
    $m16OwnershipRoot `
    "onboarding-loopback.thumbprint"
$m16PlaybackThumbprintPath = Join-Path `
    $m16OwnershipRoot `
    "playback-loopback.thumbprint"
$certificateFriendlyName = if ($EmitM16FinalArtifactSurfaces) {
    "IptvSuite M16 Final Artifact $runId"
}
else {
    "IptvSuite Local Development Package Smoke $runId"
}

$certificate = $null
$installedPackage = $null
$packageInstallRootAudit = $null
$packageInstallRootAuditResult = $null
$preResetPackageInstallRootAuditResult = $null
$packageInstallRootAuditSegmentCount = 0
$packageInstallRootResetBoundaryEquivalent = $false
$packageInstallRootAuditCompletionAttempted = $false
$launchedProcess = $null
$playbackHarnessProcess = $null
$onboardingHarnessProcess = $null
$playbackLoopbackCertificate = $null
$onboardingLoopbackCertificate = $null
$playbackLoopbackCertificateThumbprint = $null
$onboardingLoopbackCertificateThumbprint = $null
$playbackLoopbackCertificateImported = $false
$onboardingLoopbackCertificateImported = $false
$playbackHarnessReady = $false
$onboardingHarnessReady = $false
$playbackStopSignalCreated = $false
$onboardingStopSignalCreated = $false
$installAttempted = $false
$environmentBackup = @{}
$primaryFailure = $null
$successEvidence = $null
$successMessage = $null
$packageSbomResult = $null
$wackDevelopmentIdentityResult = $null
$m16CommitSha = $null
$m16SurfaceReports = $null
$m16PostScanPackageSha256 = $null
$protectedStoreDirectoryInitialized = $false
$catalogUiaContractVerified = $false
$catalogKeyboardFocusOrderVerified = $false
$catalog50kSeedVerified = $false
$cleanInstallOnboardingVerified = $false
$cleanInstallOnboardingAuthorizationVerified = $false
$cleanInstallOnboardingSourceVerified = $false
$cleanInstallOnboardingChannelsVerified = $false
$cleanInstallOnboardingResetVerified = $false
$cleanInstallOnboardingRequestCount = 0
$catalogRealizedContainerBoundVerified = $false
$catalogRealizedContainerCount = 0
$catalogTraceMarkersRequested = [bool]$EmitM14TraceMarkers
$catalogTraceMarkerBeginEmitted = $false
$catalogTraceMarkerEndEmitted = $false
$catalogTraceMarkerCount = 0
$catalogTraceMarkerProcessId = 0
$catalogTraceMarkerBeginName = ""
$catalogTraceMarkerEndName = ""
$catalogInputResponseP95Milliseconds = 0.0
$catalogFrameP95Milliseconds = 0.0
$catalogFrameMaximumMilliseconds = 0.0
$catalogDroppedFramePercent = 0.0
$catalogFrameIntervalCount = 0
$catalogExactFrameIntervalCount = 0
$catalogMultiRefreshSegmentCount = 0
$catalogUiThreadResponsivenessProxyVerified = $false
$catalogUiThreadResponsivenessProxyKind = "SendMessageTimeout(WM_NULL)"
$catalogUiThreadResponsivenessProxyTimeoutMilliseconds = 200
$catalogUiThreadResponsivenessProxySampleLimit = 64
$catalogUiThreadResponsivenessProxySampleCount = 0
$catalogUiThreadResponsivenessProxyTimeoutCount = 0
$catalogUiThreadResponsivenessProxyOverBudgetCount = 0
$catalogUiThreadResponsivenessProxyP95Milliseconds = 0.0
$catalogUiThreadResponsivenessProxyMaximumMilliseconds = 0.0
$catalogUiThreadResponsivenessProxyRawSamplesMilliseconds = @()
$catalogPlayerOffStateVerified = $false
$catalogPlayerOffSteadyWorkingSetVerified = $false
$catalogPlayerOffSteadyWorkingSetProcessAliveVerified = $false
$catalogPlayerOffSteadyWorkingSetBudgetBytes = [long](350MB)
$catalogPlayerOffSteadyWorkingSetSettleMilliseconds = 5000
$catalogPlayerOffSteadyWorkingSetSettleElapsedMilliseconds = 0.0
$catalogPlayerOffSteadyWorkingSetSampleIntervalMilliseconds = 500
$catalogPlayerOffSteadyWorkingSetSampleLimit = 60
$catalogPlayerOffSteadyWorkingSetSamplingTargetMilliseconds = 30000
$catalogPlayerOffSteadyWorkingSetSampleCount = 0
$catalogPlayerOffSteadyWorkingSetSamplingElapsedMilliseconds = 0.0
$catalogPlayerOffSteadyWorkingSetMinimumBytes = 0L
$catalogPlayerOffSteadyWorkingSetAverageBytes = 0.0
$catalogPlayerOffSteadyWorkingSetMaximumBytes = 0L
$catalogPlayerOffSteadyWorkingSetRawSamplesBytes = @()
$playbackUiAcceptanceVerified = $false
$playbackVolumeControlVerified = $false
$playbackMuteControlVerified = $false
$playbackAspectControlVerified = $false
$playbackFullscreenEnterVerified = $false
$playbackFullscreenExitVerified = $false
$playbackFullscreenFocusRestored = $false
$playbackRapidSwitchVerified = $false
$playbackRapidSwitchCount = 0
$playbackRapidSwitchP95Milliseconds = 0.0
$playbackRapidSwitchMaximumMilliseconds = 0.0
$playbackSurfaceBoundsVerified = $false
$playbackWindowResizeVerified = $false
$playbackWindowResizeCount = 0
$playbackWindowMinimizeVerified = $false
$playbackWindowRestoreVerified = $false
$playbackWindowStatePreserved = $false
$playbackResourceWarmupVerified = $false
$playbackResourceSnapshotVerified = $false
$playbackResourceBudgetVerified = $false
$playbackPrivateBytesDeltaBudget = 8MB
$playbackWorkingSetBytesDeltaBudget = 16MB
$playbackHandleCountDeltaBudget = 64
$playbackThreadCountDeltaBudget = 0
$playbackBaselinePrivateBytes = 0L
$playbackFinalPrivateBytes = 0L
$playbackPrivateBytesDelta = 0L
$playbackBaselineWorkingSetBytes = 0L
$playbackFinalWorkingSetBytes = 0L
$playbackWorkingSetBytesDelta = 0L
$playbackBaselineHandleCount = 0
$playbackFinalHandleCount = 0
$playbackHandleCountDelta = 0
$playbackBaselineThreadCount = 0
$playbackFinalThreadCount = 0
$playbackThreadCountDelta = 0
$playbackActiveCloseVerified = $false
$sourceDeletionCancelNoMutationVerified = $false
$sourceDeletionDialogCloseNoMutationVerified = $false
$sourceDeletionPendingFailureVerified = $false
$sourceDeletionPendingRestartAdmissionBlockedVerified = $false
$sourceDeletionManualRetryVerified = $false
$sourceDeletionPendingCatalogPreserved = $false
$sourceDeletionPendingConfigurationRecordPreserved = $false
$sourceDeletionPendingTombstoneBindingVerified = $false
$sourceDeletionPendingSiblingCatalogRetained = $false
$sourceDeletionFaultReleased = $false
$sourceDeletionActivePlaybackDrainVerified = $false
$sourceDeletionRestartNonAdmissionVerified = $false
$sourceDeletionTargetCatalogDeleted = $false
$sourceDeletionProtectedRecordsDeleted = $false
$sourceDeletionTombstoneBindingCompleted = $false
$sourceDeletionSiblingCatalogRetained = $false
$playbackUiRequestCount = 0
$playbackUiCompletedResponseCount = 0
$playbackUiCompletedBodyBytes = 0L
$playbackChannelARequestCount = 0
$playbackChannelBRequestCount = 0
$playbackReconnectRecoveryVerified = $false
$playbackReconnectCancelVerified = $false
$playbackReconnectCancelBudgetMilliseconds = 1000.0
$playbackReconnectCancelElapsedMilliseconds = 0.0
$playbackReconnectNoLaterOpenVerified = $false
$playbackReconnectNoLaterOpenObservationMilliseconds = 0L
$playbackReconnectNoLaterOpenRequestCountAtReady = 0
$playbackReconnectNoLaterOpenRequestCountAfterObservation = 0
$normalStreamLastAssignedRequestOrdinal = 0L
$normalStreamClientDetachCount = 0
$faultStreamExpectedCompletionCount = 0
$faultStreamClientDetachCount = 0
$windowsAppRuntimeDisposition = "NotStarted"
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$msBuildEnvironment = @{
    AppxBundle                    = "Never"
    AppxPackageDir                = "$packageOutput\"
    AppxPackageSigningEnabled     = "true"
    AppxSymbolPackageEnabled      = "false"
    DebugSymbols                  = "false"
    DebugType                     = "None"
    GenerateAppxPackageOnBuild    = "true"
    UapAppxPackageBuildMode       = "SideloadOnly"
}

function Enter-WindowsPackageIdentityMutex {
    $mutex = [System.Threading.Mutex]::new(
        $false,
        $packageIdentityMutexName)
    try {
        $acquired = $mutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        $acquired = $true
    }
    if (-not $acquired) {
        $mutex.Dispose()
        throw "Another package-smoke process owns the disposable package identity."
    }

    return $mutex
}

function Assert-WindowsPackageIdentityMutexOwnedByController {
    $probeMutex = [System.Threading.Mutex]::new(
        $false,
        $packageIdentityMutexName)
    $unexpectedlyAcquired = $false
    try {
        try {
            $unexpectedlyAcquired = $probeMutex.WaitOne(0)
        }
        catch [System.Threading.AbandonedMutexException] {
            $unexpectedlyAcquired = $true
        }
        if ($unexpectedlyAcquired) {
            $probeMutex.ReleaseMutex()
            throw "M16 package-smoke mode requires the controller-owned package-identity mutex."
        }
    }
    finally {
        $probeMutex.Dispose()
    }
}

function Exit-WindowsPackageIdentityMutex {
    param(
        [Parameter(Mandatory)]
        [System.Threading.Mutex]$Mutex
    )

    $released = $false
    try {
        $Mutex.ReleaseMutex()
        $released = $true
    }
    finally {
        $Mutex.Dispose()
    }
    if (-not $released) {
        throw "The disposable package-identity mutex could not be released."
    }
}

function Assert-ManifestPolicy {
    param(
        [Parameter(Mandatory)]
        [xml]$Manifest
    )

    $identity = $Manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    if ($null -eq $identity) {
        throw "Package identity is missing."
    }

    if ($identity.GetAttribute("Name") -ne $expectedName) {
        throw "Unexpected package name."
    }

    if ($identity.GetAttribute("Publisher") -ne $expectedPublisher) {
        throw "Unexpected package publisher."
    }

    $applications = @($Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']"))
    if ($applications.Count -ne 1 -or $applications[0].GetAttribute("Id") -ne $expectedApplicationId) {
        throw "The package must contain exactly the M1 application."
    }

    $capabilities = @(
        $Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Capabilities']/*") |
            ForEach-Object { $_.GetAttribute("Name") }
    )
    $capabilityDifference = @(Compare-Object -ReferenceObject @("runFullTrust") -DifferenceObject $capabilities)
    if ($capabilityDifference.Count -ne 0) {
        throw "Unexpected capability set: $($capabilities -join ', ')"
    }
}

function Assert-BuiltManifestPolicy {
    param(
        [Parameter(Mandatory)]
        [xml]$Manifest
    )

    Assert-ManifestPolicy -Manifest $Manifest

    $identity = $Manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    if ($identity.GetAttribute("ProcessorArchitecture") -ne "x64") {
        throw "The built package must target x64 only."
    }

    $targetFamilies = @($Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='TargetDeviceFamily']"))
    if ($targetFamilies.Count -ne 1 -or
        $targetFamilies[0].GetAttribute("Name") -ne "Windows.Desktop" -or
        $targetFamilies[0].GetAttribute("MinVersion") -ne "10.0.26100.0") {
        throw "Unexpected Windows device-family baseline."
    }

    $frameworkDependencies = @($Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']"))
    if ($frameworkDependencies.Count -ne 1 -or
        $frameworkDependencies[0].GetAttribute("Name") -ne "Microsoft.WindowsAppRuntime.2" -or
        $frameworkDependencies[0].GetAttribute("MinVersion") -ne "2.4.0.0") {
        throw "The MSIX must remain framework-dependent on Windows App Runtime 2.4.0."
    }
}

function Expand-MsixForInspection {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    $packageFullPath = [System.IO.Path]::GetFullPath($PackagePath)
    $destinationFullPath = [System.IO.Path]::GetFullPath($DestinationPath)
    if (-not [System.IO.File]::Exists($packageFullPath)) {
        throw "The MSIX inspection input does not exist."
    }

    if (Test-Path -LiteralPath $destinationFullPath) {
        throw "The MSIX inspection destination already exists."
    }

    [System.IO.Directory]::CreateDirectory($destinationFullPath) | Out-Null
    if (([System.IO.File]::GetAttributes($destinationFullPath) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The MSIX inspection destination must not be a reparse point."
    }

    $directorySeparator = [System.IO.Path]::DirectorySeparatorChar
    $destinationPrefix = $destinationFullPath
    if (-not $destinationPrefix.EndsWith($directorySeparator)) {
        $destinationPrefix += $directorySeparator
    }

    $seenTargets = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $maximumEntryCount = 25000
    [long]$maximumExtractedBytes = 2147483648
    [long]$totalExtractedBytes = 0
    $packageStream = $null
    $archive = $null

    try {
        $packageStream = [System.IO.File]::Open(
            $packageFullPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        $archive = [System.IO.Compression.ZipArchive]::new(
            $packageStream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false)

        if ($archive.Entries.Count -gt $maximumEntryCount) {
            throw "The MSIX contains too many entries for safe inspection."
        }

        foreach ($entry in $archive.Entries) {
            if ($entry.Length -gt ($maximumExtractedBytes - $totalExtractedBytes)) {
                throw "The MSIX exceeds the safe inspection size limit."
            }
            $totalExtractedBytes += $entry.Length

            $normalizedEntryName = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($normalizedEntryName) -or
                $normalizedEntryName.StartsWith('/')) {
                throw "The MSIX contains an invalid archive path."
            }

            $isDirectory = $normalizedEntryName.EndsWith('/')
            $segments = @(
                $normalizedEntryName.Split(
                    [char]'/',
                    [System.StringSplitOptions]::RemoveEmptyEntries)
            )
            if ($segments.Count -eq 0) {
                throw "The MSIX contains an empty archive path."
            }

            foreach ($segment in $segments) {
                $deviceName = $segment.Split('.')[0]
                if ($segment -in @('.', '..') -or
                    $segment.IndexOf(':') -ge 0 -or
                    $segment.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
                    $segment.EndsWith('.') -or
                    $segment.EndsWith(' ') -or
                    $deviceName -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
                    throw "The MSIX contains an unsafe archive path segment."
                }
            }

            $relativePath = [string]::Join([string]$directorySeparator, [string[]]$segments)
            $targetPath = [System.IO.Path]::GetFullPath(
                [System.IO.Path]::Combine($destinationFullPath, $relativePath))
            if (-not $targetPath.StartsWith(
                    $destinationPrefix,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "The MSIX archive path escapes the inspection directory."
            }

            if (-not $seenTargets.Add($targetPath)) {
                throw "The MSIX contains duplicate or case-colliding archive paths."
            }

            if ($isDirectory) {
                if ($entry.Length -ne 0) {
                    throw "The MSIX contains a directory entry with data."
                }
                [System.IO.Directory]::CreateDirectory($targetPath) | Out-Null
                continue
            }

            $targetDirectory = [System.IO.Path]::GetDirectoryName($targetPath)
            [System.IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
            if (([System.IO.File]::GetAttributes($targetDirectory) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "The MSIX extraction target must not be a reparse point."
            }

            $entryStream = $null
            $outputStream = $null
            try {
                $entryStream = $entry.Open()
                $outputStream = [System.IO.FileStream]::new(
                    $targetPath,
                    [System.IO.FileMode]::CreateNew,
                    [System.IO.FileAccess]::Write,
                    [System.IO.FileShare]::None,
                    81920,
                    [System.IO.FileOptions]::SequentialScan)
                $copyBuffer = New-Object byte[] 81920
                [long]$written = 0
                while (($read = $entryStream.Read($copyBuffer, 0, $copyBuffer.Length)) -gt 0) {
                    if ($written -gt ($entry.Length - $read)) {
                        throw "An MSIX entry expanded beyond its declared size."
                    }
                    $outputStream.Write($copyBuffer, 0, $read)
                    $written += $read
                }

                if ($written -ne $entry.Length) {
                    throw "An MSIX entry did not match its declared size."
                }
            }
            finally {
                if ($null -ne $outputStream) {
                    $outputStream.Dispose()
                }
                if ($null -ne $entryStream) {
                    $entryStream.Dispose()
                }
            }
        }
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        if ($null -ne $packageStream) {
            $packageStream.Dispose()
        }
    }
}

function Test-FileContainsByteSequence {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [byte[]]$Sequence
    )

    if ($Sequence.Length -eq 0) {
        throw "The payload scan sequence must not be empty."
    }

    $prefixTable = New-Object int[] $Sequence.Length
    $prefixLength = 0
    for ($index = 1; $index -lt $Sequence.Length; $index++) {
        while ($prefixLength -gt 0 -and $Sequence[$index] -ne $Sequence[$prefixLength]) {
            $prefixLength = $prefixTable[$prefixLength - 1]
        }

        if ($Sequence[$index] -eq $Sequence[$prefixLength]) {
            $prefixLength++
            $prefixTable[$index] = $prefixLength
        }
    }

    $stream = $null
    try {
        $stream = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        $buffer = New-Object byte[] 65536
        $matched = 0
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            for ($index = 0; $index -lt $read; $index++) {
                $current = $buffer[$index]
                while ($matched -gt 0 -and $current -ne $Sequence[$matched]) {
                    $matched = $prefixTable[$matched - 1]
                }

                if ($current -eq $Sequence[$matched]) {
                    $matched++
                    if ($matched -eq $Sequence.Length) {
                        return $true
                    }
                }
            }
        }

        return $false
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Assert-ProductionPackagePayload {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    $inspectionId = [Guid]::NewGuid().ToString('N')
    $inspectionLeaf = "IptvSuite-MsixInspection-$inspectionId"
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $inspectionRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($temporaryRoot, $inspectionLeaf))
    $expectedParent = [System.IO.Path]::GetDirectoryName($inspectionRoot)
    if (-not $expectedParent.Equals(
            $temporaryRoot.TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The MSIX inspection directory is outside the operating-system temp root."
    }

    $markerUtf8 = [System.Text.Encoding]::UTF8.GetBytes($testCanaryMarker)
    $base64StablePrefixLength = $testCanaryMarker.Length - ($testCanaryMarker.Length % 3)
    $base64StablePrefix = $testCanaryMarker.Substring(0, $base64StablePrefixLength)
    $markerPatterns = @(
        [pscustomobject]@{ Name = 'UTF-8'; Bytes = $markerUtf8 },
        [pscustomobject]@{ Name = 'UTF-16LE'; Bytes = [System.Text.Encoding]::Unicode.GetBytes($testCanaryMarker) },
        [pscustomobject]@{ Name = 'UTF-16BE'; Bytes = [System.Text.Encoding]::BigEndianUnicode.GetBytes($testCanaryMarker) },
        [pscustomobject]@{
            Name = 'Base64 UTF-8 prefix'
            Bytes = [System.Text.Encoding]::ASCII.GetBytes(
                [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($base64StablePrefix)))
        }
    )

    try {
        Expand-MsixForInspection -PackagePath $PackagePath -DestinationPath $inspectionRoot

        $payloadEntries = @(
            Get-ChildItem -LiteralPath $inspectionRoot -Force -Recurse |
                Sort-Object -Property FullName
        )
        if ($payloadEntries.Count -eq 0) {
            throw "The MSIX inspection produced an empty payload."
        }

        foreach ($entry in $payloadEntries) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "The extracted MSIX payload contains a reparse point."
            }

            $relativeEntryPath = $entry.FullName.Substring($inspectionRoot.Length).TrimStart('\', '/')
            if ($relativeEntryPath.IndexOf(
                    $testCanaryMarker,
                    [System.StringComparison]::Ordinal) -ge 0) {
                throw "Test canary marker detected in a production payload path."
            }

            if ($entry.PSIsContainer -and
                $entry.Name -match '^(?i:testdata|fixtures?)$') {
                throw "Forbidden test-data directory in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.SecretStoreSpike(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.ProtectedCatalogSpike(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.PackageLifecycleHarness(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.DpapiUserBoundaryHarness(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.CatalogCrashHarness(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.CatalogUiAcceptanceHarness(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.PlaybackUiAcceptanceHarness(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.Testing(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.PlaybackCompatibilitySpike(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }

            if ($entry.Name -match '^(?i:IptvSuite\.NativePlaybackCompatibilitySpike(?:\..*)?)$') {
                throw "Forbidden test infrastructure in production payload: $relativeEntryPath"
            }
        }

        $payloadFiles = @($payloadEntries | Where-Object { -not $_.PSIsContainer })
        foreach ($file in $payloadFiles) {
            $relativePath = $file.FullName.Substring($inspectionRoot.Length).TrimStart('\', '/')
            if ($file.Name -match '(?i)(?:Tests|Testing)(?:\.[A-Za-z0-9_-]+)*\.(?:dll|exe|pdb|json)$' -or
                $file.Name -match '^(?i:MSTest(?:\..+)?|testhost(?:\..+)?|Microsoft\.VisualStudio\.TestPlatform\..+|Microsoft\.TestPlatform\..+|Microsoft\.Testing\..+)$' -or
                $file.Name -match '^(?i:fixture-manifest(?:\.schema)?\.json)$') {
                throw "Forbidden test infrastructure in production payload: $relativePath"
            }

            foreach ($pattern in $markerPatterns) {
                if (Test-FileContainsByteSequence -Path $file.FullName -Sequence $pattern.Bytes) {
                    throw "Test canary marker detected in production payload: $relativePath ($($pattern.Name))."
                }
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $inspectionRoot) {
            $cleanupPath = [System.IO.Path]::GetFullPath($inspectionRoot)
            if (-not $cleanupPath.Equals($inspectionRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
                [System.IO.Path]::GetFileName($cleanupPath) -ne $inspectionLeaf -or
                ([System.IO.File]::GetAttributes($cleanupPath) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing unsafe MSIX inspection-directory cleanup."
            }

            Remove-Item -LiteralPath $cleanupPath -Recurse -Force
        }
    }
}

function Invoke-CleanupStep {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Failures,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    try {
        & $Action
    }
    catch {
        $message = $_.Exception.Message -replace '[\r\n]+', ' '
        $Failures.Add(("{0}: {1}" -f $Name, $message)) | Out-Null
    }
}

function Remove-ExactPackageOutput {
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($runId, '\A[0-9a-f]{32}\z')) {
        throw "Refusing package-output cleanup because the run id is invalid."
    }

    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $resolvedPackagesRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($resolvedArtifactRoot, 'packages'))
    $resolvedPackageOutput = [System.IO.Path]::GetFullPath($packageOutput)
    $expectedPackageOutput = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($resolvedPackagesRoot, $runId))
    $packageOutputParent = [System.IO.Directory]::GetParent($resolvedPackageOutput)

    if (-not $resolvedPackageOutput.Equals(
            $expectedPackageOutput,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $null -eq $packageOutputParent -or
        -not $packageOutputParent.FullName.Equals(
            $resolvedPackagesRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedPackageOutput) -ne $runId) {
        throw "Refusing cleanup of an unexpected package-output directory."
    }

    if (-not (Test-Path -LiteralPath $resolvedPackageOutput)) {
        return
    }

    foreach ($protectedPath in @($resolvedArtifactRoot, $resolvedPackagesRoot, $resolvedPackageOutput)) {
        if (-not (Test-Path -LiteralPath $protectedPath -PathType Container)) {
            throw "Refusing package-output cleanup because an expected directory is missing."
        }

        if (([System.IO.File]::GetAttributes($protectedPath) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing package-output cleanup through a reparse point."
        }
    }

    Remove-Item -LiteralPath $resolvedPackageOutput -Recurse -Force -ErrorAction Stop
}

function Remove-ExactPlaybackControlDirectory {
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($runId, '\A[0-9a-f]{32}\z')) {
        throw "Refusing playback-control cleanup because the run id is invalid."
    }

    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $resolvedControlRoot = [System.IO.Path]::GetFullPath($playbackControlRoot)
    $resolvedControlDirectory = [System.IO.Path]::GetFullPath($playbackControlDirectory)
    $expectedControlRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($resolvedArtifactRoot, 'playback-ui'))
    $expectedControlDirectory = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($expectedControlRoot, $runId))
    $controlParent = [System.IO.Directory]::GetParent($resolvedControlDirectory)
    if (-not $resolvedControlRoot.Equals(
            $expectedControlRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedControlDirectory.Equals(
            $expectedControlDirectory,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $null -eq $controlParent -or
        -not $controlParent.FullName.Equals(
            $resolvedControlRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedControlDirectory) -ne $runId) {
        throw "Refusing cleanup of an unexpected playback-control directory."
    }

    if (-not (Test-Path -LiteralPath $resolvedControlDirectory)) {
        return
    }

    foreach ($protectedPath in @(
            $resolvedArtifactRoot,
            $resolvedControlRoot,
            $resolvedControlDirectory)) {
        if (-not (Test-Path -LiteralPath $protectedPath -PathType Container) -or
            ([System.IO.File]::GetAttributes($protectedPath) -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing playback-control cleanup through an unsafe directory."
        }
    }

    Remove-Item -LiteralPath $resolvedControlDirectory -Recurse -Force -ErrorAction Stop
}

function Remove-ExactOnboardingControlDirectory {
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($runId, '\A[0-9a-f]{32}\z')) {
        throw "Refusing onboarding-control cleanup because the run id is invalid."
    }

    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $resolvedControlRoot = [System.IO.Path]::GetFullPath($onboardingControlRoot)
    $resolvedControlDirectory = [System.IO.Path]::GetFullPath($onboardingControlDirectory)
    $expectedControlRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($resolvedArtifactRoot, 'onboarding-ui'))
    $expectedControlDirectory = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($expectedControlRoot, $runId))
    $controlParent = [System.IO.Directory]::GetParent($resolvedControlDirectory)
    if (-not $resolvedControlRoot.Equals(
            $expectedControlRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedControlDirectory.Equals(
            $expectedControlDirectory,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $null -eq $controlParent -or
        -not $controlParent.FullName.Equals(
            $resolvedControlRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedControlDirectory) -cne $runId) {
        throw "Refusing cleanup of an unexpected onboarding-control directory."
    }

    if (-not (Test-Path -LiteralPath $resolvedControlDirectory)) {
        return
    }

    foreach ($protectedPath in @(
            $resolvedArtifactRoot,
            $resolvedControlRoot,
            $resolvedControlDirectory)) {
        if (-not (Test-Path -LiteralPath $protectedPath -PathType Container) -or
            ([System.IO.File]::GetAttributes($protectedPath) -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing onboarding-control cleanup through an unsafe directory."
        }
    }

    Remove-Item -LiteralPath $resolvedControlDirectory -Recurse -Force -ErrorAction Stop
}

function Assert-ExactOnboardingControlEntries {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$AllowedNames
    )

    if (-not (Test-Path -LiteralPath $onboardingControlDirectory -PathType Container) -or
        ([System.IO.File]::GetAttributes($onboardingControlDirectory) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The onboarding acceptance control directory is invalid."
    }

    $entries = @(Get-ChildItem -LiteralPath $onboardingControlDirectory -Force)
    if ($entries.Count -ne $AllowedNames.Count) {
        throw "The onboarding acceptance control directory has an invalid schema."
    }

    foreach ($entry in $entries) {
        if ($entry.PSIsContainer -or
            ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $AllowedNames -cnotcontains $entry.Name) {
            throw "The onboarding acceptance control directory has an invalid schema."
        }
    }
}

function Read-StrictOnboardingJsonTicket {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string[]]$AllowedProperties
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $allowedPaths = @(
        [System.IO.Path]::GetFullPath($onboardingReadyPath),
        [System.IO.Path]::GetFullPath($onboardingResultPath)
    )
    if ($allowedPaths -notcontains $resolvedPath -or
        -not [System.IO.Directory]::GetParent($resolvedPath).FullName.Equals(
            [System.IO.Path]::GetFullPath($onboardingControlDirectory),
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "The onboarding acceptance ticket path is invalid."
    }

    $ticketFile = Get-Item -LiteralPath $resolvedPath -Force
    if (($ticketFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $ticketFile.Length -le 0 -or
        $ticketFile.Length -gt 4096) {
        throw "The onboarding acceptance ticket is invalid."
    }

    try {
        $ticket = [System.IO.File]::ReadAllText(
            $resolvedPath,
            [System.Text.Encoding]::UTF8) | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "The onboarding acceptance ticket is not valid JSON."
    }

    if ($ticket -isnot [pscustomobject]) {
        throw "The onboarding acceptance ticket root is invalid."
    }

    $properties = @($ticket.PSObject.Properties)
    if ($properties.Count -ne $AllowedProperties.Count) {
        throw "The onboarding acceptance ticket schema is invalid."
    }
    foreach ($property in $properties) {
        if ($AllowedProperties -cnotcontains $property.Name) {
            throw "The onboarding acceptance ticket schema is invalid."
        }
    }
    foreach ($allowedProperty in $AllowedProperties) {
        if (@($properties.Name) -cnotcontains $allowedProperty) {
            throw "The onboarding acceptance ticket schema is invalid."
        }
    }

    return $ticket
}

function Assert-ExactPlaybackControlEntries {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$AllowedNames
    )

    if (-not (Test-Path -LiteralPath $playbackControlDirectory -PathType Container) -or
        ([System.IO.File]::GetAttributes($playbackControlDirectory) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The playback acceptance control directory is invalid."
    }

    $entries = @(Get-ChildItem -LiteralPath $playbackControlDirectory -Force)
    if ($entries.Count -ne $AllowedNames.Count) {
        throw "The playback acceptance control directory has an invalid schema."
    }

    foreach ($entry in $entries) {
        if ($entry.PSIsContainer -or
            ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $AllowedNames -cnotcontains $entry.Name) {
            throw "The playback acceptance control directory has an invalid schema."
        }
    }
}

function Read-StrictPlaybackJsonTicket {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string[]]$AllowedProperties
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $allowedPaths = @(
        [System.IO.Path]::GetFullPath($playbackReadyPath),
        [System.IO.Path]::GetFullPath($playbackResultPath),
        [System.IO.Path]::GetFullPath($playbackCancelVerificationTicketPath),
        [System.IO.Path]::GetFullPath($playbackDialogCloseVerificationTicketPath),
        [System.IO.Path]::GetFullPath($playbackDeletionFaultReadyTicketPath),
        [System.IO.Path]::GetFullPath($playbackPendingVerificationTicketPath),
        [System.IO.Path]::GetFullPath($playbackStreamFaultReadyTicketPath),
        [System.IO.Path]::GetFullPath($playbackStreamEndResultTicketPath),
        [System.IO.Path]::GetFullPath($playbackStreamRestoreResultTicketPath),
        [System.IO.Path]::GetFullPath($playbackStreamCancelReadyTicketPath),
        [System.IO.Path]::GetFullPath($playbackStreamCancelResultTicketPath)
    )
    if ($allowedPaths -notcontains $resolvedPath -or
        -not [System.IO.Directory]::GetParent($resolvedPath).FullName.Equals(
            [System.IO.Path]::GetFullPath($playbackControlDirectory),
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "The playback acceptance ticket path is invalid."
    }

    $ticketFile = Get-Item -LiteralPath $resolvedPath -Force
    if (($ticketFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $ticketFile.Length -le 0 -or
        $ticketFile.Length -gt 4096) {
        throw "The playback acceptance ticket is invalid."
    }

    try {
        $ticket = [System.IO.File]::ReadAllText(
            $resolvedPath,
            [System.Text.Encoding]::UTF8) | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "The playback acceptance ticket is not valid JSON."
    }

    if ($ticket -isnot [pscustomobject]) {
        throw "The playback acceptance ticket root is invalid."
    }

    $properties = @($ticket.PSObject.Properties)
    if ($properties.Count -ne $AllowedProperties.Count) {
        throw "The playback acceptance ticket schema is invalid."
    }

    foreach ($property in $properties) {
        if ($AllowedProperties -cnotcontains $property.Name) {
            throw "The playback acceptance ticket schema is invalid."
        }
    }
    foreach ($allowedProperty in $AllowedProperties) {
        if (@($properties.Name) -cnotcontains $allowedProperty) {
            throw "The playback acceptance ticket schema is invalid."
        }
    }

    return $ticket
}

function Write-JsonAtomically {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $resolvedDestination = [System.IO.Path]::GetFullPath($DestinationPath)
    $allowedDestinations = @(
        [System.IO.Path]::GetFullPath($evidencePath),
        [System.IO.Path]::GetFullPath($failureEvidencePath),
        [System.IO.Path]::GetFullPath($wackEvidencePath)
    )
    if ($allowedDestinations -notcontains $resolvedDestination -or
        -not [System.IO.Directory]::GetParent($resolvedDestination).FullName.Equals(
            $resolvedArtifactRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write evidence outside the exact artifact root."
    }

    if (Test-Path -LiteralPath $resolvedDestination) {
        throw "Refusing to overwrite an existing evidence file."
    }

    $temporaryPath = "$resolvedDestination.$runId.tmp"
    if (-not [System.IO.Directory]::GetParent($temporaryPath).FullName.Equals(
            $resolvedArtifactRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($temporaryPath) -ne
            "$([System.IO.Path]::GetFileName($resolvedDestination)).$runId.tmp") {
        throw "Refusing to use an unexpected evidence temporary path."
    }

    if (Test-Path -LiteralPath $temporaryPath) {
        throw "The evidence temporary path already exists."
    }

    try {
        $json = $Value | ConvertTo-Json -Depth 5
        [System.IO.File]::WriteAllText(
            $temporaryPath,
            $json + [Environment]::NewLine,
            [System.Text.UTF8Encoding]::new($false, $true))
        [System.IO.File]::Move($temporaryPath, $resolvedDestination)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction Stop
        }
    }
}

function Write-M16PrivateEvidenceAtomically {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    if (-not $EmitM16FinalArtifactSurfaces) {
        throw "M16 surface evidence cannot be written outside the explicit capture mode."
    }

    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $resolvedDestination = [System.IO.Path]::GetFullPath($DestinationPath)
    $allowedDestinations = @(
        [System.IO.Path]::GetFullPath($m16SurfaceEvidencePath),
        [System.IO.Path]::GetFullPath($m16BindingEvidencePath))
    if ($allowedDestinations -cnotcontains $resolvedDestination -or
        -not [System.IO.Directory]::GetParent($resolvedDestination).FullName.Equals(
            $resolvedArtifactRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $resolvedArtifactRoot -PathType Container) -or
        (([System.IO.File]::GetAttributes($resolvedArtifactRoot) -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Refusing to write M16 evidence through an unsafe artifact root."
    }
    if (Test-Path -LiteralPath $resolvedDestination) {
        throw "Refusing to overwrite existing M16 surface evidence."
    }

    $temporaryPath = "$resolvedDestination.$runId.tmp"
    if (-not [System.IO.Directory]::GetParent($temporaryPath).FullName.Equals(
            $resolvedArtifactRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($temporaryPath) -cne
            "$([System.IO.Path]::GetFileName($resolvedDestination)).$runId.tmp" -or
        (Test-Path -LiteralPath $temporaryPath)) {
        throw "Refusing to use an unexpected M16 evidence temporary path."
    }

    $json = $Value | ConvertTo-Json -Depth 6 -Compress
    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $bytes = $strictUtf8.GetBytes($json + [Environment]::NewLine)
    $stream = $null
    try {
        if ($bytes.Length -le 0 -or $bytes.Length -gt 65536) {
            throw "The M16 surface evidence exceeds its fixed byte budget."
        }
        $stream = [System.IO.File]::Open(
            $temporaryPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null
        [System.IO.File]::Move($temporaryPath, $resolvedDestination)
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        [System.Array]::Clear($bytes, 0, $bytes.Length)
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction Stop
        }
    }
}

function Write-M16SurfaceEvidenceAtomically {
    param(
        [Parameter(Mandatory)]
        [object]$Value
    )

    Write-M16PrivateEvidenceAtomically `
        -Value $Value `
        -DestinationPath $m16SurfaceEvidencePath
}

function Write-M16BindingEvidenceAtomically {
    param(
        [Parameter(Mandatory)]
        [object]$Value
    )

    Write-M16PrivateEvidenceAtomically `
        -Value $Value `
        -DestinationPath $m16BindingEvidencePath
}

function Write-M16CleanupOwnershipValue {
    param(
        [Parameter(Mandatory)]
        [ValidateSet(
            "signing-certificate.thumbprint",
            "package-registration.intent",
            "onboarding-loopback.thumbprint",
            "playback-loopback.thumbprint")]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Value
    )

    if (-not $EmitM16FinalArtifactSurfaces) {
        throw "M16 cleanup ownership cannot be written outside the explicit capture mode."
    }
    $pattern = if ($Name -ceq "package-registration.intent") {
        '\AIptvSuite\.LocalDev\.6f0d9a64_[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+_x64__[0-9a-z]{13}\z'
    }
    else {
        '\A[0-9A-F]{40}\z'
    }
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($Value, $pattern)) {
        throw "The M16 cleanup ownership value is invalid."
    }

    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $resolvedParent = [System.IO.Path]::GetFullPath($m16OwnershipParent)
    $resolvedRoot = [System.IO.Path]::GetFullPath($m16OwnershipRoot)
    $resolvedDestination = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($resolvedRoot, $Name))
    $expectedParent = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine(
            $resolvedArtifactRoot,
            "m16-final-artifact-ownership"))
    $expectedRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($expectedParent, $runId))
    if (-not $resolvedParent.Equals(
            $expectedParent,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedRoot.Equals(
            $expectedRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Directory]::GetParent($resolvedDestination).FullName.Equals(
            $resolvedRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $resolvedRoot -PathType Container) -or
        (([System.IO.File]::GetAttributes($resolvedParent) -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) -or
        (([System.IO.File]::GetAttributes($resolvedRoot) -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) -or
        (Test-Path -LiteralPath $resolvedDestination)) {
        throw "The M16 cleanup ownership destination is unsafe."
    }

    $serializedValue = if ($Name -ceq "package-registration.intent") {
        [ordered]@{
            SchemaVersion = 1
            RunToken = $runId
            ExpectedPackageFullName = $Value
        } | ConvertTo-Json -Depth 2 -Compress
    }
    else {
        $Value
    }
    $maximumBytes = if ($Name -ceq "package-registration.intent") { 512 } else { 128 }
    $bytes = [System.Text.UTF8Encoding]::new($false, $true).GetBytes($serializedValue)
    $stream = $null
    try {
        if ($bytes.Length -le 0 -or $bytes.Length -gt $maximumBytes) {
            throw "The M16 cleanup ownership value exceeds its fixed byte budget."
        }
        $stream = [System.IO.File]::Open(
            $resolvedDestination,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        [System.Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Initialize-M16CaptureRoot {
    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $resolvedParent = [System.IO.Path]::GetFullPath($m16CaptureParent)
    $resolvedCapture = [System.IO.Path]::GetFullPath($m16CaptureRoot)
    $expectedParent = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($resolvedArtifactRoot, "m16-final-artifact-capture"))
    $expectedCapture = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($expectedParent, $runId))
    if (-not $resolvedParent.Equals(
            $expectedParent,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedCapture.Equals(
            $expectedCapture,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $resolvedArtifactRoot -PathType Container) -or
        (([System.IO.File]::GetAttributes($resolvedArtifactRoot) -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "The M16 capture root is invalid."
    }

    if (Test-Path -LiteralPath $resolvedParent) {
        if (-not (Test-Path -LiteralPath $resolvedParent -PathType Container) -or
            (([System.IO.File]::GetAttributes($resolvedParent) -band
                    [System.IO.FileAttributes]::ReparsePoint) -ne 0) -or
            @(Get-ChildItem -LiteralPath $resolvedParent -Force).Count -ne 0) {
            throw "The M16 capture parent is unsafe or contains stale raw artifacts."
        }
    }
    else {
        [System.IO.Directory]::CreateDirectory($resolvedParent) | Out-Null
    }

    [System.IO.Directory]::CreateDirectory($resolvedCapture) | Out-Null
    [System.IO.Directory]::CreateDirectory(
        [System.IO.Path]::Combine($resolvedCapture, "scanner-io")) | Out-Null
    foreach ($createdPath in @(
            $resolvedParent,
            $resolvedCapture,
            [System.IO.Path]::Combine($resolvedCapture, "scanner-io"))) {
        if (-not (Test-Path -LiteralPath $createdPath -PathType Container) -or
            (([System.IO.File]::GetAttributes($createdPath) -band
                    [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "An M16 capture directory is unsafe."
        }
    }
}

function Get-M16CleanRepositoryCommit {
    $status = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "M16 final artifact capture could not inspect the repository."
    }
    if ($status.Count -ne 0) {
        throw "M16 final artifact capture requires a clean repository."
    }

    $commitOutput = @(& git -C $repositoryRoot rev-parse HEAD 2>&1)
    if ($LASTEXITCODE -ne 0 -or $commitOutput.Count -ne 1) {
        throw "M16 final artifact capture could not bind the repository commit."
    }
    $commit = ([string]$commitOutput[0]).Trim().ToLowerInvariant()
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch(
            $commit,
            '\A[0-9a-f]{40}\z')) {
        throw "M16 final artifact capture received an invalid repository commit."
    }

    $githubSha = [Environment]::GetEnvironmentVariable("GITHUB_SHA", "Process")
    if (-not [string]::IsNullOrWhiteSpace($githubSha) -and
        $githubSha.ToLowerInvariant() -cne $commit) {
        throw "M16 final artifact capture does not match GITHUB_SHA."
    }

    return $commit
}

function Assert-M16RepositoryStable {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('\A[0-9a-f]{40}\z')]
        [string]$ExpectedCommit
    )

    $actualCommit = Get-M16CleanRepositoryCommit
    if ($actualCommit -cne $ExpectedCommit) {
        throw "M16 final artifact capture repository commit changed during execution."
    }
}

function Remove-ExactM16CaptureRoot {
    param(
        [switch]$RetainExactPackage
    )

    if (-not (Test-Path -LiteralPath $m16CaptureRoot)) {
        return
    }
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($runId, '\A[0-9a-f]{32}\z')) {
        throw "Refusing M16 capture cleanup because the run id is invalid."
    }

    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    $resolvedParent = [System.IO.Path]::GetFullPath($m16CaptureParent)
    $resolvedCapture = [System.IO.Path]::GetFullPath($m16CaptureRoot)
    $expectedParent = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($resolvedArtifactRoot, "m16-final-artifact-capture"))
    $expectedCapture = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($expectedParent, $runId))
    if (-not $resolvedParent.Equals(
            $expectedParent,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedCapture.Equals(
            $expectedCapture,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Directory]::GetParent($resolvedCapture).FullName.Equals(
            $resolvedParent,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup of an unexpected M16 capture directory."
    }

    foreach ($protectedPath in @($resolvedArtifactRoot, $resolvedParent, $resolvedCapture)) {
        if (-not (Test-Path -LiteralPath $protectedPath -PathType Container) -or
            ([System.IO.File]::GetAttributes($protectedPath) -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing M16 capture cleanup through an unsafe directory."
        }
    }

    $captureEntries = @(
        Get-ChildItem -LiteralPath $resolvedCapture -Force -Recurse)
    if ($captureEntries.Count -gt 26000) {
        throw "Refusing M16 capture cleanup because the tree exceeds its entry budget."
    }
    [long]$captureBytes = 0
    foreach ($entry in $captureEntries) {
        if (-not $entry.PSIsContainer) {
            if ($entry.Length -gt (9GB - $captureBytes)) {
                throw "Refusing M16 capture cleanup because the tree exceeds its byte budget."
            }
            $captureBytes += $entry.Length
        }
    }

    $unsafeEntries = @(
        $captureEntries |
            Where-Object {
                ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
            })
    if ($unsafeEntries.Count -ne 0) {
        throw "Refusing M16 capture cleanup because the tree contains a reparse point."
    }

    if ($RetainExactPackage) {
        $directEntries = @(Get-ChildItem -LiteralPath $resolvedCapture -Force)
        $directNames = @($directEntries | ForEach-Object { $_.Name } | Sort-Object)
        if (($directNames -join "`n") -cne
                (@("exact-package", "scanner-io", "support-artifact") -join "`n") -or
            @($directEntries | Where-Object { -not $_.PSIsContainer }).Count -ne 0) {
            throw "The successful M16 capture has an unexpected retained surface layout."
        }
        foreach ($name in @("scanner-io", "support-artifact")) {
            $transientPath = [System.IO.Path]::GetFullPath(
                [System.IO.Path]::Combine($resolvedCapture, $name))
            if (-not [System.IO.Directory]::GetParent($transientPath).FullName.Equals(
                    $resolvedCapture,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing cleanup of an unexpected M16 transient surface."
            }
            Remove-Item -LiteralPath $transientPath -Recurse -Force -ErrorAction Stop
        }
        $retainedEntries = @(Get-ChildItem -LiteralPath $resolvedCapture -Force)
        if ($retainedEntries.Count -ne 1 -or
            $retainedEntries[0].Name -cne "exact-package" -or
            -not $retainedEntries[0].PSIsContainer -or
            (($retainedEntries[0].Attributes -band
                    [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "The exact M16 package surface was not retained exclusively."
        }
        return
    }

    Remove-Item -LiteralPath $resolvedCapture -Recurse -Force -ErrorAction Stop
    if (@(Get-ChildItem -LiteralPath $resolvedParent -Force).Count -eq 0) {
        Remove-Item -LiteralPath $resolvedParent -Force -ErrorAction Stop
    }
}

function Assert-M16ReleaseAcceptanceSupportArtifact {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$ExpectedArtifact,

        [string]$ExpectedSha256
    )

    $record = Read-WindowsM16FinalStrictJson `
        -Path $Path `
        -InputRoot $m16CaptureRoot
    Assert-WindowsM16FinalExactPropertySet `
        -Value $record.Value `
        -Expected @($ExpectedArtifact.Keys)
    foreach ($name in @($ExpectedArtifact.Keys)) {
        $expected = $ExpectedArtifact[$name]
        $actual = Get-WindowsM16FinalExactProperty $record.Value $name
        if ($expected -is [bool]) {
            if ($actual -isnot [bool] -or $actual -ne $expected) {
                throw "The M16 support artifact boolean contract is invalid."
            }
        }
        elseif ($expected -is [byte] -or $expected -is [int16] -or
            $expected -is [int32] -or $expected -is [int64]) {
            if (($actual -isnot [int32] -and $actual -isnot [int64]) -or
                [long]$actual -ne [long]$expected) {
                throw "The M16 support artifact integer contract is invalid."
            }
        }
        elseif ($expected -is [string]) {
            if ($actual -isnot [string] -or $actual -cne $expected) {
                throw "The M16 support artifact string contract is invalid."
            }
        }
        else {
            throw "The M16 support artifact contains an unsupported value type."
        }
    }
    if (-not [string]::IsNullOrEmpty($ExpectedSha256) -and
        $record.Sha256 -cne $ExpectedSha256) {
        throw "The M16 support artifact changed after validation."
    }
    return $record
}

function New-M16ReleaseAcceptanceSupportArtifact {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('\A[0-9a-f]{64}\z')]
        [string]$PackageSha256,

        [Parameter(Mandatory)]
        [string]$DestinationDirectory
    )

    $resolvedCapture = [System.IO.Path]::GetFullPath($m16CaptureRoot)
    $resolvedDestination = [System.IO.Path]::GetFullPath($DestinationDirectory)
    $expectedDestination = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($resolvedCapture, "support-artifact"))
    if (-not $resolvedDestination.Equals(
            $expectedDestination,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        (Test-Path -LiteralPath $resolvedDestination)) {
        throw "The M16 support-artifact destination is invalid."
    }

    [System.IO.Directory]::CreateDirectory($resolvedDestination) | Out-Null
    if (([System.IO.File]::GetAttributes($resolvedDestination) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The M16 support-artifact destination is unsafe."
    }

    $artifact = [ordered]@{
        SchemaVersion = 1
        Milestone = "M16"
        Scope = "ReleaseAcceptanceSupportArtifact"
        RunId = $runId
        PackageSha256 = $PackageSha256
        CleanInstallOnboardingVerified = $cleanInstallOnboardingVerified
        CleanInstallOnboardingRequestCount = $cleanInstallOnboardingRequestCount
        CatalogUiaContractVerified = $catalogUiaContractVerified
        CatalogKeyboardFocusOrderVerified = $catalogKeyboardFocusOrderVerified
        Catalog50kSeedVerified = $catalog50kSeedVerified
        CatalogRealizedContainerBoundVerified = $catalogRealizedContainerBoundVerified
        PlaybackUiAcceptanceVerified = $playbackUiAcceptanceVerified
        PlaybackRapidSwitchVerified = $playbackRapidSwitchVerified
        PlaybackRapidSwitchCount = $playbackRapidSwitchCount
        PlaybackResourceBudgetVerified = $playbackResourceBudgetVerified
        PlaybackReconnectRecoveryVerified = $playbackReconnectRecoveryVerified
        PlaybackReconnectCancelVerified = $playbackReconnectCancelVerified
        PlaybackReconnectNoLaterOpenVerified = $playbackReconnectNoLaterOpenVerified
        SourceDeletionCancelNoMutationVerified = $sourceDeletionCancelNoMutationVerified
        SourceDeletionDialogCloseNoMutationVerified = $sourceDeletionDialogCloseNoMutationVerified
        SourceDeletionPendingFailureVerified = $sourceDeletionPendingFailureVerified
        SourceDeletionManualRetryVerified = $sourceDeletionManualRetryVerified
        SourceDeletionTargetCatalogDeleted = $sourceDeletionTargetCatalogDeleted
        SourceDeletionProtectedRecordsDeleted = $sourceDeletionProtectedRecordsDeleted
        SourceDeletionSiblingCatalogRetained = $sourceDeletionSiblingCatalogRetained
        RawLocatorIncluded = $false
        RequestHeadersOrBodiesIncluded = $false
        FullMemoryDumpIncluded = $false
        AutomatedUploadEnabled = $false
    }
    $json = $artifact | ConvertTo-Json -Depth 3 -Compress
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $bytes = $utf8.GetBytes($json + [Environment]::NewLine)
    $destinationPath = [System.IO.Path]::Combine(
        $resolvedDestination,
        "release-acceptance-summary.json")
    $stream = $null
    try {
        if ($bytes.Length -le 0 -or $bytes.Length -gt 65536) {
            throw "The M16 support artifact exceeds its fixed byte budget."
        }
        $stream = [System.IO.File]::Open(
            $destinationPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        [System.Array]::Clear($bytes, 0, $bytes.Length)
    }

    $record = Assert-M16ReleaseAcceptanceSupportArtifact `
        -Path $destinationPath `
        -ExpectedArtifact $artifact
    return [pscustomobject][ordered]@{
        RootPath = $resolvedDestination
        FilePath = $destinationPath
        Sha256 = $record.Sha256
        ExpectedArtifact = $artifact
    }
}

function Invoke-M16ReleaseSurfaceScan {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("owned-app-data", "exact-package", "support-artifact")]
        [string]$SurfaceId,

        [Parameter(Mandatory)]
        [string]$RootPath
    )

    if (-not [System.IO.File]::Exists($testingAssemblyPath)) {
        throw "The M16 artifact scanner assembly is unavailable."
    }
    $resolvedRoot = [System.IO.Path]::GetFullPath($RootPath)
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container) -or
        ([System.IO.File]::GetAttributes($resolvedRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The M16 artifact scan surface is invalid."
    }

    $scannerIoRoot = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($m16CaptureRoot, "scanner-io"))
    if (-not (Test-Path -LiteralPath $scannerIoRoot -PathType Container) -or
        ([System.IO.File]::GetAttributes($scannerIoRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The M16 scanner I/O root is invalid."
    }
    $standardOutputPath = [System.IO.Path]::Combine($scannerIoRoot, "$SurfaceId.stdout")
    $standardErrorPath = [System.IO.Path]::Combine($scannerIoRoot, "$SurfaceId.stderr")
    if ((Test-Path -LiteralPath $standardOutputPath) -or
        (Test-Path -LiteralPath $standardErrorPath)) {
        throw "The M16 scanner output already exists."
    }

    $argumentValues = @(
        $testingAssemblyPath,
        "scan-release-artifacts",
        $resolvedRoot,
        "M16",
        "FINAL_ARTIFACTS")
    foreach ($argumentValue in $argumentValues) {
        if ([string]::IsNullOrWhiteSpace($argumentValue) -or $argumentValue.Contains('"')) {
            throw "An M16 scanner argument is invalid."
        }
    }
    $scannerArguments = ($argumentValues | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $scannerProcess = Invoke-WindowsBoundedProcess `
        -FilePath ([System.IO.Path]::GetFullPath($DotNetPath)) `
        -ArgumentString $scannerArguments `
        -WorkingDirectory $repositoryRoot `
        -StandardOutputPath $standardOutputPath `
        -StandardErrorPath $standardErrorPath `
        -TimeoutMilliseconds 600000 `
        -MaximumOutputBytes 131072
    if ([int]$scannerProcess.ExitCode -ne 0) {
        throw "The M16 artifact scanner rejected a final surface."
    }

    foreach ($outputPath in @($standardOutputPath, $standardErrorPath)) {
        if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf) -or
            ([System.IO.File]::GetAttributes($outputPath) -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The M16 scanner output is invalid."
        }
    }
    $standardOutputFile = Get-Item -LiteralPath $standardOutputPath -Force
    $standardErrorFile = Get-Item -LiteralPath $standardErrorPath -Force
    if ($standardOutputFile.Length -le 0 -or $standardOutputFile.Length -gt 4096 -or
        $standardErrorFile.Length -ne 0) {
        throw "The M16 scanner output contract is invalid."
    }

    $scannerRecord = Read-WindowsM16FinalStrictJson `
        -Path $standardOutputPath `
        -InputRoot $m16CaptureRoot
    $report = $scannerRecord.Value
    $allowedProperties = @(
        "schemaVersion",
        "profile",
        "result",
        "fileCount",
        "directoryCount",
        "totalFileBytes",
        "inventorySha256",
        "findingCount")
    Assert-WindowsM16FinalExactPropertySet `
        -Value $report `
        -Expected $allowedProperties
    if ($report.schemaVersion -isnot [int] -or $report.schemaVersion -ne 1 -or
        $report.profile -isnot [string] -or $report.profile -cne "M16ReleaseCandidate" -or
        $report.result -isnot [string] -or $report.result -cne "clean" -or
        $report.fileCount -isnot [int] -or $report.fileCount -le 0 -or
        $report.directoryCount -isnot [int] -or $report.directoryCount -lt 0 -or
        ($report.totalFileBytes -isnot [int] -and $report.totalFileBytes -isnot [long]) -or
        [long]$report.totalFileBytes -le 0 -or
        $report.inventorySha256 -isnot [string] -or
        -not [System.Text.RegularExpressions.Regex]::IsMatch(
            $report.inventorySha256,
            '\A[0-9a-f]{64}\z') -or
        $report.findingCount -isnot [int] -or $report.findingCount -ne 0) {
        throw "The M16 scanner report schema is invalid."
    }

    return [pscustomobject][ordered]@{
        SurfaceId = $SurfaceId
        SchemaVersion = 1
        Profile = "M16ReleaseCandidate"
        Result = "clean"
        FileCount = [int]$report.fileCount
        DirectoryCount = [int]$report.directoryCount
        TotalFileBytes = [long]$report.totalFileBytes
        InventorySha256 = [string]$report.inventorySha256
        FindingCount = 0
    }
}

function Remove-ExactDevelopmentPackage {
    $packages = @(
        Get-AppxPackage -Name $expectedName -ErrorAction SilentlyContinue |
            Where-Object { $_.Publisher -eq $expectedPublisher }
    )

    if ($packages.Count -gt 1) {
        throw "Refusing cleanup: more than one exact development package is registered."
    }

    if ($packages.Count -eq 1) {
        Remove-AppxPackage -Package $packages[0].PackageFullName
    }
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

function Get-RequiredAutomationElement {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$AutomationId,

        [Parameter(Mandatory)]
        [System.Windows.Automation.ControlType]$ControlType,

        [Parameter(Mandatory)]
        [string]$AccessibleName
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $element = $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $element -or
        $element.Current.ControlType -ne $ControlType -or
        $element.Current.Name -ne $AccessibleName) {
        throw "The packaged catalog UI Automation contract is invalid."
    }

    return $element
}

function Get-AutomationElementById {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$AutomationId
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Read-BoundedOnboardingPipeChunk {
    param(
        [Parameter(Mandatory)]
        [System.IO.Pipes.NamedPipeClientStream]$Pipe,

        [Parameter(Mandatory)]
        [byte[]]$Buffer,

        [Parameter(Mandatory)]
        [int]$Offset,

        [Parameter(Mandatory)]
        [int]$Count,

        [Parameter(Mandatory)]
        [System.DateTimeOffset]$Deadline
    )

    if ($Offset -lt 0 -or $Count -le 0 -or
        $Offset -gt $Buffer.Length - $Count) {
        throw "The onboarding locator channel read bounds are invalid."
    }

    $remaining = $Deadline - [System.DateTimeOffset]::UtcNow
    if ($remaining -le [TimeSpan]::Zero) {
        throw "The onboarding locator channel read timed out."
    }
    $remainingMilliseconds = [int][Math]::Min(
        [int]::MaxValue,
        [Math]::Max(1, [Math]::Ceiling($remaining.TotalMilliseconds)))
    $readTask = $Pipe.ReadAsync($Buffer, $Offset, $Count)
    if (-not $readTask.Wait($remainingMilliseconds)) {
        $Pipe.Dispose()
        try {
            [void]$readTask.Wait(1000)
        }
        catch {
        }
        throw "The onboarding locator channel read timed out."
    }

    return [int]$readTask.GetAwaiter().GetResult()
}

function Read-ExactOnboardingLocator {
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch(
            $onboardingPipeName,
            '\Aiptvsuite-onboarding-[0-9a-f]{32}\z')) {
        throw "The onboarding locator channel identity is invalid."
    }

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $onboardingPipeName,
        [System.IO.Pipes.PipeDirection]::In,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    $lengthBytes = [byte[]]::new(4)
    $locatorBytes = $null
    try {
        $pipe.Connect(10000)
        $readDeadline = [System.DateTimeOffset]::UtcNow.AddSeconds(10)
        $lengthOffset = 0
        while ($lengthOffset -lt $lengthBytes.Length) {
            $read = Read-BoundedOnboardingPipeChunk `
                -Pipe $pipe `
                -Buffer $lengthBytes `
                -Offset $lengthOffset `
                -Count ($lengthBytes.Length - $lengthOffset) `
                -Deadline $readDeadline
            if ($read -le 0) {
                throw "The onboarding locator channel ended before its length was received."
            }
            $lengthOffset += $read
        }

        if (-not [System.BitConverter]::IsLittleEndian) {
            throw "The onboarding locator channel byte order is unsupported."
        }
        $locatorLength = [System.BitConverter]::ToInt32($lengthBytes, 0)
        if ($locatorLength -le 0 -or $locatorLength -gt 4096) {
            throw "The onboarding locator channel length is invalid."
        }

        $locatorBytes = [byte[]]::new($locatorLength)
        $locatorOffset = 0
        while ($locatorOffset -lt $locatorBytes.Length) {
            $read = Read-BoundedOnboardingPipeChunk `
                -Pipe $pipe `
                -Buffer $locatorBytes `
                -Offset $locatorOffset `
                -Count ($locatorBytes.Length - $locatorOffset) `
                -Deadline $readDeadline
            if ($read -le 0) {
                throw "The onboarding locator channel ended before its payload was received."
            }
            $locatorOffset += $read
        }

        $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
        $locator = $strictUtf8.GetString($locatorBytes)
        $uri = $null
        if (-not [System.Uri]::TryCreate(
                $locator,
                [System.UriKind]::Absolute,
                [ref]$uri) -or
            $uri.Scheme -cne "https" -or
            -not [System.Net.IPAddress]::IsLoopback(
                [System.Net.IPAddress]::Parse($uri.Host)) -or
            $uri.AbsolutePath -cne $expectedOnboardingPlaylistPath -or
            -not [string]::IsNullOrEmpty($uri.Query) -or
            -not [string]::IsNullOrEmpty($uri.Fragment) -or
            -not [string]::IsNullOrEmpty($uri.UserInfo)) {
            $locator = $null
            throw "The onboarding locator channel payload is invalid."
        }

        [System.Array]::Clear($lengthBytes, 0, $lengthBytes.Length)
        $trailingByteCount = Read-BoundedOnboardingPipeChunk `
            -Pipe $pipe `
            -Buffer $lengthBytes `
            -Offset 0 `
            -Count 1 `
            -Deadline $readDeadline
        if ($trailingByteCount -ne 0) {
            $locator = $null
            throw "The onboarding locator channel contains trailing data."
        }

        return $locator
    }
    catch {
        throw "The onboarding locator could not be received through the transient channel."
    }
    finally {
        if ($null -ne $locatorBytes) {
            [System.Array]::Clear($locatorBytes, 0, $locatorBytes.Length)
        }
        [System.Array]::Clear($lengthBytes, 0, $lengthBytes.Length)
        $pipe.Dispose()
    }
}

function Invoke-PackagedOnboardingButton {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$ButtonElement
    )

    Assert-PackagedProcessAlive -Process $Process
    if (-not $ButtonElement.Current.IsEnabled -or
        $ButtonElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Button) {
        throw "A packaged onboarding command is unavailable."
    }

    $invokePatternObject = $null
    if (-not $ButtonElement.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$invokePatternObject)) {
        throw "A packaged onboarding command has no InvokePattern."
    }
    ([System.Windows.Automation.InvokePattern]$invokePatternObject).Invoke()
}

function Set-PackagedOnboardingText {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element,

        [Parameter(Mandatory)]
        [string]$Value
    )

    if (-not $Element.Current.IsEnabled -or
        $Element.Current.ControlType -ne [System.Windows.Automation.ControlType]::Edit) {
        throw "A packaged onboarding input is unavailable."
    }
    $valuePatternObject = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$valuePatternObject)) {
        throw "A packaged onboarding input has no ValuePattern."
    }
    ([System.Windows.Automation.ValuePattern]$valuePatternObject).SetValue($Value)
}

function Invoke-ExactDevelopmentPackageReset {
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedPackageFullName,

        [Parameter(Mandatory)]
        [string]$ExpectedPackageFamilyName,

        [Parameter(Mandatory)]
        [string]$ExpectedInstallRoot,

        [Parameter(Mandatory)]
        [string]$CatalogStatePath,

        [Parameter(Mandatory)]
        [string]$ProtectedStorePath
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedPackageFullName) -or
        [string]::IsNullOrWhiteSpace($ExpectedPackageFamilyName)) {
        throw "The exact development package identity is unavailable for reset."
    }

    $expectedCatalogStatePath = [System.IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA `
            "Packages\$ExpectedPackageFamilyName\LocalCache\Catalog\v2"))
    $expectedProtectedStorePath = [System.IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA `
            "Packages\$ExpectedPackageFamilyName\LocalCache\ProtectedStore\v2"))
    if (-not [System.IO.Path]::GetFullPath($CatalogStatePath).Equals(
            $expectedCatalogStatePath,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFullPath($ProtectedStorePath).Equals(
            $expectedProtectedStorePath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The exact onboarding-owned package state paths are invalid."
    }

    $deadline = (Get-Date).AddSeconds(5)
    $consecutiveAbsentObservations = 0
    while ((Get-Date) -lt $deadline) {
        $processes = @([System.Diagnostics.Process]::GetProcessesByName("IptvSuite.Windows"))
        try {
            if ($processes.Count -eq 0) {
                $consecutiveAbsentObservations++
                if ($consecutiveAbsentObservations -ge 3) {
                    break
                }
            }
            else {
                $consecutiveAbsentObservations = 0
            }
        }
        finally {
            foreach ($process in $processes) {
                $process.Dispose()
            }
        }
        Start-Sleep -Milliseconds 250
    }
    if ($consecutiveAbsentObservations -lt 3) {
        throw "The exact packaged application did not become quiescent before reset."
    }

    try {
        Reset-AppxPackage `
            -Package $ExpectedPackageFullName `
            -Confirm:$false `
            -ErrorAction Stop
    }
    catch {
        throw "The exact clean-install onboarding package reset failed."
    }

    $registrationDeadline = (Get-Date).AddSeconds(15)
    $registrations = @()
    $resetInstallRoot = $null
    $canonicalExpectedInstallRoot = [System.IO.Path]::GetFullPath(
        $ExpectedInstallRoot).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    do {
        $namedRegistrations = @(Get-AppxPackage -Name $expectedName -ErrorAction Stop)
        $conflictingRegistrations = @($namedRegistrations | Where-Object {
            $_.PackageFullName -cne $ExpectedPackageFullName -or
            $_.PackageFamilyName -cne $ExpectedPackageFamilyName -or
            $_.Publisher -cne $expectedPublisher
        })
        if ($conflictingRegistrations.Count -ne 0) {
            throw "The exact package registration changed during onboarding reset."
        }

        $registrations = @($namedRegistrations | Where-Object {
            $_.PackageFullName -ceq $ExpectedPackageFullName -and
            $_.PackageFamilyName -ceq $ExpectedPackageFamilyName -and
            $_.Publisher -ceq $expectedPublisher
        })
        if ($registrations.Count -eq 1) {
            $registrationInstallLocation = [string]$registrations[0].InstallLocation
            if (-not [string]::IsNullOrWhiteSpace($registrationInstallLocation)) {
                if (-not [System.IO.Path]::IsPathRooted($registrationInstallLocation)) {
                    throw "The exact package install-root binding changed during onboarding reset."
                }
                $resetInstallRoot = [System.IO.Path]::GetFullPath(
                    $registrationInstallLocation).TrimEnd(
                        [System.IO.Path]::DirectorySeparatorChar,
                        [System.IO.Path]::AltDirectorySeparatorChar)
                if (-not $resetInstallRoot.Equals(
                        $canonicalExpectedInstallRoot,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "The exact package install-root binding changed during onboarding reset."
                }
                break
            }
        }
        if ($registrations.Count -gt 1) {
            throw "The exact package registration changed during onboarding reset."
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $registrationDeadline)
    if ($registrations.Count -ne 1 -or $null -eq $resetInstallRoot) {
        throw "The exact package registration changed during onboarding reset."
    }

    $stateDeadline = (Get-Date).AddSeconds(15)
    while (((Test-Path -LiteralPath $CatalogStatePath) -or
            (Test-Path -LiteralPath $ProtectedStorePath)) -and
        (Get-Date) -lt $stateDeadline) {
        Start-Sleep -Milliseconds 250
    }
    if ((Test-Path -LiteralPath $CatalogStatePath) -or
        (Test-Path -LiteralPath $ProtectedStorePath)) {
        throw "The exact onboarding-owned package state remained after reset."
    }
}

function Assert-ExactPackageInstallRootAuditResult {
    param(
        [Parameter(Mandatory)]
        [object]$Result
    )

    if ($Result.SchemaVersion -ne 1 -or
        $Result.Scope -cne "ExactRegisteredProductPackageInstallLocation" -or
        $Result.ExcludedEntryCount -ne 0 -or
        $Result.BaselineEntryCount -le 0 -or
        $Result.BaselineFileCount -le 0 -or
        $Result.BaselineTotalBytes -le 0 -or
        $Result.FinalEntryCount -ne $Result.BaselineEntryCount -or
        $Result.FinalFileCount -ne $Result.BaselineFileCount -or
        $Result.FinalTotalBytes -ne $Result.BaselineTotalBytes -or
        -not [System.Text.RegularExpressions.Regex]::IsMatch(
            $Result.BaselineManifestSha256,
            '\A[0-9a-f]{64}\z') -or
        $Result.FinalManifestSha256 -cne $Result.BaselineManifestSha256 -or
        $Result.MutationEventCount -ne 0 -or
        $Result.WatcherOverflow -ne $false -or
        $Result.SnapshotEquivalent -ne $true -or
        $Result.RuntimeWriteAuditPassed -ne $true) {
        throw "The packaged install-root runtime audit result is invalid."
    }
}

function Test-AutomationElementContainsExactText {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$ExpectedText
    )

    $controlTypeCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $nameCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $ExpectedText)
    $condition = [System.Windows.Automation.AndCondition]::new(
        $controlTypeCondition,
        $nameCondition)
    return $null -ne $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Get-Percentile95 {
    param(
        [Parameter(Mandatory)]
        [double[]]$Samples
    )

    if ($Samples.Count -eq 0) {
        throw "At least one UI response sample is required."
    }
    $ordered = @($Samples | Sort-Object)
    $index = [Math]::Ceiling(0.95 * $ordered.Count) - 1
    return [double]$ordered[[Math]::Max(0, [int]$index)]
}

function Write-M14CatalogTraceMarker {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('\AIptvSuite\.M14\.CatalogInteraction\.(Begin|End)\.Pid[1-9][0-9]{0,9}\z')]
        [string]$Name
    )

    if (-not $EmitM14TraceMarkers) {
        throw "M14 catalog trace markers were not requested."
    }
    if ([string]::IsNullOrWhiteSpace($env:SystemRoot)) {
        throw "The Windows system root is unavailable for M14 trace markers."
    }

    $wprPath = Join-Path $env:SystemRoot "System32\wpr.exe"
    if (-not (Test-Path -LiteralPath $wprPath -PathType Leaf)) {
        throw "Windows Performance Recorder is unavailable for M14 trace markers."
    }

    & $wprPath -marker $Name -flush | Out-Null
    $markerExitCode = $LASTEXITCODE
    if ($markerExitCode -ne 0) {
        throw "Windows Performance Recorder rejected an M14 trace marker."
    }
}

function Assert-FocusedAutomationElement {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$ExpectedElement,

        [Parameter(Mandatory)]
        [string]$ExpectedAutomationId,

        [switch]$RequestFocus
    )

    $deadline = (Get-Date).AddSeconds(5)
    $observedFocusTarget = "None"
    do {
        if ($RequestFocus) {
            try {
                $ExpectedElement.SetFocus()
            }
            catch {
                # UI Automation focus transfer can race foreground activation;
                # the bounded identity assertion below remains fail-closed.
            }
        }

        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        $observedFocusTarget = if ($null -eq $focused) { "None" } else { "Other" }
        for ($depth = 0; $null -ne $focused -and $depth -lt 32; $depth++) {
            $focusedAutomationId = $focused.Current.AutomationId
            if ($focusedAutomationId -in @(
                    "CatalogSourceSelector",
                    "CatalogCategorySelector",
                    "CatalogSearchBox",
                    "CatalogChannelList")) {
                $observedFocusTarget = $focusedAutomationId
            }
            if ($focusedAutomationId -eq "PlaybackFullscreenButton") {
                $observedFocusTarget = $focusedAutomationId
            }
            if ([System.Windows.Automation.Automation]::Compare($focused, $ExpectedElement) -or
                $focusedAutomationId -eq $ExpectedAutomationId) {
                return
            }

            $focused = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($focused)
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    throw "The packaged catalog keyboard focus order is invalid at $ExpectedAutomationId (Observed$observedFocusTarget)."
}

function Assert-PackagedWindowForeground {
    param(
        [Parameter(Mandatory)]
        [IntPtr]$WindowHandle,

        [Parameter(Mandatory)]
        [uint32]$ExpectedProcessId
    )

    [void][IptvSuite.PackageSmoke.WindowInspector]::SetForegroundWindow($WindowHandle)
    $deadline = (Get-Date).AddSeconds(5)
    do {
        $foreground = [IptvSuite.PackageSmoke.WindowInspector]::GetForegroundWindow()
        if ($foreground -ne [IntPtr]::Zero) {
            [uint32]$ownerProcessId = 0
            [void][IptvSuite.PackageSmoke.WindowInspector]::GetWindowThreadProcessId(
                $foreground,
                [ref]$ownerProcessId)
            if ($ownerProcessId -eq $ExpectedProcessId) {
                return
            }
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    throw "The packaged catalog window did not own foreground keyboard input."
}

function Assert-PackagedProcessAlive {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    try {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "The packaged playback application exited before acceptance completed."
        }
    }
    catch {
        throw "The packaged playback application exited before acceptance completed."
    }
}

function Get-PackagedProcessResourceSnapshot {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    Assert-PackagedProcessAlive -Process $Process
    try {
        $Process.Refresh()
        return [pscustomobject][ordered]@{
            PrivateBytes = [long]$Process.PrivateMemorySize64
            WorkingSetBytes = [long]$Process.WorkingSet64
            HandleCount = [int]$Process.HandleCount
            ThreadCount = [int]$Process.Threads.Count
        }
    }
    catch {
        throw "The packaged playback process resource snapshot is unavailable."
    }
}

function Wait-PackagedPlaybackSurfaceBounds {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element,

        [Parameter(Mandatory)]
        [IntPtr]$WindowHandle,

        [double]$PreviousWidth = -1.0,

        [double]$PreviousHeight = -1.0
    )

    $deadline = (Get-Date).AddSeconds(5)
    do {
        Assert-PackagedProcessAlive -Process $Process
        try {
            $bounds = $Element.Current.BoundingRectangle
            $windowBounds =
                [IptvSuite.PackageSmoke.WindowInspector]::GetWindowBounds($WindowHandle)
            $changedFromPrevious =
                $PreviousWidth -lt 0.0 -or
                [Math]::Abs($bounds.Width - $PreviousWidth) -gt 1.0 -or
                [Math]::Abs($bounds.Height - $PreviousHeight) -gt 1.0
            if (-not [double]::IsNaN($bounds.Width) -and
                -not [double]::IsInfinity($bounds.Width) -and
                -not [double]::IsNaN($bounds.Height) -and
                -not [double]::IsInfinity($bounds.Height) -and
                $bounds.Width -gt 0.0 -and
                $bounds.Height -gt 0.0 -and
                $bounds.Left -ge ($windowBounds.Left - 1) -and
                $bounds.Top -ge ($windowBounds.Top - 1) -and
                $bounds.Right -le ($windowBounds.Right + 1) -and
                $bounds.Bottom -le ($windowBounds.Bottom + 1) -and
                $changedFromPrevious) {
                return $bounds
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    throw "The packaged playback surface did not reach valid in-window bounds."
}

function Set-PackagedWindowSize {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [IntPtr]$WindowHandle,

        [Parameter(Mandatory)]
        [int]$Width,

        [Parameter(Mandatory)]
        [int]$Height
    )

    Assert-PackagedProcessAlive -Process $Process
    [IptvSuite.PackageSmoke.WindowInspector]::RestoreWindow($WindowHandle)
    $restoreDeadline = (Get-Date).AddSeconds(5)
    do {
        Assert-PackagedProcessAlive -Process $Process
        if (-not [IptvSuite.PackageSmoke.WindowInspector]::IsWindowMinimized($WindowHandle)) {
            break
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $restoreDeadline)
    if ([IptvSuite.PackageSmoke.WindowInspector]::IsWindowMinimized($WindowHandle)) {
        throw "The packaged playback window did not restore before resize."
    }

    [IptvSuite.PackageSmoke.WindowInspector]::ResizeWindow(
        $WindowHandle,
        $Width,
        $Height)
    $deadline = (Get-Date).AddSeconds(5)
    $bounds = $null
    do {
        Assert-PackagedProcessAlive -Process $Process
        $bounds = [IptvSuite.PackageSmoke.WindowInspector]::GetWindowBounds($WindowHandle)
        if ($bounds.Width -eq $Width -and $bounds.Height -eq $Height) {
            return
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    $observedSize = if ($null -eq $bounds) {
        "unavailable"
    }
    else {
        "$($bounds.Width)x$($bounds.Height)"
    }
    throw "The packaged playback window did not reach requested size $($Width)x$Height (observed $observedSize)."
}

function Invoke-PackagedWindowMinimize {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [IntPtr]$WindowHandle
    )

    Assert-PackagedProcessAlive -Process $Process
    [IptvSuite.PackageSmoke.WindowInspector]::MinimizeWindow($WindowHandle)
    $deadline = (Get-Date).AddSeconds(5)
    do {
        Assert-PackagedProcessAlive -Process $Process
        if ([IptvSuite.PackageSmoke.WindowInspector]::IsWindowMinimized($WindowHandle)) {
            return
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    throw "The packaged playback window did not minimize before the deadline."
}

function Invoke-PackagedWindowRestore {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [IntPtr]$WindowHandle
    )

    Assert-PackagedProcessAlive -Process $Process
    [IptvSuite.PackageSmoke.WindowInspector]::RestoreWindow($WindowHandle)
    $deadline = (Get-Date).AddSeconds(5)
    do {
        Assert-PackagedProcessAlive -Process $Process
        if (-not [IptvSuite.PackageSmoke.WindowInspector]::IsWindowMinimized($WindowHandle) -and
            [IptvSuite.PackageSmoke.WindowInspector]::IsWindowVisible($WindowHandle)) {
            return
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    throw "The packaged playback window did not restore before the deadline."
}

function Wait-PackagedPlaybackStatus {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$StatusElement,

        [Parameter(Mandatory)]
        [string]$ExpectedStatus,

        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Assert-PackagedProcessAlive -Process $Process
        if ($StatusElement.Current.Name -ceq $ExpectedStatus) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "The packaged playback UI did not reach the expected safe state."
}

function Wait-PackagedPlaybackSelection {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$StatusElement,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$ChannelElement,

        [Parameter(Mandatory)]
        [string]$ExpectedChannelName,

        [int]$TimeoutMilliseconds = 30000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        Assert-PackagedProcessAlive -Process $Process
        if ($StatusElement.Current.Name -ceq "Channel is playing." -and
            $ChannelElement.Current.Name -ceq $ExpectedChannelName) {
            return
        }

        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "The packaged playback switch did not reach the expected channel-bound state."
}

function Wait-PackagedPlaybackStoppedWithinBudget {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$StatusElement,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$ChannelElement,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$StopButtonElement,

        [Parameter(Mandatory)]
        [System.Diagnostics.Stopwatch]$Timer,

        [Parameter(Mandatory)]
        [double]$BudgetMilliseconds
    )

    do {
        Assert-PackagedProcessAlive -Process $Process
        if ($StatusElement.Current.Name -ceq "Playback stopped." -and
            $ChannelElement.Current.Name -ceq "No channel selected." -and
            $StopButtonElement.Current.Name -ceq "Stop channel" -and
            -not $StopButtonElement.Current.IsEnabled) {
            $Timer.Stop()
            if ($Timer.Elapsed.TotalMilliseconds -gt $BudgetMilliseconds) {
                break
            }

            return [double]$Timer.Elapsed.TotalMilliseconds
        }

        Start-Sleep -Milliseconds 25
    } while ($Timer.Elapsed.TotalMilliseconds -le $BudgetMilliseconds)

    $Timer.Stop()
    throw "Reconnect cancellation did not stop packaged playback within the exact budget."
}

function Invoke-PackagedPlaybackChannelItem {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$ChannelItem,

        [Parameter(Mandatory)]
        [IntPtr]$WindowHandle,

        [Parameter(Mandatory)]
        [uint32]$ExpectedProcessId
    )

    Assert-PackagedProcessAlive -Process $Process
    $invokePatternObject = $null
    if ($ChannelItem.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$invokePatternObject)) {
        ([System.Windows.Automation.InvokePattern]$invokePatternObject).Invoke()
        return
    }

    Assert-PackagedWindowForeground -WindowHandle $WindowHandle -ExpectedProcessId $ExpectedProcessId
    Assert-FocusedAutomationElement $ChannelItem "CatalogChannelList" -RequestFocus
    [IptvSuite.PackageSmoke.KeyboardInspector]::PressEnter()
}

function Wait-PackagedAutomationName {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element,

        [Parameter(Mandatory)]
        [string]$ExpectedName,

        [int]$TimeoutSeconds = 10
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Assert-PackagedProcessAlive -Process $Process
        if ($Element.Current.Name -ceq $ExpectedName) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "A packaged playback control did not reach the expected safe state."
}

function Wait-PackagedAutomationElementByName {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$AutomationId,

        [Parameter(Mandatory)]
        [System.Windows.Automation.ControlType]$ControlType,

        [Parameter(Mandatory)]
        [string]$ExpectedName,

        [int]$TimeoutSeconds = 10
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Assert-PackagedProcessAlive -Process $Process
        try {
            $element = Get-AutomationElementById `
                -Root $Root `
                -AutomationId $AutomationId
            if ($null -ne $element -and
                $element.Current.ControlType -eq $ControlType -and
                $element.Current.Name -ceq $ExpectedName) {
                return $element
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "A packaged playback automation element did not reach the expected safe state."
}

function Invoke-PackagedPlaybackControlButton {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$ButtonElement
    )

    Assert-PackagedProcessAlive -Process $Process
    if (-not $ButtonElement.Current.IsEnabled -or
        $ButtonElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Button) {
        throw "A packaged playback command is unavailable."
    }

    $invokePatternObject = $null
    if (-not $ButtonElement.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$invokePatternObject)) {
        throw "A packaged playback command has no InvokePattern."
    }

    ([System.Windows.Automation.InvokePattern]$invokePatternObject).Invoke()
}

function Invoke-PackagedPlaybackButton {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$ButtonElement,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$StatusElement,

        [Parameter(Mandatory)]
        [string]$ExpectedStatus
    )

    Invoke-PackagedPlaybackControlButton `
        -Process $Process `
        -ButtonElement $ButtonElement
    Wait-PackagedPlaybackStatus `
        -Process $Process `
        -StatusElement $StatusElement `
        -ExpectedStatus $ExpectedStatus
}

function New-ExactPlaybackControlSignal {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $allowedPaths = @(
        [System.IO.Path]::GetFullPath($playbackCancelVerificationSignalPath),
        [System.IO.Path]::GetFullPath($playbackDialogCloseVerificationSignalPath),
        [System.IO.Path]::GetFullPath($playbackDeletionFaultArmSignalPath),
        [System.IO.Path]::GetFullPath($playbackPendingVerificationSignalPath),
        [System.IO.Path]::GetFullPath($playbackStreamFaultArmSignalPath),
        [System.IO.Path]::GetFullPath($playbackStreamEndSignalPath),
        [System.IO.Path]::GetFullPath($playbackStreamRestoreSignalPath),
        [System.IO.Path]::GetFullPath($playbackStreamEndForCancelSignalPath),
        [System.IO.Path]::GetFullPath($playbackStreamCancelVerificationSignalPath),
        [System.IO.Path]::GetFullPath($playbackStopSignalPath)
    )
    if ($allowedPaths -notcontains $resolvedPath -or
        -not [System.IO.Directory]::GetParent($resolvedPath).FullName.Equals(
            [System.IO.Path]::GetFullPath($playbackControlDirectory),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The playback acceptance signal path is invalid."
    }

    $signalStream = [System.IO.File]::Open(
        $resolvedPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $signalStream.Dispose()
}

function Wait-PlaybackStreamTicket {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$HarnessProcess,

        [Parameter(Mandatory)]
        [string]$TicketPath,

        [Parameter(Mandatory)]
        [string[]]$AllowedControlNames,

        [Parameter(Mandatory)]
        [string[]]$AllowedProperties,

        [ValidateRange(1, 120)]
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $HarnessProcess.Refresh()
        if ($HarnessProcess.HasExited) {
            throw "The playback acceptance harness exited during stream verification."
        }
        if (Test-Path -LiteralPath $TicketPath -PathType Leaf) {
            Assert-ExactPlaybackControlEntries -AllowedNames $AllowedControlNames
            return Read-StrictPlaybackJsonTicket `
                -Path $TicketPath `
                -AllowedProperties $AllowedProperties
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "The playback stream result was not published before the deadline."
}

function Wait-PlaybackPreservationTicket {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$HarnessProcess,

        [Parameter(Mandatory)]
        [string]$TicketPath,

        [Parameter(Mandatory)]
        [string[]]$AllowedControlNames
    )

    $deadline = (Get-Date).AddSeconds(30)
    do {
        $HarnessProcess.Refresh()
        if ($HarnessProcess.HasExited) {
            throw "The playback acceptance harness exited during preservation verification."
        }
        if (Test-Path -LiteralPath $TicketPath -PathType Leaf) {
            Assert-ExactPlaybackControlEntries -AllowedNames $AllowedControlNames
            $ticket = Read-StrictPlaybackJsonTicket `
                -Path $TicketPath `
                -AllowedProperties @(
                    "IsVerified",
                    "TargetCatalogPreserved",
                    "ConfigurationRecordPreserved",
                    "NoDeletionTombstone",
                    "SiblingCatalogRetained")
            foreach ($propertyName in @(
                    "IsVerified",
                    "TargetCatalogPreserved",
                    "ConfigurationRecordPreserved",
                    "NoDeletionTombstone",
                    "SiblingCatalogRetained")) {
                if ($ticket.$propertyName -isnot [bool] -or -not $ticket.$propertyName) {
                    throw "The playback preservation result is invalid."
                }
            }

            return
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "The playback preservation result was not published before the deadline."
}

function Wait-PlaybackDeletionFaultReadyTicket {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$HarnessProcess,

        [Parameter(Mandatory)]
        [string[]]$AllowedControlNames
    )

    $deadline = (Get-Date).AddSeconds(30)
    do {
        $HarnessProcess.Refresh()
        if ($HarnessProcess.HasExited) {
            throw "The playback acceptance harness exited before preparing deletion failure."
        }
        if (Test-Path -LiteralPath $playbackDeletionFaultReadyTicketPath -PathType Leaf) {
            Assert-ExactPlaybackControlEntries -AllowedNames $AllowedControlNames
            $ticket = Read-StrictPlaybackJsonTicket `
                -Path $playbackDeletionFaultReadyTicketPath `
                -AllowedProperties @("IsReady")
            if ($ticket.IsReady -isnot [bool] -or -not $ticket.IsReady) {
                throw "The playback deletion-failure readiness result is invalid."
            }

            return
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "The playback deletion-failure readiness result was not published before the deadline."
}

function Wait-PlaybackPendingDeletionTicket {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$HarnessProcess,

        [Parameter(Mandatory)]
        [string[]]$AllowedControlNames
    )

    $allowedProperties = @(
        "IsVerified",
        "TargetCatalogPreserved",
        "ConfigurationRecordPreserved",
        "TombstoneBindingPending",
        "SiblingCatalogRetained",
        "DeletionFaultReleased")
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $HarnessProcess.Refresh()
        if ($HarnessProcess.HasExited) {
            throw "The playback acceptance harness exited during pending-deletion verification."
        }
        if (Test-Path -LiteralPath $playbackPendingVerificationTicketPath -PathType Leaf) {
            Assert-ExactPlaybackControlEntries -AllowedNames $AllowedControlNames
            $ticket = Read-StrictPlaybackJsonTicket `
                -Path $playbackPendingVerificationTicketPath `
                -AllowedProperties $allowedProperties
            foreach ($propertyName in $allowedProperties) {
                if ($ticket.$propertyName -isnot [bool] -or -not $ticket.$propertyName) {
                    throw "The playback pending-deletion result is invalid."
                }
            }

            return $ticket
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "The playback pending-deletion result was not published before the deadline."
}

function Start-PackagedPlaybackApplicationInstance {
    $existingProcesses = @(Get-Process -Name "IptvSuite.Windows" -ErrorAction SilentlyContinue)
    if ($existingProcesses.Count -ne 0) {
        throw "IptvSuite.Windows is already running; refusing an ambiguous playback launch."
    }

    $processId = [IptvSuite.PackageSmoke.PackagedApplicationActivator]::Activate($aumid)
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        throw "The packaged playback application exited before its process could be observed."
    }
    $ownershipTransferred = $false
    try {
        try {
            $null = $process.Handle
        }
        catch {
            throw "The packaged playback application exited before its process handle could be retained."
        }

        Assert-PackagedProcessAlive -Process $process
        if ($process.ProcessName -ne "IptvSuite.Windows") {
            throw "Playback package activation returned an unexpected process."
        }

        $deadline = (Get-Date).AddSeconds(30)
        $windowHandle = [IntPtr]::Zero
        do {
            Assert-PackagedProcessAlive -Process $process
            $windowHandle = $process.MainWindowHandle
            if ($windowHandle -ne [IntPtr]::Zero -and
                [IptvSuite.PackageSmoke.WindowInspector]::IsWindowVisible($windowHandle)) {
                [uint32]$ownerProcessId = 0
                [void][IptvSuite.PackageSmoke.WindowInspector]::GetWindowThreadProcessId(
                    $windowHandle,
                    [ref]$ownerProcessId)
                if ($ownerProcessId -eq [uint32]$processId) {
                    $root = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
                    if ($null -eq $root) {
                        throw "The packaged playback application has no UI Automation root."
                    }

                    $instance = [pscustomobject]@{
                        Process = $process
                        ProcessId = [uint32]$processId
                        WindowHandle = $windowHandle
                        Root = $root
                    }
                    $ownershipTransferred = $true
                    return $instance
                }
            }

            Start-Sleep -Milliseconds 250
        } while ((Get-Date) -lt $deadline)

        throw "The packaged playback application did not create a visible window."
    }
    finally {
        if (-not $ownershipTransferred) {
            try {
                $process.Refresh()
                if (-not $process.HasExited) {
                    $process.Kill()
                    if (-not $process.WaitForExit(10000)) {
                        throw "The packaged playback application did not stop after activation failed."
                    }
                }
            }
            catch {
                throw "The packaged playback application could not be stopped after activation failed."
            }
            finally {
                $process.Dispose()
            }
        }
    }
}

function Get-PackagedPlaybackTargetContext {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Instance
    )

    $process = $Instance.Process
    $root = $Instance.Root
    $sourceElement = Get-RequiredAutomationElement `
        $root `
        "CatalogSourceSelector" `
        ([System.Windows.Automation.ControlType]::ComboBox) `
        "Playlist source"
    $channelListElement = Get-RequiredAutomationElement `
        $root `
        "CatalogChannelList" `
        ([System.Windows.Automation.ControlType]::List) `
        "Channels"
    $statusElement = Get-AutomationElementById $root "PlaybackStatusText"
    $currentChannelElement = Get-AutomationElementById $root "PlaybackChannelText"
    if ($null -eq $statusElement -or
        $statusElement.Current.ControlType -ne [System.Windows.Automation.ControlType]::Text -or
        $null -eq $currentChannelElement -or
        $currentChannelElement.Current.ControlType -ne [System.Windows.Automation.ControlType]::Text) {
        throw "The packaged playback state automation contract is invalid."
    }

    $selectionObject = $null
    $expandObject = $null
    if (-not $sourceElement.TryGetCurrentPattern(
            [System.Windows.Automation.SelectionPattern]::Pattern,
            [ref]$selectionObject) -or
        -not $sourceElement.TryGetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern,
            [ref]$expandObject)) {
        throw "The packaged playback source selector pattern contract is invalid."
    }
    $selection = [System.Windows.Automation.SelectionPattern]$selectionObject
    $expand = [System.Windows.Automation.ExpandCollapsePattern]$expandObject
    $expand.Expand()
    $listItemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $deadline = (Get-Date).AddSeconds(10)
    do {
        Assert-PackagedProcessAlive -Process $process
        $sourceItems = $sourceElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $listItemCondition)
        if ($sourceItems.Count -eq 2) {
            break
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    if ($sourceItems.Count -ne 2) {
        throw "The packaged playback catalog did not expose exactly two sources."
    }

    $targetItem = $null
    $siblingItem = $null
    for ($index = 0; $index -lt $sourceItems.Count; $index++) {
        if ($sourceItems[$index].Current.Name -ceq $expectedPlaybackSourceName) {
            $targetItem = $sourceItems[$index]
        }
        elseif ($sourceItems[$index].Current.Name -ceq $expectedCatalogSourceName) {
            $siblingItem = $sourceItems[$index]
        }
        else {
            throw "The packaged playback source list contains an unexpected source."
        }
    }
    if ($null -eq $targetItem -or $null -eq $siblingItem) {
        throw "The packaged playback source list is incomplete."
    }

    $selected = @($selection.Current.GetSelection())
    if ($selected.Count -ne 1 -or $selected[0].Current.Name -cne $expectedPlaybackSourceName) {
        $selectionItemObject = $null
        if (-not $targetItem.TryGetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern,
                [ref]$selectionItemObject)) {
            throw "The packaged playback target source has no SelectionItemPattern."
        }
        ([System.Windows.Automation.SelectionItemPattern]$selectionItemObject).Select()
    }
    if ($expand.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $expand.Collapse()
    }

    $catalogStatusElement = $null
    $expectedStatus = "Showing 1$([char]0x2013)2 of 2 channels."
    $deadline = (Get-Date).AddSeconds(15)
    $targetReady = $false
    do {
        Assert-PackagedProcessAlive -Process $process
        try {
            if ($null -eq $catalogStatusElement) {
                $catalogStatusElement = Get-AutomationElementById $root "CatalogStatusText"
            }
            $selected = @($selection.Current.GetSelection())
            if ($selected.Count -eq 1 -and
                $selected[0].Current.Name -ceq $expectedPlaybackSourceName -and
                $null -ne $catalogStatusElement -and
                $catalogStatusElement.Current.Name -ceq $expectedStatus) {
                $targetReady = $true
                break
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
            $catalogStatusElement = $null
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    if (-not $targetReady) {
        if ($null -eq $catalogStatusElement) {
            throw "The packaged playback catalog status automation element is missing after relaunch."
        }
        throw "The packaged playback target source did not become ready."
    }

    $deadline = (Get-Date).AddSeconds(10)
    do {
        Assert-PackagedProcessAlive -Process $process
        $channels = $channelListElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $listItemCondition)
        if ($channels.Count -eq 2) {
            break
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    if ($channels.Count -ne 2) {
        throw "The packaged playback target channel list is invalid."
    }

    $channelA = $null
    $channelB = $null
    for ($index = 0; $index -lt $channels.Count; $index++) {
        if (Test-AutomationElementContainsExactText -Root $channels[$index] -ExpectedText $expectedPlaybackChannelAName) {
            $channelA = $channels[$index]
        }
        elseif (Test-AutomationElementContainsExactText -Root $channels[$index] -ExpectedText $expectedPlaybackChannelBName) {
            $channelB = $channels[$index]
        }
        else {
            throw "The packaged playback target channel list contains an unexpected channel."
        }
    }
    if ($null -eq $channelA -or $null -eq $channelB) {
        throw "The packaged playback target channel list is incomplete."
    }

    Invoke-PackagedPlaybackChannelItem `
        -Process $process `
        -ChannelItem $channelA `
        -WindowHandle $Instance.WindowHandle `
        -ExpectedProcessId $Instance.ProcessId
    Wait-PackagedPlaybackSelection `
        -Process $process `
        -StatusElement $statusElement `
        -ChannelElement $currentChannelElement `
        -ExpectedChannelName $expectedPlaybackChannelAName

    $deleteButton = Get-RequiredAutomationElement `
        $root `
        "CatalogDeleteSourceButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Delete selected playlist source"
    return [pscustomobject]@{
        StatusElement = $statusElement
        CurrentChannelElement = $currentChannelElement
        DeleteButton = $deleteButton
    }
}

function Wait-PackagedSourceDeletionDialogButton {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [ValidateSet("Cancel", "Delete")]
        [string]$ExpectedButtonName
    )

    $textCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $deadline = (Get-Date).AddSeconds(10)
    do {
        Assert-PackagedProcessAlive -Process $Process
        $titles = @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition) |
            Where-Object { $_.Current.Name -ceq "Delete source?" })
        $buttons = @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition) |
            Where-Object { $_.Current.Name -ceq $ExpectedButtonName })
        if ($titles.Count -eq 1 -and $buttons.Count -eq 1) {
            return $buttons[0]
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "The packaged source-deletion confirmation dialog is invalid."
}

function Wait-PackagedSourceDeletionDialogDismissed {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$DeleteButton
    )

    $textCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $deadline = (Get-Date).AddSeconds(10)
    do {
        Assert-PackagedProcessAlive -Process $Process
        try {
            $titles = @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition) |
                Where-Object { $_.Current.Name -ceq "Delete source?" })
            if ($titles.Count -eq 0 -and $DeleteButton.Current.IsEnabled) {
                return
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "The packaged source-deletion confirmation dialog did not close before the deadline."
}

function Wait-PackagedPendingSourceCleanupState {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Instance
    )

    $disabledControlIds = @(
        "CatalogSourceSelector",
        "CatalogCategorySelector",
        "CatalogSearchBox",
        "CatalogChannelList",
        "CatalogDeleteSourceButton",
        "CatalogPreviousPage",
        "CatalogNextPage")
    $deadline = (Get-Date).AddSeconds(30)
    do {
        Assert-PackagedProcessAlive -Process $Instance.Process
        try {
            $catalogStatus = Get-AutomationElementById $Instance.Root "CatalogStatusText"
            $retryButton = Get-AutomationElementById `
                $Instance.Root `
                "CatalogRetryPendingDeletionButton"
            $playbackStatus = Get-AutomationElementById $Instance.Root "PlaybackStatusText"
            $playbackChannel = Get-AutomationElementById $Instance.Root "PlaybackChannelText"
            $controlsDisabled = $true
            foreach ($controlId in $disabledControlIds) {
                $control = Get-AutomationElementById $Instance.Root $controlId
                if ($null -eq $control -or $control.Current.IsEnabled) {
                    $controlsDisabled = $false
                    break
                }
            }

            if ($controlsDisabled -and
                $null -ne $catalogStatus -and
                $catalogStatus.Current.Name -ceq
                    "Pending source cleanup must finish before the catalog can be opened." -and
                $null -ne $retryButton -and
                $retryButton.Current.ControlType -eq
                    [System.Windows.Automation.ControlType]::Button -and
                $retryButton.Current.Name -ceq "Retry pending source cleanup" -and
                $retryButton.Current.IsEnabled -and
                -not $retryButton.Current.IsOffscreen -and
                $null -ne $playbackStatus -and
                $playbackStatus.Current.Name -ceq "Playback stopped." -and
                $null -ne $playbackChannel -and
                $playbackChannel.Current.Name -ceq "No channel selected.") {
                return [pscustomobject]@{
                    RetryButton = $retryButton
                }
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "The packaged catalog did not reach the pending source-cleanup state."
}

function Wait-PackagedDeletedSourceState {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Instance
    )

    $deadline = (Get-Date).AddSeconds(30)
    $listItemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    do {
        Assert-PackagedProcessAlive -Process $Instance.Process
        try {
            $sourceElement = Get-AutomationElementById $Instance.Root "CatalogSourceSelector"
            $statusElement = Get-AutomationElementById $Instance.Root "PlaybackStatusText"
            $channelElement = Get-AutomationElementById $Instance.Root "PlaybackChannelText"
            if ($null -ne $sourceElement -and $null -ne $statusElement -and $null -ne $channelElement) {
                $expandObject = $null
                if ($sourceElement.TryGetCurrentPattern(
                        [System.Windows.Automation.ExpandCollapsePattern]::Pattern,
                        [ref]$expandObject)) {
                    $expand = [System.Windows.Automation.ExpandCollapsePattern]$expandObject
                    $expand.Expand()
                    $items = $sourceElement.FindAll(
                        [System.Windows.Automation.TreeScope]::Descendants,
                        $listItemCondition)
                    $verified = $items.Count -eq 1 -and
                        $items[0].Current.Name -ceq $expectedCatalogSourceName -and
                        $statusElement.Current.Name -ceq "Playback stopped." -and
                        $channelElement.Current.Name -ceq "No channel selected."
                    if ($expand.Current.ExpandCollapseState -eq
                        [System.Windows.Automation.ExpandCollapseState]::Expanded) {
                        $expand.Collapse()
                    }
                    if ($verified) {
                        return
                    }
                }
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "The deleted source remained admitted to the packaged catalog."
}

$packageIdentityMutex = $null
try {
    if ($EmitM16FinalArtifactSurfaces) {
        Assert-WindowsPackageIdentityMutexOwnedByController
    }
    else {
        $packageIdentityMutex = Enter-WindowsPackageIdentityMutex
    }
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

try {
    if ($EmitM16FinalArtifactSurfaces) {
        $m16CommitSha = Get-M16CleanRepositoryCommit
    }

    $staleEvidencePaths = @(
        $evidencePath,
        $failureEvidencePath,
        $packageSbomPath,
        $packageSbomSummaryPath,
        $wackEvidencePath)
    if ($EmitM16FinalArtifactSurfaces) {
        $staleEvidencePaths += @(
            $m16SurfaceEvidencePath,
            $m16BindingEvidencePath)
    }
    foreach ($staleEvidencePath in $staleEvidencePaths) {
        if (Test-Path -LiteralPath $staleEvidencePath) {
            Remove-Item -LiteralPath $staleEvidencePath -Force -ErrorAction Stop
        }
    }

    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
    if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this smoke test from an elevated PowerShell session so the temporary public certificate can be trusted and removed."
    }

    $enableLua = Get-ItemPropertyValue `
        -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" `
        -Name "EnableLUA" `
        -ErrorAction Stop
    if ([int]$enableLua -ne 1) {
        throw "Package activation requires the Windows app-model UAC service to be enabled."
    }

    $expectedSdk = (Get-Content -Raw (Join-Path $repositoryRoot "global.json") | ConvertFrom-Json).sdk.version
    $actualSdk = (& $DotNetPath --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) {
        throw "Expected .NET SDK $expectedSdk, received '$actualSdk'."
    }

    [xml]$sourceManifest = Get-Content -Raw $sourceManifestPath
    Assert-ManifestPolicy -Manifest $sourceManifest
    $sourceIdentity = $sourceManifest.SelectSingleNode(
        "/*[local-name()='Package']/*[local-name()='Identity']")
    $sourcePackageVersion = [version]$sourceIdentity.GetAttribute("Version")
    $expectedPackageFullName =
        [IptvSuite.PackageSmoke.PackagedApplicationActivator]::GetPackageFullName(
            $expectedName,
            $expectedPublisher,
            [uint16]$sourcePackageVersion.Major,
            [uint16]$sourcePackageVersion.Minor,
            [uint16]$sourcePackageVersion.Build,
            [uint16]$sourcePackageVersion.Revision)
    if ($expectedPackageFullName -cnotmatch
            '\AIptvSuite\.LocalDev\.6f0d9a64_[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+_x64__[0-9a-z]{13}\z') {
        throw "The expected disposable package full name is invalid."
    }

    if (Get-ChildItem -Path (Join-Path $repositoryRoot "apps") -Filter "Package.StoreAssociation.xml" -Recurse -File) {
        throw "Package.StoreAssociation.xml is forbidden for the disposable M1 identity."
    }

    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
    if ($EmitM16FinalArtifactSurfaces) {
        Initialize-M16CaptureRoot
    }

    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $expectedPublisher `
        -FriendlyName $certificateFriendlyName `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -KeyUsage DigitalSignature `
        -NotAfter (Get-Date).AddDays(7) `
        -TextExtension @(
            "2.5.29.37={text}1.3.6.1.5.5.7.3.3",
            "2.5.29.19={text}"
        )

    if (-not $certificate.HasPrivateKey -or $certificate.Subject -ne $expectedPublisher) {
        throw "The local signing certificate does not match the manifest publisher."
    }
    if ($EmitM16FinalArtifactSurfaces) {
        Write-M16CleanupOwnershipValue `
            -Name "signing-certificate.thumbprint" `
            -Value $certificate.Thumbprint
    }

    $enhancedKeyUsageExtension = $certificate.Extensions |
        Where-Object { $_.Oid.Value -eq "2.5.29.37" } |
        Select-Object -First 1
    $enhancedKeyUsage = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$enhancedKeyUsageExtension
    $codeSigningUsage = @($enhancedKeyUsage.EnhancedKeyUsages | ForEach-Object { $_.Value })
    if ($codeSigningUsage -notcontains "1.3.6.1.5.5.7.3.3") {
        throw "The local signing certificate is missing the code-signing EKU."
    }

    Export-Certificate -Cert $certificate -FilePath $publicCertificatePath | Out-Null
    Import-Certificate -FilePath $publicCertificatePath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null

    $msBuildEnvironment.PackageCertificateThumbprint = $certificate.Thumbprint
    foreach ($entry in $msBuildEnvironment.GetEnumerator()) {
        $environmentBackup[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }

    & $DotNetPath build $projectPath -c $Configuration -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Signed MSIX build failed."
    }

    & $DotNetPath build $catalogUiHarnessProjectPath -c $Configuration -p:Platform=x64 `
        --no-restore --nologo -m:1 -nr:false
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $catalogUiHarnessAssemblyPath -PathType Leaf)) {
        throw "The catalog UI acceptance harness build failed."
    }

    & $DotNetPath build $playbackUiHarnessProjectPath -c $Configuration -p:Platform=x64 `
        --no-restore --nologo -m:1 -nr:false
    if ($LASTEXITCODE -ne 0 -or
        -not (Test-Path -LiteralPath $playbackUiHarnessAssemblyPath -PathType Leaf)) {
        throw "The playback UI acceptance harness build failed."
    }

    if ($EmitM16FinalArtifactSurfaces) {
        & $DotNetPath build $testingProjectPath -c $Configuration -p:Platform=x64 `
            --no-restore --nologo -m:1 -nr:false
        if ($LASTEXITCODE -ne 0 -or
            -not (Test-Path -LiteralPath $testingAssemblyPath -PathType Leaf)) {
            throw "The M16 artifact scanner build failed."
        }
    }

    $packages = @(
        Get-ChildItem -Path $packageOutput -Filter "IptvSuite.Windows_*.msix" -Recurse -File |
            Where-Object { $_.FullName -notmatch "[\\/]Dependencies[\\/]" }
    )
    if ($packages.Count -ne 1) {
        throw "Expected exactly one x64 MSIX, found $($packages.Count)."
    }

    $runtimeDependencies = @(
        Get-ChildItem -Path $packageOutput -Filter "Microsoft.WindowsAppRuntime.2.msix" -Recurse -File |
            Where-Object { $_.Directory.Name -eq "x64" }
    )
    if ($runtimeDependencies.Count -ne 1) {
        throw "Expected exactly one x64 Windows App Runtime dependency package."
    }

    $signature = Get-AuthenticodeSignature -FilePath $packages[0].FullName
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
        $signature.Status.ToString() -cne "Valid") {
        throw "The generated MSIX signature validation failed."
    }

    $runtimeDependencySignature = Get-AuthenticodeSignature -FilePath $runtimeDependencies[0].FullName
    if ($null -eq $runtimeDependencySignature.SignerCertificate -or
        $runtimeDependencySignature.SignerCertificate.Subject -cne $expectedRuntimeDependencyPublisher -or
        $runtimeDependencySignature.Status.ToString() -cne "Valid") {
        throw "The Windows App Runtime dependency signature validation failed."
    }

    $packageSha256 = (Get-FileHash -LiteralPath $packages[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-ProductionPackagePayload -PackagePath $packages[0].FullName

    $packageSbomResults = @(& (Join-Path $PSScriptRoot "Invoke-WindowsPackageSbom.ps1") `
        -PackagePath $packages[0].FullName `
        -RuntimePackagePath $runtimeDependencies[0].FullName `
        -DotNetPath $DotNetPath `
        -RepositoryRoot $repositoryRoot)
    if ($packageSbomResults.Count -ne 1) {
        throw "The package-bound SBOM runner returned an invalid result."
    }
    $packageSbomResult = $packageSbomResults[0]
    if ($packageSbomResult.Result -cne "Pass" -or
        $packageSbomResult.OfficialValidationPassed -ne $true -or
        $packageSbomResult.StrictValidationPassed -ne $true -or
        $packageSbomResult.ApplicationPackageSha256 -cne $packageSha256) {
        throw "The package-bound SBOM result is invalid."
    }

    $runtimeDependencyPackagesBefore = @(Get-RuntimeDependencyPackages)
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

    Remove-ExactDevelopmentPackage
    if ($EmitM16FinalArtifactSurfaces) {
        Write-M16CleanupOwnershipValue `
            -Name "package-registration.intent" `
            -Value $expectedPackageFullName
    }
    $installAttempted = $true
    if ($compatibleRuntimeDependencyRegistered) {
        Write-Host "Compatible Windows App Runtime dependency is already registered; package install will reuse it."
        Add-AppxPackage -Path $packages[0].FullName
        $windowsAppRuntimeDisposition = "ReusedRegisteredFramework"
    }
    else {
        Add-AppxPackage -Path $packages[0].FullName -DependencyPath $runtimeDependencies[0].FullName
        $windowsAppRuntimeDisposition = "InstalledLockedDependency"
    }

    $installedPackages = @(
        Get-AppxPackage -Name $expectedName |
            Where-Object { $_.Publisher -eq $expectedPublisher }
    )
    if ($installedPackages.Count -ne 1) {
        throw "Expected exactly one installed development package."
    }

    $installedPackage = $installedPackages[0]
    if ($installedPackage.Architecture -ne "X64") {
        throw "Expected an x64 package, received $($installedPackage.Architecture)."
    }

    $installedManifest = $installedPackage | Get-AppxPackageManifest
    [xml]$installedManifestXml = $installedManifest.Package.OuterXml
    Assert-BuiltManifestPolicy -Manifest $installedManifestXml

    $installedPackageFullName = [string]$installedPackage.PackageFullName
    $installedPackageLocation = [string]$installedPackage.InstallLocation
    if ($installedPackageFullName -cne $expectedPackageFullName -or
        [string]::IsNullOrWhiteSpace($installedPackageLocation) -or
        -not [System.IO.Path]::IsPathRooted($installedPackageLocation)) {
        throw "The exact installed development package has no auditable install-root binding."
    }
    $canonicalInstalledPackageLocation = [System.IO.Path]::GetFullPath(
        $installedPackageLocation).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    $trimmedInstalledPackageLocation = $installedPackageLocation.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    if (-not [string]::Equals(
            $canonicalInstalledPackageLocation,
            $trimmedInstalledPackageLocation,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [System.IO.Path]::GetFileName($canonicalInstalledPackageLocation),
            $installedPackageFullName,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The exact installed development package has an invalid install-root binding."
    }
    $packageInstallRootAudit = Start-WindowsPackageInstallRootAudit `
        -RootPath $canonicalInstalledPackageLocation
    $packageInstallRootAuditSegmentCount = 1

    $packageFamilyName = $installedPackage.PackageFamilyName
    $catalogDatabasePath = Join-Path $env:LOCALAPPDATA `
        "Packages\$packageFamilyName\LocalCache\Catalog\v2\catalog.db"
    $catalogStatePath = Split-Path -Parent $catalogDatabasePath
    $protectedStorePath = Join-Path $env:LOCALAPPDATA `
        "Packages\$packageFamilyName\LocalCache\ProtectedStore\v2"
    $aumid = "$($installedPackage.PackageFamilyName)!$expectedApplicationId"

    $freshStateDeadline = (Get-Date).AddSeconds(15)
    while (((Test-Path -LiteralPath $catalogStatePath) -or
            (Test-Path -LiteralPath $protectedStorePath)) -and
        (Get-Date) -lt $freshStateDeadline) {
        Start-Sleep -Milliseconds 250
    }
    if ((Test-Path -LiteralPath $catalogStatePath) -or
        (Test-Path -LiteralPath $protectedStorePath)) {
        throw "The clean-install onboarding acceptance did not begin from fresh package state."
    }
    if (-not (Test-Path -LiteralPath $playbackFixtureRoot -PathType Container) -or
        ([System.IO.File]::GetAttributes($playbackFixtureRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The committed onboarding acceptance fixture root is invalid."
    }
    if (Test-Path -LiteralPath $onboardingControlDirectory) {
        throw "The onboarding acceptance control directory already exists."
    }

    New-Item -ItemType Directory -Path $onboardingControlRoot -Force | Out-Null
    if (([System.IO.File]::GetAttributes($artifactRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ([System.IO.File]::GetAttributes($onboardingControlRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The onboarding acceptance control root is invalid."
    }
    New-Item -ItemType Directory -Path $onboardingControlDirectory | Out-Null
    Assert-ExactOnboardingControlEntries -AllowedNames @()

    $onboardingHarnessArgumentValues = @(
        $playbackUiHarnessAssemblyPath,
        "serve-onboarding",
        $playbackFixtureRoot,
        $onboardingControlDirectory,
        $onboardingPipeName
    )
    if ($EmitM16FinalArtifactSurfaces) {
        $onboardingHarnessArgumentValues += $testCanaryMarker
    }
    foreach ($argumentValue in $onboardingHarnessArgumentValues) {
        if ([string]::IsNullOrWhiteSpace($argumentValue) -or $argumentValue.Contains('"')) {
            throw "An onboarding acceptance harness argument is invalid."
        }
    }
    $onboardingHarnessArguments = ($onboardingHarnessArgumentValues |
        ForEach-Object { '"' + $_ + '"' }) -join ' '
    try {
        $onboardingHarnessProcess = Start-Process `
            -FilePath $DotNetPath `
            -ArgumentList $onboardingHarnessArguments `
            -WorkingDirectory $repositoryRoot `
            -WindowStyle Hidden `
            -PassThru
    }
    catch {
        throw "The onboarding acceptance harness could not be started."
    }
    try {
        $null = $onboardingHarnessProcess.Handle
    }
    catch {
        throw "The onboarding acceptance harness exited before its process handle could be retained."
    }

    $onboardingReadyDeadline = (Get-Date).AddSeconds(60)
    do {
        $onboardingHarnessProcess.Refresh()
        if ($onboardingHarnessProcess.HasExited) {
            throw "The onboarding acceptance harness exited before publishing readiness."
        }
        if ((Test-Path -LiteralPath $onboardingReadyPath -PathType Leaf) -and
            (Test-Path -LiteralPath $onboardingPublicCertificatePath -PathType Leaf)) {
            $onboardingHarnessReady = $true
            break
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $onboardingReadyDeadline)
    if (-not $onboardingHarnessReady) {
        throw "The onboarding acceptance harness did not publish readiness before the deadline."
    }

    Assert-ExactOnboardingControlEntries -AllowedNames @("loopback.cer", "ready.json")
    $onboardingReadyTicket = Read-StrictOnboardingJsonTicket `
        -Path $onboardingReadyPath `
        -AllowedProperties @("IsReady", "CertificateThumbprint")
    if ($onboardingReadyTicket.IsReady -isnot [bool] -or
        $onboardingReadyTicket.CertificateThumbprint -isnot [string] -or
        -not $onboardingReadyTicket.IsReady -or
        -not [System.Text.RegularExpressions.Regex]::IsMatch(
            $onboardingReadyTicket.CertificateThumbprint,
            '\A[0-9A-F]{40}\z')) {
        throw "The onboarding acceptance readiness ticket is invalid."
    }

    $onboardingLoopbackCertificateThumbprint =
        $onboardingReadyTicket.CertificateThumbprint
    try {
        $onboardingLoopbackCertificate =
            [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $onboardingPublicCertificatePath)
    }
    catch {
        throw "The onboarding acceptance public certificate is invalid."
    }
    $onboardingNow = Get-Date
    if ($onboardingLoopbackCertificate.HasPrivateKey -or
        $onboardingLoopbackCertificate.Subject -cne $expectedPlaybackCertificateSubject -or
        $onboardingLoopbackCertificate.Issuer -cne $expectedPlaybackCertificateSubject -or
        $onboardingLoopbackCertificate.Thumbprint -cne
            $onboardingLoopbackCertificateThumbprint -or
        $onboardingLoopbackCertificate.NotBefore -gt $onboardingNow -or
        $onboardingLoopbackCertificate.NotAfter -le $onboardingNow) {
        throw "The onboarding acceptance public certificate does not match readiness."
    }

    $onboardingServerAuthenticationExtensions = @(
        $onboardingLoopbackCertificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.37" }
    )
    $onboardingBasicConstraintExtensions = @(
        $onboardingLoopbackCertificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.19" }
    )
    $onboardingKeyUsageExtensions = @(
        $onboardingLoopbackCertificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.15" }
    )
    if ($onboardingServerAuthenticationExtensions.Count -ne 1 -or
        $onboardingBasicConstraintExtensions.Count -ne 1 -or
        $onboardingKeyUsageExtensions.Count -ne 1) {
        throw "The onboarding acceptance public certificate constraints are invalid."
    }
    $onboardingServerAuthenticationExtension =
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$onboardingServerAuthenticationExtensions[0]
    $onboardingServerAuthenticationUsages = @(
        $onboardingServerAuthenticationExtension.EnhancedKeyUsages |
            ForEach-Object { $_.Value }
    )
    $onboardingBasicConstraintExtension =
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]$onboardingBasicConstraintExtensions[0]
    $onboardingKeyUsageExtension =
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]$onboardingKeyUsageExtensions[0]
    if (-not $onboardingServerAuthenticationExtension.Critical -or
        $onboardingServerAuthenticationUsages.Count -ne 1 -or
        $onboardingServerAuthenticationUsages[0] -cne "1.3.6.1.5.5.7.3.1" -or
        -not $onboardingBasicConstraintExtension.Critical -or
        $onboardingBasicConstraintExtension.CertificateAuthority -or
        -not $onboardingKeyUsageExtension.Critical -or
        $onboardingKeyUsageExtension.KeyUsages -ne
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) {
        throw "The onboarding acceptance public certificate constraints are invalid."
    }

    $onboardingRootCertificatePath =
        "Cert:\LocalMachine\Root\$onboardingLoopbackCertificateThumbprint"
    if (Test-Path -LiteralPath $onboardingRootCertificatePath) {
        throw "The exact onboarding acceptance certificate is already trusted."
    }
    if ($EmitM16FinalArtifactSurfaces) {
        Write-M16CleanupOwnershipValue `
            -Name "onboarding-loopback.thumbprint" `
            -Value $onboardingLoopbackCertificateThumbprint
    }
    $onboardingLoopbackCertificateImported = $true
    try {
        $importedOnboardingCertificates = @(
            Import-Certificate `
                -FilePath $onboardingPublicCertificatePath `
                -CertStoreLocation "Cert:\LocalMachine\Root"
        )
    }
    catch {
        throw "The onboarding acceptance public certificate could not be trusted."
    }
    if ($importedOnboardingCertificates.Count -ne 1 -or
        $importedOnboardingCertificates[0].Thumbprint -cne
            $onboardingLoopbackCertificateThumbprint) {
        throw "The onboarding acceptance public certificate import is invalid."
    }

    $onboardingLocator = Read-ExactOnboardingLocator
    try {
        $onboardingInstance = Start-PackagedPlaybackApplicationInstance
        $launchedProcess = $onboardingInstance.Process
        $onboardingRoot = $onboardingInstance.Root
        $onboardingWindowHandle = $onboardingInstance.WindowHandle
        $onboardingProcessId = $onboardingInstance.ProcessId

        $onboardingStatusElement = Get-AutomationElementById `
            $onboardingRoot `
            "CatalogStatusText"
        $onboardingEmptyDeadline = (Get-Date).AddSeconds(30)
        while (($null -eq $onboardingStatusElement -or
                $onboardingStatusElement.Current.Name -cne $expectedOnboardingEmptyStatus) -and
            (Get-Date) -lt $onboardingEmptyDeadline) {
            Assert-PackagedProcessAlive -Process $launchedProcess
            if ($null -eq $onboardingStatusElement) {
                $onboardingStatusElement = Get-AutomationElementById `
                    $onboardingRoot `
                    "CatalogStatusText"
            }
            Start-Sleep -Milliseconds 100
        }
        if ($null -eq $onboardingStatusElement -or
            $onboardingStatusElement.Current.Name -cne $expectedOnboardingEmptyStatus) {
            throw "The clean-install packaged catalog did not expose its exact empty state."
        }

        $onboardingOpenButton = Get-RequiredAutomationElement `
            $onboardingRoot `
            "CatalogAddSourceButton" `
            ([System.Windows.Automation.ControlType]::Button) `
            "Add authorized remote M3U catalog"
        Invoke-PackagedOnboardingButton `
            -Process $launchedProcess `
            -ButtonElement $onboardingOpenButton

        $onboardingNameInput = Get-RequiredAutomationElement `
            $onboardingRoot `
            "RemotePlaylistSourceNameTextBox" `
            ([System.Windows.Automation.ControlType]::Edit) `
            "Source name"
        $onboardingLocatorInput = Get-RequiredAutomationElement `
            $onboardingRoot `
            "RemotePlaylistLocatorTextBox" `
            ([System.Windows.Automation.ControlType]::Edit) `
            "Secure playlist URL"
        # Windows PowerShell 5.1 reads BOM-less UTF-8 script literals through the
        # active ANSI code page, so decode the localized exact name from ASCII.
        $onboardingAuthorizationAccessibleName =
            [System.Text.Encoding]::UTF8.GetString(
                [System.Convert]::FromBase64String(
                    "S2F5bmFrIGVyacWfaW0gdmUgw7Z6ZWwgdmV5YSB5ZXJlbCBhxJ8gZ8O8dmVuaW5pIG9uYXlsYQ=="))
        $onboardingAuthorization = Get-RequiredAutomationElement `
            $onboardingRoot `
            "RemotePlaylistAuthorizationCheckBox" `
            ([System.Windows.Automation.ControlType]::CheckBox) `
            $onboardingAuthorizationAccessibleName
        $onboardingSubmitButton = Get-RequiredAutomationElement `
            $onboardingRoot `
            "RemotePlaylistAddButton" `
            ([System.Windows.Automation.ControlType]::Button) `
            "Validate and add source"
        if ($onboardingSubmitButton.Current.IsEnabled) {
            throw "The packaged onboarding command did not require explicit authorization."
        }

        Set-PackagedOnboardingText `
            -Element $onboardingNameInput `
            -Value $expectedPlaybackSourceName
        Set-PackagedOnboardingText `
            -Element $onboardingLocatorInput `
            -Value $onboardingLocator

        $togglePatternObject = $null
        if (-not $onboardingAuthorization.TryGetCurrentPattern(
                [System.Windows.Automation.TogglePattern]::Pattern,
                [ref]$togglePatternObject)) {
            throw "The packaged onboarding authorization has no TogglePattern."
        }
        $togglePattern = [System.Windows.Automation.TogglePattern]$togglePatternObject
        if ($togglePattern.Current.ToggleState -ne
            [System.Windows.Automation.ToggleState]::Off) {
            throw "The packaged onboarding authorization was not initially clear."
        }
        $togglePattern.Toggle()
        $authorizationDeadline = (Get-Date).AddSeconds(5)
        while (($togglePattern.Current.ToggleState -ne
                [System.Windows.Automation.ToggleState]::On -or
                -not $onboardingSubmitButton.Current.IsEnabled) -and
            (Get-Date) -lt $authorizationDeadline) {
            Assert-PackagedProcessAlive -Process $launchedProcess
            Start-Sleep -Milliseconds 50
        }
        if ($togglePattern.Current.ToggleState -ne
                [System.Windows.Automation.ToggleState]::On -or
            -not $onboardingSubmitButton.Current.IsEnabled) {
            throw "The packaged onboarding authorization did not enable the exact command."
        }
        $cleanInstallOnboardingAuthorizationVerified = $true

        Invoke-PackagedOnboardingButton `
            -Process $launchedProcess `
            -ButtonElement $onboardingSubmitButton
        $onboardingLocator = $null

        $onboardingSourceSelector = Get-RequiredAutomationElement `
            $onboardingRoot `
            "CatalogSourceSelector" `
            ([System.Windows.Automation.ControlType]::ComboBox) `
            "Playlist source"
        $onboardingChannelList = Get-RequiredAutomationElement `
            $onboardingRoot `
            "CatalogChannelList" `
            ([System.Windows.Automation.ControlType]::List) `
            "Channels"
        $onboardingListItemCondition =
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)
        $onboardingCatalogDeadline = (Get-Date).AddSeconds(60)
        $onboardingCatalogReady = $false
        do {
            Assert-PackagedProcessAlive -Process $launchedProcess
            $onboardingStatusElement = Get-AutomationElementById `
                $onboardingRoot `
                "CatalogStatusText"
            $onboardingChannelItems = $onboardingChannelList.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $onboardingListItemCondition)
            if ($null -ne $onboardingStatusElement -and
                $onboardingStatusElement.Current.Name -ceq
                    $expectedOnboardingCatalogStatus -and
                $onboardingChannelItems.Count -eq 2) {
                $onboardingCatalogReady = $true
                break
            }
            Start-Sleep -Milliseconds 100
        } while ((Get-Date) -lt $onboardingCatalogDeadline)
        if (-not $onboardingCatalogReady) {
            throw "The packaged onboarding import did not expose exactly two channels."
        }

        $onboardingSourceExpandObject = $null
        if (-not $onboardingSourceSelector.TryGetCurrentPattern(
                [System.Windows.Automation.ExpandCollapsePattern]::Pattern,
                [ref]$onboardingSourceExpandObject)) {
            throw "The packaged onboarding source selector has no ExpandCollapsePattern."
        }
        $onboardingSourceExpand =
            [System.Windows.Automation.ExpandCollapsePattern]$onboardingSourceExpandObject
        $onboardingSourceExpand.Expand()
        $sourceDeadline = (Get-Date).AddSeconds(10)
        do {
            $onboardingSourceItems = $onboardingSourceSelector.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $onboardingListItemCondition)
            if ($onboardingSourceItems.Count -eq 1) {
                break
            }
            Start-Sleep -Milliseconds 100
        } while ((Get-Date) -lt $sourceDeadline)
        if ($onboardingSourceItems.Count -ne 1 -or
            $onboardingSourceItems[0].Current.Name -cne $expectedPlaybackSourceName) {
            throw "The packaged onboarding source binding is not exact."
        }
        $onboardingSourceExpand.Collapse()
        $cleanInstallOnboardingSourceVerified = $true

        $channelAFound = $false
        $channelBFound = $false
        for ($channelIndex = 0;
            $channelIndex -lt $onboardingChannelItems.Count;
            $channelIndex++) {
            $channelItem = $onboardingChannelItems[$channelIndex]
            if (Test-AutomationElementContainsExactText `
                    -Root $channelItem `
                    -ExpectedText $expectedPlaybackChannelAName) {
                if ($channelAFound) {
                    throw "The packaged onboarding catalog contains a duplicate channel."
                }
                $channelAFound = $true
            }
            elseif (Test-AutomationElementContainsExactText `
                    -Root $channelItem `
                    -ExpectedText $expectedPlaybackChannelBName) {
                if ($channelBFound) {
                    throw "The packaged onboarding catalog contains a duplicate channel."
                }
                $channelBFound = $true
            }
            else {
                throw "The packaged onboarding catalog contains an unexpected channel."
            }
        }
        if (-not $channelAFound -or -not $channelBFound) {
            throw "The packaged onboarding catalog channel binding is incomplete."
        }
        $cleanInstallOnboardingChannelsVerified = $true

        if (-not $launchedProcess.CloseMainWindow() -or
            -not $launchedProcess.WaitForExit(10000)) {
            throw "The packaged onboarding application did not close normally."
        }
        $launchedProcess.Refresh()
        if ([int]$launchedProcess.ExitCode -ne 0) {
            throw "The packaged onboarding application returned a non-zero exit code."
        }
        $launchedProcess.Dispose()
        $launchedProcess = $null
    }
    finally {
        $onboardingLocator = $null
    }

    $onboardingStopSignalStream = [System.IO.File]::Open(
        $onboardingStopSignalPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $onboardingStopSignalStream.Dispose()
    $onboardingStopSignalCreated = $true
    if (-not $onboardingHarnessProcess.WaitForExit(10000)) {
        throw "The onboarding acceptance harness did not stop after exact verification."
    }
    $onboardingHarnessProcess.Refresh()
    if ([int]$onboardingHarnessProcess.ExitCode -ne 0) {
        throw "The onboarding acceptance harness returned a failure result."
    }
    $onboardingHarnessProcess.Dispose()
    $onboardingHarnessProcess = $null

    Assert-ExactOnboardingControlEntries `
        -AllowedNames @("loopback.cer", "ready.json", "result.json", "stop.signal")
    $onboardingResult = Read-StrictOnboardingJsonTicket `
        -Path $onboardingResultPath `
        -AllowedProperties @(
            "ReadyPublished",
            "LocatorTransferred",
            "StopObserved",
            "StoppedGracefully",
            "CertificateThumbprint",
            "RequestCount",
            "CompletedResponseCount",
            "FailureCount",
            "PlaylistRequestCount",
            "MediaRequestCount")
    if ($onboardingResult.ReadyPublished -isnot [bool] -or
        $onboardingResult.LocatorTransferred -isnot [bool] -or
        $onboardingResult.StopObserved -isnot [bool] -or
        $onboardingResult.StoppedGracefully -isnot [bool] -or
        $onboardingResult.CertificateThumbprint -isnot [string] -or
        ($onboardingResult.RequestCount -isnot [int] -and
            $onboardingResult.RequestCount -isnot [long]) -or
        ($onboardingResult.CompletedResponseCount -isnot [int] -and
            $onboardingResult.CompletedResponseCount -isnot [long]) -or
        ($onboardingResult.FailureCount -isnot [int] -and
            $onboardingResult.FailureCount -isnot [long]) -or
        ($onboardingResult.PlaylistRequestCount -isnot [int] -and
            $onboardingResult.PlaylistRequestCount -isnot [long]) -or
        ($onboardingResult.MediaRequestCount -isnot [int] -and
            $onboardingResult.MediaRequestCount -isnot [long]) -or
        -not $onboardingResult.ReadyPublished -or
        -not $onboardingResult.LocatorTransferred -or
        -not $onboardingResult.StopObserved -or
        -not $onboardingResult.StoppedGracefully -or
        $onboardingResult.CertificateThumbprint -cne
            $onboardingLoopbackCertificateThumbprint -or
        [int]$onboardingResult.RequestCount -ne 2 -or
        [int]$onboardingResult.CompletedResponseCount -ne 2 -or
        [int]$onboardingResult.FailureCount -ne 0 -or
        [int]$onboardingResult.PlaylistRequestCount -ne 2 -or
        [int]$onboardingResult.MediaRequestCount -ne 0) {
        throw "The clean-install onboarding acceptance result is invalid."
    }
    $cleanInstallOnboardingRequestCount = [int]$onboardingResult.RequestCount
    $cleanInstallOnboardingVerified =
        $cleanInstallOnboardingAuthorizationVerified -and
        $cleanInstallOnboardingSourceVerified -and
        $cleanInstallOnboardingChannelsVerified
    if (-not $cleanInstallOnboardingVerified) {
        throw "The clean-install onboarding UI acceptance is incomplete."
    }

    $onboardingCertificatePath =
        "Cert:\LocalMachine\Root\$onboardingLoopbackCertificateThumbprint"
    $onboardingCertificateCandidate = Get-Item `
        -LiteralPath $onboardingCertificatePath `
        -ErrorAction SilentlyContinue
    if ($null -eq $onboardingCertificateCandidate -or
        $onboardingCertificateCandidate.Subject -cne
            $expectedPlaybackCertificateSubject -or
        $onboardingCertificateCandidate.Thumbprint -cne
            $onboardingLoopbackCertificateThumbprint) {
        throw "The exact onboarding certificate identity changed before cleanup."
    }
    Remove-Item -LiteralPath $onboardingCertificatePath -Force -ErrorAction Stop
    $onboardingLoopbackCertificateImported = $false
    $onboardingLoopbackCertificate.Dispose()
    $onboardingLoopbackCertificate = $null
    Remove-ExactOnboardingControlDirectory

    $packageInstallRootAuditCompletionAttempted = $true
    $preResetPackageInstallRootAuditResult =
        Complete-WindowsPackageInstallRootAudit -Audit $packageInstallRootAudit
    Assert-ExactPackageInstallRootAuditResult `
        -Result $preResetPackageInstallRootAuditResult
    $packageInstallRootAudit = $null
    $packageInstallRootAuditCompletionAttempted = $false

    Invoke-ExactDevelopmentPackageReset `
        -ExpectedPackageFullName $installedPackageFullName `
        -ExpectedPackageFamilyName $packageFamilyName `
        -ExpectedInstallRoot $canonicalInstalledPackageLocation `
        -CatalogStatePath $catalogStatePath `
        -ProtectedStorePath $protectedStorePath
    $cleanInstallOnboardingResetVerified = $true

    $packageInstallRootAudit = Start-WindowsPackageInstallRootAudit `
        -RootPath $canonicalInstalledPackageLocation
    $packageInstallRootAuditSegmentCount = 2

    & $DotNetPath $catalogUiHarnessAssemblyPath seed $catalogDatabasePath 50000
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $catalogDatabasePath -PathType Leaf)) {
        throw "The disposable 50k packaged catalog seed failed."
    }
    $catalog50kSeedVerified = $true

    $existingProcesses = @(Get-Process -Name "IptvSuite.Windows" -ErrorAction SilentlyContinue)
    if ($existingProcesses.Count -ne 0) {
        throw "IptvSuite.Windows is already running; refusing an ambiguous launch smoke."
    }

    $activationProcessId = [IptvSuite.PackageSmoke.PackagedApplicationActivator]::Activate($aumid)
    $launchedProcess = Get-Process -Id $activationProcessId -ErrorAction SilentlyContinue
    if ($null -eq $launchedProcess) {
        throw "The packaged application exited before its process could be observed."
    }
    try {
        # Cache the native handle while the PID is live. Windows PowerShell 5.1
        # otherwise exposes ExitCode as null after an attached process exits.
        $null = $launchedProcess.Handle
    }
    catch {
        throw "The packaged application exited before its process handle could be retained."
    }
    $launchedProcess.Refresh()
    if ($launchedProcess.HasExited) {
        throw ("The packaged application exited during activation (exit code 0x{0:X8})." -f [int]$launchedProcess.ExitCode)
    }
    if ($launchedProcess.ProcessName -ne "IptvSuite.Windows") {
        throw "Package activation returned an unexpected process."
    }

    $launchDeadline = (Get-Date).AddSeconds(30)
    $visibleWindow = $false
    do {
        $launchedProcess.Refresh()
        if ($launchedProcess.HasExited) {
            throw ("The packaged application exited before creating a visible window (exit code 0x{0:X8})." -f [int]$launchedProcess.ExitCode)
        }

        $windowHandle = $launchedProcess.MainWindowHandle
        if ($windowHandle -ne [IntPtr]::Zero -and
            [IptvSuite.PackageSmoke.WindowInspector]::IsWindowVisible($windowHandle)) {
            [uint32]$windowOwnerProcessId = 0
            [void][IptvSuite.PackageSmoke.WindowInspector]::GetWindowThreadProcessId(
                $windowHandle,
                [ref]$windowOwnerProcessId)
            if ($windowOwnerProcessId -eq [uint32]$activationProcessId) {
                $visibleWindow = $true
                break
            }
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $launchDeadline)

    if (-not $visibleWindow) {
        throw "The packaged application remained running but did not create a visible window before the launch deadline."
    }

    $automationRoot = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    if ($null -eq $automationRoot) {
        throw "The packaged application window has no UI Automation root."
    }
    $sourceElement = Get-RequiredAutomationElement $automationRoot "CatalogSourceSelector" `
        ([System.Windows.Automation.ControlType]::ComboBox) "Playlist source"
    $categoryElement = Get-RequiredAutomationElement $automationRoot "CatalogCategorySelector" `
        ([System.Windows.Automation.ControlType]::ComboBox) "Channel category"
    $searchElement = Get-RequiredAutomationElement $automationRoot "CatalogSearchBox" `
        ([System.Windows.Automation.ControlType]::Group) "Search channels"
    $channelListElement = Get-RequiredAutomationElement $automationRoot "CatalogChannelList" `
        ([System.Windows.Automation.ControlType]::List) "Channels"
    $statusElement = Get-AutomationElementById $automationRoot "CatalogStatusText"
    if ($null -eq $statusElement) {
        throw "The packaged catalog status automation element is missing."
    }
    $catalogUiaContractVerified = $true

    $expectedCatalogStatus = "Showing 1$([char]0x2013)200 of 50000 channels."
    $catalogReadyDeadline = (Get-Date).AddSeconds(30)
    while ($statusElement.Current.Name -ne $expectedCatalogStatus -and
        (Get-Date) -lt $catalogReadyDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if ($statusElement.Current.Name -ne $expectedCatalogStatus) {
        throw "The packaged application did not expose the seeded 50k catalog page."
    }

    $listItemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $realizedDeadline = (Get-Date).AddSeconds(10)
    do {
        $realizedItems = $channelListElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $listItemCondition)
        $catalogRealizedContainerCount = $realizedItems.Count
        if ($catalogRealizedContainerCount -gt 0) { break }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $realizedDeadline)
    if ($catalogRealizedContainerCount -lt 1 -or $catalogRealizedContainerCount -gt 300) {
        throw "The packaged catalog realized-container bound failed."
    }
    $catalogRealizedContainerBoundVerified = $true

    $editCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $searchEdit = $searchElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $editCondition)
    $valuePatternObject = $null
    if ($null -eq $searchEdit -or
        -not $searchEdit.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$valuePatternObject)) {
        throw "The packaged catalog search value pattern is unavailable."
    }
    $valuePattern = [System.Windows.Automation.ValuePattern]$valuePatternObject
    if ($EmitM14TraceMarkers) {
        $catalogTraceMarkerProcessId = [int]$activationProcessId
        $catalogTraceMarkerBeginName =
            "IptvSuite.M14.CatalogInteraction.Begin.Pid$catalogTraceMarkerProcessId"
        $catalogTraceMarkerEndName =
            "IptvSuite.M14.CatalogInteraction.End.Pid$catalogTraceMarkerProcessId"
        Write-M14CatalogTraceMarker -Name $catalogTraceMarkerBeginName
        $catalogTraceMarkerBeginEmitted = $true
        $catalogTraceMarkerCount++
    }
    $inputSamples = [System.Collections.Generic.List[double]]::new()
    for ($sample = 0; $sample -lt 20; $sample++) {
        $value = if (($sample % 2) -eq 0) { "Synthetic" } else { "channel" }
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $valuePattern.SetValue($value)
        $watch.Stop()
        $inputSamples.Add($watch.Elapsed.TotalMilliseconds)
    }
    $valuePattern.SetValue("")
    $catalogInputResponseP95Milliseconds = Get-Percentile95 $inputSamples.ToArray()
    if ($catalogInputResponseP95Milliseconds -gt 100.0) {
        throw "The packaged catalog input response budget failed."
    }

    $catalogRestoredDeadline = (Get-Date).AddSeconds(10)
    while ($statusElement.Current.Name -ne $expectedCatalogStatus -and
        (Get-Date) -lt $catalogRestoredDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if ($statusElement.Current.Name -ne $expectedCatalogStatus) {
        throw "The packaged catalog did not settle after the input-response probe."
    }

    $focusReadyWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $focusStableWatch = $null
    do {
        $focusControlsReady =
            $statusElement.Current.Name -eq $expectedCatalogStatus -and
            $sourceElement.Current.IsEnabled -and
            $sourceElement.Current.IsKeyboardFocusable -and
            $categoryElement.Current.IsEnabled -and
            $categoryElement.Current.IsKeyboardFocusable
        if ($focusControlsReady) {
            if ($null -eq $focusStableWatch) {
                $focusStableWatch = [System.Diagnostics.Stopwatch]::StartNew()
            }
            if ($focusStableWatch.ElapsedMilliseconds -ge 750) {
                break
            }
        }
        else {
            $focusStableWatch = $null
        }

        Start-Sleep -Milliseconds 50
    } while ($focusReadyWatch.Elapsed -lt [TimeSpan]::FromSeconds(15))
    if ($null -eq $focusStableWatch -or $focusStableWatch.ElapsedMilliseconds -lt 750) {
        throw "The packaged catalog controls did not remain keyboard-focusable after the input-response probe."
    }
    Assert-PackagedWindowForeground $windowHandle ([uint32]$activationProcessId)
    Assert-FocusedAutomationElement $sourceElement "CatalogSourceSelector" -RequestFocus
    [IptvSuite.PackageSmoke.KeyboardInspector]::PressTab()
    Start-Sleep -Milliseconds 150
    Assert-FocusedAutomationElement $categoryElement "CatalogCategorySelector"
    [IptvSuite.PackageSmoke.KeyboardInspector]::PressTab()
    Start-Sleep -Milliseconds 150
    Assert-FocusedAutomationElement $searchElement "CatalogSearchBox"
    $catalogKeyboardFocusOrderVerified = $true
    $scrollFocusItem = $channelListElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $listItemCondition)
    if ($null -eq $scrollFocusItem) {
        throw "The packaged catalog has no realized item for the scroll probe."
    }
    Assert-FocusedAutomationElement $scrollFocusItem "CatalogChannelList" -RequestFocus
    $catalogDwmMinimumScrollInputCount = 240
    $catalogDwmMaximumScrollInputCount = 480
    $catalogDwmMinimumExactFrameIntervalCount = 30
    [IptvSuite.PackageSmoke.DwmFrameSampler]::Start()
    $frameResult = $null
    try {
        for ($frameInput = 0; $frameInput -lt $catalogDwmMaximumScrollInputCount; $frameInput++) {
            if (($frameInput % 2) -eq 0) {
                [IptvSuite.PackageSmoke.KeyboardInspector]::PressPageDown()
            }
            else {
                [IptvSuite.PackageSmoke.KeyboardInspector]::PressPageUp()
            }
            Start-Sleep -Milliseconds 16
            if (($frameInput + 1) -ge $catalogDwmMinimumScrollInputCount -and
                [IptvSuite.PackageSmoke.DwmFrameSampler]::HasMinimumExactIntervalSample(
                    $catalogDwmMinimumExactFrameIntervalCount)) {
                break
            }
        }
    }
    finally {
        $frameResult = [IptvSuite.PackageSmoke.DwmFrameSampler]::Stop()
    }
    $catalogFrameP95Milliseconds = $frameResult.P95Milliseconds
    $catalogFrameMaximumMilliseconds = $frameResult.MaximumMilliseconds
    $catalogDroppedFramePercent = $frameResult.DroppedPercent
    $catalogFrameIntervalCount = $frameResult.IntervalCount
    $catalogExactFrameIntervalCount = $frameResult.ExactIntervalCount
    $catalogMultiRefreshSegmentCount = $frameResult.MultiRefreshSegmentCount
    if ($catalogFrameP95Milliseconds -gt 33.3 -or
        $catalogDroppedFramePercent -ge 1.0 -or
        $catalogFrameMaximumMilliseconds -gt 200.0) {
        throw (
            "The packaged catalog DWM frame budget failed: " +
            "p95=$catalogFrameP95Milliseconds, " +
            "maximum=$catalogFrameMaximumMilliseconds, " +
            "droppedPercent=$catalogDroppedFramePercent, " +
            "intervals=$catalogFrameIntervalCount, " +
            "exactIntervals=$catalogExactFrameIntervalCount, " +
            "multiRefreshSegments=$catalogMultiRefreshSegmentCount.")
    }

    # This app-window UI-thread proxy is intentionally a separate scroll pass.
    # The existing DWM pass remains the authoritative system-compositor proxy.
    Assert-PackagedWindowForeground $windowHandle ([uint32]$activationProcessId)
    $responsivenessFocusItem = $channelListElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $listItemCondition)
    if ($null -eq $responsivenessFocusItem) {
        throw "The packaged catalog has no realized item for the UI-thread responsiveness proxy."
    }
    Assert-FocusedAutomationElement `
        $responsivenessFocusItem `
        "CatalogChannelList" `
        -RequestFocus
    $uiThreadResponsivenessSamples = [System.Collections.Generic.List[double]]::new()
    $uiThreadResponsivenessSampleTarget = 60
    if ($uiThreadResponsivenessSampleTarget -gt
            $catalogUiThreadResponsivenessProxySampleLimit) {
        throw "The packaged UI-thread responsiveness proxy sample target is unbounded."
    }
    for ($responsivenessInput = 0;
        $responsivenessInput -lt $uiThreadResponsivenessSampleTarget;
        $responsivenessInput++) {
        Assert-PackagedProcessAlive -Process $launchedProcess
        if (($responsivenessInput % 2) -eq 0) {
            [IptvSuite.PackageSmoke.KeyboardInspector]::PressPageDown()
        }
        else {
            [IptvSuite.PackageSmoke.KeyboardInspector]::PressPageUp()
        }

        $uiThreadProbe =
            [IptvSuite.PackageSmoke.WindowInspector]::ProbeUiThreadResponsiveness(
                $windowHandle)
        $uiThreadResponseMilliseconds = [double]$uiThreadProbe.ElapsedMilliseconds
        if ([double]::IsNaN($uiThreadResponseMilliseconds) -or
            [double]::IsInfinity($uiThreadResponseMilliseconds) -or
            $uiThreadResponseMilliseconds -lt 0.0 -or
            [uint32]$uiThreadProbe.TimeoutMilliseconds -ne
                [uint32]$catalogUiThreadResponsivenessProxyTimeoutMilliseconds) {
            throw "The packaged UI-thread responsiveness proxy sample is invalid."
        }
        if ([bool]$uiThreadProbe.TimedOut) {
            $catalogUiThreadResponsivenessProxyTimeoutCount++
        }
        if ($uiThreadResponseMilliseconds -gt
            [double]$catalogUiThreadResponsivenessProxyTimeoutMilliseconds) {
            $catalogUiThreadResponsivenessProxyOverBudgetCount++
        }
        $uiThreadResponsivenessSamples.Add($uiThreadResponseMilliseconds)
        Assert-PackagedProcessAlive -Process $launchedProcess
        Start-Sleep -Milliseconds 16
    }
    $catalogUiThreadResponsivenessProxySampleCount =
        $uiThreadResponsivenessSamples.Count
    if ($catalogUiThreadResponsivenessProxySampleCount -ne
            $uiThreadResponsivenessSampleTarget -or
        $catalogUiThreadResponsivenessProxySampleCount -gt
            $catalogUiThreadResponsivenessProxySampleLimit) {
        throw "The packaged UI-thread responsiveness proxy sample count is invalid."
    }
    $catalogUiThreadResponsivenessProxyP95Milliseconds =
        Get-Percentile95 $uiThreadResponsivenessSamples.ToArray()
    $catalogUiThreadResponsivenessProxyMaximumMilliseconds = [double](
        $uiThreadResponsivenessSamples |
            Measure-Object -Maximum |
            Select-Object -ExpandProperty Maximum)
    $catalogUiThreadResponsivenessProxyRawSamplesMilliseconds = @(
        $uiThreadResponsivenessSamples |
            ForEach-Object { [Math]::Round([double]$_, 3) })
    if ($catalogUiThreadResponsivenessProxyRawSamplesMilliseconds.Count -ne
            $catalogUiThreadResponsivenessProxySampleCount -or
        $catalogUiThreadResponsivenessProxyTimeoutCount -ne 0 -or
        $catalogUiThreadResponsivenessProxyOverBudgetCount -ne 0 -or
        $catalogUiThreadResponsivenessProxyMaximumMilliseconds -gt
            [double]$catalogUiThreadResponsivenessProxyTimeoutMilliseconds) {
        throw "The packaged catalog UI-thread responsiveness proxy budget failed."
    }
    $catalogUiThreadResponsivenessProxyVerified = $true

    $rapidScrollRealizedItems = $channelListElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $listItemCondition)
    $rapidScrollRealizedContainerCount = $rapidScrollRealizedItems.Count
    if ($rapidScrollRealizedContainerCount -lt 1 -or
        $rapidScrollRealizedContainerCount -gt 300) {
        throw "The packaged catalog rapid-scroll realized-container bound failed."
    }
    $catalogRealizedContainerCount = [Math]::Max(
        $catalogRealizedContainerCount,
        $rapidScrollRealizedContainerCount)

    if ($EmitM14TraceMarkers) {
        Write-M14CatalogTraceMarker -Name $catalogTraceMarkerEndName
        $catalogTraceMarkerEndEmitted = $true
        $catalogTraceMarkerCount++
        if ($catalogTraceMarkerCount -ne 2) {
            throw "The M14 catalog trace marker count is invalid."
        }
    }

    $catalogPlayerOffStatusElement =
        Get-AutomationElementById $automationRoot "PlaybackStatusText"
    $catalogPlayerOffChannelElement =
        Get-AutomationElementById $automationRoot "PlaybackChannelText"
    if ($null -eq $catalogPlayerOffStatusElement -or
        $catalogPlayerOffStatusElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Text -or
        $catalogPlayerOffStatusElement.Current.Name -cne "Playback stopped." -or
        $null -eq $catalogPlayerOffChannelElement -or
        $catalogPlayerOffChannelElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Text -or
        $catalogPlayerOffChannelElement.Current.Name -cne "No channel selected.") {
        throw "The packaged catalog player-off state is invalid before working-set sampling."
    }
    $catalogPlayerOffStateVerified = $true

    # Fixed bounded settling and process observation deliberately avoid forced
    # GC or working-set trimming before the steady-state absolute measurement.
    $catalogWorkingSetSettleTimer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($catalogWorkingSetSettleTimer.ElapsedMilliseconds -lt
        $catalogPlayerOffSteadyWorkingSetSettleMilliseconds) {
        Assert-PackagedProcessAlive -Process $launchedProcess
        $settleRemainingMilliseconds =
            $catalogPlayerOffSteadyWorkingSetSettleMilliseconds -
            $catalogWorkingSetSettleTimer.ElapsedMilliseconds
        Start-Sleep -Milliseconds ([Math]::Max(
            1,
            [Math]::Min(100, [int]$settleRemainingMilliseconds)))
    }
    $catalogWorkingSetSettleTimer.Stop()
    $catalogPlayerOffSteadyWorkingSetSettleElapsedMilliseconds =
        $catalogWorkingSetSettleTimer.Elapsed.TotalMilliseconds
    Assert-PackagedProcessAlive -Process $launchedProcess

    $catalogWorkingSetSamples = [System.Collections.Generic.List[long]]::new()
    $catalogWorkingSetSampleTarget = 60
    if ($catalogWorkingSetSampleTarget -gt
        $catalogPlayerOffSteadyWorkingSetSampleLimit) {
        throw "The packaged player-off working-set sample target is unbounded."
    }
    $catalogWorkingSetSamplingTimer = [System.Diagnostics.Stopwatch]::StartNew()
    for ($workingSetSample = 1;
        $workingSetSample -le $catalogWorkingSetSampleTarget;
        $workingSetSample++) {
        $workingSetSampleDeadlineMilliseconds =
            $workingSetSample *
            $catalogPlayerOffSteadyWorkingSetSampleIntervalMilliseconds
        while ($catalogWorkingSetSamplingTimer.ElapsedMilliseconds -lt
            $workingSetSampleDeadlineMilliseconds) {
            Assert-PackagedProcessAlive -Process $launchedProcess
            $sampleRemainingMilliseconds =
                $workingSetSampleDeadlineMilliseconds -
                $catalogWorkingSetSamplingTimer.ElapsedMilliseconds
            Start-Sleep -Milliseconds ([Math]::Max(
                1,
                [Math]::Min(100, [int]$sampleRemainingMilliseconds)))
        }

        Assert-PackagedProcessAlive -Process $launchedProcess
        try {
            $launchedProcess.Refresh()
            $workingSetBytes = [long]$launchedProcess.WorkingSet64
        }
        catch {
            throw "The packaged player-off working-set sample is unavailable."
        }
        if ($workingSetBytes -le 0L) {
            throw "The packaged player-off working-set sample is invalid."
        }
        $catalogWorkingSetSamples.Add($workingSetBytes)
    }
    $catalogWorkingSetSamplingTimer.Stop()
    $catalogPlayerOffSteadyWorkingSetSamplingElapsedMilliseconds =
        $catalogWorkingSetSamplingTimer.Elapsed.TotalMilliseconds
    $catalogPlayerOffSteadyWorkingSetSampleCount = $catalogWorkingSetSamples.Count
    $catalogWorkingSetTargetDurationMilliseconds =
        $catalogWorkingSetSampleTarget *
        $catalogPlayerOffSteadyWorkingSetSampleIntervalMilliseconds
    if ($catalogPlayerOffSteadyWorkingSetSampleCount -ne
            $catalogWorkingSetSampleTarget -or
        $catalogPlayerOffSteadyWorkingSetSampleCount -gt
            $catalogPlayerOffSteadyWorkingSetSampleLimit -or
        $catalogWorkingSetTargetDurationMilliseconds -ne
            $catalogPlayerOffSteadyWorkingSetSamplingTargetMilliseconds -or
        $catalogPlayerOffSteadyWorkingSetSamplingElapsedMilliseconds -lt
            $catalogWorkingSetTargetDurationMilliseconds) {
        throw "The packaged player-off working-set sampling interval is invalid."
    }
    Assert-PackagedProcessAlive -Process $launchedProcess
    if ($catalogPlayerOffStatusElement.Current.Name -cne "Playback stopped." -or
        $catalogPlayerOffChannelElement.Current.Name -cne "No channel selected.") {
        throw "The packaged catalog player did not remain off during working-set sampling."
    }
    $catalogPlayerOffSteadyWorkingSetProcessAliveVerified = $true

    $catalogPlayerOffSteadyWorkingSetMinimumBytes = [long](
        $catalogWorkingSetSamples |
            Measure-Object -Minimum |
            Select-Object -ExpandProperty Minimum)
    $catalogPlayerOffSteadyWorkingSetAverageBytes = [double](
        $catalogWorkingSetSamples |
            Measure-Object -Average |
            Select-Object -ExpandProperty Average)
    $catalogPlayerOffSteadyWorkingSetMaximumBytes = [long](
        $catalogWorkingSetSamples |
            Measure-Object -Maximum |
            Select-Object -ExpandProperty Maximum)
    $catalogPlayerOffSteadyWorkingSetRawSamplesBytes = @(
        $catalogWorkingSetSamples | ForEach-Object { [long]$_ })
    if ($catalogPlayerOffSteadyWorkingSetRawSamplesBytes.Count -ne
            $catalogPlayerOffSteadyWorkingSetSampleCount -or
        $catalogPlayerOffSteadyWorkingSetMaximumBytes -gt
            $catalogPlayerOffSteadyWorkingSetBudgetBytes) {
        throw "The packaged player-off steady catalog working-set budget failed."
    }
    $catalogPlayerOffSteadyWorkingSetVerified = $true

    Start-Sleep -Seconds 2
    $launchedProcess.Refresh()
    if ($launchedProcess.HasExited) {
        throw ("The packaged application exited during the launch stability interval (exit code 0x{0:X8})." -f [int]$launchedProcess.ExitCode)
    }

    $protectedStoreDirectoryExists = $false
    $protectedStoreAttributes = [System.IO.FileAttributes]0
    try {
        $protectedStorePath = Join-Path `
            $env:LOCALAPPDATA `
            "Packages\$packageFamilyName\LocalCache\ProtectedStore\v2"
        $protectedStoreDirectoryExists = Test-Path `
            -LiteralPath $protectedStorePath `
            -PathType Container
        if ($protectedStoreDirectoryExists) {
            $protectedStoreAttributes = [System.IO.File]::GetAttributes($protectedStorePath)
        }
    }
    catch {
        throw "The packaged protected-store directory could not be inspected safely."
    }
    if (-not $protectedStoreDirectoryExists) {
        throw "The packaged application did not initialize its protected-store directory."
    }
    if (($protectedStoreAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The packaged protected-store directory must not be a reparse point."
    }
    $protectedStoreDirectoryInitialized = $true

    if (-not $launchedProcess.CloseMainWindow()) {
        throw "The application rejected a normal window-close request."
    }

    if (-not $launchedProcess.WaitForExit(10000)) {
        throw "The application did not exit after a normal window-close request."
    }
    $launchedProcess.Refresh()
    $exitCode = $launchedProcess.ExitCode
    if ($null -eq $exitCode) {
        throw "The application exit code could not be read after the normal window-close request."
    }
    if ([int]$exitCode -ne 0) {
        throw ("The application returned a non-zero exit code after the normal window-close request (exit code 0x{0:X8})." -f [int]$exitCode)
    }

    $launchedProcess.Dispose()
    $launchedProcess = $null

    if (-not (Test-Path -LiteralPath $playbackFixtureRoot -PathType Container) -or
        ([System.IO.File]::GetAttributes($playbackFixtureRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The committed playback acceptance fixture root is invalid."
    }
    if (Test-Path -LiteralPath $playbackControlDirectory) {
        throw "The playback acceptance control directory already exists."
    }

    New-Item -ItemType Directory -Path $playbackControlRoot -Force | Out-Null
    if (([System.IO.File]::GetAttributes($artifactRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ([System.IO.File]::GetAttributes($playbackControlRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The playback acceptance control root is invalid."
    }
    New-Item -ItemType Directory -Path $playbackControlDirectory | Out-Null
    Assert-ExactPlaybackControlEntries -AllowedNames @()

    $playbackHarnessArgumentValues = @(
        $playbackUiHarnessAssemblyPath,
        "serve-and-seed",
        $catalogDatabasePath,
        $protectedStorePath,
        $playbackFixtureRoot,
        $playbackControlDirectory
    )
    foreach ($argumentValue in $playbackHarnessArgumentValues) {
        if ([string]::IsNullOrWhiteSpace($argumentValue) -or $argumentValue.Contains('"')) {
            throw "A playback acceptance harness argument is invalid."
        }
    }
    $playbackHarnessArguments = ($playbackHarnessArgumentValues |
        ForEach-Object { '"' + $_ + '"' }) -join ' '
    try {
        $playbackHarnessProcess = Start-Process `
            -FilePath $DotNetPath `
            -ArgumentList $playbackHarnessArguments `
            -WorkingDirectory $repositoryRoot `
            -WindowStyle Hidden `
            -PassThru
    }
    catch {
        throw "The playback acceptance harness could not be started."
    }
    try {
        $null = $playbackHarnessProcess.Handle
    }
    catch {
        throw "The playback acceptance harness exited before its process handle could be retained."
    }

    $playbackReadyDeadline = (Get-Date).AddSeconds(60)
    do {
        $playbackHarnessProcess.Refresh()
        if ($playbackHarnessProcess.HasExited) {
            throw "The playback acceptance harness exited before publishing readiness."
        }
        if ((Test-Path -LiteralPath $playbackReadyPath -PathType Leaf) -and
            (Test-Path -LiteralPath $playbackPublicCertificatePath -PathType Leaf)) {
            $playbackHarnessReady = $true
            break
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $playbackReadyDeadline)
    if (-not $playbackHarnessReady) {
        throw "The playback acceptance harness did not publish readiness before the deadline."
    }

    Assert-ExactPlaybackControlEntries -AllowedNames @("loopback.cer", "ready.json")
    $readyTicket = Read-StrictPlaybackJsonTicket `
        -Path $playbackReadyPath `
        -AllowedProperties @("IsReady", "SeedCompleted", "CertificateThumbprint")
    if ($readyTicket.IsReady -isnot [bool] -or
        $readyTicket.SeedCompleted -isnot [bool] -or
        $readyTicket.CertificateThumbprint -isnot [string] -or
        -not $readyTicket.IsReady -or
        -not $readyTicket.SeedCompleted -or
        -not [System.Text.RegularExpressions.Regex]::IsMatch(
            $readyTicket.CertificateThumbprint,
            '\A[0-9A-F]{40}\z')) {
        throw "The playback acceptance readiness ticket is invalid."
    }

    $playbackLoopbackCertificateThumbprint = $readyTicket.CertificateThumbprint
    try {
        $playbackLoopbackCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $playbackPublicCertificatePath)
    }
    catch {
        throw "The playback acceptance public certificate is invalid."
    }
    $now = Get-Date
    if ($playbackLoopbackCertificate.HasPrivateKey -or
        $playbackLoopbackCertificate.Subject -cne $expectedPlaybackCertificateSubject -or
        $playbackLoopbackCertificate.Issuer -cne $expectedPlaybackCertificateSubject -or
        $playbackLoopbackCertificate.Thumbprint -cne $playbackLoopbackCertificateThumbprint -or
        $playbackLoopbackCertificate.NotBefore -gt $now -or
        $playbackLoopbackCertificate.NotAfter -le $now) {
        throw "The playback acceptance public certificate does not match readiness."
    }

    $serverAuthenticationExtensions = @(
        $playbackLoopbackCertificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.37" }
    )
    if ($serverAuthenticationExtensions.Count -ne 1) {
        throw "The playback acceptance public certificate usage is invalid."
    }
    $serverAuthenticationExtension =
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$serverAuthenticationExtensions[0]
    $serverAuthenticationUsages = @(
        $serverAuthenticationExtension.EnhancedKeyUsages |
            ForEach-Object { $_.Value }
    )
    if (-not $serverAuthenticationExtension.Critical -or
        $serverAuthenticationUsages.Count -ne 1 -or
        $serverAuthenticationUsages[0] -cne "1.3.6.1.5.5.7.3.1") {
        throw "The playback acceptance public certificate usage is invalid."
    }

    $basicConstraintExtensions = @(
        $playbackLoopbackCertificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.19" }
    )
    $keyUsageExtensions = @(
        $playbackLoopbackCertificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.15" }
    )
    if ($basicConstraintExtensions.Count -ne 1 -or
        $keyUsageExtensions.Count -ne 1) {
        throw "The playback acceptance public certificate constraints are invalid."
    }
    $basicConstraintExtension =
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]$basicConstraintExtensions[0]
    $keyUsageExtension =
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]$keyUsageExtensions[0]
    if (-not $basicConstraintExtension.Critical -or
        $basicConstraintExtension.CertificateAuthority -or
        -not $keyUsageExtension.Critical -or
        $keyUsageExtension.KeyUsages -ne
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) {
        throw "The playback acceptance public certificate constraints are invalid."
    }

    $playbackRootCertificatePath =
        "Cert:\LocalMachine\Root\$playbackLoopbackCertificateThumbprint"
    if (Test-Path -LiteralPath $playbackRootCertificatePath) {
        throw "The exact playback acceptance certificate is already trusted."
    }
    if ($EmitM16FinalArtifactSurfaces) {
        Write-M16CleanupOwnershipValue `
            -Name "playback-loopback.thumbprint" `
            -Value $playbackLoopbackCertificateThumbprint
    }
    $playbackLoopbackCertificateImported = $true
    try {
        $importedPlaybackCertificates = @(
            Import-Certificate `
                -FilePath $playbackPublicCertificatePath `
                -CertStoreLocation "Cert:\LocalMachine\Root"
        )
    }
    catch {
        throw "The playback acceptance public certificate could not be trusted."
    }
    if ($importedPlaybackCertificates.Count -ne 1 -or
        $importedPlaybackCertificates[0].Thumbprint -cne
            $playbackLoopbackCertificateThumbprint) {
        throw "The playback acceptance public certificate import is invalid."
    }

    $existingProcesses = @(Get-Process -Name "IptvSuite.Windows" -ErrorAction SilentlyContinue)
    if ($existingProcesses.Count -ne 0) {
        throw "IptvSuite.Windows is already running; refusing an ambiguous playback launch."
    }

    $playbackActivationProcessId =
        [IptvSuite.PackageSmoke.PackagedApplicationActivator]::Activate($aumid)
    $launchedProcess = Get-Process -Id $playbackActivationProcessId -ErrorAction SilentlyContinue
    if ($null -eq $launchedProcess) {
        throw "The packaged playback application exited before its process could be observed."
    }
    try {
        $null = $launchedProcess.Handle
    }
    catch {
        throw "The packaged playback application exited before its process handle could be retained."
    }
    Assert-PackagedProcessAlive -Process $launchedProcess
    if ($launchedProcess.ProcessName -ne "IptvSuite.Windows") {
        throw "Playback package activation returned an unexpected process."
    }

    $playbackLaunchDeadline = (Get-Date).AddSeconds(30)
    $playbackWindowVisible = $false
    do {
        Assert-PackagedProcessAlive -Process $launchedProcess
        $playbackWindowHandle = $launchedProcess.MainWindowHandle
        if ($playbackWindowHandle -ne [IntPtr]::Zero -and
            [IptvSuite.PackageSmoke.WindowInspector]::IsWindowVisible($playbackWindowHandle)) {
            [uint32]$playbackWindowOwnerProcessId = 0
            [void][IptvSuite.PackageSmoke.WindowInspector]::GetWindowThreadProcessId(
                $playbackWindowHandle,
                [ref]$playbackWindowOwnerProcessId)
            if ($playbackWindowOwnerProcessId -eq [uint32]$playbackActivationProcessId) {
                $playbackWindowVisible = $true
                break
            }
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $playbackLaunchDeadline)
    if (-not $playbackWindowVisible) {
        throw "The packaged playback application did not create a visible window."
    }

    $playbackAutomationRoot =
        [System.Windows.Automation.AutomationElement]::FromHandle($playbackWindowHandle)
    if ($null -eq $playbackAutomationRoot) {
        throw "The packaged playback application has no UI Automation root."
    }
    $playbackSourceElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "CatalogSourceSelector" `
        ([System.Windows.Automation.ControlType]::ComboBox) `
        "Playlist source"
    $playbackChannelListElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "CatalogChannelList" `
        ([System.Windows.Automation.ControlType]::List) `
        "Channels"
    $playbackStatusElement = Get-AutomationElementById `
        $playbackAutomationRoot `
        "PlaybackStatusText"
    if ($null -eq $playbackStatusElement -or
        $playbackStatusElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Text) {
        throw "The packaged playback status automation element is invalid."
    }
    $playbackCurrentChannelElement = Get-AutomationElementById `
        $playbackAutomationRoot `
        "PlaybackChannelText"
    if ($null -eq $playbackCurrentChannelElement -or
        $playbackCurrentChannelElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Text -or
        $playbackCurrentChannelElement.Current.Name -cne "No channel selected.") {
        throw "The packaged current playback channel automation element is invalid."
    }
    $playButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackPlayButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Play channel"
    $pauseButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackPauseButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Pause channel"
    $stopButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackStopButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Stop channel"
    $volumeDownButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackVolumeDownButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Decrease playback volume"
    $volumeUpButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackVolumeUpButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Increase playback volume"
    $muteButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackMuteButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Mute playback"
    $aspectModeButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackAspectModeButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Use fill aspect mode"
    $fullscreenButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "PlaybackFullscreenButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Enter fullscreen"
    $volumeTextElement = Get-AutomationElementById `
        $playbackAutomationRoot `
        "PlaybackVolumeText"
    if ($null -eq $volumeTextElement -or
        $volumeTextElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Text -or
        $volumeTextElement.Current.Name -cne "Volume 100%") {
        throw "The packaged playback volume status automation element is invalid."
    }

    $sourceSelectionPatternObject = $null
    if (-not $playbackSourceElement.TryGetCurrentPattern(
            [System.Windows.Automation.SelectionPattern]::Pattern,
            [ref]$sourceSelectionPatternObject)) {
        throw "The packaged playback source selector has no SelectionPattern."
    }
    $sourceSelectionPattern =
        [System.Windows.Automation.SelectionPattern]$sourceSelectionPatternObject
    $sourceExpandPatternObject = $null
    if (-not $playbackSourceElement.TryGetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern,
            [ref]$sourceExpandPatternObject)) {
        throw "The packaged playback source selector has no ExpandCollapsePattern."
    }
    $sourceExpandPattern =
        [System.Windows.Automation.ExpandCollapsePattern]$sourceExpandPatternObject
    $sourceExpandPattern.Expand()

    $playbackListItemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $sourceItemsDeadline = (Get-Date).AddSeconds(10)
    do {
        Assert-PackagedProcessAlive -Process $launchedProcess
        $sourceItems = $playbackSourceElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $playbackListItemCondition)
        if ($sourceItems.Count -eq 2) {
            break
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $sourceItemsDeadline)
    if ($sourceItems.Count -ne 2) {
        throw "The packaged playback catalog did not expose exactly two sources."
    }

    $playbackSourceItem = $null
    $catalogSourceItem = $null
    for ($sourceItemIndex = 0; $sourceItemIndex -lt $sourceItems.Count; $sourceItemIndex++) {
        $sourceItem = $sourceItems[$sourceItemIndex]
        if ($sourceItem.Current.Name -ceq $expectedPlaybackSourceName) {
            if ($null -ne $playbackSourceItem) {
                throw "The packaged playback source list contains a duplicate acceptance source."
            }
            $playbackSourceItem = $sourceItem
        }
        elseif ($sourceItem.Current.Name -ceq $expectedCatalogSourceName) {
            if ($null -ne $catalogSourceItem) {
                throw "The packaged playback source list contains a duplicate catalog source."
            }
            $catalogSourceItem = $sourceItem
        }
        else {
            throw "The packaged playback source list contains an unexpected source."
        }
    }
    if ($null -eq $playbackSourceItem -or $null -eq $catalogSourceItem) {
        throw "The packaged playback source list is incomplete."
    }

    $selectedSources = @($sourceSelectionPattern.Current.GetSelection())
    $playbackSourceSelected =
        $selectedSources.Count -eq 1 -and
        $selectedSources[0].Current.Name -ceq $expectedPlaybackSourceName
    if (-not $playbackSourceSelected) {
        $sourceSelectionItemPatternObject = $null
        $selectedWithUia = $false
        try {
            if ($playbackSourceItem.TryGetCurrentPattern(
                    [System.Windows.Automation.SelectionItemPattern]::Pattern,
                    [ref]$sourceSelectionItemPatternObject)) {
                ([System.Windows.Automation.SelectionItemPattern]$sourceSelectionItemPatternObject).Select()
                $selectedWithUia = $true
            }
        }
        catch {
            $selectedWithUia = $false
        }

        if (-not $selectedWithUia) {
            Assert-PackagedWindowForeground `
                $playbackWindowHandle `
                ([uint32]$playbackActivationProcessId)
            Assert-FocusedAutomationElement `
                $playbackSourceElement `
                "CatalogSourceSelector" `
                -RequestFocus
            [IptvSuite.PackageSmoke.KeyboardInspector]::PressHome()
            [IptvSuite.PackageSmoke.KeyboardInspector]::PressEnter()
        }
    }
    if ($sourceExpandPattern.Current.ExpandCollapseState -eq
        [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $sourceExpandPattern.Collapse()
    }

    $expectedPlaybackCatalogStatus =
        "Showing 1$([char]0x2013)2 of 2 channels."
    $catalogStatusElement = $null
    $playbackCatalogDeadline = (Get-Date).AddSeconds(15)
    $playbackCatalogReady = $false
    do {
        Assert-PackagedProcessAlive -Process $launchedProcess
        if ($null -eq $catalogStatusElement) {
            $catalogStatusElement = Get-AutomationElementById `
                $playbackAutomationRoot `
                "CatalogStatusText"
        }
        $selectedSources = @($sourceSelectionPattern.Current.GetSelection())
        if ($selectedSources.Count -eq 1 -and
            $selectedSources[0].Current.Name -ceq $expectedPlaybackSourceName -and
            $null -ne $catalogStatusElement -and
            $catalogStatusElement.Current.Name -ceq $expectedPlaybackCatalogStatus) {
            $playbackCatalogReady = $true
            break
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $playbackCatalogDeadline)
    if (-not $playbackCatalogReady) {
        if ($null -eq $catalogStatusElement) {
            throw "The packaged playback catalog status automation element is missing."
        }
        throw "The packaged playback catalog did not expose the seeded acceptance channel."
    }

    $playbackChannelDeadline = (Get-Date).AddSeconds(10)
    do {
        Assert-PackagedProcessAlive -Process $launchedProcess
        $playbackChannelItems = $playbackChannelListElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $playbackListItemCondition)
        if ($playbackChannelItems.Count -eq 2) {
            break
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $playbackChannelDeadline)
    if ($playbackChannelItems.Count -ne 2) {
        throw "The packaged playback catalog did not expose exactly two channel items."
    }

    $playbackChannelItemA = $null
    $playbackChannelItemB = $null
    for ($channelItemIndex = 0;
        $channelItemIndex -lt $playbackChannelItems.Count;
        $channelItemIndex++) {
        $channelItem = $playbackChannelItems[$channelItemIndex]
        if (Test-AutomationElementContainsExactText `
                -Root $channelItem `
                -ExpectedText $expectedPlaybackChannelAName) {
            if ($null -ne $playbackChannelItemA) {
                throw "The packaged playback channel list contains a duplicate acceptance channel."
            }
            $playbackChannelItemA = $channelItem
        }
        elseif (Test-AutomationElementContainsExactText `
                -Root $channelItem `
                -ExpectedText $expectedPlaybackChannelBName) {
            if ($null -ne $playbackChannelItemB) {
                throw "The packaged playback channel list contains a duplicate acceptance channel."
            }
            $playbackChannelItemB = $channelItem
        }
        else {
            throw "The packaged playback channel list contains an unexpected channel."
        }
    }
    if ($null -eq $playbackChannelItemA -or $null -eq $playbackChannelItemB) {
        throw "The packaged playback channel list is incomplete."
    }

    Assert-PackagedWindowForeground `
        $playbackWindowHandle `
        ([uint32]$playbackActivationProcessId)
    Invoke-PackagedPlaybackChannelItem `
        -Process $launchedProcess `
        -ChannelItem $playbackChannelItemA `
        -WindowHandle $playbackWindowHandle `
        -ExpectedProcessId ([uint32]$playbackActivationProcessId)
    Wait-PackagedPlaybackSelection `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ChannelElement $playbackCurrentChannelElement `
        -ExpectedChannelName $expectedPlaybackChannelAName

    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $volumeDownButtonElement
    Wait-PackagedAutomationName `
        -Process $launchedProcess `
        -Element $volumeTextElement `
        -ExpectedName "Volume 95%"
    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $volumeUpButtonElement
    Wait-PackagedAutomationName `
        -Process $launchedProcess `
        -Element $volumeTextElement `
        -ExpectedName "Volume 100%"
    $playbackVolumeControlVerified = $true

    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $muteButtonElement
    Wait-PackagedAutomationName `
        -Process $launchedProcess `
        -Element $muteButtonElement `
        -ExpectedName "Unmute playback"
    $playbackMuteControlVerified = $true

    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $aspectModeButtonElement
    Wait-PackagedAutomationName `
        -Process $launchedProcess `
        -Element $aspectModeButtonElement `
        -ExpectedName "Use fit aspect mode"
    $playbackAspectControlVerified = $true

    Assert-FocusedAutomationElement `
        $fullscreenButtonElement `
        "PlaybackFullscreenButton" `
        -RequestFocus
    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $fullscreenButtonElement
    $fullscreenButtonElement = Wait-PackagedAutomationElementByName `
        -Process $launchedProcess `
        -Root $playbackAutomationRoot `
        -AutomationId "PlaybackFullscreenButton" `
        -ControlType ([System.Windows.Automation.ControlType]::Button) `
        -ExpectedName "Exit fullscreen"
    Wait-PackagedPlaybackStatus `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Channel is playing."
    $playbackFullscreenEnterVerified = $true
    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $fullscreenButtonElement
    $fullscreenButtonElement = Wait-PackagedAutomationElementByName `
        -Process $launchedProcess `
        -Root $playbackAutomationRoot `
        -AutomationId "PlaybackFullscreenButton" `
        -ControlType ([System.Windows.Automation.ControlType]::Button) `
        -ExpectedName "Enter fullscreen"
    Assert-FocusedAutomationElement `
        $fullscreenButtonElement `
        "PlaybackFullscreenButton"
    Wait-PackagedPlaybackStatus `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Channel is playing."
    $playbackFullscreenExitVerified = $true
    $playbackFullscreenFocusRestored = $true

    Invoke-PackagedPlaybackButton `
        -Process $launchedProcess `
        -ButtonElement $pauseButtonElement `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Playback paused."
    Invoke-PackagedPlaybackButton `
        -Process $launchedProcess `
        -ButtonElement $playButtonElement `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Channel is playing."

    Invoke-PackagedPlaybackChannelItem `
        -Process $launchedProcess `
        -ChannelItem $playbackChannelItemB `
        -WindowHandle $playbackWindowHandle `
        -ExpectedProcessId ([uint32]$playbackActivationProcessId)
    Wait-PackagedPlaybackSelection `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ChannelElement $playbackCurrentChannelElement `
        -ExpectedChannelName $expectedPlaybackChannelBName
    Invoke-PackagedPlaybackButton `
        -Process $launchedProcess `
        -ButtonElement $stopButtonElement `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Playback stopped."
    Wait-PackagedAutomationName `
        -Process $launchedProcess `
        -Element $playbackCurrentChannelElement `
        -ExpectedName "No channel selected."
    Invoke-PackagedPlaybackChannelItem `
        -Process $launchedProcess `
        -ChannelItem $playbackChannelItemA `
        -WindowHandle $playbackWindowHandle `
        -ExpectedProcessId ([uint32]$playbackActivationProcessId)
    Wait-PackagedPlaybackSelection `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ChannelElement $playbackCurrentChannelElement `
        -ExpectedChannelName $expectedPlaybackChannelAName
    $playbackResourceWarmupVerified = $true

    $playbackResourceBaseline =
        Get-PackagedProcessResourceSnapshot -Process $launchedProcess
    $rapidSwitchSamples = [System.Collections.Generic.List[double]]::new(25)
    for ($switchOrdinal = 1; $switchOrdinal -le 25; $switchOrdinal++) {
        $targetChannelItem = if (($switchOrdinal % 2) -eq 1) {
            $playbackChannelItemB
        }
        else {
            $playbackChannelItemA
        }
        $targetChannelName = if (($switchOrdinal % 2) -eq 1) {
            $expectedPlaybackChannelBName
        }
        else {
            $expectedPlaybackChannelAName
        }

        $switchTimer = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-PackagedPlaybackChannelItem `
            -Process $launchedProcess `
            -ChannelItem $targetChannelItem `
            -WindowHandle $playbackWindowHandle `
            -ExpectedProcessId ([uint32]$playbackActivationProcessId)
        Wait-PackagedPlaybackSelection `
            -Process $launchedProcess `
            -StatusElement $playbackStatusElement `
            -ChannelElement $playbackCurrentChannelElement `
            -ExpectedChannelName $targetChannelName
        $switchTimer.Stop()
        $rapidSwitchSamples.Add($switchTimer.Elapsed.TotalMilliseconds)
    }

    $playbackRapidSwitchP95Milliseconds = Get-Percentile95 $rapidSwitchSamples.ToArray()
    $playbackRapidSwitchMaximumMilliseconds = [double](
        ($rapidSwitchSamples | Measure-Object -Maximum).Maximum)
    Write-Output (
        "Packaged playback rapid-switch diagnostic: count=$($rapidSwitchSamples.Count), " +
        "p95=$([Math]::Round($playbackRapidSwitchP95Milliseconds, 4)), " +
        "maximum=$([Math]::Round($playbackRapidSwitchMaximumMilliseconds, 4)).")
    if ($playbackRapidSwitchP95Milliseconds -gt 3000.0) {
        throw "The packaged playback rapid-switch p95 budget was exceeded."
    }
    $playbackRapidSwitchCount = $rapidSwitchSamples.Count
    $playbackRapidSwitchVerified = $playbackRapidSwitchCount -eq 25

    Invoke-PackagedPlaybackChannelItem `
        -Process $launchedProcess `
        -ChannelItem $playbackChannelItemA `
        -WindowHandle $playbackWindowHandle `
        -ExpectedProcessId ([uint32]$playbackActivationProcessId)
    Wait-PackagedPlaybackSelection `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ChannelElement $playbackCurrentChannelElement `
        -ExpectedChannelName $expectedPlaybackChannelAName

    $playbackSurfaceElement = Get-AutomationElementById `
        $playbackAutomationRoot `
        "PlaybackSurface"
    if ($null -eq $playbackSurfaceElement -or
        $playbackSurfaceElement.Current.Name -cne "Live TV playback surface") {
        throw "The packaged playback surface automation element is invalid."
    }
    $playbackSurfaceBounds = Wait-PackagedPlaybackSurfaceBounds `
        -Process $launchedProcess `
        -Element $playbackSurfaceElement `
        -WindowHandle $playbackWindowHandle

    foreach ($windowSize in @(
            @(800, 600),
            @(1000, 700))) {
        Set-PackagedWindowSize `
            -Process $launchedProcess `
            -WindowHandle $playbackWindowHandle `
            -Width $windowSize[0] `
            -Height $windowSize[1]
        $playbackWindowResizeCount++
        $playbackSurfaceBounds = Wait-PackagedPlaybackSurfaceBounds `
            -Process $launchedProcess `
            -Element $playbackSurfaceElement `
            -WindowHandle $playbackWindowHandle `
            -PreviousWidth $playbackSurfaceBounds.Width `
            -PreviousHeight $playbackSurfaceBounds.Height
        Wait-PackagedPlaybackSelection `
            -Process $launchedProcess `
            -StatusElement $playbackStatusElement `
            -ChannelElement $playbackCurrentChannelElement `
            -ExpectedChannelName $expectedPlaybackChannelAName
    }
    if ($playbackWindowResizeCount -ne 2) {
        throw "The packaged playback window resize count is invalid."
    }
    $playbackWindowResizeVerified = $true
    $playbackSurfaceBoundsVerified = $true

    Invoke-PackagedWindowMinimize `
        -Process $launchedProcess `
        -WindowHandle $playbackWindowHandle
    Assert-PackagedProcessAlive -Process $launchedProcess
    $playbackWindowMinimizeVerified = $true

    Invoke-PackagedWindowRestore `
        -Process $launchedProcess `
        -WindowHandle $playbackWindowHandle
    Assert-PackagedWindowForeground `
        $playbackWindowHandle `
        ([uint32]$playbackActivationProcessId)
    $playbackAutomationRoot =
        [System.Windows.Automation.AutomationElement]::FromHandle($playbackWindowHandle)
    if ($null -eq $playbackAutomationRoot) {
        throw "The restored packaged playback application has no UI Automation root."
    }
    $playbackStatusElement = Get-AutomationElementById `
        $playbackAutomationRoot `
        "PlaybackStatusText"
    $playbackCurrentChannelElement = Get-AutomationElementById `
        $playbackAutomationRoot `
        "PlaybackChannelText"
    $playbackSurfaceElement = Get-AutomationElementById `
        $playbackAutomationRoot `
        "PlaybackSurface"
    if ($null -eq $playbackStatusElement -or
        $playbackStatusElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Text -or
        $null -eq $playbackCurrentChannelElement -or
        $playbackCurrentChannelElement.Current.ControlType -ne
            [System.Windows.Automation.ControlType]::Text -or
        $null -eq $playbackSurfaceElement -or
        $playbackSurfaceElement.Current.Name -cne "Live TV playback surface") {
        throw "The restored packaged playback UI Automation contract is invalid."
    }
    Wait-PackagedPlaybackSelection `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ChannelElement $playbackCurrentChannelElement `
        -ExpectedChannelName $expectedPlaybackChannelAName
    $null = Wait-PackagedPlaybackSurfaceBounds `
        -Process $launchedProcess `
        -Element $playbackSurfaceElement `
        -WindowHandle $playbackWindowHandle
    $playbackWindowRestoreVerified = $true
    $playbackWindowStatePreserved = $true

    Invoke-PackagedPlaybackButton `
        -Process $launchedProcess `
        -ButtonElement $stopButtonElement `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Playback stopped."
    Wait-PackagedAutomationName `
        -Process $launchedProcess `
        -Element $playbackCurrentChannelElement `
        -ExpectedName "No channel selected."
    $playbackResourceFinal =
        Get-PackagedProcessResourceSnapshot -Process $launchedProcess
    $playbackBaselinePrivateBytes = $playbackResourceBaseline.PrivateBytes
    $playbackFinalPrivateBytes = $playbackResourceFinal.PrivateBytes
    $playbackPrivateBytesDelta =
        $playbackFinalPrivateBytes - $playbackBaselinePrivateBytes
    $playbackBaselineWorkingSetBytes = $playbackResourceBaseline.WorkingSetBytes
    $playbackFinalWorkingSetBytes = $playbackResourceFinal.WorkingSetBytes
    $playbackWorkingSetBytesDelta =
        $playbackFinalWorkingSetBytes - $playbackBaselineWorkingSetBytes
    $playbackBaselineHandleCount = $playbackResourceBaseline.HandleCount
    $playbackFinalHandleCount = $playbackResourceFinal.HandleCount
    $playbackHandleCountDelta =
        $playbackFinalHandleCount - $playbackBaselineHandleCount
    $playbackBaselineThreadCount = $playbackResourceBaseline.ThreadCount
    $playbackFinalThreadCount = $playbackResourceFinal.ThreadCount
    $playbackThreadCountDelta =
        $playbackFinalThreadCount - $playbackBaselineThreadCount
    $playbackResourceSnapshotVerified = $true

    $playbackResourceDiagnostic = (
        "Packaged playback short-run resource diagnostic: " +
        "privateBytesDelta=$playbackPrivateBytesDelta, " +
        "privateBytesBudget=$playbackPrivateBytesDeltaBudget, " +
        "workingSetBytesDelta=$playbackWorkingSetBytesDelta, " +
        "workingSetBytesBudget=$playbackWorkingSetBytesDeltaBudget, " +
        "handleCountDelta=$playbackHandleCountDelta, " +
        "handleCountBudget=$playbackHandleCountDeltaBudget, " +
        "threadCountDelta=$playbackThreadCountDelta, " +
        "threadCountBudget=$playbackThreadCountDeltaBudget.")
    Write-Host $playbackResourceDiagnostic
    if ($playbackPrivateBytesDelta -gt $playbackPrivateBytesDeltaBudget -or
        $playbackWorkingSetBytesDelta -gt $playbackWorkingSetBytesDeltaBudget -or
        $playbackHandleCountDelta -gt $playbackHandleCountDeltaBudget -or
        $playbackThreadCountDelta -gt $playbackThreadCountDeltaBudget) {
        throw "The packaged playback short-run resource budget was exceeded."
    }
    $playbackResourceBudgetVerified = $true

    $streamBaseControlNames = @("loopback.cer", "ready.json")
    New-ExactPlaybackControlSignal -Path $playbackStreamFaultArmSignalPath
    $streamFaultReadyTicket = Wait-PlaybackStreamTicket `
        -HarnessProcess $playbackHarnessProcess `
        -TicketPath $playbackStreamFaultReadyTicketPath `
        -AllowedControlNames ($streamBaseControlNames + @(
                "arm-stream-fault.signal",
                "stream-fault-ready.json")) `
        -AllowedProperties @("IsReady", "MaximumRequestOrdinals")
    if ($streamFaultReadyTicket.IsReady -isnot [bool] -or
        -not $streamFaultReadyTicket.IsReady -or
        $streamFaultReadyTicket.MaximumRequestOrdinals -isnot [int] -or
        [int]$streamFaultReadyTicket.MaximumRequestOrdinals -ne 3) {
        throw "The playback stream-fault readiness result is invalid."
    }

    Invoke-PackagedPlaybackChannelItem `
        -Process $launchedProcess `
        -ChannelItem $playbackChannelItemB `
        -WindowHandle $playbackWindowHandle `
        -ExpectedProcessId ([uint32]$playbackActivationProcessId)
    Wait-PackagedPlaybackSelection `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ChannelElement $playbackCurrentChannelElement `
        -ExpectedChannelName $expectedPlaybackChannelBName

    New-ExactPlaybackControlSignal -Path $playbackStreamEndSignalPath
    $streamEndResultTicket = Wait-PlaybackStreamTicket `
        -HarnessProcess $playbackHarnessProcess `
        -TicketPath $playbackStreamEndResultTicketPath `
        -AllowedControlNames ($streamBaseControlNames + @(
                "arm-stream-fault.signal",
                "stream-fault-ready.json",
                "end-stream.signal",
                "stream-end-result.json")) `
        -AllowedProperties @(
            "IsVerified",
            "LastAssignedRequestOrdinal",
            "ActiveRequestOrdinal",
            "CurrentHeldRequestCount",
            "ExpectedCompletionCount",
            "LastExpectedCompletionOrdinal")
    $streamEndExpected = [ordered]@{
        LastAssignedRequestOrdinal = 2L
        ActiveRequestOrdinal = 0L
        CurrentHeldRequestCount = 1L
        ExpectedCompletionCount = 1L
        LastExpectedCompletionOrdinal = 1L
    }
    if ($streamEndResultTicket.IsVerified -isnot [bool] -or
        -not $streamEndResultTicket.IsVerified) {
        throw "The first playback stream completion was not verified."
    }
    foreach ($propertyName in $streamEndExpected.Keys) {
        $actualValue = $streamEndResultTicket.$propertyName
        if (($actualValue -isnot [int] -and $actualValue -isnot [long]) -or
            [long]$actualValue -ne [long]$streamEndExpected[$propertyName]) {
            throw "The first playback stream completion result is invalid."
        }
    }
    Wait-PackagedPlaybackStatus `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Reconnect attempt 1 of 3 is starting." `
        -TimeoutSeconds 10
    $cancelReconnectButtonElement = Wait-PackagedAutomationElementByName `
        -Process $launchedProcess `
        -Root $playbackAutomationRoot `
        -AutomationId "PlaybackStopButton" `
        -ControlType ([System.Windows.Automation.ControlType]::Button) `
        -ExpectedName "Cancel reconnect" `
        -TimeoutSeconds 10

    New-ExactPlaybackControlSignal -Path $playbackStreamRestoreSignalPath
    $streamRestoreResultTicket = Wait-PlaybackStreamTicket `
        -HarnessProcess $playbackHarnessProcess `
        -TicketPath $playbackStreamRestoreResultTicketPath `
        -AllowedControlNames ($streamBaseControlNames + @(
                "arm-stream-fault.signal",
                "stream-fault-ready.json",
                "end-stream.signal",
                "stream-end-result.json",
                "restore-stream.signal",
                "stream-restore-result.json")) `
        -AllowedProperties @(
            "IsVerified",
            "LastAssignedRequestOrdinal",
            "ActiveRequestOrdinal",
            "CurrentHeldRequestCount",
            "ExpectedCompletionCount",
            "LastExpectedCompletionOrdinal")
    $streamRestoreExpected = [ordered]@{
        LastAssignedRequestOrdinal = 2L
        ActiveRequestOrdinal = 2L
        CurrentHeldRequestCount = 0L
        ExpectedCompletionCount = 1L
        LastExpectedCompletionOrdinal = 1L
    }
    if ($streamRestoreResultTicket.IsVerified -isnot [bool] -or
        -not $streamRestoreResultTicket.IsVerified) {
        throw "The playback stream recovery was not verified."
    }
    foreach ($propertyName in $streamRestoreExpected.Keys) {
        $actualValue = $streamRestoreResultTicket.$propertyName
        if (($actualValue -isnot [int] -and $actualValue -isnot [long]) -or
            [long]$actualValue -ne [long]$streamRestoreExpected[$propertyName]) {
            throw "The playback stream recovery result is invalid."
        }
    }
    Wait-PackagedPlaybackSelection `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ChannelElement $playbackCurrentChannelElement `
        -ExpectedChannelName $expectedPlaybackChannelBName `
        -TimeoutMilliseconds 10000
    $playbackReconnectRecoveryVerified = $true

    New-ExactPlaybackControlSignal -Path $playbackStreamEndForCancelSignalPath
    $streamCancelReadyTicket = Wait-PlaybackStreamTicket `
        -HarnessProcess $playbackHarnessProcess `
        -TicketPath $playbackStreamCancelReadyTicketPath `
        -AllowedControlNames ($streamBaseControlNames + @(
                "arm-stream-fault.signal",
                "stream-fault-ready.json",
                "end-stream.signal",
                "stream-end-result.json",
                "restore-stream.signal",
                "stream-restore-result.json",
                "end-stream-for-cancel.signal",
                "stream-cancel-ready.json")) `
        -AllowedProperties @(
            "IsVerified",
            "LastAssignedRequestOrdinal",
            "ActiveRequestOrdinal",
            "CurrentHeldRequestCount",
            "ExpectedCompletionCount",
            "LastExpectedCompletionOrdinal",
            "RequestCountAtReady")
    $streamCancelReadyExpected = [ordered]@{
        LastAssignedRequestOrdinal = 3L
        ActiveRequestOrdinal = 0L
        CurrentHeldRequestCount = 1L
        ExpectedCompletionCount = 2L
        LastExpectedCompletionOrdinal = 2L
    }
    if ($streamCancelReadyTicket.IsVerified -isnot [bool] -or
        -not $streamCancelReadyTicket.IsVerified) {
        throw "The reconnect-cancel stream boundary was not prepared."
    }
    foreach ($propertyName in $streamCancelReadyExpected.Keys) {
        $actualValue = $streamCancelReadyTicket.$propertyName
        if (($actualValue -isnot [int] -and $actualValue -isnot [long]) -or
            [long]$actualValue -ne [long]$streamCancelReadyExpected[$propertyName]) {
            throw "The reconnect-cancel stream boundary is invalid."
        }
    }
    if (($streamCancelReadyTicket.RequestCountAtReady -isnot [int] -and
            $streamCancelReadyTicket.RequestCountAtReady -isnot [long]) -or
        [long]$streamCancelReadyTicket.RequestCountAtReady -le 0) {
        throw "The reconnect-cancel request baseline is invalid."
    }
    $playbackReconnectNoLaterOpenRequestCountAtReady =
        [int]$streamCancelReadyTicket.RequestCountAtReady
    Wait-PackagedPlaybackStatus `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ExpectedStatus "Reconnect attempt 1 of 3 is starting." `
        -TimeoutSeconds 10
    $cancelReconnectButtonElement = Wait-PackagedAutomationElementByName `
        -Process $launchedProcess `
        -Root $playbackAutomationRoot `
        -AutomationId "PlaybackStopButton" `
        -ControlType ([System.Windows.Automation.ControlType]::Button) `
        -ExpectedName "Cancel reconnect" `
        -TimeoutSeconds 10

    $playbackReconnectCancelTimer = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $cancelReconnectButtonElement
    $playbackReconnectCancelElapsedMilliseconds = Wait-PackagedPlaybackStoppedWithinBudget `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ChannelElement $playbackCurrentChannelElement `
        -StopButtonElement $cancelReconnectButtonElement `
        -Timer $playbackReconnectCancelTimer `
        -BudgetMilliseconds $playbackReconnectCancelBudgetMilliseconds
    if ($playbackReconnectCancelElapsedMilliseconds -gt
        $playbackReconnectCancelBudgetMilliseconds) {
        throw "Packaged reconnect cancellation exceeded the exact budget."
    }
    $playbackReconnectCancelVerified = $true

    New-ExactPlaybackControlSignal -Path $playbackStreamCancelVerificationSignalPath
    $streamCancelResultProperties = @(
        "IsVerified",
        "IsHolding",
        "ObservationMilliseconds",
        "RequestCountAtReady",
        "RequestCountAfterObservation",
        "LastAssignedRequestOrdinal",
        "ActiveRequestOrdinal",
        "CurrentHeldRequestCount",
        "PeakHeldRequestCount",
        "PeakActiveRequestCount",
        "OverlapViolationCount",
        "ExpectedCompletionCount",
        "LastExpectedCompletionOrdinal",
        "ExpectedAbortCount",
        "LastExpectedAbortOrdinal",
        "ExpectedRejectCount",
        "LastExpectedRejectOrdinal",
        "ClientDetachCount",
        "LastClientDetachOrdinal",
        "DisabledFallbackCount",
        "LastDisabledFallbackOrdinal",
        "CapacityRejectCount",
        "UnexpectedFailureCount",
        "LastUnexpectedFailureOrdinal")
    $streamCancelResultTicket = Wait-PlaybackStreamTicket `
        -HarnessProcess $playbackHarnessProcess `
        -TicketPath $playbackStreamCancelResultTicketPath `
        -AllowedControlNames ($streamBaseControlNames + $playbackStreamProtocolControlNames) `
        -AllowedProperties $streamCancelResultProperties `
        -TimeoutSeconds 60
    $streamCancelFinalExpected = [ordered]@{
        LastAssignedRequestOrdinal = 3L
        ActiveRequestOrdinal = 0L
        CurrentHeldRequestCount = 0L
        PeakHeldRequestCount = 1L
        PeakActiveRequestCount = 1L
        OverlapViolationCount = 0L
        ExpectedCompletionCount = 2L
        LastExpectedCompletionOrdinal = 2L
        ExpectedAbortCount = 0L
        LastExpectedAbortOrdinal = 0L
        ExpectedRejectCount = 0L
        LastExpectedRejectOrdinal = 0L
        ClientDetachCount = 1L
        LastClientDetachOrdinal = 3L
        DisabledFallbackCount = 0L
        LastDisabledFallbackOrdinal = 0L
        CapacityRejectCount = 0L
        UnexpectedFailureCount = 0L
        LastUnexpectedFailureOrdinal = 0L
    }
    if ($streamCancelResultTicket.IsVerified -isnot [bool] -or
        $streamCancelResultTicket.IsHolding -isnot [bool] -or
        -not $streamCancelResultTicket.IsVerified -or
        -not $streamCancelResultTicket.IsHolding) {
        throw "The reconnect-cancel verification result is invalid."
    }
    foreach ($propertyName in $streamCancelFinalExpected.Keys) {
        $actualValue = $streamCancelResultTicket.$propertyName
        if (($actualValue -isnot [int] -and $actualValue -isnot [long]) -or
            [long]$actualValue -ne [long]$streamCancelFinalExpected[$propertyName]) {
            throw "The reconnect-cancel controlled-stream accounting is invalid."
        }
    }
    foreach ($propertyName in @(
            "ObservationMilliseconds",
            "RequestCountAtReady",
            "RequestCountAfterObservation")) {
        $actualValue = $streamCancelResultTicket.$propertyName
        if ($actualValue -isnot [int] -and $actualValue -isnot [long]) {
            throw "The reconnect-cancel bounded result is invalid."
        }
    }
    if ([long]$streamCancelResultTicket.ObservationMilliseconds -lt 31000 -or
        [long]$streamCancelResultTicket.RequestCountAtReady -ne
            [long]$streamCancelResultTicket.RequestCountAfterObservation -or
        [long]$streamCancelResultTicket.RequestCountAtReady -ne
            [long]$streamCancelReadyTicket.RequestCountAtReady) {
        throw "Playback reopened after reconnect cancellation."
    }
    $playbackReconnectNoLaterOpenObservationMilliseconds =
        [long]$streamCancelResultTicket.ObservationMilliseconds
    $playbackReconnectNoLaterOpenRequestCountAfterObservation =
        [int]$streamCancelResultTicket.RequestCountAfterObservation
    $playbackReconnectNoLaterOpenVerified = $true

    Invoke-PackagedPlaybackChannelItem `
        -Process $launchedProcess `
        -ChannelItem $playbackChannelItemA `
        -WindowHandle $playbackWindowHandle `
        -ExpectedProcessId ([uint32]$playbackActivationProcessId)
    Wait-PackagedPlaybackSelection `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ChannelElement $playbackCurrentChannelElement `
        -ExpectedChannelName $expectedPlaybackChannelAName

    $deleteSourceButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "CatalogDeleteSourceButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Delete selected playlist source"
    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $deleteSourceButtonElement
    $cancelDeleteButtonElement = Wait-PackagedSourceDeletionDialogButton `
        -Process $launchedProcess `
        -Root $playbackAutomationRoot `
        -ExpectedButtonName "Cancel"
    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $cancelDeleteButtonElement
    Wait-PackagedSourceDeletionDialogDismissed `
        -Process $launchedProcess `
        -Root $playbackAutomationRoot `
        -DeleteButton $deleteSourceButtonElement
    Wait-PackagedPlaybackSelection `
        -Process $launchedProcess `
        -StatusElement $playbackStatusElement `
        -ChannelElement $playbackCurrentChannelElement `
        -ExpectedChannelName $expectedPlaybackChannelAName

    New-ExactPlaybackControlSignal -Path $playbackCancelVerificationSignalPath
    Wait-PlaybackPreservationTicket `
        -HarnessProcess $playbackHarnessProcess `
        -TicketPath $playbackCancelVerificationTicketPath `
        -AllowedControlNames ($streamBaseControlNames + $playbackStreamProtocolControlNames + @(
            "verify-cancel.signal",
            "cancel-result.json"))
    $sourceDeletionCancelNoMutationVerified = $true

    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $deleteSourceButtonElement
    $null = Wait-PackagedSourceDeletionDialogButton `
        -Process $launchedProcess `
        -Root $playbackAutomationRoot `
        -ExpectedButtonName "Delete"
    Assert-PackagedProcessAlive -Process $launchedProcess
    if (-not $launchedProcess.CloseMainWindow()) {
        throw "The packaged playback application rejected a normal window-close request."
    }
    if (-not $launchedProcess.WaitForExit(10000)) {
        throw "The packaged playback application did not exit after a normal close request."
    }
    $launchedProcess.Refresh()
    $playbackExitCode = $launchedProcess.ExitCode
    if ($null -eq $playbackExitCode -or [int]$playbackExitCode -ne 0) {
        throw "The packaged playback application did not return a successful normal-close result."
    }
    $playbackActiveCloseVerified = $true

    $launchedProcess.Dispose()
    $launchedProcess = $null
    $playbackWindowHandle = [IntPtr]::Zero
    $playbackAutomationRoot = $null
    $playbackSourceElement = $null
    $playbackChannelListElement = $null
    $playbackStatusElement = $null
    $playbackCurrentChannelElement = $null
    $playbackChannelItemA = $null
    $playbackChannelItemB = $null
    $deleteSourceButtonElement = $null
    $cancelDeleteButtonElement = $null

    New-ExactPlaybackControlSignal -Path $playbackDialogCloseVerificationSignalPath
    Wait-PlaybackPreservationTicket `
        -HarnessProcess $playbackHarnessProcess `
        -TicketPath $playbackDialogCloseVerificationTicketPath `
        -AllowedControlNames ($streamBaseControlNames + $playbackStreamProtocolControlNames + @(
            "verify-cancel.signal",
            "cancel-result.json",
            "verify-dialog-close.signal",
            "dialog-close-result.json"))
    $sourceDeletionDialogCloseNoMutationVerified = $true

    New-ExactPlaybackControlSignal -Path $playbackDeletionFaultArmSignalPath
    Wait-PlaybackDeletionFaultReadyTicket `
        -HarnessProcess $playbackHarnessProcess `
        -AllowedControlNames ($streamBaseControlNames + $playbackStreamProtocolControlNames + @(
            "verify-cancel.signal",
            "cancel-result.json",
            "verify-dialog-close.signal",
            "dialog-close-result.json",
            "arm-delete-failure.signal",
            "delete-failure-ready.json"))

    $deleteInstance = Start-PackagedPlaybackApplicationInstance
    $launchedProcess = $deleteInstance.Process
    $playbackActivationProcessId = $deleteInstance.ProcessId
    $playbackWindowHandle = $deleteInstance.WindowHandle
    $playbackAutomationRoot = $deleteInstance.Root
    $deleteContext = Get-PackagedPlaybackTargetContext -Instance $deleteInstance
    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $deleteContext.DeleteButton
    $confirmDeleteButtonElement = Wait-PackagedSourceDeletionDialogButton `
        -Process $launchedProcess `
        -Root $playbackAutomationRoot `
        -ExpectedButtonName "Delete"
    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $confirmDeleteButtonElement
    $pendingDeleteState = Wait-PackagedPendingSourceCleanupState -Instance $deleteInstance
    $sourceDeletionPendingFailureVerified = $true
    $sourceDeletionActivePlaybackDrainVerified = $true

    if (-not $launchedProcess.CloseMainWindow() -or
        -not $launchedProcess.WaitForExit(10000)) {
        throw "The packaged playback application did not close after pending source deletion."
    }
    $launchedProcess.Refresh()
    if ($null -eq $launchedProcess.ExitCode -or [int]$launchedProcess.ExitCode -ne 0) {
        throw "The packaged playback application returned a failure after pending source deletion."
    }
    $launchedProcess.Dispose()
    $launchedProcess = $null
    $deleteContext = $null
    $confirmDeleteButtonElement = $null
    $pendingDeleteState = $null
    $deleteInstance = $null
    $playbackWindowHandle = [IntPtr]::Zero
    $playbackAutomationRoot = $null

    $pendingRestartInstance = Start-PackagedPlaybackApplicationInstance
    $launchedProcess = $pendingRestartInstance.Process
    $playbackActivationProcessId = $pendingRestartInstance.ProcessId
    $playbackWindowHandle = $pendingRestartInstance.WindowHandle
    $playbackAutomationRoot = $pendingRestartInstance.Root
    $pendingRestartState = Wait-PackagedPendingSourceCleanupState `
        -Instance $pendingRestartInstance
    $sourceDeletionPendingRestartAdmissionBlockedVerified = $true

    New-ExactPlaybackControlSignal -Path $playbackPendingVerificationSignalPath
    $pendingTicket = Wait-PlaybackPendingDeletionTicket `
        -HarnessProcess $playbackHarnessProcess `
        -AllowedControlNames ($streamBaseControlNames + $playbackStreamProtocolControlNames + @(
            "verify-cancel.signal",
            "cancel-result.json",
            "verify-dialog-close.signal",
            "dialog-close-result.json",
            "arm-delete-failure.signal",
            "delete-failure-ready.json",
            "verify-pending.signal",
            "pending-result.json"))
    $sourceDeletionPendingCatalogPreserved = $pendingTicket.TargetCatalogPreserved
    $sourceDeletionPendingConfigurationRecordPreserved =
        $pendingTicket.ConfigurationRecordPreserved
    $sourceDeletionPendingTombstoneBindingVerified = $pendingTicket.TombstoneBindingPending
    $sourceDeletionPendingSiblingCatalogRetained = $pendingTicket.SiblingCatalogRetained
    $sourceDeletionFaultReleased = $pendingTicket.DeletionFaultReleased

    $retryPendingDeletionButtonElement = Get-RequiredAutomationElement `
        $playbackAutomationRoot `
        "CatalogRetryPendingDeletionButton" `
        ([System.Windows.Automation.ControlType]::Button) `
        "Retry pending source cleanup"
    Invoke-PackagedPlaybackControlButton `
        -Process $launchedProcess `
        -ButtonElement $retryPendingDeletionButtonElement
    Wait-PackagedDeletedSourceState -Instance $pendingRestartInstance
    $sourceDeletionManualRetryVerified = $true
    if (-not $launchedProcess.CloseMainWindow() -or
        -not $launchedProcess.WaitForExit(10000)) {
        throw "The retried packaged playback application did not close normally."
    }
    $launchedProcess.Refresh()
    if ($null -eq $launchedProcess.ExitCode -or [int]$launchedProcess.ExitCode -ne 0) {
        throw "The retried packaged playback application returned a failure."
    }
    $launchedProcess.Dispose()
    $launchedProcess = $null
    $pendingRestartInstance = $null
    $pendingRestartState = $null
    $retryPendingDeletionButtonElement = $null
    $playbackWindowHandle = [IntPtr]::Zero
    $playbackAutomationRoot = $null

    $restartInstance = Start-PackagedPlaybackApplicationInstance
    $launchedProcess = $restartInstance.Process
    $playbackActivationProcessId = $restartInstance.ProcessId
    $playbackWindowHandle = $restartInstance.WindowHandle
    $playbackAutomationRoot = $restartInstance.Root
    Wait-PackagedDeletedSourceState -Instance $restartInstance
    $sourceDeletionRestartNonAdmissionVerified = $true
    if (-not $launchedProcess.CloseMainWindow() -or
        -not $launchedProcess.WaitForExit(10000)) {
        throw "The restarted packaged playback application did not close normally."
    }
    $launchedProcess.Refresh()
    if ($null -eq $launchedProcess.ExitCode -or [int]$launchedProcess.ExitCode -ne 0) {
        throw "The restarted packaged playback application returned a failure."
    }
    $launchedProcess.Dispose()
    $launchedProcess = $null
    $restartInstance = $null
    $playbackWindowHandle = [IntPtr]::Zero
    $playbackAutomationRoot = $null

    New-ExactPlaybackControlSignal -Path $playbackStopSignalPath
    $playbackStopSignalCreated = $true
    if (-not $playbackHarnessProcess.WaitForExit(15000)) {
        throw "The playback acceptance harness did not stop before the deadline."
    }
    $playbackHarnessProcess.Refresh()
    if ([int]$playbackHarnessProcess.ExitCode -ne 0) {
        throw "The playback acceptance harness returned a failure result."
    }

    Assert-ExactPlaybackControlEntries `
        -AllowedNames ($streamBaseControlNames + $playbackStreamProtocolControlNames + @(
            "verify-cancel.signal",
            "cancel-result.json",
            "verify-dialog-close.signal",
            "dialog-close-result.json",
            "arm-delete-failure.signal",
            "delete-failure-ready.json",
            "verify-pending.signal",
            "pending-result.json",
            "result.json",
            "stop.signal"))
    $resultTicket = Read-StrictPlaybackJsonTicket `
        -Path $playbackResultPath `
        -AllowedProperties @(
            "ReadyPublished",
            "SeedCompleted",
            "StopObserved",
            "StoppedGracefully",
            "CertificateThumbprint",
            "RequestCount",
            "CompletedResponseCount",
            "CompletedBodyBytes",
            "FailureCount",
            "ChannelARequestCount",
            "ChannelBRequestCount",
            "CancelNoMutationVerified",
            "DialogCloseNoMutationVerified",
            "PendingDeletionVerified",
            "PendingTargetCatalogPreserved",
            "PendingConfigurationRecordPreserved",
            "PendingTombstoneBindingVerified",
            "PendingSiblingCatalogRetained",
            "DeletionFaultReleased",
            "TargetCatalogDeleted",
            "TargetProtectedRecordsDeleted",
            "TombstoneBindingCompleted",
            "SiblingCatalogRetained",
            "StreamRecoveryVerified",
            "StreamCancelVerified",
            "StreamNoLaterOpenVerified",
            "StreamNoLaterOpenObservationMilliseconds",
            "StreamNoLaterOpenRequestCountAtReady",
            "StreamNoLaterOpenRequestCountAfterObservation",
            "NormalStreamLastAssignedRequestOrdinal",
            "NormalStreamActiveRequestOrdinal",
            "NormalStreamCurrentHeldRequestCount",
            "NormalStreamPeakHeldRequestCount",
            "NormalStreamPeakActiveRequestCount",
            "NormalStreamOverlapViolationCount",
            "NormalStreamExpectedCompletionCount",
            "NormalStreamLastExpectedCompletionOrdinal",
            "NormalStreamExpectedAbortCount",
            "NormalStreamLastExpectedAbortOrdinal",
            "NormalStreamExpectedRejectCount",
            "NormalStreamLastExpectedRejectOrdinal",
            "NormalStreamClientDetachCount",
            "NormalStreamLastClientDetachOrdinal",
            "NormalStreamDisabledFallbackCount",
            "NormalStreamLastDisabledFallbackOrdinal",
            "NormalStreamCapacityRejectCount",
            "NormalStreamUnexpectedFailureCount",
            "NormalStreamLastUnexpectedFailureOrdinal",
            "FaultStreamHolding",
            "FaultStreamLastAssignedRequestOrdinal",
            "FaultStreamActiveRequestOrdinal",
            "FaultStreamCurrentHeldRequestCount",
            "FaultStreamPeakHeldRequestCount",
            "FaultStreamPeakActiveRequestCount",
            "FaultStreamOverlapViolationCount",
            "FaultStreamExpectedCompletionCount",
            "FaultStreamLastExpectedCompletionOrdinal",
            "FaultStreamExpectedAbortCount",
            "FaultStreamLastExpectedAbortOrdinal",
            "FaultStreamExpectedRejectCount",
            "FaultStreamLastExpectedRejectOrdinal",
            "FaultStreamClientDetachCount",
            "FaultStreamLastClientDetachOrdinal",
            "FaultStreamDisabledFallbackCount",
            "FaultStreamLastDisabledFallbackOrdinal",
            "FaultStreamCapacityRejectCount",
            "FaultStreamUnexpectedFailureCount",
            "FaultStreamLastUnexpectedFailureOrdinal")
    $streamBooleanResultProperties = @(
        "StreamRecoveryVerified",
        "StreamCancelVerified",
        "StreamNoLaterOpenVerified",
        "FaultStreamHolding")
    foreach ($propertyName in $streamBooleanResultProperties) {
        if ($resultTicket.$propertyName -isnot [bool] -or
            -not $resultTicket.$propertyName) {
            throw "The playback stream Boolean result is invalid."
        }
    }
    $streamIntegralResultProperties = @(
        "StreamNoLaterOpenObservationMilliseconds",
        "StreamNoLaterOpenRequestCountAtReady",
        "StreamNoLaterOpenRequestCountAfterObservation",
        "NormalStreamLastAssignedRequestOrdinal",
        "NormalStreamActiveRequestOrdinal",
        "NormalStreamCurrentHeldRequestCount",
        "NormalStreamPeakHeldRequestCount",
        "NormalStreamPeakActiveRequestCount",
        "NormalStreamOverlapViolationCount",
        "NormalStreamExpectedCompletionCount",
        "NormalStreamLastExpectedCompletionOrdinal",
        "NormalStreamExpectedAbortCount",
        "NormalStreamLastExpectedAbortOrdinal",
        "NormalStreamExpectedRejectCount",
        "NormalStreamLastExpectedRejectOrdinal",
        "NormalStreamClientDetachCount",
        "NormalStreamLastClientDetachOrdinal",
        "NormalStreamDisabledFallbackCount",
        "NormalStreamLastDisabledFallbackOrdinal",
        "NormalStreamCapacityRejectCount",
        "NormalStreamUnexpectedFailureCount",
        "NormalStreamLastUnexpectedFailureOrdinal",
        "FaultStreamLastAssignedRequestOrdinal",
        "FaultStreamActiveRequestOrdinal",
        "FaultStreamCurrentHeldRequestCount",
        "FaultStreamPeakHeldRequestCount",
        "FaultStreamPeakActiveRequestCount",
        "FaultStreamOverlapViolationCount",
        "FaultStreamExpectedCompletionCount",
        "FaultStreamLastExpectedCompletionOrdinal",
        "FaultStreamExpectedAbortCount",
        "FaultStreamLastExpectedAbortOrdinal",
        "FaultStreamExpectedRejectCount",
        "FaultStreamLastExpectedRejectOrdinal",
        "FaultStreamClientDetachCount",
        "FaultStreamLastClientDetachOrdinal",
        "FaultStreamDisabledFallbackCount",
        "FaultStreamLastDisabledFallbackOrdinal",
        "FaultStreamCapacityRejectCount",
        "FaultStreamUnexpectedFailureCount",
        "FaultStreamLastUnexpectedFailureOrdinal")
    foreach ($propertyName in $streamIntegralResultProperties) {
        if ($resultTicket.$propertyName -isnot [int] -and
            $resultTicket.$propertyName -isnot [long]) {
            throw "The playback stream scalar result is invalid."
        }
    }
    $faultStreamFinalExpected = [ordered]@{
        FaultStreamLastAssignedRequestOrdinal = 3L
        FaultStreamActiveRequestOrdinal = 0L
        FaultStreamCurrentHeldRequestCount = 0L
        FaultStreamPeakHeldRequestCount = 1L
        FaultStreamPeakActiveRequestCount = 1L
        FaultStreamOverlapViolationCount = 0L
        FaultStreamExpectedCompletionCount = 2L
        FaultStreamLastExpectedCompletionOrdinal = 2L
        FaultStreamExpectedAbortCount = 0L
        FaultStreamLastExpectedAbortOrdinal = 0L
        FaultStreamExpectedRejectCount = 0L
        FaultStreamLastExpectedRejectOrdinal = 0L
        FaultStreamClientDetachCount = 1L
        FaultStreamLastClientDetachOrdinal = 3L
        FaultStreamDisabledFallbackCount = 0L
        FaultStreamLastDisabledFallbackOrdinal = 0L
        FaultStreamCapacityRejectCount = 0L
        FaultStreamUnexpectedFailureCount = 0L
        FaultStreamLastUnexpectedFailureOrdinal = 0L
    }
    foreach ($propertyName in $faultStreamFinalExpected.Keys) {
        if ([long]$resultTicket.$propertyName -ne
            [long]$faultStreamFinalExpected[$propertyName]) {
            throw "The final playback fault-stream accounting is invalid."
        }
    }
    if ($resultTicket.ReadyPublished -isnot [bool] -or
        $resultTicket.SeedCompleted -isnot [bool] -or
        $resultTicket.StopObserved -isnot [bool] -or
        $resultTicket.StoppedGracefully -isnot [bool] -or
        $resultTicket.CertificateThumbprint -isnot [string] -or
        $resultTicket.RequestCount -isnot [int] -or
        $resultTicket.CompletedResponseCount -isnot [int] -or
        ($resultTicket.CompletedBodyBytes -isnot [int] -and
            $resultTicket.CompletedBodyBytes -isnot [long]) -or
        $resultTicket.FailureCount -isnot [int] -or
        $resultTicket.ChannelARequestCount -isnot [int] -or
        $resultTicket.ChannelBRequestCount -isnot [int] -or
        $resultTicket.CancelNoMutationVerified -isnot [bool] -or
        $resultTicket.DialogCloseNoMutationVerified -isnot [bool] -or
        $resultTicket.PendingDeletionVerified -isnot [bool] -or
        $resultTicket.PendingTargetCatalogPreserved -isnot [bool] -or
        $resultTicket.PendingConfigurationRecordPreserved -isnot [bool] -or
        $resultTicket.PendingTombstoneBindingVerified -isnot [bool] -or
        $resultTicket.PendingSiblingCatalogRetained -isnot [bool] -or
        $resultTicket.DeletionFaultReleased -isnot [bool] -or
        $resultTicket.TargetCatalogDeleted -isnot [bool] -or
        $resultTicket.TargetProtectedRecordsDeleted -isnot [bool] -or
        $resultTicket.TombstoneBindingCompleted -isnot [bool] -or
        $resultTicket.SiblingCatalogRetained -isnot [bool] -or
        -not $resultTicket.ReadyPublished -or
        -not $resultTicket.SeedCompleted -or
        -not $resultTicket.StopObserved -or
        -not $resultTicket.StoppedGracefully -or
        $resultTicket.CertificateThumbprint -cne
            $playbackLoopbackCertificateThumbprint -or
        [int]$resultTicket.RequestCount -lt 27 -or
        [int]$resultTicket.CompletedResponseCount +
            [int]$resultTicket.NormalStreamClientDetachCount +
            [int]$resultTicket.FaultStreamClientDetachCount -ne
            [int]$resultTicket.RequestCount -or
        [long]$resultTicket.CompletedBodyBytes -le 0 -or
        [int]$resultTicket.FailureCount -ne 0 -or
        [int]$resultTicket.ChannelARequestCount -le 0 -or
        [int]$resultTicket.ChannelBRequestCount -le 0 -or
        -not $resultTicket.CancelNoMutationVerified -or
        -not $resultTicket.DialogCloseNoMutationVerified -or
        -not $resultTicket.PendingDeletionVerified -or
        -not $resultTicket.PendingTargetCatalogPreserved -or
        -not $resultTicket.PendingConfigurationRecordPreserved -or
        -not $resultTicket.PendingTombstoneBindingVerified -or
        -not $resultTicket.PendingSiblingCatalogRetained -or
        -not $resultTicket.DeletionFaultReleased -or
        -not $resultTicket.TargetCatalogDeleted -or
        -not $resultTicket.TargetProtectedRecordsDeleted -or
        -not $resultTicket.TombstoneBindingCompleted -or
        -not $resultTicket.SiblingCatalogRetained -or
        [long]$resultTicket.StreamNoLaterOpenObservationMilliseconds -lt 31000 -or
        [long]$resultTicket.StreamNoLaterOpenRequestCountAtReady -ne
            [long]$resultTicket.StreamNoLaterOpenRequestCountAfterObservation -or
        [long]$resultTicket.StreamNoLaterOpenRequestCountAtReady -ne
            [long]$streamCancelResultTicket.RequestCountAtReady -or
        [long]$resultTicket.NormalStreamLastAssignedRequestOrdinal -le 0 -or
        [long]$resultTicket.NormalStreamLastAssignedRequestOrdinal -gt 64 -or
        [long]$resultTicket.NormalStreamActiveRequestOrdinal -ne 0 -or
        [int]$resultTicket.NormalStreamCurrentHeldRequestCount -ne 0 -or
        [int]$resultTicket.NormalStreamPeakActiveRequestCount -ne 1 -or
        [int]$resultTicket.NormalStreamOverlapViolationCount -ne 0 -or
        [int]$resultTicket.NormalStreamExpectedCompletionCount -ne 0 -or
        [long]$resultTicket.NormalStreamLastExpectedCompletionOrdinal -ne 0 -or
        [int]$resultTicket.NormalStreamExpectedAbortCount -ne 0 -or
        [long]$resultTicket.NormalStreamLastExpectedAbortOrdinal -ne 0 -or
        [int]$resultTicket.NormalStreamExpectedRejectCount -ne 0 -or
        [long]$resultTicket.NormalStreamLastExpectedRejectOrdinal -ne 0 -or
        [int]$resultTicket.NormalStreamClientDetachCount -ne
            [long]$resultTicket.NormalStreamLastAssignedRequestOrdinal -or
        [long]$resultTicket.NormalStreamLastClientDetachOrdinal -ne
            [long]$resultTicket.NormalStreamLastAssignedRequestOrdinal -or
        [int]$resultTicket.NormalStreamDisabledFallbackCount -ne 0 -or
        [long]$resultTicket.NormalStreamLastDisabledFallbackOrdinal -ne 0 -or
        [int]$resultTicket.NormalStreamCapacityRejectCount -ne 0 -or
        [int]$resultTicket.NormalStreamUnexpectedFailureCount -ne 0 -or
        [long]$resultTicket.NormalStreamLastUnexpectedFailureOrdinal -ne 0 -or
        ([int]$resultTicket.ChannelARequestCount +
            [int]$resultTicket.ChannelBRequestCount) -ne
            [int]$resultTicket.RequestCount) {
        throw "The playback acceptance result ticket is invalid."
    }

    $playbackUiRequestCount = [int]$resultTicket.RequestCount
    $playbackUiCompletedResponseCount = [int]$resultTicket.CompletedResponseCount
    $playbackUiCompletedBodyBytes = [long]$resultTicket.CompletedBodyBytes
    $playbackChannelARequestCount = [int]$resultTicket.ChannelARequestCount
    $playbackChannelBRequestCount = [int]$resultTicket.ChannelBRequestCount
    $playbackReconnectRecoveryVerified = [bool]$resultTicket.StreamRecoveryVerified
    $playbackReconnectCancelVerified = [bool]$resultTicket.StreamCancelVerified
    $playbackReconnectNoLaterOpenVerified = [bool]$resultTicket.StreamNoLaterOpenVerified
    $playbackReconnectNoLaterOpenObservationMilliseconds =
        [long]$resultTicket.StreamNoLaterOpenObservationMilliseconds
    $playbackReconnectNoLaterOpenRequestCountAtReady =
        [int]$resultTicket.StreamNoLaterOpenRequestCountAtReady
    $playbackReconnectNoLaterOpenRequestCountAfterObservation =
        [int]$resultTicket.StreamNoLaterOpenRequestCountAfterObservation
    $normalStreamLastAssignedRequestOrdinal =
        [long]$resultTicket.NormalStreamLastAssignedRequestOrdinal
    $normalStreamClientDetachCount = [int]$resultTicket.NormalStreamClientDetachCount
    $faultStreamExpectedCompletionCount =
        [int]$resultTicket.FaultStreamExpectedCompletionCount
    $faultStreamClientDetachCount = [int]$resultTicket.FaultStreamClientDetachCount
    $sourceDeletionPendingCatalogPreserved = $resultTicket.PendingTargetCatalogPreserved
    $sourceDeletionPendingConfigurationRecordPreserved =
        $resultTicket.PendingConfigurationRecordPreserved
    $sourceDeletionPendingTombstoneBindingVerified =
        $resultTicket.PendingTombstoneBindingVerified
    $sourceDeletionPendingSiblingCatalogRetained = $resultTicket.PendingSiblingCatalogRetained
    $sourceDeletionFaultReleased = $resultTicket.DeletionFaultReleased
    $sourceDeletionTargetCatalogDeleted = $resultTicket.TargetCatalogDeleted
    $sourceDeletionProtectedRecordsDeleted = $resultTicket.TargetProtectedRecordsDeleted
    $sourceDeletionTombstoneBindingCompleted = $resultTicket.TombstoneBindingCompleted
    $sourceDeletionSiblingCatalogRetained = $resultTicket.SiblingCatalogRetained
    $playbackUiAcceptanceVerified = $true

    if ($RunWack) {
        $wackDevelopmentIdentityResult = Invoke-WindowsWackDevelopmentIdentityPreflight `
            -PackageFullName $installedPackageFullName `
            -PackageSha256 $packageSha256 `
            -ArtifactRoot $artifactRoot
        if ($wackDevelopmentIdentityResult.SchemaVersion -ne 1 -or
            $wackDevelopmentIdentityResult.Scope -cne
                "DevelopmentIdentityWackPreflightOnly" -or
            $wackDevelopmentIdentityResult.ClosedBlocker -cne "None" -or
            $wackDevelopmentIdentityResult.ReleaseReady -ne $false -or
            $wackDevelopmentIdentityResult.PackageSha256 -cne $packageSha256 -or
            $wackDevelopmentIdentityResult.OverallResult -cne "PASS" -or
            $wackDevelopmentIdentityResult.PartialRun -cne "FALSE") {
            throw "The development-identity WACK preflight result is invalid."
        }
    }

    $packageInstallRootAuditCompletionAttempted = $true
    $packageInstallRootAuditResult = Complete-WindowsPackageInstallRootAudit `
        -Audit $packageInstallRootAudit
    Assert-ExactPackageInstallRootAuditResult -Result $packageInstallRootAuditResult
    if ($null -ne $preResetPackageInstallRootAuditResult) {
        $packageInstallRootResetBoundaryEquivalent =
            $preResetPackageInstallRootAuditResult.FinalEntryCount -eq
                $packageInstallRootAuditResult.BaselineEntryCount -and
            $preResetPackageInstallRootAuditResult.FinalFileCount -eq
                $packageInstallRootAuditResult.BaselineFileCount -and
            $preResetPackageInstallRootAuditResult.FinalTotalBytes -eq
                $packageInstallRootAuditResult.BaselineTotalBytes -and
            $preResetPackageInstallRootAuditResult.FinalManifestSha256 -ceq
                $packageInstallRootAuditResult.BaselineManifestSha256
    }
    if ($packageInstallRootAuditSegmentCount -ne 2 -or
        $null -eq $preResetPackageInstallRootAuditResult -or
        -not $packageInstallRootResetBoundaryEquivalent) {
        throw "The packaged install-root runtime audit segmentation is incomplete."
    }

    if ($EmitM16FinalArtifactSurfaces) {
        if ($packageSbomResult.ApplicationPackageSha256 -cne $packageSha256) {
            throw "The M16 exact package does not match its package-bound SBOM."
        }

        $ownedAppDataSurfaceRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $env:LOCALAPPDATA "Packages\$packageFamilyName"))
        if (-not (Test-Path -LiteralPath $ownedAppDataSurfaceRoot -PathType Container) -or
            (([System.IO.File]::GetAttributes($ownedAppDataSurfaceRoot) -band
                    [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "The exact package app-data surface is unavailable or unsafe."
        }

        $exactPackageSurfaceRoot = [System.IO.Path]::GetFullPath(
            [System.IO.Path]::Combine($m16CaptureRoot, "exact-package"))
        $expectedExactPackageSurfaceRoot = [System.IO.Path]::GetFullPath(
            [System.IO.Path]::Combine(
                [System.IO.Path]::GetFullPath($m16CaptureRoot),
                "exact-package"))
        if (-not $exactPackageSurfaceRoot.Equals(
                $expectedExactPackageSurfaceRoot,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            (Test-Path -LiteralPath $exactPackageSurfaceRoot) -or
            (($packages[0].Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "The M16 exact-package staging surface is invalid."
        }
        [System.IO.Directory]::CreateDirectory($exactPackageSurfaceRoot) | Out-Null
        if (([System.IO.File]::GetAttributes($exactPackageSurfaceRoot) -band
                [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The M16 exact-package staging surface is unsafe."
        }

        $stagedPackagePath = [System.IO.Path]::Combine(
            $exactPackageSurfaceRoot,
            "package.msix")
        Copy-Item -LiteralPath $packages[0].FullName -Destination $stagedPackagePath
        $stagedPackageLock = $null
        try {
            $stagedPackageLock = [System.IO.File]::Open(
                $stagedPackagePath,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::Read)
            if (-not (Test-Path -LiteralPath $stagedPackagePath -PathType Leaf) -or
                (([System.IO.File]::GetAttributes($stagedPackagePath) -band
                        [System.IO.FileAttributes]::ReparsePoint) -ne 0) -or
                (Get-FileHash -LiteralPath $stagedPackagePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne
                    $packageSha256) {
                throw "The staged M16 package is not bound to the signed package bytes."
            }
            Expand-MsixForInspection `
                -PackagePath $stagedPackagePath `
                -DestinationPath ([System.IO.Path]::Combine(
                    $exactPackageSurfaceRoot,
                    "expanded"))

            $supportArtifactSurface = New-M16ReleaseAcceptanceSupportArtifact `
                -PackageSha256 $packageSha256 `
                -DestinationDirectory ([System.IO.Path]::Combine(
                    $m16CaptureRoot,
                    "support-artifact"))

            $ownedAppDataSurfaceReport = Invoke-M16ReleaseSurfaceScan `
                -SurfaceId "owned-app-data" `
                -RootPath $ownedAppDataSurfaceRoot
            $exactPackageSurfaceReport = Invoke-M16ReleaseSurfaceScan `
                -SurfaceId "exact-package" `
                -RootPath $exactPackageSurfaceRoot
            $supportArtifactSurfaceReport = Invoke-M16ReleaseSurfaceScan `
                -SurfaceId "support-artifact" `
                -RootPath $supportArtifactSurface.RootPath
            $m16PostScanPackageSha256 = (Get-FileHash `
                    -LiteralPath $stagedPackagePath `
                    -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($m16PostScanPackageSha256 -cne $packageSha256) {
                throw "The staged M16 package changed during its exact-package scan."
            }
            $supportArtifactAfterScan = Assert-M16ReleaseAcceptanceSupportArtifact `
                -Path $supportArtifactSurface.FilePath `
                -ExpectedArtifact $supportArtifactSurface.ExpectedArtifact `
                -ExpectedSha256 $supportArtifactSurface.Sha256
            if ($supportArtifactAfterScan.Sha256 -cne $supportArtifactSurface.Sha256) {
                throw "The M16 support artifact changed during its scan."
            }
        }
        finally {
            if ($null -ne $stagedPackageLock) {
                $stagedPackageLock.Dispose()
            }
        }
        $m16SurfaceReports = @(
            $ownedAppDataSurfaceReport,
            $exactPackageSurfaceReport,
            $supportArtifactSurfaceReport)

        [long]$m16AggregateEntryCount = 0
        [long]$m16AggregateFileBytes = 0
        foreach ($surfaceReport in $m16SurfaceReports) {
            $m16AggregateEntryCount +=
                [long]$surfaceReport.FileCount + [long]$surfaceReport.DirectoryCount
            $m16AggregateFileBytes += [long]$surfaceReport.TotalFileBytes
        }
        if ($m16SurfaceReports.Count -ne 3 -or
            $m16SurfaceReports[0].SurfaceId -cne "owned-app-data" -or
            $m16SurfaceReports[1].SurfaceId -cne "exact-package" -or
            $m16SurfaceReports[2].SurfaceId -cne "support-artifact" -or
            $m16AggregateEntryCount -gt 25000 -or
            $m16AggregateFileBytes -gt 8589934592) {
            throw "The M16 package-side surface aggregate is invalid or exceeds its fixed bounds."
        }
        Assert-M16RepositoryStable -ExpectedCommit $m16CommitSha
    }

    $packageFileName = $packages[0].Name
    Remove-ExactDevelopmentPackage

    if (Get-AppxPackage -Name $expectedName -ErrorAction SilentlyContinue) {
        throw "The development package is still registered after uninstall."
    }
    $installedPackage = $null
    $installAttempted = $false

    $appDataPath = Join-Path $env:LOCALAPPDATA "Packages\$packageFamilyName"
    $cleanupDeadline = (Get-Date).AddSeconds(10)
    while ((Test-Path -LiteralPath $appDataPath) -and (Get-Date) -lt $cleanupDeadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Test-Path -LiteralPath $appDataPath) {
        throw "Package app-data remains after uninstall."
    }
    if ($windowsAppRuntimeDisposition -notin @("ReusedRegisteredFramework", "InstalledLockedDependency")) {
        throw "The Windows App Runtime installation disposition is invalid."
    }

    $successEvidence = [ordered]@{
        RunId             = $runId
        CompletedAt       = (Get-Date).ToUniversalTime().ToString("O")
        Configuration     = $Configuration
        DotNetSdk         = $actualSdk
        PackageFile       = $packageFileName
        PackageSha256     = $packageSha256
        PackageName       = $expectedName
        PackagePublisher  = $expectedPublisher
        PackageFamilyName = $packageFamilyName
        Architecture      = "x64"
        Capabilities      = @("runFullTrust")
        SignatureStatus   = $signature.Status.ToString()
        RuntimeDependencySignatureStatus = $runtimeDependencySignature.Status.ToString()
        WindowsAppRuntimeDisposition = $windowsAppRuntimeDisposition
        PayloadLeakGate   = $true
        PackageSbomSchemaVersion = $packageSbomResult.SchemaVersion
        PackageSbomFormat = $packageSbomResult.SbomFormat
        PackageSbomFile = $packageSbomResult.SbomFile
        PackageSbomSha256 = $packageSbomResult.SbomSha256
        PackageSbomToolVersion = $packageSbomResult.ToolVersion
        PackageSbomOfficialValidationPassed = $packageSbomResult.OfficialValidationPassed
        PackageSbomStrictValidationPassed = $packageSbomResult.StrictValidationPassed
        PackageSbomProductionInputSetSha256 = $packageSbomResult.ProductionInputSetSha256
        PackageSbomApplicationPackageSha256 = $packageSbomResult.ApplicationPackageSha256
        PackageSbomRuntimePackageSha256 = $packageSbomResult.RuntimePackageSha256
        PackageSbomFileCount = $packageSbomResult.FileCount
        PackageSbomComponentCount = $packageSbomResult.ComponentCount
        PackageSbomPackageCount = $packageSbomResult.PackageCount
        PackageSbomRelationshipCount = $packageSbomResult.RelationshipCount
        PackageSbomBlockerDisposition = $packageSbomResult.BlockerDisposition
        PackageInstallRootAuditSegmentCount = $packageInstallRootAuditSegmentCount
        PackageInstallRootResetBoundaryInventoryEquivalent =
            $packageInstallRootResetBoundaryEquivalent
        PackageInstallRootPreResetBaselineManifestSha256 =
            $preResetPackageInstallRootAuditResult.BaselineManifestSha256
        PackageInstallRootPreResetFinalManifestSha256 =
            $preResetPackageInstallRootAuditResult.FinalManifestSha256
        PackageInstallRootPreResetMutationEventCount =
            $preResetPackageInstallRootAuditResult.MutationEventCount
        PackageInstallRootPreResetWatcherOverflow =
            $preResetPackageInstallRootAuditResult.WatcherOverflow
        PackageInstallRootPreResetInventoryEquivalent =
            $preResetPackageInstallRootAuditResult.SnapshotEquivalent
        PackageInstallRootPreResetAuditPassed =
            $preResetPackageInstallRootAuditResult.RuntimeWriteAuditPassed
        PackageInstallRootAuditSchemaVersion = $packageInstallRootAuditResult.SchemaVersion
        PackageInstallRootAuditScope = $packageInstallRootAuditResult.Scope
        PackageInstallRootAuditExcludedEntryCount =
            $packageInstallRootAuditResult.ExcludedEntryCount
        PackageInstallRootBaselineEntryCount =
            $packageInstallRootAuditResult.BaselineEntryCount
        PackageInstallRootBaselineFileCount =
            $packageInstallRootAuditResult.BaselineFileCount
        PackageInstallRootBaselineTotalBytes =
            $packageInstallRootAuditResult.BaselineTotalBytes
        PackageInstallRootBaselineManifestSha256 =
            $packageInstallRootAuditResult.BaselineManifestSha256
        PackageInstallRootFinalEntryCount =
            $packageInstallRootAuditResult.FinalEntryCount
        PackageInstallRootFinalFileCount =
            $packageInstallRootAuditResult.FinalFileCount
        PackageInstallRootFinalTotalBytes =
            $packageInstallRootAuditResult.FinalTotalBytes
        PackageInstallRootFinalManifestSha256 =
            $packageInstallRootAuditResult.FinalManifestSha256
        PackageInstallRootMutationEventCount =
            $packageInstallRootAuditResult.MutationEventCount
        PackageInstallRootWatcherOverflow =
            $packageInstallRootAuditResult.WatcherOverflow
        PackageInstallRootPrePostInventoryEquivalent =
            $packageInstallRootAuditResult.SnapshotEquivalent
        PackageInstallRootAuditPassed =
            $packageInstallRootAuditResult.RuntimeWriteAuditPassed
        ProtectedStoreDirectoryInitialized = $protectedStoreDirectoryInitialized
        CleanInstallOnboardingVerified = $cleanInstallOnboardingVerified
        CleanInstallOnboardingAuthorizationVerified =
            $cleanInstallOnboardingAuthorizationVerified
        CleanInstallOnboardingSourceVerified = $cleanInstallOnboardingSourceVerified
        CleanInstallOnboardingChannelsVerified = $cleanInstallOnboardingChannelsVerified
        CleanInstallOnboardingResetVerified = $cleanInstallOnboardingResetVerified
        CleanInstallOnboardingRequestCount = $cleanInstallOnboardingRequestCount
        CatalogUiaContractVerified = $catalogUiaContractVerified
        CatalogKeyboardFocusOrderVerified = $catalogKeyboardFocusOrderVerified
        Catalog50kSeedVerified = $catalog50kSeedVerified
        CatalogTraceMarkersRequested = $catalogTraceMarkersRequested
        CatalogTraceMarkerBeginEmitted = $catalogTraceMarkerBeginEmitted
        CatalogTraceMarkerEndEmitted = $catalogTraceMarkerEndEmitted
        CatalogTraceMarkerCount = $catalogTraceMarkerCount
        CatalogTraceMarkerProcessId = $catalogTraceMarkerProcessId
        CatalogRealizedContainerBoundVerified = $catalogRealizedContainerBoundVerified
        CatalogRealizedContainerCount = $catalogRealizedContainerCount
        CatalogInputResponseP95Milliseconds = [Math]::Round($catalogInputResponseP95Milliseconds, 3)
        CatalogDwmFrameP95Milliseconds = [Math]::Round($catalogFrameP95Milliseconds, 3)
        CatalogDwmFrameMaximumMilliseconds = [Math]::Round($catalogFrameMaximumMilliseconds, 3)
        CatalogDwmDroppedFramePercent = [Math]::Round($catalogDroppedFramePercent, 3)
        CatalogDwmFrameIntervalCount = $catalogFrameIntervalCount
        CatalogDwmExactFrameIntervalCount = $catalogExactFrameIntervalCount
        CatalogDwmMultiRefreshSegmentCount = $catalogMultiRefreshSegmentCount
        CatalogUiThreadResponsivenessProxyVerified =
            $catalogUiThreadResponsivenessProxyVerified
        CatalogUiThreadResponsivenessProxyKind =
            $catalogUiThreadResponsivenessProxyKind
        CatalogUiThreadResponsivenessProxyTimeoutMilliseconds =
            $catalogUiThreadResponsivenessProxyTimeoutMilliseconds
        CatalogUiThreadResponsivenessProxySampleLimit =
            $catalogUiThreadResponsivenessProxySampleLimit
        CatalogUiThreadResponsivenessProxySampleCount =
            $catalogUiThreadResponsivenessProxySampleCount
        CatalogUiThreadResponsivenessProxyTimeoutCount =
            $catalogUiThreadResponsivenessProxyTimeoutCount
        CatalogUiThreadResponsivenessProxyOverBudgetCount =
            $catalogUiThreadResponsivenessProxyOverBudgetCount
        CatalogUiThreadResponsivenessProxyP95Milliseconds = [Math]::Round(
            $catalogUiThreadResponsivenessProxyP95Milliseconds,
            3)
        CatalogUiThreadResponsivenessProxyMaximumMilliseconds = [Math]::Round(
            $catalogUiThreadResponsivenessProxyMaximumMilliseconds,
            3)
        CatalogUiThreadResponsivenessProxyRawSamplesMilliseconds =
            $catalogUiThreadResponsivenessProxyRawSamplesMilliseconds
        CatalogPlayerOffStateVerified = $catalogPlayerOffStateVerified
        CatalogPlayerOffSteadyWorkingSetVerified =
            $catalogPlayerOffSteadyWorkingSetVerified
        CatalogPlayerOffSteadyWorkingSetProcessAliveVerified =
            $catalogPlayerOffSteadyWorkingSetProcessAliveVerified
        CatalogPlayerOffSteadyWorkingSetBudgetBytes =
            $catalogPlayerOffSteadyWorkingSetBudgetBytes
        CatalogPlayerOffSteadyWorkingSetSettleMilliseconds =
            $catalogPlayerOffSteadyWorkingSetSettleMilliseconds
        CatalogPlayerOffSteadyWorkingSetSettleElapsedMilliseconds = [Math]::Round(
            $catalogPlayerOffSteadyWorkingSetSettleElapsedMilliseconds,
            3)
        CatalogPlayerOffSteadyWorkingSetSampleIntervalMilliseconds =
            $catalogPlayerOffSteadyWorkingSetSampleIntervalMilliseconds
        CatalogPlayerOffSteadyWorkingSetSampleLimit =
            $catalogPlayerOffSteadyWorkingSetSampleLimit
        CatalogPlayerOffSteadyWorkingSetSamplingTargetMilliseconds =
            $catalogPlayerOffSteadyWorkingSetSamplingTargetMilliseconds
        CatalogPlayerOffSteadyWorkingSetSampleCount =
            $catalogPlayerOffSteadyWorkingSetSampleCount
        CatalogPlayerOffSteadyWorkingSetSamplingElapsedMilliseconds = [Math]::Round(
            $catalogPlayerOffSteadyWorkingSetSamplingElapsedMilliseconds,
            3)
        CatalogPlayerOffSteadyWorkingSetMinimumBytes =
            $catalogPlayerOffSteadyWorkingSetMinimumBytes
        CatalogPlayerOffSteadyWorkingSetAverageBytes = [Math]::Round(
            $catalogPlayerOffSteadyWorkingSetAverageBytes,
            3)
        CatalogPlayerOffSteadyWorkingSetMaximumBytes =
            $catalogPlayerOffSteadyWorkingSetMaximumBytes
        CatalogPlayerOffSteadyWorkingSetRawSamplesBytes =
            $catalogPlayerOffSteadyWorkingSetRawSamplesBytes
        PlaybackUiAcceptanceVerified = $playbackUiAcceptanceVerified
        PlaybackVolumeControlVerified = $playbackVolumeControlVerified
        PlaybackMuteControlVerified = $playbackMuteControlVerified
        PlaybackAspectControlVerified = $playbackAspectControlVerified
        PlaybackFullscreenEnterVerified = $playbackFullscreenEnterVerified
        PlaybackFullscreenExitVerified = $playbackFullscreenExitVerified
        PlaybackFullscreenFocusRestored = $playbackFullscreenFocusRestored
        PlaybackRapidSwitchVerified = $playbackRapidSwitchVerified
        PlaybackRapidSwitchCount = $playbackRapidSwitchCount
        PlaybackRapidSwitchP95Milliseconds = [Math]::Round(
            $playbackRapidSwitchP95Milliseconds,
            3)
        PlaybackRapidSwitchMaximumMilliseconds = [Math]::Round(
            $playbackRapidSwitchMaximumMilliseconds,
            3)
        PlaybackSurfaceBoundsVerified = $playbackSurfaceBoundsVerified
        PlaybackWindowResizeVerified = $playbackWindowResizeVerified
        PlaybackWindowResizeCount = $playbackWindowResizeCount
        PlaybackWindowMinimizeVerified = $playbackWindowMinimizeVerified
        PlaybackWindowRestoreVerified = $playbackWindowRestoreVerified
        PlaybackWindowStatePreserved = $playbackWindowStatePreserved
        PlaybackResourceWarmupVerified = $playbackResourceWarmupVerified
        PlaybackResourceSnapshotVerified = $playbackResourceSnapshotVerified
        PlaybackResourceBudgetVerified = $playbackResourceBudgetVerified
        PlaybackBaselinePrivateBytes = $playbackBaselinePrivateBytes
        PlaybackFinalPrivateBytes = $playbackFinalPrivateBytes
        PlaybackPrivateBytesDelta = $playbackPrivateBytesDelta
        PlaybackBaselineWorkingSetBytes = $playbackBaselineWorkingSetBytes
        PlaybackFinalWorkingSetBytes = $playbackFinalWorkingSetBytes
        PlaybackWorkingSetBytesDelta = $playbackWorkingSetBytesDelta
        PlaybackBaselineHandleCount = $playbackBaselineHandleCount
        PlaybackFinalHandleCount = $playbackFinalHandleCount
        PlaybackHandleCountDelta = $playbackHandleCountDelta
        PlaybackBaselineThreadCount = $playbackBaselineThreadCount
        PlaybackFinalThreadCount = $playbackFinalThreadCount
        PlaybackThreadCountDelta = $playbackThreadCountDelta
        PlaybackActiveCloseVerified = $playbackActiveCloseVerified
        PlaybackReconnectRecoveryVerified = $playbackReconnectRecoveryVerified
        PlaybackReconnectCancelVerified = $playbackReconnectCancelVerified
        PlaybackReconnectCancelBudgetMilliseconds = $playbackReconnectCancelBudgetMilliseconds
        PlaybackReconnectCancelElapsedMilliseconds = [Math]::Round(
            $playbackReconnectCancelElapsedMilliseconds,
            3)
        PlaybackReconnectNoLaterOpenVerified = $playbackReconnectNoLaterOpenVerified
        PlaybackReconnectNoLaterOpenObservationMilliseconds =
            $playbackReconnectNoLaterOpenObservationMilliseconds
        PlaybackReconnectNoLaterOpenRequestCountAtReady =
            $playbackReconnectNoLaterOpenRequestCountAtReady
        PlaybackReconnectNoLaterOpenRequestCountAfterObservation =
            $playbackReconnectNoLaterOpenRequestCountAfterObservation
        NormalStreamLastAssignedRequestOrdinal = $normalStreamLastAssignedRequestOrdinal
        NormalStreamPeakHeldRequestCount = [int]$resultTicket.NormalStreamPeakHeldRequestCount
        NormalStreamClientDetachCount = $normalStreamClientDetachCount
        NormalStreamCapacityRejectCount = [int]$resultTicket.NormalStreamCapacityRejectCount
        NormalStreamUnexpectedFailureCount = [int]$resultTicket.NormalStreamUnexpectedFailureCount
        FaultStreamHolding = [bool]$resultTicket.FaultStreamHolding
        FaultStreamLastAssignedRequestOrdinal =
            [long]$resultTicket.FaultStreamLastAssignedRequestOrdinal
        FaultStreamExpectedCompletionCount = $faultStreamExpectedCompletionCount
        FaultStreamClientDetachCount = $faultStreamClientDetachCount
        FaultStreamCapacityRejectCount = [int]$resultTicket.FaultStreamCapacityRejectCount
        FaultStreamUnexpectedFailureCount = [int]$resultTicket.FaultStreamUnexpectedFailureCount
        SourceDeletionCancelNoMutationVerified = $sourceDeletionCancelNoMutationVerified
        SourceDeletionDialogCloseNoMutationVerified = $sourceDeletionDialogCloseNoMutationVerified
        SourceDeletionPendingFailureVerified = $sourceDeletionPendingFailureVerified
        SourceDeletionPendingRestartAdmissionBlockedVerified =
            $sourceDeletionPendingRestartAdmissionBlockedVerified
        SourceDeletionPendingCatalogPreserved = $sourceDeletionPendingCatalogPreserved
        SourceDeletionPendingConfigurationRecordPreserved =
            $sourceDeletionPendingConfigurationRecordPreserved
        SourceDeletionPendingTombstoneBindingVerified =
            $sourceDeletionPendingTombstoneBindingVerified
        SourceDeletionPendingSiblingCatalogRetained =
            $sourceDeletionPendingSiblingCatalogRetained
        SourceDeletionFaultReleased = $sourceDeletionFaultReleased
        SourceDeletionManualRetryVerified = $sourceDeletionManualRetryVerified
        SourceDeletionActivePlaybackDrainVerified = $sourceDeletionActivePlaybackDrainVerified
        SourceDeletionRestartNonAdmissionVerified = $sourceDeletionRestartNonAdmissionVerified
        SourceDeletionTargetCatalogDeleted = $sourceDeletionTargetCatalogDeleted
        SourceDeletionProtectedRecordsDeleted = $sourceDeletionProtectedRecordsDeleted
        SourceDeletionTombstoneBindingCompleted = $sourceDeletionTombstoneBindingCompleted
        SourceDeletionSiblingCatalogRetained = $sourceDeletionSiblingCatalogRetained
        PlaybackUiRequestCount = $playbackUiRequestCount
        PlaybackUiCompletedResponseCount = $playbackUiCompletedResponseCount
        PlaybackUiCompletedBodyBytes = $playbackUiCompletedBodyBytes
        PlaybackChannelARequestCount = $playbackChannelARequestCount
        PlaybackChannelBRequestCount = $playbackChannelBRequestCount
        NormalClose       = $true
        PackageRemoved    = $true
    }
    $githubSha = [Environment]::GetEnvironmentVariable("GITHUB_SHA", "Process")
    if (-not [string]::IsNullOrWhiteSpace($githubSha) -and
        [System.Text.RegularExpressions.Regex]::IsMatch($githubSha, '\A[0-9a-fA-F]{40}\z')) {
        $successEvidence.CommitSha = $githubSha.ToLowerInvariant()
    }

    $successMessage = "MSIX smoke passed: signed, installed, launched, closed, and uninstalled $packageFileName."
}
catch {
    $primaryFailure = $_
}
finally {
    Invoke-CleanupStep -Failures $cleanupFailures -Name "End M14 catalog trace marker" -Action {
        if ($catalogTraceMarkersRequested -and
            $catalogTraceMarkerBeginEmitted -and
            -not $catalogTraceMarkerEndEmitted) {
            Write-M14CatalogTraceMarker -Name $catalogTraceMarkerEndName
            $catalogTraceMarkerEndEmitted = $true
            $catalogTraceMarkerCount++
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Stop launched application" -Action {
        if ($null -ne $launchedProcess) {
            try {
                $launchedProcess.Refresh()
                if (-not $launchedProcess.HasExited) {
                    $launchedProcess.Kill()
                    if (-not $launchedProcess.WaitForExit(10000)) {
                        throw "The exact packaged application process did not stop during cleanup."
                    }
                }
            }
            finally {
                $launchedProcess.Dispose()
            }
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Stop onboarding acceptance harness" -Action {
        if ($null -ne $onboardingHarnessProcess) {
            try {
                $onboardingHarnessProcess.Refresh()
                if (-not $onboardingHarnessProcess.HasExited) {
                    if ($onboardingHarnessReady -and -not $onboardingStopSignalCreated) {
                        if (Test-Path -LiteralPath $onboardingStopSignalPath) {
                            $existingStopSignal = Get-Item `
                                -LiteralPath $onboardingStopSignalPath `
                                -Force
                            if ($existingStopSignal.PSIsContainer -or
                                ($existingStopSignal.Attributes -band
                                    [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
                                $existingStopSignal.Length -ne 0) {
                                throw "The onboarding acceptance stop signal is invalid during cleanup."
                            }
                        }
                        else {
                            $cleanupStopSignalStream = [System.IO.File]::Open(
                                $onboardingStopSignalPath,
                                [System.IO.FileMode]::CreateNew,
                                [System.IO.FileAccess]::Write,
                                [System.IO.FileShare]::None)
                            $cleanupStopSignalStream.Dispose()
                        }
                        $onboardingStopSignalCreated = $true
                    }

                    if (-not $onboardingHarnessReady -or
                        -not $onboardingHarnessProcess.WaitForExit(10000)) {
                        $onboardingHarnessProcess.Kill()
                        if (-not $onboardingHarnessProcess.WaitForExit(10000)) {
                            throw "The exact onboarding acceptance harness process did not stop during cleanup."
                        }
                    }
                }
            }
            finally {
                $onboardingHarnessProcess.Dispose()
            }
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Stop playback acceptance harness" -Action {
        if ($null -ne $playbackHarnessProcess) {
            try {
                $playbackHarnessProcess.Refresh()
                if (-not $playbackHarnessProcess.HasExited) {
                    if ($playbackHarnessReady -and -not $playbackStopSignalCreated) {
                        if (Test-Path -LiteralPath $playbackStopSignalPath) {
                            $existingStopSignal = Get-Item -LiteralPath $playbackStopSignalPath -Force
                            if ($existingStopSignal.PSIsContainer -or
                                ($existingStopSignal.Attributes -band
                                    [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
                                $existingStopSignal.Length -ne 0) {
                                throw "The playback acceptance stop signal is invalid during cleanup."
                            }
                        }
                        else {
                            $cleanupStopSignalStream = [System.IO.File]::Open(
                                $playbackStopSignalPath,
                                [System.IO.FileMode]::CreateNew,
                                [System.IO.FileAccess]::Write,
                                [System.IO.FileShare]::None)
                            $cleanupStopSignalStream.Dispose()
                        }
                        $playbackStopSignalCreated = $true
                    }

                    if (-not $playbackHarnessReady -or
                        -not $playbackHarnessProcess.WaitForExit(10000)) {
                        $playbackHarnessProcess.Kill()
                        if (-not $playbackHarnessProcess.WaitForExit(10000)) {
                            throw "The exact playback acceptance harness process did not stop during cleanup."
                        }
                    }
                }
            }
            finally {
                $playbackHarnessProcess.Dispose()
            }
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Complete exact product install-root audit" -Action {
        if ($null -ne $packageInstallRootAudit -and
            -not $packageInstallRootAuditCompletionAttempted) {
            $null = Complete-WindowsPackageInstallRootAudit `
                -Audit $packageInstallRootAudit
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Stop exact product install-root audit" -Action {
        if ($null -ne $packageInstallRootAudit) {
            Stop-WindowsPackageInstallRootAudit -Audit $packageInstallRootAudit
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exact development package" -Action {
        if ($installAttempted -or $null -ne $installedPackage) {
            Remove-ExactDevelopmentPackage
        }
    }

    foreach ($environmentEntry in @($environmentBackup.GetEnumerator())) {
        $environmentName = [string]$environmentEntry.Key
        $previousEnvironmentValue = $environmentEntry.Value
        Invoke-CleanupStep -Failures $cleanupFailures -Name "Restore process environment '$environmentName'" -Action {
            [Environment]::SetEnvironmentVariable(
                $environmentName,
                $previousEnvironmentValue,
                "Process")
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exact playback acceptance certificate" -Action {
        if ($playbackLoopbackCertificateImported) {
            if ([string]::IsNullOrWhiteSpace($playbackLoopbackCertificateThumbprint) -or
                -not [System.Text.RegularExpressions.Regex]::IsMatch(
                    $playbackLoopbackCertificateThumbprint,
                    '\A[0-9A-F]{40}\z')) {
                throw "Refusing playback certificate cleanup because the thumbprint is invalid."
            }

            $playbackCertificatePath =
                "Cert:\LocalMachine\Root\$playbackLoopbackCertificateThumbprint"
            $playbackCertificateCandidate = Get-Item `
                -LiteralPath $playbackCertificatePath `
                -ErrorAction SilentlyContinue
            if ($null -ne $playbackCertificateCandidate) {
                if ($playbackCertificateCandidate.Subject -cne
                        $expectedPlaybackCertificateSubject -or
                    $playbackCertificateCandidate.Thumbprint -cne
                        $playbackLoopbackCertificateThumbprint) {
                    throw "Refusing playback certificate cleanup because its identity does not match."
                }

                Remove-Item -LiteralPath $playbackCertificatePath -Force -ErrorAction Stop
            }
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exact onboarding acceptance certificate" -Action {
        if ($onboardingLoopbackCertificateImported) {
            if ([string]::IsNullOrWhiteSpace($onboardingLoopbackCertificateThumbprint) -or
                -not [System.Text.RegularExpressions.Regex]::IsMatch(
                    $onboardingLoopbackCertificateThumbprint,
                    '\A[0-9A-F]{40}\z')) {
                throw "Refusing onboarding certificate cleanup because the thumbprint is invalid."
            }

            $onboardingCertificatePath =
                "Cert:\LocalMachine\Root\$onboardingLoopbackCertificateThumbprint"
            $onboardingCertificateCandidate = Get-Item `
                -LiteralPath $onboardingCertificatePath `
                -ErrorAction SilentlyContinue
            if ($null -ne $onboardingCertificateCandidate) {
                if ($onboardingCertificateCandidate.Subject -cne
                        $expectedPlaybackCertificateSubject -or
                    $onboardingCertificateCandidate.Thumbprint -cne
                        $onboardingLoopbackCertificateThumbprint) {
                    throw "Refusing onboarding certificate cleanup because its identity does not match."
                }

                Remove-Item -LiteralPath $onboardingCertificatePath -Force -ErrorAction Stop
            }
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Dispose playback acceptance certificate" -Action {
        if ($null -ne $playbackLoopbackCertificate) {
            $playbackLoopbackCertificate.Dispose()
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Dispose onboarding acceptance certificate" -Action {
        if ($null -ne $onboardingLoopbackCertificate) {
            $onboardingLoopbackCertificate.Dispose()
        }
    }

    if ($null -ne $certificate) {
        foreach ($certificateStore in @("Cert:\LocalMachine\TrustedPeople", "Cert:\CurrentUser\My")) {
            $store = $certificateStore
            Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exact certificate from '$store'" -Action {
                $certificatePath = "$store\$($certificate.Thumbprint)"
                $candidate = Get-Item -LiteralPath $certificatePath -ErrorAction SilentlyContinue
                if ($null -ne $candidate) {
                    if ($candidate.Subject -ne $expectedPublisher) {
                        throw "Refusing certificate cleanup because the subject does not match."
                    }

                    Remove-Item -LiteralPath $certificatePath -Force -ErrorAction Stop
                }
            }
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exported public certificate" -Action {
        if (Test-Path -LiteralPath $publicCertificatePath) {
            Remove-Item -LiteralPath $publicCertificatePath -Force -ErrorAction Stop
        }
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exact playback-control directory" -Action {
        Remove-ExactPlaybackControlDirectory
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exact onboarding-control directory" -Action {
        Remove-ExactOnboardingControlDirectory
    }

    Invoke-CleanupStep -Failures $cleanupFailures -Name "Remove exact package-output directory" -Action {
        Remove-ExactPackageOutput
    }
}

if ($EmitM16FinalArtifactSurfaces -and
    ($null -ne $primaryFailure -or $cleanupFailures.Count -ne 0)) {
    Invoke-CleanupStep `
        -Failures $cleanupFailures `
        -Name "Remove failed M16 final-artifact capture" `
        -Action { Remove-ExactM16CaptureRoot }
}

if ($null -ne $primaryFailure -or $cleanupFailures.Count -ne 0) {
    $failureMessage = if ($null -ne $primaryFailure) {
        $primaryFailure.Exception.Message
    }
    else {
        "Cleanup failed after an otherwise successful MSIX smoke."
    }
    $failureEvidence = [ordered]@{
        RunId         = $runId
        FailedAt      = (Get-Date).ToUniversalTime().ToString("O")
        Configuration = $Configuration
        Error         = $failureMessage
    }
    if ($cleanupFailures.Count -ne 0) {
        $failureEvidence.CleanupFailures = @($cleanupFailures)
    }
    Write-JsonAtomically -Value $failureEvidence -DestinationPath $failureEvidencePath

    if ($cleanupFailures.Count -ne 0) {
        $aggregateMessage = "Cleanup failures: $($cleanupFailures -join ' | ')"
        if ($null -ne $primaryFailure) {
            throw [System.InvalidOperationException]::new(
                "MSIX smoke failed: $failureMessage. $aggregateMessage",
                $primaryFailure.Exception)
        }

        throw [System.InvalidOperationException]::new($aggregateMessage)
    }

    throw $primaryFailure
}

try {
    if ($EmitM16FinalArtifactSurfaces) {
        if ($null -eq $m16SurfaceReports -or $m16SurfaceReports.Count -ne 3 -or
            $m16PostScanPackageSha256 -cne $packageSha256) {
            throw "The M16 package-side surface reports are incomplete."
        }
        Assert-M16RepositoryStable -ExpectedCommit $m16CommitSha
        $m16BindingEvidence = [ordered]@{
            SchemaVersion = 1
            EvidenceKind = "PackageBoundFinalArtifactBinding"
            RunId = $runId
            CommitSha = $m16CommitSha
            PackageSha256 = $m16PostScanPackageSha256
            PackageSbomApplicationPackageSha256 =
                $packageSbomResult.ApplicationPackageSha256
            ExactPackageInventorySha256 =
                $m16SurfaceReports[1].InventorySha256
            PostScanPackageRehashPassed = $true
        }
        $m16SurfaceEvidence = [ordered]@{
            SchemaVersion = 1
            Milestone = "M16"
            EvidenceKind = "PackageBoundFinalArtifactSurfaces"
            Result = "passed"
            RunId = $runId
            CommitSha = $m16CommitSha
            PackageSha256 = $packageSha256
            PackageSbomApplicationPackageSha256 =
                $packageSbomResult.ApplicationPackageSha256
            ScannerProfile = "M16ReleaseCandidate"
            Surfaces = @($m16SurfaceReports)
            SameBuildBindingPassed = $true
            RepositoryStable = $true
            RawSurfacesUploaded = $false
            SupportArtifactScope = "ReleaseAcceptanceOnly"
        }
        Write-M16BindingEvidenceAtomically -Value $m16BindingEvidence
        Write-M16SurfaceEvidenceAtomically -Value $m16SurfaceEvidence
        Remove-ExactM16CaptureRoot -RetainExactPackage
    }
    if ($RunWack) {
        Write-JsonAtomically `
            -Value $wackDevelopmentIdentityResult `
            -DestinationPath $wackEvidencePath
    }
    Write-JsonAtomically -Value $successEvidence -DestinationPath $evidencePath
}
catch {
    $successEvidenceFailure = $_
    $successEvidenceCleanupFailures = [System.Collections.Generic.List[string]]::new()
    $partialEvidencePaths = @(
        $evidencePath,
        $wackEvidencePath)
    if ($EmitM16FinalArtifactSurfaces) {
        $partialEvidencePaths += @(
            $m16SurfaceEvidencePath,
            $m16BindingEvidencePath)
    }
    foreach ($partialEvidencePath in $partialEvidencePaths) {
        $ownedPartialEvidencePath = $partialEvidencePath
        Invoke-CleanupStep `
            -Failures $successEvidenceCleanupFailures `
            -Name "Remove partial success evidence" `
            -Action {
                if (Test-Path -LiteralPath $ownedPartialEvidencePath -PathType Leaf) {
                    Remove-Item `
                        -LiteralPath $ownedPartialEvidencePath `
                        -Force `
                        -ErrorAction Stop
                }
            }
    }
    if ($EmitM16FinalArtifactSurfaces) {
        Invoke-CleanupStep `
            -Failures $successEvidenceCleanupFailures `
            -Name "Remove failed M16 final-artifact capture" `
            -Action { Remove-ExactM16CaptureRoot }
    }
    $failureEvidence = [ordered]@{
        RunId         = $runId
        FailedAt      = (Get-Date).ToUniversalTime().ToString("O")
        Configuration = $Configuration
        Error         = "Atomic success-evidence write failed: $($_.Exception.Message)"
    }
    if ($successEvidenceCleanupFailures.Count -ne 0) {
        $failureEvidence.CleanupFailures = @($successEvidenceCleanupFailures)
    }
    try {
        Write-JsonAtomically -Value $failureEvidence -DestinationPath $failureEvidencePath
    }
    catch {
        throw [System.InvalidOperationException]::new(
            "Success evidence and failure evidence could not be written atomically.",
            $successEvidenceFailure.Exception)
    }

    if ($successEvidenceCleanupFailures.Count -ne 0) {
        throw [System.InvalidOperationException]::new(
            "Success evidence cleanup failed: $($successEvidenceCleanupFailures -join ' | ')",
            $successEvidenceFailure.Exception)
    }

    throw $successEvidenceFailure
}

Write-Host $successMessage
}
finally {
    if ($null -ne $packageIdentityMutex) {
        Exit-WindowsPackageIdentityMutex -Mutex $packageIdentityMutex
    }
}
