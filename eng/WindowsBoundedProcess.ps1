#requires -Version 5.1

Set-StrictMode -Version Latest

if ($null -eq ('IptvSuite.WindowsBoundedProcess.BoundedProcessRunner' -as [type])) {
    try {
        Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace IptvSuite.WindowsBoundedProcess
{
    public sealed class BoundedProcessResult
    {
        public int ExitCode { get; set; }
        public string StandardOutputPath { get; set; }
        public string StandardErrorPath { get; set; }
        public long StandardOutputLength { get; set; }
        public long StandardErrorLength { get; set; }
    }

    internal sealed class BoundedProcessException : Exception
    {
        internal BoundedProcessException(string code)
            : base("WindowsBoundedProcess:" + code)
        {
        }
    }

    public static class BoundedProcessRunner
    {
        private const uint CreateNoWindow = 0x08000000;
        private const uint CreateSuspended = 0x00000004;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint StartfUseStdHandles = 0x00000100;
        private const uint HandleFlagInherit = 0x00000001;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint Infinite = 0xffffffff;
        private const uint WaitObject0 = 0x00000000;
        private const uint WaitTimeout = 0x00000102;
        private const uint WaitFailed = 0xffffffff;
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private const int ErrorFileExists = 80;
        private const int ErrorAlreadyExists = 183;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
        private static readonly IntPtr ProcThreadAttributeHandleList =
            new IntPtr(0x00020002);

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            internal int Length;
            internal IntPtr SecurityDescriptor;
            internal int InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            internal int Size;
            internal string Reserved;
            internal string Desktop;
            internal string Title;
            internal uint X;
            internal uint Y;
            internal uint XSize;
            internal uint YSize;
            internal uint XCountChars;
            internal uint YCountChars;
            internal uint FillAttribute;
            internal uint Flags;
            internal short ShowWindow;
            internal short Reserved2Length;
            internal IntPtr Reserved2;
            internal IntPtr StandardInput;
            internal IntPtr StandardOutput;
            internal IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            internal StartupInfo StartupInfo;
            internal IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            internal IntPtr Process;
            internal IntPtr Thread;
            internal uint ProcessId;
            internal uint ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize;
            internal UIntPtr MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformationValue
        {
            internal JobObjectBasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal UIntPtr ProcessMemoryLimit;
            internal UIntPtr JobMemoryLimit;
            internal UIntPtr PeakProcessMemoryUsed;
            internal UIntPtr PeakJobMemoryUsed;
        }

        private sealed class CaptureState
        {
            internal readonly object JobSync = new object();
            internal IntPtr JobHandle;
            internal long TotalBytes;
            internal long MaximumBytes;
            internal int ExpectedStop;
            internal int OutputLimitExceeded;
            internal int CaptureFailed;
            internal int TerminationFailed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateJobObject(
            IntPtr jobAttributes,
            string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            ref JobObjectExtendedLimitInformationValue information,
            int informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(
            IntPtr job,
            IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(
            IntPtr job,
            uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(
            out IntPtr readPipe,
            out IntPtr writePipe,
            ref SecurityAttributes pipeAttributes,
            int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(
            IntPtr handle,
            uint mask,
            uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            ref SecurityAttributes securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            IntPtr attribute,
            IntPtr value,
            IntPtr size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(
            IntPtr attributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(
            IntPtr handle,
            uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(
            IntPtr process,
            out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(
            IntPtr process,
            uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static void Fail(string code)
        {
            throw new BoundedProcessException(code);
        }

        private static bool IsHandleValid(IntPtr handle)
        {
            return handle != IntPtr.Zero && handle != InvalidHandleValue;
        }

        private static void CloseNativeHandle(ref IntPtr handle)
        {
            IntPtr value = handle;
            handle = IntPtr.Zero;
            if (IsHandleValid(value))
            {
                CloseHandle(value);
            }
        }

        private static string ValidateExecutablePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 ||
                !Path.IsPathRooted(path))
            {
                Fail("ExecutablePathInvalid");
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
                if (HasAlternateDataStreamSyntax(fullPath) ||
                    !File.Exists(fullPath) ||
                    (File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
                {
                    Fail("ExecutablePathInvalid");
                }
            }
            catch (BoundedProcessException)
            {
                throw;
            }
            catch
            {
                Fail("ExecutablePathInvalid");
                return null;
            }

            return fullPath;
        }

        private static string ValidateWorkingDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 ||
                !Path.IsPathRooted(path))
            {
                Fail("WorkingDirectoryInvalid");
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
                if (HasAlternateDataStreamSyntax(fullPath) ||
                    !Directory.Exists(fullPath))
                {
                    Fail("WorkingDirectoryInvalid");
                }
            }
            catch (BoundedProcessException)
            {
                throw;
            }
            catch
            {
                Fail("WorkingDirectoryInvalid");
                return null;
            }

            return fullPath;
        }

        private static string ValidateOutputPath(string path, string code)
        {
            if (String.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 ||
                !Path.IsPathRooted(path))
            {
                Fail(code);
            }

            string fullPath;
            string directory;
            try
            {
                fullPath = Path.GetFullPath(path);
                directory = Path.GetDirectoryName(fullPath);
                if (HasAlternateDataStreamSyntax(fullPath) ||
                    String.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    Fail(code);
                }
            }
            catch (BoundedProcessException)
            {
                throw;
            }
            catch
            {
                Fail(code);
                return null;
            }

            return fullPath;
        }

        private static bool HasAlternateDataStreamSyntax(string fullPath)
        {
            string root = Path.GetPathRoot(fullPath);
            int rootLength = String.IsNullOrEmpty(root) ? 0 : root.Length;
            return fullPath.IndexOf(':', rootLength) >= 0;
        }

        private static FileStream CreateOutputFile(string path)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.SequentialScan);
            }
            catch (IOException)
            {
                int error = Marshal.GetLastWin32Error();
                if (File.Exists(path) || error == ErrorFileExists ||
                    error == ErrorAlreadyExists)
                {
                    Fail("OutputAlreadyExists");
                }
                Fail("OutputFileCreateFailed");
                return null;
            }
            catch
            {
                Fail("OutputFileCreateFailed");
                return null;
            }
        }

        private static string QuoteCommandLineArgument(string value)
        {
            StringBuilder result = new StringBuilder();
            result.Append('"');
            int backslashes = 0;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    result.Append('\\', (backslashes * 2) + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    result.Append('\\', backslashes);
                    backslashes = 0;
                }
                result.Append(character);
            }

            if (backslashes > 0)
            {
                result.Append('\\', backslashes * 2);
            }
            result.Append('"');
            return result.ToString();
        }

        private static int ReserveBytes(CaptureState state, int requested)
        {
            while (true)
            {
                long current = Interlocked.Read(ref state.TotalBytes);
                long remaining = state.MaximumBytes - current;
                int accepted = remaining <= 0
                    ? 0
                    : (int)Math.Min((long)requested, remaining);
                long next = current + accepted;
                if (Interlocked.CompareExchange(
                    ref state.TotalBytes,
                    next,
                    current) == current)
                {
                    return accepted;
                }
            }
        }

        private static void RequestStop(CaptureState state)
        {
            Interlocked.Exchange(ref state.ExpectedStop, 1);
            lock (state.JobSync)
            {
                if (IsHandleValid(state.JobHandle) &&
                    !TerminateJobObject(state.JobHandle, 1))
                {
                    Interlocked.Exchange(ref state.TerminationFailed, 1);
                }
            }
        }

        private static bool CloseJob(CaptureState state)
        {
            lock (state.JobSync)
            {
                IntPtr handle = state.JobHandle;
                state.JobHandle = IntPtr.Zero;
                if (!IsHandleValid(handle))
                {
                    return true;
                }
                return CloseHandle(handle);
            }
        }

        private static void CopyBounded(
            Stream source,
            FileStream destination,
            CaptureState state)
        {
            byte[] buffer = new byte[8192];
            bool destinationOperation = false;
            try
            {
                while (true)
                {
                    int read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        destinationOperation = true;
                        destination.Flush();
                        destinationOperation = false;
                        return;
                    }

                    int accepted = ReserveBytes(state, read);
                    if (accepted > 0)
                    {
                        destinationOperation = true;
                        destination.Write(buffer, 0, accepted);
                        destinationOperation = false;
                    }
                    if (accepted != read)
                    {
                        destinationOperation = true;
                        destination.Flush();
                        destinationOperation = false;
                        Interlocked.Exchange(ref state.OutputLimitExceeded, 1);
                        RequestStop(state);
                        return;
                    }
                }
            }
            catch
            {
                bool stopWasExpected = Interlocked.CompareExchange(
                    ref state.ExpectedStop,
                    0,
                    0) != 0;
                if (destinationOperation || !stopWasExpected)
                {
                    Interlocked.Exchange(ref state.CaptureFailed, 1);
                }
                if (!stopWasExpected)
                {
                    RequestStop(state);
                }
            }
            finally
            {
                Array.Clear(buffer, 0, buffer.Length);
            }
        }

        private static bool WaitForProcessExit(IntPtr process, int milliseconds)
        {
            uint waitResult = WaitForSingleObject(process, (uint)milliseconds);
            if (waitResult == WaitObject0)
            {
                return true;
            }
            if (waitResult == WaitTimeout)
            {
                return false;
            }
            Fail("ProcessWaitFailed");
            return false;
        }

        public static BoundedProcessResult Run(
            string filePath,
            string arguments,
            string workingDirectory,
            string standardOutputPath,
            string standardErrorPath,
            int timeoutMilliseconds,
            long maximumOutputBytes)
        {
            try
            {
                return RunCore(
                    filePath,
                    arguments,
                    workingDirectory,
                    standardOutputPath,
                    standardErrorPath,
                    timeoutMilliseconds,
                    maximumOutputBytes);
            }
            catch (BoundedProcessException)
            {
                throw;
            }
            catch
            {
                throw new BoundedProcessException("UnexpectedFailure");
            }
        }

        private static BoundedProcessResult RunCore(
            string filePath,
            string arguments,
            string workingDirectory,
            string standardOutputPath,
            string standardErrorPath,
            int timeoutMilliseconds,
            long maximumOutputBytes)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Fail("PlatformUnsupported");
            }
            if (arguments == null || arguments.IndexOf('\0') >= 0)
            {
                Fail("ArgumentStringInvalid");
            }
            if (timeoutMilliseconds <= 0)
            {
                Fail("TimeoutInvalid");
            }
            if (maximumOutputBytes <= 0)
            {
                Fail("OutputLimitInvalid");
            }

            string executable = ValidateExecutablePath(filePath);
            string workDirectory = ValidateWorkingDirectory(workingDirectory);
            string outputPath = ValidateOutputPath(
                standardOutputPath,
                "StandardOutputPathInvalid");
            string errorPath = ValidateOutputPath(
                standardErrorPath,
                "StandardErrorPathInvalid");
            if (String.Equals(outputPath, errorPath, StringComparison.OrdinalIgnoreCase))
            {
                Fail("OutputPathsNotDistinct");
            }

            string commandLineValue = QuoteCommandLineArgument(executable);
            if (arguments.Length > 0)
            {
                commandLineValue = commandLineValue + " " + arguments;
            }
            if (commandLineValue.Length > 32767)
            {
                Fail("CommandLineTooLong");
            }

            FileStream standardOutput = null;
            FileStream standardError = null;
            Stream outputPipeStream = null;
            Stream errorPipeStream = null;
            Thread outputReader = null;
            Thread errorReader = null;
            IntPtr job = IntPtr.Zero;
            IntPtr outputReadPipe = IntPtr.Zero;
            IntPtr outputWritePipe = IntPtr.Zero;
            IntPtr errorReadPipe = IntPtr.Zero;
            IntPtr errorWritePipe = IntPtr.Zero;
            IntPtr nullInput = IntPtr.Zero;
            IntPtr processAttributeList = IntPtr.Zero;
            IntPtr inheritedHandleList = IntPtr.Zero;
            ProcessInformation processInformation = new ProcessInformation();
            CaptureState state = null;
            bool processAttributeListInitialized = false;
            bool processCreated = false;
            bool processAssigned = false;
            bool outputReaderStarted = false;
            bool errorReaderStarted = false;

            try
            {
                standardOutput = CreateOutputFile(outputPath);
                standardError = CreateOutputFile(errorPath);

                job = CreateJobObject(IntPtr.Zero, null);
                if (!IsHandleValid(job))
                {
                    Fail("JobCreationFailed");
                }

                JobObjectExtendedLimitInformationValue jobInformation =
                    new JobObjectExtendedLimitInformationValue();
                jobInformation.BasicLimitInformation.LimitFlags =
                    JobObjectLimitKillOnJobClose;
                if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformation,
                    ref jobInformation,
                    Marshal.SizeOf(typeof(JobObjectExtendedLimitInformationValue))))
                {
                    Fail("JobConfigurationFailed");
                }

                SecurityAttributes inheritable = new SecurityAttributes();
                inheritable.Length = Marshal.SizeOf(typeof(SecurityAttributes));
                inheritable.InheritHandle = 1;
                if (!CreatePipe(
                        out outputReadPipe,
                        out outputWritePipe,
                        ref inheritable,
                        0) ||
                    !SetHandleInformation(
                        outputReadPipe,
                        HandleFlagInherit,
                        0))
                {
                    Fail("OutputPipeCreationFailed");
                }
                if (!CreatePipe(
                        out errorReadPipe,
                        out errorWritePipe,
                        ref inheritable,
                        0) ||
                    !SetHandleInformation(
                        errorReadPipe,
                        HandleFlagInherit,
                        0))
                {
                    Fail("ErrorPipeCreationFailed");
                }

                nullInput = CreateFile(
                    "NUL",
                    GenericRead,
                    FileShareRead | FileShareWrite,
                    ref inheritable,
                    OpenExisting,
                    FileAttributeNormal,
                    IntPtr.Zero);
                if (!IsHandleValid(nullInput))
                {
                    Fail("StandardInputCreationFailed");
                }

                IntPtr processAttributeListSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(
                    IntPtr.Zero,
                    1,
                    0,
                    ref processAttributeListSize);
                if (processAttributeListSize == IntPtr.Zero ||
                    processAttributeListSize.ToInt64() <= 0)
                {
                    Fail("AttributeListInitializationFailed");
                }
                processAttributeList = Marshal.AllocHGlobal(
                    processAttributeListSize);
                if (!InitializeProcThreadAttributeList(
                    processAttributeList,
                    1,
                    0,
                    ref processAttributeListSize))
                {
                    Fail("AttributeListInitializationFailed");
                }
                processAttributeListInitialized = true;

                int inheritedHandleListSize = checked(IntPtr.Size * 3);
                inheritedHandleList = Marshal.AllocHGlobal(
                    inheritedHandleListSize);
                Marshal.WriteIntPtr(
                    inheritedHandleList,
                    0,
                    nullInput);
                Marshal.WriteIntPtr(
                    inheritedHandleList,
                    IntPtr.Size,
                    outputWritePipe);
                Marshal.WriteIntPtr(
                    inheritedHandleList,
                    IntPtr.Size * 2,
                    errorWritePipe);
                if (!UpdateProcThreadAttribute(
                    processAttributeList,
                    0,
                    ProcThreadAttributeHandleList,
                    inheritedHandleList,
                    new IntPtr(inheritedHandleListSize),
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    Fail("HandleListConfigurationFailed");
                }

                StartupInfoEx startupInfo = new StartupInfoEx();
                startupInfo.StartupInfo.Size =
                    Marshal.SizeOf(typeof(StartupInfoEx));
                startupInfo.StartupInfo.Flags = StartfUseStdHandles;
                startupInfo.StartupInfo.StandardInput = nullInput;
                startupInfo.StartupInfo.StandardOutput = outputWritePipe;
                startupInfo.StartupInfo.StandardError = errorWritePipe;
                startupInfo.AttributeList = processAttributeList;
                StringBuilder commandLine = new StringBuilder(commandLineValue);
                if (!CreateProcess(
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    CreateNoWindow | CreateSuspended |
                        ExtendedStartupInfoPresent,
                    IntPtr.Zero,
                    workDirectory,
                    ref startupInfo,
                    out processInformation))
                {
                    Fail("ProcessLaunchFailed");
                }
                processCreated = true;

                CloseNativeHandle(ref outputWritePipe);
                CloseNativeHandle(ref errorWritePipe);
                CloseNativeHandle(ref nullInput);

                DeleteProcThreadAttributeList(processAttributeList);
                processAttributeListInitialized = false;
                Marshal.FreeHGlobal(processAttributeList);
                processAttributeList = IntPtr.Zero;
                Marshal.FreeHGlobal(inheritedHandleList);
                inheritedHandleList = IntPtr.Zero;

                if (!AssignProcessToJobObject(job, processInformation.Process))
                {
                    TerminateProcess(processInformation.Process, 1);
                    WaitForSingleObject(processInformation.Process, 10000);
                    Fail("JobAssignmentFailed");
                }
                processAssigned = true;

                state = new CaptureState();
                state.JobHandle = job;
                state.MaximumBytes = maximumOutputBytes;
                job = IntPtr.Zero;

                SafeFileHandle outputSafeHandle = new SafeFileHandle(
                    outputReadPipe,
                    true);
                outputReadPipe = IntPtr.Zero;
                outputPipeStream = new FileStream(
                    outputSafeHandle,
                    FileAccess.Read,
                    4096,
                    false);
                SafeFileHandle errorSafeHandle = new SafeFileHandle(
                    errorReadPipe,
                    true);
                errorReadPipe = IntPtr.Zero;
                errorPipeStream = new FileStream(
                    errorSafeHandle,
                    FileAccess.Read,
                    4096,
                    false);

                outputReader = new Thread(delegate()
                {
                    CopyBounded(outputPipeStream, standardOutput, state);
                });
                errorReader = new Thread(delegate()
                {
                    CopyBounded(errorPipeStream, standardError, state);
                });
                outputReader.IsBackground = true;
                errorReader.IsBackground = true;
                outputReader.Name = "WindowsBoundedProcess-stdout";
                errorReader.Name = "WindowsBoundedProcess-stderr";
                try
                {
                    outputReader.Start();
                    outputReaderStarted = true;
                    errorReader.Start();
                    errorReaderStarted = true;
                }
                catch
                {
                    Interlocked.Exchange(ref state.CaptureFailed, 1);
                    RequestStop(state);
                    Fail("OutputCaptureFailed");
                }

                if (ResumeThread(processInformation.Thread) == Infinite)
                {
                    RequestStop(state);
                    Fail("ProcessResumeFailed");
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                bool processExited = false;
                bool timedOut = false;
                while (!processExited)
                {
                    long remainingMilliseconds =
                        timeoutMilliseconds - stopwatch.ElapsedMilliseconds;
                    if (remainingMilliseconds <= 0)
                    {
                        timedOut = true;
                        RequestStop(state);
                    }
                    if (timedOut)
                    {
                        processExited = WaitForProcessExit(
                            processInformation.Process,
                            10000);
                        if (!processExited)
                        {
                            CloseJob(state);
                            processExited = WaitForProcessExit(
                                processInformation.Process,
                                10000);
                        }
                        if (!processExited)
                        {
                            Fail("ProcessTerminationFailed");
                        }
                        break;
                    }

                    uint waitResult = WaitForSingleObject(
                        processInformation.Process,
                        (uint)Math.Min(50L, remainingMilliseconds));
                    if (waitResult == WaitObject0)
                    {
                        processExited = true;
                        break;
                    }
                    if (waitResult == WaitFailed)
                    {
                        RequestStop(state);
                        Fail("ProcessWaitFailed");
                    }

                    if (Interlocked.CompareExchange(
                            ref state.OutputLimitExceeded,
                            0,
                            0) != 0 ||
                        Interlocked.CompareExchange(
                            ref state.CaptureFailed,
                            0,
                            0) != 0)
                    {
                        RequestStop(state);
                    }
                    if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
                    {
                        timedOut = true;
                        RequestStop(state);
                    }
                    if (timedOut ||
                        Interlocked.CompareExchange(
                            ref state.OutputLimitExceeded,
                            0,
                            0) != 0 ||
                        Interlocked.CompareExchange(
                            ref state.CaptureFailed,
                            0,
                            0) != 0)
                    {
                        processExited = WaitForProcessExit(
                            processInformation.Process,
                            10000);
                        if (!processExited)
                        {
                            CloseJob(state);
                            processExited = WaitForProcessExit(
                                processInformation.Process,
                                10000);
                        }
                        if (!processExited)
                        {
                            Fail("ProcessTerminationFailed");
                        }
                    }
                }

                uint processExitCode;
                if (!GetExitCodeProcess(
                    processInformation.Process,
                    out processExitCode))
                {
                    Fail("ProcessExitCodeUnavailable");
                }

                Interlocked.Exchange(ref state.ExpectedStop, 1);
                if (!CloseJob(state))
                {
                    Fail("JobCloseFailed");
                }
                bool outputJoined = outputReader.Join(10000);
                bool errorJoined = errorReader.Join(10000);
                if (!outputJoined || !errorJoined)
                {
                    Interlocked.Exchange(ref state.CaptureFailed, 1);
                }

                standardOutput.Flush();
                standardError.Flush();
                long outputLength = standardOutput.Length;
                long errorLength = standardError.Length;
                if (outputLength < 0 || errorLength < 0 ||
                    outputLength > maximumOutputBytes ||
                    errorLength > maximumOutputBytes - outputLength)
                {
                    Fail("OutputValidationFailed");
                }
                if (Interlocked.CompareExchange(
                        ref state.TerminationFailed,
                        0,
                        0) != 0)
                {
                    Fail("ProcessTerminationFailed");
                }
                if (Interlocked.CompareExchange(
                        ref state.OutputLimitExceeded,
                        0,
                        0) != 0)
                {
                    Fail("OutputLimitExceeded");
                }
                if (Interlocked.CompareExchange(
                        ref state.CaptureFailed,
                        0,
                        0) != 0)
                {
                    Fail("OutputCaptureFailed");
                }
                if (timedOut)
                {
                    Fail("ProcessTimeout");
                }

                BoundedProcessResult result = new BoundedProcessResult();
                result.ExitCode = unchecked((int)processExitCode);
                result.StandardOutputPath = outputPath;
                result.StandardErrorPath = errorPath;
                result.StandardOutputLength = outputLength;
                result.StandardErrorLength = errorLength;
                return result;
            }
            finally
            {
                if (state != null)
                {
                    Interlocked.Exchange(ref state.ExpectedStop, 1);
                    if (processCreated && processAssigned)
                    {
                        RequestStop(state);
                    }
                    CloseJob(state);
                }
                else if (processCreated && IsHandleValid(processInformation.Process))
                {
                    TerminateProcess(processInformation.Process, 1);
                }

                if (processCreated && IsHandleValid(processInformation.Process))
                {
                    WaitForSingleObject(processInformation.Process, 10000);
                }
                if (outputReaderStarted && outputReader != null &&
                    outputReader.IsAlive)
                {
                    outputReader.Join(1000);
                }
                if (errorReaderStarted && errorReader != null &&
                    errorReader.IsAlive)
                {
                    errorReader.Join(1000);
                }

                if (outputPipeStream != null)
                {
                    outputPipeStream.Dispose();
                }
                if (errorPipeStream != null)
                {
                    errorPipeStream.Dispose();
                }
                if (standardOutput != null)
                {
                    standardOutput.Dispose();
                }
                if (standardError != null)
                {
                    standardError.Dispose();
                }

                CloseNativeHandle(ref outputReadPipe);
                CloseNativeHandle(ref outputWritePipe);
                CloseNativeHandle(ref errorReadPipe);
                CloseNativeHandle(ref errorWritePipe);
                CloseNativeHandle(ref nullInput);
                if (processAttributeListInitialized &&
                    processAttributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(processAttributeList);
                }
                if (processAttributeList != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(processAttributeList);
                }
                if (inheritedHandleList != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(inheritedHandleList);
                }
                CloseNativeHandle(ref processInformation.Thread);
                CloseNativeHandle(ref processInformation.Process);
                CloseNativeHandle(ref job);
            }
        }
    }
}
"@ -ErrorAction Stop
    }
    catch {
        throw "WindowsBoundedProcess:HelperInitializationFailed"
    }
}

function Invoke-WindowsBoundedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$ArgumentString,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [string]$StandardOutputPath,

        [Parameter(Mandatory = $true)]
        [string]$StandardErrorPath,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutMilliseconds,

        [Parameter(Mandatory = $true)]
        [long]$MaximumOutputBytes
    )

    try {
        return [IptvSuite.WindowsBoundedProcess.BoundedProcessRunner]::Run(
            $FilePath,
            $ArgumentString,
            $WorkingDirectory,
            $StandardOutputPath,
            $StandardErrorPath,
            $TimeoutMilliseconds,
            $MaximumOutputBytes)
    }
    catch {
        $exception = $_.Exception
        while ($null -ne $exception) {
            if ($exception.Message -cmatch
                '^WindowsBoundedProcess:[A-Za-z][A-Za-z0-9]+$') {
                throw $exception.Message
            }
            $exception = $exception.InnerException
        }
        throw "WindowsBoundedProcess:UnexpectedFailure"
    }
}
