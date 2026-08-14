#requires -Version 5.1

[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",

    [string]$DotNetPath = "dotnet"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$nativeSource = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace IptvSuite.DpapiUserBoundarySmoke
{
    public sealed class RetainedProcess : IDisposable
    {
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 258;
        private const uint StillActive = 259;
        private IntPtr processHandle;

        internal RetainedProcess(IntPtr processHandle, int processId, long creationTimeFileTimeUtc)
        {
            this.processHandle = processHandle;
            ProcessId = processId;
            CreationTimeFileTimeUtc = creationTimeFileTimeUtc;
        }

        public int ProcessId { get; private set; }

        public long CreationTimeFileTimeUtc { get; private set; }

        public bool HasExited
        {
            get
            {
                IntPtr handle = RequireHandle();
                uint result = WaitForSingleObject(handle, 0);
                if (result == WaitObject0)
                {
                    return true;
                }

                if (result == WaitTimeout)
                {
                    return false;
                }

                throw new Win32Exception(Marshal.GetLastWin32Error(), "The retained process state is unavailable.");
            }
        }

        public bool WaitForExit(int milliseconds)
        {
            if (milliseconds < 0)
            {
                throw new ArgumentOutOfRangeException("milliseconds");
            }

            uint result = WaitForSingleObject(RequireHandle(), checked((uint)milliseconds));
            if (result == WaitObject0)
            {
                return true;
            }

            if (result == WaitTimeout)
            {
                return false;
            }

            throw new Win32Exception(Marshal.GetLastWin32Error(), "The retained process wait failed.");
        }

        public int GetExitCode()
        {
            uint exitCode;
            if (!GetExitCodeProcess(RequireHandle(), out exitCode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The retained process exit code is unavailable.");
            }

            if (exitCode == StillActive)
            {
                throw new InvalidOperationException("The retained process is still active.");
            }

            return unchecked((int)exitCode);
        }

        public void Terminate(int exitCode)
        {
            IntPtr handle = RequireHandle();
            if (HasExited)
            {
                return;
            }

            if (!TerminateProcess(handle, unchecked((uint)exitCode)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The retained process could not be terminated.");
            }

            if (!WaitForExit(10000))
            {
                throw new InvalidOperationException("The retained process did not terminate in time.");
            }
        }

        public void Dispose()
        {
            IntPtr handle = processHandle;
            processHandle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }

            GC.SuppressFinalize(this);
        }

        ~RetainedProcess()
        {
            Dispose();
        }

        private IntPtr RequireHandle()
        {
            if (processHandle == IntPtr.Zero)
            {
                throw new ObjectDisposedException("RetainedProcess");
            }

            return processHandle;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    public static class NativeBoundaryHost
    {
        private const uint WaitObject0 = 0;
        private const uint LogonWithProfile = 0x00000001;
        private const uint LogonNetCredentialsOnly = 0x00000002;
        private const uint CreateNoWindow = 0x08000000;

        public static RetainedProcess Launch(
            string userName,
            SecureString password,
            string applicationPath,
            string[] arguments,
            string workingDirectory)
        {
            if (String.IsNullOrWhiteSpace(userName))
            {
                throw new ArgumentException("A local account name is required.", "userName");
            }

            if (password == null || password.Length != 32)
            {
                throw new ArgumentException("A 32-character secure password is required.", "password");
            }

            if (String.IsNullOrWhiteSpace(applicationPath) || !System.IO.Path.IsPathRooted(applicationPath))
            {
                throw new ArgumentException("An absolute application path is required.", "applicationPath");
            }

            if (arguments == null)
            {
                throw new ArgumentNullException("arguments");
            }

            if (String.IsNullOrWhiteSpace(workingDirectory) || !System.IO.Path.IsPathRooted(workingDirectory))
            {
                throw new ArgumentException("An absolute working directory is required.", "workingDirectory");
            }

            IntPtr passwordPointer = IntPtr.Zero;
            PROCESS_INFORMATION processInformation = new PROCESS_INFORMATION();
            STARTUPINFO startupInfo = new STARTUPINFO();
            startupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            StringBuilder commandLine = BuildCommandLine(applicationPath, arguments);
            bool processCreated = false;
            bool processHandleTransferred = false;

            try
            {
                passwordPointer = Marshal.SecureStringToGlobalAllocUnicode(password);
                uint logonFlags = LogonWithProfile;
                if ((logonFlags & LogonNetCredentialsOnly) != 0)
                {
                    throw new InvalidOperationException("Network-credentials-only logon is forbidden.");
                }

                bool created = CreateProcessWithLogonW(
                    userName,
                    ".",
                    passwordPointer,
                    logonFlags,
                    applicationPath,
                    commandLine,
                    CreateNoWindow,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startupInfo,
                    out processInformation);
                if (!created)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The alternate-user process could not be created.");
                }
                processCreated = true;

                if (processInformation.dwProcessId == 0 || processInformation.dwProcessId > Int32.MaxValue)
                {
                    throw new InvalidOperationException("The alternate-user process identifier is invalid.");
                }

                FILETIME creationTime;
                FILETIME exitTime;
                FILETIME kernelTime;
                FILETIME userTime;
                if (!GetProcessTimes(
                    processInformation.hProcess,
                    out creationTime,
                    out exitTime,
                    out kernelTime,
                    out userTime))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The alternate-user process start time is unavailable.");
                }

                long creationFileTime = ((long)creationTime.dwHighDateTime << 32) |
                    (uint)creationTime.dwLowDateTime;
                IntPtr retainedHandle = processInformation.hProcess;
                RetainedProcess retainedProcess = new RetainedProcess(
                    retainedHandle,
                    checked((int)processInformation.dwProcessId),
                    creationFileTime);
                processInformation.hProcess = IntPtr.Zero;
                processHandleTransferred = true;
                return retainedProcess;
            }
            catch
            {
                if (processCreated && !processHandleTransferred && processInformation.hProcess != IntPtr.Zero)
                {
                    uint initialWait = WaitForSingleObject(processInformation.hProcess, 0);
                    if (initialWait != WaitObject0)
                    {
                        bool terminated = TerminateProcess(processInformation.hProcess, 18);
                        if (!terminated)
                        {
                            int terminateError = Marshal.GetLastWin32Error();
                            if (WaitForSingleObject(processInformation.hProcess, 0) != WaitObject0)
                            {
                                throw new Win32Exception(
                                    terminateError,
                                    "A partially created alternate-user process could not be terminated.");
                            }
                        }
                        else if (WaitForSingleObject(processInformation.hProcess, 10000) != WaitObject0)
                        {
                            throw new InvalidOperationException(
                                "A partially created alternate-user process did not terminate in time.");
                        }
                    }
                }

                throw;
            }
            finally
            {
                if (passwordPointer != IntPtr.Zero)
                {
                    Marshal.ZeroFreeGlobalAllocUnicode(passwordPointer);
                }

                if (processInformation.hThread != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hThread);
                }

                if (processInformation.hProcess != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hProcess);
                }
            }
        }

        public static bool DeleteProfile(string sid, string profilePath)
        {
            if (String.IsNullOrWhiteSpace(sid) || String.IsNullOrWhiteSpace(profilePath))
            {
                throw new ArgumentException("An exact profile identity and path are required.");
            }

            return DeleteProfileW(sid, profilePath, null);
        }

        private static StringBuilder BuildCommandLine(string applicationPath, string[] arguments)
        {
            StringBuilder commandLine = new StringBuilder();
            AppendQuotedArgument(commandLine, applicationPath);
            foreach (string argument in arguments)
            {
                if (argument == null)
                {
                    throw new ArgumentException("A process argument is null.", "arguments");
                }

                commandLine.Append(' ');
                AppendQuotedArgument(commandLine, argument);
            }

            return commandLine;
        }

        private static void AppendQuotedArgument(StringBuilder destination, string value)
        {
            destination.Append('"');
            int backslashCount = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (character == '"')
                {
                    destination.Append('\\', checked((backslashCount * 2) + 1));
                    destination.Append('"');
                    backslashCount = 0;
                    continue;
                }

                destination.Append('\\', backslashCount);
                backslashCount = 0;
                destination.Append(character);
            }

            destination.Append('\\', checked(backslashCount * 2));
            destination.Append('"');
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessWithLogonW(
            string userName,
            string domain,
            IntPtr password,
            uint logonFlags,
            string applicationName,
            StringBuilder commandLine,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(
            IntPtr process,
            out FILETIME creationTime,
            out FILETIME exitTime,
            out FILETIME kernelTime,
            out FILETIME userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteProfileW(string sid, string profilePath, string computerName);
    }
}
'@

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$expectedControllerScriptPath = Join-Path $repositoryRoot "eng\Invoke-WindowsDpapiUserBoundarySmoke.ps1"
$globalJsonPath = Join-Path $repositoryRoot "global.json"
$nuGetConfigPath = Join-Path $repositoryRoot "NuGet.config"
$harnessProjectPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.DpapiUserBoundaryHarness\IptvSuite.DpapiUserBoundaryHarness.csproj"
$harnessOutputDirectory = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.DpapiUserBoundaryHarness\bin\x64\$Configuration\net10.0"
$harnessAssemblyName = "IptvSuite.DpapiUserBoundaryHarness.dll"
$testingProjectPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.Testing\IptvSuite.Testing.csproj"
$testingToolPath = Join-Path $repositoryRoot "apps\windows\tests\IptvSuite.Testing\bin\$Configuration\net10.0\IptvSuite.Testing.dll"
$artifactRoot = Join-Path $repositoryRoot ".artifacts\dpapi-user-boundary"
$successEvidencePath = Join-Path $artifactRoot "last-success.json"
$failureEvidencePath = Join-Path $artifactRoot "last-failure.json"
$programData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
$workspaceBase = Join-Path $programData "ProtectedStoreTests\DpapiUserBoundary\v1"
$runsRoot = Join-Path $workspaceBase "runs"
$toolsRoot = Join-Path $workspaceBase "tools"
$runId = [Guid]::NewGuid()
$runIdText = $runId.ToString("N").ToLowerInvariant()
$accountDescription = "DPAPI-BOUNDARY:" + $runIdText
$runRoot = Join-Path $runsRoot $runIdText
$toolRoot = Join-Path $toolsRoot $runIdText
$stagedHarnessPath = Join-Path $toolRoot $harnessAssemblyName
$probeResultPath = Join-Path $runRoot "result\probe-result.bin"
$requiredProbeEvidenceMask = [UInt64]0x1FFF
$usersSid = [System.Security.Principal.SecurityIdentifier]::new("S-1-5-32-545")
$administratorsSid = [System.Security.Principal.SecurityIdentifier]::new("S-1-5-32-544")
$systemSid = [System.Security.Principal.SecurityIdentifier]::new("S-1-5-18")
$failureStage = "Bootstrap"
$failureCode = "UnexpectedFailure"
$primaryFailure = $false
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$securePassword = $null
$createdUser = $null
$createdUserSid = $null
$createdUserName = $null
$accountCreated = $false
$usersMembershipPresent = $false
$profileObserved = $false
$probeProcess = $null
$toolRootCreated = $false
$runRootCreated = $false
$successCandidate = $null
$processCleanupPassed = $false
$groupCleanupPassed = $false
$accountCleanupPassed = $false
$profileCleanupPassed = $false
$workspaceCleanupPassed = $false
$repositoryHead = $null
$dotNetExecutable = $null
$actualSdk = $null
$controllerScriptSha256 = $null
$stagedHarnessSha256 = $null

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
            throw "A protected path contains a reparse point."
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
        throw "A required directory is unavailable."
    }

    Assert-NoReparsePath -Path $Path
    $attributes = [System.IO.File]::GetAttributes([System.IO.Path]::GetFullPath($Path))
    if (($attributes -band [System.IO.FileAttributes]::Directory) -eq 0 -or
        ($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "A required directory is unsafe."
    }
}

function Assert-RegularFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "A required file is unavailable."
    }

    $attributes = [System.IO.File]::GetAttributes([System.IO.Path]::GetFullPath($Path))
    if (($attributes -band ([System.IO.FileAttributes]::Directory -bor [System.IO.FileAttributes]::ReparsePoint)) -ne 0) {
        throw "A required file is unsafe."
    }
}

function New-RegularDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Assert-NoReparsePath -Path $Path
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        throw "A directory path is occupied by a file."
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
    $expectedParent = [System.IO.Directory]::GetParent($fullChild)
    if ($null -eq $expectedParent -or
        -not $expectedParent.FullName.Equals($fullParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "A cleanup path escaped its exact parent."
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
                throw "An owned cleanup tree contains a reparse point."
            }

            if ($entry -is [System.IO.DirectoryInfo]) {
                $pending.Enqueue($entry)
            }
        }
    }

    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $Path) {
        throw "An exact owned cleanup tree remains."
    }
}

function Remove-EmptyOwnedDirectory {
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
    if (@(Get-ChildItem -LiteralPath $Path -Force).Count -eq 0) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
    }
}

function Add-NumericAccessRule {
    param(
        [Parameter(Mandatory)]
        [System.Security.AccessControl.DirectorySecurity]$Security,

        [Parameter(Mandatory)]
        [System.Security.Principal.SecurityIdentifier]$Sid,

        [Parameter(Mandatory)]
        [System.Security.AccessControl.FileSystemRights]$Rights
    )

    $inheritance = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $Sid,
        $Rights,
        $inheritance,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    [void]$Security.AddAccessRule($rule)
}

function Set-NumericDirectoryAcl {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [System.Security.Principal.SecurityIdentifier]$PrimarySid,

        [System.Security.Principal.SecurityIdentifier]$SecondarySid,

        [System.Security.AccessControl.FileSystemRights]$SecondaryRights = 0
    )

    Assert-RegularDirectory -Path $Path
    $security = [System.Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($PrimarySid)
    Add-NumericAccessRule -Security $security -Sid $PrimarySid -Rights ([System.Security.AccessControl.FileSystemRights]::FullControl)
    Add-NumericAccessRule -Security $security -Sid $script:systemSid -Rights ([System.Security.AccessControl.FileSystemRights]::FullControl)
    Add-NumericAccessRule -Security $security -Sid $script:administratorsSid -Rights ([System.Security.AccessControl.FileSystemRights]::FullControl)
    if ($null -ne $SecondarySid -and $SecondaryRights -ne 0) {
        Add-NumericAccessRule -Security $security -Sid $SecondarySid -Rights $SecondaryRights
    }

    [System.IO.Directory]::SetAccessControl([System.IO.Path]::GetFullPath($Path), $security)
}

function Set-NumericFileAcl {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [System.Security.Principal.SecurityIdentifier]$PrimarySid,

        [Parameter(Mandatory)]
        [System.Security.Principal.SecurityIdentifier]$SecondarySid,

        [Parameter(Mandatory)]
        [System.Security.AccessControl.FileSystemRights]$SecondaryRights
    )

    Assert-RegularFile -Path $Path
    Assert-NoReparsePath -Path $Path
    $security = [System.Security.AccessControl.FileSecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($PrimarySid)
    foreach ($entry in @(
        [pscustomobject]@{ Sid = $PrimarySid; Rights = [System.Security.AccessControl.FileSystemRights]::FullControl },
        [pscustomobject]@{ Sid = $script:systemSid; Rights = [System.Security.AccessControl.FileSystemRights]::FullControl },
        [pscustomobject]@{ Sid = $script:administratorsSid; Rights = [System.Security.AccessControl.FileSystemRights]::FullControl },
        [pscustomobject]@{ Sid = $SecondarySid; Rights = $SecondaryRights }
    )) {
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $entry.Sid,
            $entry.Rights,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }

    [System.IO.File]::SetAccessControl([System.IO.Path]::GetFullPath($Path), $security)
}

function Assert-ExactNumericAcl {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [System.Security.Principal.SecurityIdentifier]$PrimarySid,

        [Parameter(Mandatory)]
        [System.Security.Principal.SecurityIdentifier]$SecondarySid,

        [Parameter(Mandatory)]
        [System.Security.AccessControl.FileSystemRights]$SecondaryRights,

        [switch]$Directory
    )

    if ($Directory) {
        Assert-RegularDirectory -Path $Path
        $security = [System.IO.Directory]::GetAccessControl([System.IO.Path]::GetFullPath($Path))
        $expectedInheritance = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    }
    else {
        Assert-RegularFile -Path $Path
        Assert-NoReparsePath -Path $Path
        $security = [System.IO.File]::GetAccessControl([System.IO.Path]::GetFullPath($Path))
        $expectedInheritance = [System.Security.AccessControl.InheritanceFlags]::None
    }

    $owner = $security.GetOwner([System.Security.Principal.SecurityIdentifier])
    if (-not $owner.Equals($PrimarySid) -or -not $security.AreAccessRulesProtected) {
        throw "A staged tool ACL owner or inheritance boundary is invalid."
    }

    $expectedRules = @{}
    $expectedRules.Add($PrimarySid.Value, [int][System.Security.AccessControl.FileSystemRights]::FullControl)
    $expectedRules.Add($script:systemSid.Value, [int][System.Security.AccessControl.FileSystemRights]::FullControl)
    $expectedRules.Add($script:administratorsSid.Value, [int][System.Security.AccessControl.FileSystemRights]::FullControl)
    $normalizedSecondaryRights = $SecondaryRights -bor
        [System.Security.AccessControl.FileSystemRights]::Synchronize
    $expectedRules.Add($SecondarySid.Value, [int]$normalizedSecondaryRights)
    $seenRules = @{}
    $rules = @($security.GetAccessRules(
        $true,
        $false,
        [System.Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne $expectedRules.Count) {
        throw "A staged tool ACL contains an unexpected rule count."
    }

    foreach ($rule in $rules) {
        $sidValue = $rule.IdentityReference.Value
        if (-not $expectedRules.ContainsKey($sidValue) -or $seenRules.ContainsKey($sidValue) -or
            $rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow -or
            $rule.IsInherited -or [int]$rule.FileSystemRights -ne $expectedRules[$sidValue] -or
            $rule.InheritanceFlags -ne $expectedInheritance -or
            $rule.PropagationFlags -ne [System.Security.AccessControl.PropagationFlags]::None) {
            throw "A staged tool ACL rule is invalid."
        }

        $seenRules.Add($sidValue, $true)
    }

    if ($seenRules.Count -ne $expectedRules.Count) {
        throw "A staged tool ACL rule is missing."
    }
}

function Get-CryptoIndex {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.RandomNumberGenerator]$Generator,

        [Parameter(Mandatory)]
        [int]$UpperBound
    )

    if ($UpperBound -le 0 -or $UpperBound -gt 256) {
        throw "A password alphabet bound is invalid."
    }

    $buffer = New-Object byte[] 1
    $limit = 256 - (256 % $UpperBound)
    do {
        $Generator.GetBytes($buffer)
    } while ([int]$buffer[0] -ge $limit)

    return [int]$buffer[0] % $UpperBound
}

function Add-RandomSecureCharacter {
    param(
        [Parameter(Mandatory)]
        [System.Security.SecureString]$Password,

        [Parameter(Mandatory)]
        [System.Security.Cryptography.RandomNumberGenerator]$Generator,

        [Parameter(Mandatory)]
        [string]$Alphabet
    )

    $index = Get-CryptoIndex -Generator $Generator -UpperBound $Alphabet.Length
    $Password.AppendChar($Alphabet[$index])
}

function New-RandomSecurePassword {
    $password = [System.Security.SecureString]::new()
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $lower = "abcdefghijkmnopqrstuvwxyz"
    $upper = "ABCDEFGHJKLMNPQRSTUVWXYZ"
    $digits = "23456789"
    $symbols = "!#%&*+-=?@_"
    $combined = $lower + $upper + $digits + $symbols

    try {
        Add-RandomSecureCharacter -Password $password -Generator $generator -Alphabet $lower
        Add-RandomSecureCharacter -Password $password -Generator $generator -Alphabet $upper
        Add-RandomSecureCharacter -Password $password -Generator $generator -Alphabet $digits
        Add-RandomSecureCharacter -Password $password -Generator $generator -Alphabet $symbols
        while ($password.Length -lt 32) {
            Add-RandomSecureCharacter -Password $password -Generator $generator -Alphabet $combined
        }

        if ($password.Length -ne 32) {
            throw "The secure password length is invalid."
        }

        $password.MakeReadOnly()
        return $password
    }
    catch {
        $password.Dispose()
        throw
    }
    finally {
        $generator.Dispose()
    }
}

function Invoke-CheckedDotNet {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$FailureCode
    )

    & $script:dotNetExecutable @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        $script:failureCode = $FailureCode
        throw "A checked .NET operation failed."
    }
}

function Invoke-HarnessPrimary {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$FailureCode
    )

    & $script:dotNetExecutable $script:stagedHarnessPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        $script:failureCode = $FailureCode
        throw "A primary harness phase failed."
    }
}

function Get-BoundedRegularTree {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [ValidateRange(1, 4096)]
        [int]$MaxEntries = 512,

        [ValidateRange(1, 32)]
        [int]$MaxDepth = 12,

        [ValidateRange(1, 1073741824)]
        [long]$MaxTotalBytes = 268435456
    )

    Assert-RegularDirectory -Path $Root
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar)
    $rootPrefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar
    $pending = [System.Collections.Generic.Queue[object]]::new()
    $pending.Enqueue([pscustomobject]@{
        Directory = [System.IO.DirectoryInfo]::new($fullRoot)
        Depth = 0
    })
    $entries = [System.Collections.Generic.List[object]]::new()
    [long]$totalBytes = 0

    while ($pending.Count -gt 0) {
        $node = $pending.Dequeue()
        foreach ($entry in $node.Directory.GetFileSystemInfos()) {
            if ($entries.Count -ge $MaxEntries) {
                throw "The staged tool tree exceeds its entry bound."
            }
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "The staged tool tree contains a reparse point."
            }

            $fullEntryPath = [System.IO.Path]::GetFullPath($entry.FullName)
            $entryParent = [System.IO.Directory]::GetParent($fullEntryPath)
            if ($null -eq $entryParent -or
                -not $entryParent.FullName.Equals(
                    $node.Directory.FullName,
                    [System.StringComparison]::OrdinalIgnoreCase) -or
                -not $fullEntryPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "The staged tool tree contains an escaped entry."
            }

            $depth = [int]$node.Depth + 1
            if ($depth -gt $MaxDepth) {
                throw "The staged tool tree exceeds its depth bound."
            }
            $relativePath = $fullEntryPath.Substring($rootPrefix.Length)
            if ([string]::IsNullOrWhiteSpace($relativePath)) {
                throw "The staged tool tree contains an invalid entry."
            }

            if ($entry -is [System.IO.DirectoryInfo]) {
                $entries.Add([pscustomobject]@{
                    RelativePath = $relativePath
                    FullPath = $fullEntryPath
                    IsDirectory = $true
                    Length = [long]0
                    Sha256 = $null
                })
                $pending.Enqueue([pscustomobject]@{
                    Directory = $entry
                    Depth = $depth
                })
            }
            elseif ($entry -is [System.IO.FileInfo]) {
                if ($entry.Length -lt 0 -or $entry.Length -gt ($MaxTotalBytes - $totalBytes)) {
                    throw "The staged tool tree exceeds its byte bound."
                }
                $totalBytes += $entry.Length
                $entries.Add([pscustomobject]@{
                    RelativePath = $relativePath
                    FullPath = $fullEntryPath
                    IsDirectory = $false
                    Length = [long]$entry.Length
                    Sha256 = Get-RegularFileSha256 -Path $fullEntryPath
                })
            }
            else {
                throw "The staged tool tree contains an unsupported entry."
            }
        }
    }

    return $entries.ToArray()
}

function Assert-EquivalentRegularTrees {
    param(
        [Parameter(Mandatory)]
        [object[]]$Expected,

        [Parameter(Mandatory)]
        [object[]]$Actual
    )

    if ($Expected.Count -ne $Actual.Count) {
        throw "The staged tool tree entry count changed."
    }

    $actualByPath = @{}
    foreach ($entry in $Actual) {
        if ($actualByPath.ContainsKey($entry.RelativePath)) {
            throw "The staged tool tree contains a duplicate path."
        }
        $actualByPath.Add($entry.RelativePath, $entry)
    }

    foreach ($expectedEntry in $Expected) {
        if (-not $actualByPath.ContainsKey($expectedEntry.RelativePath)) {
            throw "The staged tool tree is missing an expected path."
        }
        $actualEntry = $actualByPath[$expectedEntry.RelativePath]
        if ($actualEntry.RelativePath -cne $expectedEntry.RelativePath -or
            $actualEntry.IsDirectory -ne $expectedEntry.IsDirectory -or
            $actualEntry.Length -ne $expectedEntry.Length -or
            $actualEntry.Sha256 -cne $expectedEntry.Sha256) {
            throw "The staged tool tree differs from its exact source."
        }
    }
}

function Copy-RegularTree {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    Assert-RegularDirectory -Path $Source
    Assert-RegularDirectory -Path $Destination
    if (@(Get-ChildItem -LiteralPath $Destination -Force -ErrorAction Stop).Count -ne 0) {
        throw "The staged tool destination is not empty."
    }
    $sourceEntries = @(Get-BoundedRegularTree -Root $Source)
    foreach ($entry in Get-ChildItem -LiteralPath $Source -Force -ErrorAction Stop) {
        Copy-Item -LiteralPath $entry.FullName -Destination $Destination -Recurse -Force -ErrorAction Stop
    }

    Assert-RegularDirectory -Path $Destination
    $destinationEntries = @(Get-BoundedRegularTree -Root $Destination)
    Assert-EquivalentRegularTrees -Expected $sourceEntries -Actual $destinationEntries
}

function Set-StagedToolTreeAcl {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [System.Security.Principal.SecurityIdentifier]$PrimarySid,

        [Parameter(Mandatory)]
        [System.Security.Principal.SecurityIdentifier]$SecondarySid
    )

    $before = @(Get-BoundedRegularTree -Root $Root)
    $secondaryRights = [System.Security.AccessControl.FileSystemRights]::ReadAndExecute
    Set-NumericDirectoryAcl `
        -Path $Root `
        -PrimarySid $PrimarySid `
        -SecondarySid $SecondarySid `
        -SecondaryRights $secondaryRights
    Assert-ExactNumericAcl `
        -Path $Root `
        -PrimarySid $PrimarySid `
        -SecondarySid $SecondarySid `
        -SecondaryRights $secondaryRights `
        -Directory

    foreach ($entry in @($before | Where-Object { $_.IsDirectory })) {
        Set-NumericDirectoryAcl `
            -Path $entry.FullPath `
            -PrimarySid $PrimarySid `
            -SecondarySid $SecondarySid `
            -SecondaryRights $secondaryRights
        Assert-ExactNumericAcl `
            -Path $entry.FullPath `
            -PrimarySid $PrimarySid `
            -SecondarySid $SecondarySid `
            -SecondaryRights $secondaryRights `
            -Directory
    }

    foreach ($entry in @($before | Where-Object { -not $_.IsDirectory })) {
        Set-NumericFileAcl `
            -Path $entry.FullPath `
            -PrimarySid $PrimarySid `
            -SecondarySid $SecondarySid `
            -SecondaryRights $secondaryRights
        Assert-ExactNumericAcl `
            -Path $entry.FullPath `
            -PrimarySid $PrimarySid `
            -SecondarySid $SecondarySid `
            -SecondaryRights $secondaryRights
    }

    $after = @(Get-BoundedRegularTree -Root $Root)
    Assert-EquivalentRegularTrees -Expected $before -Actual $after
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
        throw "A required file hash is invalid."
    }

    return $hash
}

function Wait-ProbeResult {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [IptvSuite.DpapiUserBoundarySmoke.RetainedProcess]$Process
    )

    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path -LiteralPath $Path -PathType Leaf) -and (Get-Date) -lt $deadline) {
        if ($Process.HasExited) {
            throw "The probe exited before publishing its result."
        }

        Start-Sleep -Milliseconds 100
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or $Process.HasExited) {
        throw "The probe result was not published while the process was live."
    }
}

function Test-AllZero {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Value,

        [Parameter(Mandatory)]
        [int]$Offset,

        [Parameter(Mandatory)]
        [int]$Length
    )

    $combined = 0
    for ($index = 0; $index -lt $Length; $index++) {
        $combined = $combined -bor [int]$Value[$Offset + $index]
    }

    return $combined -eq 0
}

function Read-ProbeEvidenceMask {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [Guid]$ExpectedRunId
    )

    Assert-RegularFile -Path $Path
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.Length -ne 100) {
        throw "The probe result length is invalid."
    }

    $encoded = [System.IO.File]::ReadAllBytes($file.FullName)
    try {
        $magic = [System.Text.Encoding]::ASCII.GetString($encoded, 0, 8)
        if ($magic -cne "IPDUBR01" -or $encoded[8] -ne 1 -or
            -not (Test-AllZero -Value $encoded -Offset 9 -Length 3)) {
            throw "The probe result header is invalid."
        }

        $runBytes = New-Object byte[] 16
        [Array]::Copy($encoded, 12, $runBytes, 0, 16)
        [Array]::Reverse($runBytes, 0, 4)
        [Array]::Reverse($runBytes, 4, 2)
        [Array]::Reverse($runBytes, 6, 2)
        $actualRunId = [Guid]::new($runBytes)
        [Array]::Clear($runBytes, 0, $runBytes.Length)
        if ($actualRunId -ne $ExpectedRunId) {
            throw "The probe result run binding is invalid."
        }

        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $digest = $sha256.ComputeHash($encoded, 0, 68)
        }
        finally {
            $sha256.Dispose()
        }

        try {
            $digestMatches = $true
            for ($index = 0; $index -lt 32; $index++) {
                $digestMatches = $digestMatches -and ($digest[$index] -eq $encoded[68 + $index])
            }
            if (-not $digestMatches) {
                throw "The probe result integrity binding is invalid."
            }
        }
        finally {
            [Array]::Clear($digest, 0, $digest.Length)
        }

        [UInt64]$mask = 0
        for ($index = 0; $index -lt 8; $index++) {
            $mask = ($mask -shl 8) -bor [UInt64]$encoded[60 + $index]
        }

        if ($mask -ne $script:requiredProbeEvidenceMask) {
            throw "The probe result evidence mask is incomplete."
        }

        return $mask
    }
    finally {
        [Array]::Clear($encoded, 0, $encoded.Length)
    }
}

function Assert-LiveProbeIdentity {
    param(
        [Parameter(Mandatory)]
        [IptvSuite.DpapiUserBoundarySmoke.RetainedProcess]$Process,

        [Parameter(Mandatory)]
        [string]$ExpectedSid,

        [Parameter(Mandatory)]
        [string]$ExpectedExecutable
    )

    if ($Process.HasExited) {
        throw "The probe is no longer live."
    }

    $instances = @(Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = {0}" -f $Process.ProcessId) -ErrorAction Stop)
    if ($instances.Count -ne 1) {
        throw "The exact probe process is unavailable."
    }

    $instance = $instances[0]
    $owner = Invoke-CimMethod -InputObject $instance -MethodName GetOwnerSid -ErrorAction Stop
    if ([int]$owner.ReturnValue -ne 0 -or
        -not [string]::Equals([string]$owner.Sid, $ExpectedSid, [System.StringComparison]::Ordinal)) {
        throw "The probe process owner is unexpected."
    }

    $actualExecutable = [System.IO.Path]::GetFullPath([string]$instance.ExecutablePath)
    if (-not $actualExecutable.Equals(
            [System.IO.Path]::GetFullPath($ExpectedExecutable),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The probe process executable is unexpected."
    }

    $cimStart = ([DateTime]$instance.CreationDate).ToUniversalTime().ToFileTimeUtc()
    $delta = [Math]::Abs([double]($cimStart - $Process.CreationTimeFileTimeUtc))
    if ($delta -gt [TimeSpan]::FromSeconds(2).Ticks) {
        throw "The probe process start binding is unexpected."
    }
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
        $script:cleanupFailures.Add($Code)
    }
}

function Set-CleanupFailurePoint {
    $priority = @(
        "ProcessCleanupFailed",
        "ProfileCleanupFailed",
        "GroupCleanupFailed",
        "AccountCleanupFailed",
        "WorkspaceCleanupFailed"
    )
    $matched = @($priority | Where-Object { $script:cleanupFailures.Contains($_) })
    $script:failureStage = "Cleanup"
    if ($script:cleanupFailures.Count -eq 1 -and $matched.Count -eq 1) {
        $script:failureCode = $matched[0]
    }
    else {
        $script:failureCode = "MultipleCleanupFailures"
    }
}

function Get-ExactUserProfile {
    param(
        [Parameter(Mandatory)]
        [string]$Sid,

        [Parameter(Mandatory)]
        [string]$UserName
    )

    $escapedSid = $Sid.Replace("'", "''")
    $profiles = @(Get-CimInstance -ClassName Win32_UserProfile -Filter ("SID = '{0}'" -f $escapedSid) -ErrorAction Stop)
    if ($profiles.Count -eq 0) {
        return $null
    }
    if ($profiles.Count -ne 1) {
        throw "The exact test profile is ambiguous."
    }

    $profile = $profiles[0]
    if ([bool]$profile.Special) {
        throw "The exact test profile is special."
    }

    $expectedProfilePath = Get-ExpectedUserProfilePath -UserName $UserName
    $profilePath = [System.IO.Path]::GetFullPath([string]$profile.LocalPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar)
    if (-not $profilePath.Equals($expectedProfilePath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The exact test profile path is unexpected."
    }

    return $profile
}

function Get-ExpectedUserProfilePath {
    param(
        [Parameter(Mandatory)]
        [string]$UserName
    )

    $profilesDirectoryValue = Get-ItemPropertyValue `
        -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList" `
        -Name "ProfilesDirectory" `
        -ErrorAction Stop
    $profilesDirectory = [System.IO.Path]::GetFullPath(
        [Environment]::ExpandEnvironmentVariables([string]$profilesDirectoryValue)).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar)
    Assert-RegularDirectory -Path $profilesDirectory
    $expectedProfilePath = [System.IO.Path]::GetFullPath(
        (Join-Path $profilesDirectory $UserName)).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar)
    $profileParent = [System.IO.Directory]::GetParent($expectedProfilePath)
    if ($null -eq $profileParent -or
        -not $profileParent.FullName.Equals($profilesDirectory, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($expectedProfilePath).Equals($UserName, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The exact test profile path is unexpected."
    }

    return $expectedProfilePath
}

function Assert-ProfilePathAbsent {
    param(
        [Parameter(Mandatory)]
        [string]$UserName
    )

    $expectedProfilePath = Get-ExpectedUserProfilePath -UserName $UserName
    $profileParent = [System.IO.Directory]::GetParent($expectedProfilePath)
    if ($null -eq $profileParent) {
        throw "The exact synthetic profile parent is unavailable."
    }
    Assert-RegularDirectory -Path $profileParent.FullName
    $profileRemnants = @(Get-ChildItem `
        -LiteralPath $profileParent.FullName `
        -Force `
        -ErrorAction Stop | Where-Object {
            [System.IO.Path]::GetFullPath($_.FullName).Equals(
                $expectedProfilePath,
                [System.StringComparison]::OrdinalIgnoreCase)
        })
    if ($profileRemnants.Count -ne 0) {
        throw "The exact synthetic profile path is already occupied."
    }

    return $expectedProfilePath
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
    $temporaryPath = Join-Path $directory ("staging-" + [Guid]::NewGuid().ToString("N") + ".json")
    try {
        $Value | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
        Assert-RegularFile -Path $temporaryPath
        Move-Item -LiteralPath $temporaryPath -Destination $DestinationPath -Force -ErrorAction Stop
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Assert-RegularFile -Path $temporaryPath
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction Stop
        }
    }
}

try {
    Set-FailurePoint -Stage "WorkspaceValidation" -Code "ArtifactWorkspaceRejected"
    Assert-RegularDirectory -Path $repositoryRoot
    Assert-RegularFile -Path $PSCommandPath
    if (-not [System.IO.Path]::GetFullPath($PSCommandPath).Equals(
            [System.IO.Path]::GetFullPath($expectedControllerScriptPath),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The controller script path is unexpected."
    }
    New-RegularDirectory -Path (Join-Path $repositoryRoot ".artifacts")
    New-RegularDirectory -Path $artifactRoot
    foreach ($staleEvidence in @($successEvidencePath, $failureEvidencePath)) {
        if (Test-Path -LiteralPath $staleEvidence) {
            Assert-RegularFile -Path $staleEvidence
            Remove-Item -LiteralPath $staleEvidence -Force -ErrorAction Stop
        }
    }

    Set-FailurePoint -Stage "HostValidation" -Code "WindowsPowerShell51Required"
    if ($PSVersionTable.PSEdition -ne "Desktop" -or $PSVersionTable.PSVersion.Major -ne 5) {
        throw "Windows PowerShell 5.1 is required."
    }

    if (-not [Environment]::Is64BitProcess) {
        throw "A 64-bit controller is required."
    }

    Set-FailurePoint -Stage "HostValidation" -Code "ElevationRequired"
    $currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $currentPrincipal = [System.Security.Principal.WindowsPrincipal]::new($currentIdentity)
        if (-not $currentPrincipal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator) -or
            $null -eq $currentIdentity.User) {
            throw "An elevated Windows identity is required."
        }

        $primarySid = $currentIdentity.User
    }
    finally {
        $currentIdentity.Dispose()
    }

    Set-FailurePoint -Stage "NativeHostValidation" -Code "NativeHostUnavailable"
    Add-Type -TypeDefinition $nativeSource -Language CSharp -ErrorAction Stop

    Set-FailurePoint -Stage "SdkValidation" -Code "SdkMismatch"
    $globalJson = Get-Content -Raw -LiteralPath $globalJsonPath | ConvertFrom-Json -ErrorAction Stop
    if ($globalJson.sdk.rollForward -ne "disable" -or $globalJson.sdk.allowPrerelease -ne $false) {
        throw "The exact stable SDK contract is not configured."
    }

    $dotNetCommand = Get-Command -Name $DotNetPath -CommandType Application -ErrorAction Stop |
        Select-Object -First 1
    $dotNetExecutable = [System.IO.Path]::GetFullPath($dotNetCommand.Source)
    Assert-RegularFile -Path $dotNetExecutable
    Assert-NoReparsePath -Path $dotNetExecutable
    $actualSdkOutput = @(& $dotNetExecutable --version 2>$null)
    if ($LASTEXITCODE -ne 0 -or $actualSdkOutput.Count -ne 1) {
        throw "The exact repository SDK is unavailable."
    }
    $actualSdk = $actualSdkOutput[0].Trim()
    if ($actualSdk -ne [string]$globalJson.sdk.version) {
        throw "The exact repository SDK is unavailable."
    }

    Set-FailurePoint -Stage "RepositoryBinding" -Code "RepositoryDirty"
    if ((Get-RepositoryStatus).Count -ne 0) {
        throw "The repository worktree is not clean."
    }

    $repositoryHead = Get-RepositoryHead
    $controllerScriptSha256 = Get-RegularFileSha256 -Path $PSCommandPath
    $githubSha = [Environment]::GetEnvironmentVariable("GITHUB_SHA", "Process")
    if (-not [string]::IsNullOrWhiteSpace($githubSha) -and
        ($githubSha -notmatch '\A[0-9a-fA-F]{40}\z' -or
         -not $repositoryHead.Equals($githubSha, [System.StringComparison]::OrdinalIgnoreCase))) {
        $failureCode = "RepositoryHeadMismatch"
        throw "The workflow commit is not bound to repository HEAD."
    }

    Set-FailurePoint -Stage "HarnessBuild" -Code "HarnessRestoreFailed"
    Invoke-CheckedDotNet -FailureCode "HarnessRestoreFailed" -Arguments @(
        "restore",
        $harnessProjectPath,
        "--locked-mode",
        "--configfile", $nuGetConfigPath,
        "-p:Platform=x64",
        "--disable-parallel",
        "--nologo"
    )

    Invoke-CheckedDotNet -FailureCode "HarnessBuildFailed" -Arguments @(
        "build",
        $harnessProjectPath,
        "-c", $Configuration,
        "-p:Platform=x64",
        "--no-restore",
        "--no-incremental",
        "-maxcpucount:1",
        "-p:UseSharedCompilation=false",
        "--nologo"
    )

    Invoke-CheckedDotNet -FailureCode "ScannerBuildFailed" -Arguments @(
        "build",
        $testingProjectPath,
        "-c", $Configuration,
        "--no-restore",
        "--no-incremental",
        "-maxcpucount:1",
        "-p:UseSharedCompilation=false",
        "--nologo"
    )

    Assert-RegularDirectory -Path $harnessOutputDirectory
    Assert-RegularFile -Path (Join-Path $harnessOutputDirectory $harnessAssemblyName)
    Assert-RegularFile -Path (Join-Path $harnessOutputDirectory "IptvSuite.DpapiUserBoundaryHarness.deps.json")
    Assert-RegularFile -Path (Join-Path $harnessOutputDirectory "IptvSuite.DpapiUserBoundaryHarness.runtimeconfig.json")
    Assert-RegularFile -Path $testingToolPath

    Set-FailurePoint -Stage "AccountPreparation" -Code "StaleSyntheticAccountDetected"
    $accountPrefix = "iptvm4b"
    $staleAccounts = @(Get-LocalUser -ErrorAction Stop | Where-Object {
        $_.Name.StartsWith($accountPrefix, [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($staleAccounts.Count -ne 0) {
        throw "A stale synthetic boundary account requires manual review."
    }

    $createdUserName = $accountPrefix + $runIdText.Substring(0, 12)
    if ($createdUserName.Length -gt 20) {
        throw "The synthetic local account name is invalid."
    }
    [void](Assert-ProfilePathAbsent -UserName $createdUserName)

    $securePassword = New-RandomSecurePassword
    $createdUser = New-LocalUser `
        -Name $createdUserName `
        -Password $securePassword `
        -AccountNeverExpires `
        -PasswordNeverExpires `
        -UserMayNotChangePassword `
        -Description $accountDescription `
        -ErrorAction Stop
    $accountCreated = $true
    if ($null -eq $createdUser.SID -or -not $createdUser.Enabled) {
        $failureCode = "SyntheticAccountInvalid"
        throw "The synthetic local account is invalid."
    }

    $createdUserSid = $createdUser.SID
    if ($createdUserSid.Equals($primarySid) -or
        $createdUserSid.IsWellKnown([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid)) {
        $failureCode = "SyntheticAccountIdentityInvalid"
        throw "The synthetic local account identity is invalid."
    }

    Set-FailurePoint -Stage "AccountPreparation" -Code "StandardUsersMembershipFailed"
    $usersGroup = Get-LocalGroup -SID $usersSid -ErrorAction Stop
    $usersMembership = @(Get-LocalGroupMember -Group $usersGroup -ErrorAction Stop | Where-Object {
        $null -ne $_.SID -and $_.SID.Equals($createdUserSid)
    })
    if ($usersMembership.Count -eq 0) {
        Add-LocalGroupMember -Group $usersGroup -Member $createdUser -ErrorAction Stop
    }

    $usersMembership = @(Get-LocalGroupMember -Group $usersGroup -ErrorAction Stop | Where-Object {
        $null -ne $_.SID -and $_.SID.Equals($createdUserSid)
    })
    if ($usersMembership.Count -ne 1) {
        throw "The synthetic account is not an exact standard Users member."
    }
    $usersMembershipPresent = $true

    $administratorsGroup = Get-LocalGroup -SID $administratorsSid -ErrorAction Stop
    $administratorMembership = @(Get-LocalGroupMember -Group $administratorsGroup -ErrorAction Stop | Where-Object {
        $null -ne $_.SID -and $_.SID.Equals($createdUserSid)
    })
    if ($administratorMembership.Count -ne 0) {
        $failureCode = "SyntheticAccountIsAdministrator"
        throw "The synthetic account must not be an administrator."
    }

    Set-FailurePoint -Stage "ToolStaging" -Code "ProgramDataWorkspaceRejected"
    Assert-RegularDirectory -Path $programData
    New-RegularDirectory -Path (Join-Path $programData "ProtectedStoreTests")
    New-RegularDirectory -Path (Join-Path $programData "ProtectedStoreTests\DpapiUserBoundary")
    New-RegularDirectory -Path $workspaceBase
    New-RegularDirectory -Path $runsRoot
    New-RegularDirectory -Path $toolsRoot
    if ((Test-Path -LiteralPath $runRoot) -or (Test-Path -LiteralPath $toolRoot)) {
        throw "A fresh exact run workspace is required."
    }

    New-RegularDirectory -Path $runRoot
    $runRootCreated = $true
    if (@(Get-ChildItem -LiteralPath $runRoot -Force).Count -ne 0) {
        throw "The exact run workspace is not empty."
    }

    New-RegularDirectory -Path $toolRoot
    $toolRootCreated = $true
    Set-NumericDirectoryAcl `
        -Path $runRoot `
        -PrimarySid $primarySid
    Set-NumericDirectoryAcl `
        -Path $toolRoot `
        -PrimarySid $primarySid `
        -SecondarySid $createdUserSid `
        -SecondaryRights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute)
    Copy-RegularTree -Source $harnessOutputDirectory -Destination $toolRoot
    Set-FailurePoint -Stage "ToolStaging" -Code "StagedToolAclFailed"
    Set-StagedToolTreeAcl `
        -Root $toolRoot `
        -PrimarySid $primarySid `
        -SecondarySid $createdUserSid
    Assert-RegularFile -Path $stagedHarnessPath
    $stagedHarnessSha256 = Get-RegularFileSha256 -Path $stagedHarnessPath

    Set-FailurePoint -Stage "PrimaryPrepare" -Code "PrimaryPrepareFailed"
    Invoke-HarnessPrimary -FailureCode "PrimaryPrepareFailed" -Arguments @(
        "prepare-primary",
        [System.IO.Path]::GetFullPath($runRoot),
        $createdUserSid.Value
    )

    Set-FailurePoint -Stage "WorkspaceAuthorization" -Code "PreparedLayoutInvalid"
    $inputPath = Join-Path $runRoot "input"
    $primaryStorePath = Join-Path $runRoot "primary-store"
    $resultPath = Join-Path $runRoot "result"
    $secondaryStorePath = Join-Path $runRoot "secondary-store"
    foreach ($directory in @($inputPath, $primaryStorePath, $resultPath, $secondaryStorePath)) {
        Assert-RegularDirectory -Path $directory
    }

    $boundaryTicketPath = Join-Path $inputPath "boundary-ticket.bin"
    $primaryRawPath = Join-Path $inputPath "primary-raw.dpapi"
    Assert-RegularFile -Path $boundaryTicketPath
    Assert-RegularFile -Path $primaryRawPath
    $primaryStoreEntries = @(Get-ChildItem -LiteralPath $primaryStorePath -Force -ErrorAction Stop)
    if ($primaryStoreEntries.Count -ne 1 -or
        $primaryStoreEntries[0] -isnot [System.IO.FileInfo] -or
        $primaryStoreEntries[0].Name -cnotmatch '\Arecord-v2-[0-9A-F]{64}\.dpapi\z') {
        throw "The prepared primary-store layout is invalid."
    }
    $primaryRecordPath = $primaryStoreEntries[0].FullName
    Assert-RegularFile -Path $primaryRecordPath

    Set-NumericDirectoryAcl `
        -Path $runRoot `
        -PrimarySid $primarySid `
        -SecondarySid $createdUserSid `
        -SecondaryRights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute)
    Set-NumericDirectoryAcl `
        -Path $inputPath `
        -PrimarySid $primarySid `
        -SecondarySid $createdUserSid `
        -SecondaryRights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute)
    Set-NumericDirectoryAcl `
        -Path $primaryStorePath `
        -PrimarySid $primarySid `
        -SecondarySid $createdUserSid `
        -SecondaryRights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute)
    Set-NumericDirectoryAcl `
        -Path $resultPath `
        -PrimarySid $primarySid `
        -SecondarySid $createdUserSid `
        -SecondaryRights ([System.Security.AccessControl.FileSystemRights]::Modify)
    Set-NumericDirectoryAcl `
        -Path $secondaryStorePath `
        -PrimarySid $primarySid `
        -SecondarySid $createdUserSid `
        -SecondaryRights ([System.Security.AccessControl.FileSystemRights]::Modify)
    Set-FailurePoint -Stage "WorkspaceAuthorization" -Code "PreparedFileAclFailed"
    foreach ($preparedInputPath in @($boundaryTicketPath, $primaryRawPath, $primaryRecordPath)) {
        Set-NumericFileAcl `
            -Path $preparedInputPath `
            -PrimarySid $primarySid `
            -SecondarySid $createdUserSid `
            -SecondaryRights ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute)
    }

    Set-FailurePoint -Stage "SecondaryProbeLaunch" -Code "SecondaryProbeLaunchFailed"
    $probeProcess = [IptvSuite.DpapiUserBoundarySmoke.NativeBoundaryHost]::Launch(
        $createdUserName,
        $securePassword,
        $dotNetExecutable,
        @($stagedHarnessPath, "probe-secondary", [System.IO.Path]::GetFullPath($runRoot)),
        [System.IO.Path]::GetFullPath($runRoot))

    Set-FailurePoint -Stage "SecondaryProbeHandshake" -Code "ProbeResultTimedOut"
    Wait-ProbeResult -Path $probeResultPath -Process $probeProcess

    Set-FailurePoint -Stage "SecondaryProbeIdentity" -Code "ProbeIdentityMismatch"
    Assert-LiveProbeIdentity `
        -Process $probeProcess `
        -ExpectedSid $createdUserSid.Value `
        -ExpectedExecutable $dotNetExecutable
    $profile = Get-ExactUserProfile -Sid $createdUserSid.Value -UserName $createdUserName
    if ($null -eq $profile -or -not [bool]$profile.Loaded) {
        throw "The alternate-user profile was not loaded for the live probe."
    }
    $profileObserved = $true

    Set-FailurePoint -Stage "SecondaryProbeResult" -Code "ProbeResultRejected"
    $probeEvidenceMask = Read-ProbeEvidenceMask -Path $probeResultPath -ExpectedRunId $runId
    if (($probeEvidenceMask -band [UInt64]4) -eq 0) {
        throw "The probe did not prove a non-administrator token."
    }

    Set-FailurePoint -Stage "PrimaryVerification" -Code "PrimaryVerificationFailed"
    Invoke-HarnessPrimary -FailureCode "PrimaryVerificationFailed" -Arguments @(
        "verify-primary",
        [System.IO.Path]::GetFullPath($runRoot)
    )

    Set-FailurePoint -Stage "SecondaryProbeExit" -Code "SecondaryProbeExitTimedOut"
    if (-not $probeProcess.WaitForExit(15000)) {
        throw "The alternate-user probe did not exit after the harness release."
    }
    if ($probeProcess.GetExitCode() -ne 0) {
        $failureCode = "SecondaryProbeFailed"
        throw "The alternate-user probe returned a fixed failure exit code."
    }

    $successCandidate = [ordered]@{
        SchemaVersion = 1
        Milestone = "M4"
        EvidenceKind = "dpapi-current-user-boundary"
        Configuration = $Configuration
        Platform = "x64"
        DataProtectionScope = "CurrentUser"
        ExactSdkVerified = $true
        DotNetSdk = $actualSdk
        CleanHeadBound = $true
        CommitSha = $repositoryHead
        ControllerScriptSha256 = $controllerScriptSha256
        HarnessAssemblySha256 = $stagedHarnessSha256
        DistinctWindowsAccountVerified = $true
        StandardUsersMembershipVerified = $true
        SecondaryTokenNonAdministrator = $true
        NumericSidAclApplied = $true
        LogonWithProfileUsed = $true
        NetCredentialsOnlyForbidden = $true
        CreateNoWindowUsed = $true
        ProbeProcessOwnerVerified = $true
        ProbeProcessStartVerified = $true
        ProfileLoadedForProbe = $true
        RawInputDigestMatched = $true
        RecordInputDigestMatched = $true
        SecondaryRawRoundTripPassed = $true
        CreatorRawRejectedCryptographically = $true
        SecondaryAdapterRoundTripPassed = $true
        SecondaryStoreClean = $true
        CreatorRecordUnavailable = $true
        CreatorRecordLeaseAbsent = $true
        CreatorRecordImmutable = $true
        OwnedDataCanaryScanPassed = $true
        PrimaryVerificationPassed = $true
        ProbeExitedSuccessfully = $true
    }
}
catch {
    $primaryFailure = $true
}
finally {
    Invoke-CleanupStep -Code "ProcessCleanupFailed" -Action {
        if ($null -ne $script:probeProcess) {
            try {
                if (-not $script:probeProcess.HasExited) {
                    $script:probeProcess.Terminate(18)
                }
            }
            finally {
                $script:probeProcess.Dispose()
                $script:probeProcess = $null
            }
        }
        $script:processCleanupPassed = $true
    }

    Invoke-CleanupStep -Code "ProfileCleanupFailed" -Action {
        if ($null -ne $script:createdUserSid -and -not [string]::IsNullOrWhiteSpace($script:createdUserName)) {
            $exactProfilePath = Get-ExpectedUserProfilePath -UserName $script:createdUserName
            $deadline = (Get-Date).AddSeconds(15)
            $profile = Get-ExactUserProfile `
                -Sid $script:createdUserSid.Value `
                -UserName $script:createdUserName
            while ($null -ne $profile -and [bool]$profile.Loaded -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 250
                $profile = Get-ExactUserProfile `
                    -Sid $script:createdUserSid.Value `
                    -UserName $script:createdUserName
            }

            if ($null -ne $profile) {
                if ([bool]$profile.Loaded) {
                    throw "The exact synthetic profile remains loaded."
                }

                $exactProfilePath = [System.IO.Path]::GetFullPath(
                    [string]$profile.LocalPath).TrimEnd(
                        [System.IO.Path]::DirectorySeparatorChar)

                if (-not [IptvSuite.DpapiUserBoundarySmoke.NativeBoundaryHost]::DeleteProfile(
                        $script:createdUserSid.Value,
                        $exactProfilePath)) {
                    throw "The exact synthetic profile could not be deleted."
                }

                $deleteDeadline = (Get-Date).AddSeconds(15)
                do {
                    Start-Sleep -Milliseconds 250
                    $profile = Get-ExactUserProfile `
                        -Sid $script:createdUserSid.Value `
                        -UserName $script:createdUserName
                } while ($null -ne $profile -and (Get-Date) -lt $deleteDeadline)
            }

            if ($null -ne $profile) {
                throw "The exact synthetic profile remains."
            }

            $absentProfilePath = Assert-ProfilePathAbsent -UserName $script:createdUserName
            if (-not $absentProfilePath.Equals(
                    $exactProfilePath,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "The exact synthetic profile path changed during cleanup."
            }
        }
        $script:profileCleanupPassed = $true
    }

    Invoke-CleanupStep -Code "GroupCleanupFailed" -Action {
        if (-not $script:profileCleanupPassed) {
            throw "Profile cleanup must complete before membership cleanup."
        }

        if ($script:usersMembershipPresent -and $null -ne $script:createdUserSid) {
            $group = Get-LocalGroup -SID $script:usersSid -ErrorAction Stop
            $membership = @(Get-LocalGroupMember -Group $group -ErrorAction Stop | Where-Object {
                $null -ne $_.SID -and $_.SID.Equals($script:createdUserSid)
            })
            if ($membership.Count -gt 1) {
                throw "The synthetic group membership is ambiguous."
            }
            if ($membership.Count -eq 1) {
                Remove-LocalGroupMember -Group $group -Member $membership[0] -Confirm:$false -ErrorAction Stop
            }

            $remaining = @(Get-LocalGroupMember -Group $group -ErrorAction Stop | Where-Object {
                $null -ne $_.SID -and $_.SID.Equals($script:createdUserSid)
            })
            if ($remaining.Count -ne 0) {
                throw "The exact synthetic group membership remains."
            }
        }
        $script:groupCleanupPassed = $true
    }

    Invoke-CleanupStep -Code "AccountCleanupFailed" -Action {
        if (-not $script:profileCleanupPassed -or -not $script:groupCleanupPassed) {
            throw "Profile and membership cleanup must complete before account cleanup."
        }

        if ($script:accountCreated) {
            $candidates = @(Get-LocalUser -ErrorAction Stop | Where-Object {
                $_.Name.Equals(
                    $script:createdUserName,
                    [System.StringComparison]::OrdinalIgnoreCase)
            })
            if ($candidates.Count -gt 1) {
                throw "The exact synthetic local account is ambiguous."
            }
            if ($candidates.Count -eq 1) {
                $candidate = $candidates[0]
                if ($null -eq $candidate.SID -or
                    -not $candidate.SID.Equals($script:createdUserSid) -or
                    -not [string]::Equals(
                        [string]$candidate.Description,
                        $script:accountDescription,
                        [System.StringComparison]::Ordinal)) {
                    throw "Refusing to remove an unexpected local account."
                }

                Remove-LocalUser -InputObject $candidate -Confirm:$false -ErrorAction Stop
            }

            $remainingAccounts = @(Get-LocalUser -ErrorAction Stop | Where-Object {
                $_.Name.Equals(
                    $script:createdUserName,
                    [System.StringComparison]::OrdinalIgnoreCase)
            })
            if ($remainingAccounts.Count -ne 0) {
                throw "The exact synthetic local account remains."
            }
        }
        $script:accountCleanupPassed = $true
    }

    if ($null -ne $securePassword) {
        $securePassword.Dispose()
        $securePassword = $null
    }

    Invoke-CleanupStep -Code "WorkspaceCleanupFailed" -Action {
        if ($script:runRootCreated) {
            Remove-ExactOwnedTree -Path $script:runRoot -ExpectedParent $script:runsRoot
        }
        if ($script:toolRootCreated) {
            Remove-ExactOwnedTree -Path $script:toolRoot -ExpectedParent $script:toolsRoot
        }

        if ((Test-Path -LiteralPath $script:runRoot) -or (Test-Path -LiteralPath $script:toolRoot)) {
            throw "An exact boundary workspace remains."
        }

        Remove-EmptyOwnedDirectory -Path $script:runsRoot -ExpectedParent $script:workspaceBase
        Remove-EmptyOwnedDirectory -Path $script:toolsRoot -ExpectedParent $script:workspaceBase
        $script:workspaceCleanupPassed = $true
    }
}

if ($cleanupFailures.Count -ne 0) {
    Set-CleanupFailurePoint
}

if (-not $primaryFailure -and $cleanupFailures.Count -eq 0) {
    Set-FailurePoint -Stage "RepositoryBinding" -Code "RepositoryChanged"
    if ((Get-RepositoryStatus).Count -ne 0 -or (Get-RepositoryHead) -ne $repositoryHead) {
        $primaryFailure = $true
    }
}

if (-not $primaryFailure -and $cleanupFailures.Count -eq 0) {
    if ($null -eq $successCandidate -or
        -not $processCleanupPassed -or
        -not $groupCleanupPassed -or
        -not $accountCleanupPassed -or
        -not $profileCleanupPassed -or
        -not $workspaceCleanupPassed -or
        -not $profileObserved) {
        $failureStage = "Cleanup"
        $failureCode = "CleanupEvidenceIncomplete"
        $primaryFailure = $true
    }
}

if (-not $primaryFailure -and $cleanupFailures.Count -eq 0) {
    $successCandidate["ProcessCleanupPassed"] = $true
    $successCandidate["StandardUsersMembershipRemoved"] = $true
    $successCandidate["LocalAccountRemoved"] = $true
    $successCandidate["ProfileRemoved"] = $true
    $successCandidate["RunWorkspaceRemoved"] = $true
    $successCandidate["ToolWorkspaceRemoved"] = $true
    $successCandidate["RepositoryCleanAfterRun"] = $true
    $successCandidate["EvidenceCanaryScanPassed"] = $true

    Set-FailurePoint -Stage "EvidenceScan" -Code "EvidenceCanaryScanFailed"
    $evidenceStagingRoot = Join-Path $artifactRoot ("evidence-staging-" + [Guid]::NewGuid().ToString("N"))
    $stagedEvidencePath = Join-Path $evidenceStagingRoot "last-success.json"
    try {
        New-RegularDirectory -Path $evidenceStagingRoot
        Write-JsonAtomically -Value $successCandidate -DestinationPath $stagedEvidencePath
        foreach ($caseId in @("PRIMARY_PAYLOAD", "SECONDARY_PAYLOAD")) {
            & $dotNetExecutable $testingToolPath `
                scan-artifacts $evidenceStagingRoot M4_DPAPI_USER_BOUNDARY $caseId | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "The sanitized evidence failed its canary scan."
            }
        }

        Assert-RegularFile -Path $stagedEvidencePath
        if (Test-Path -LiteralPath $successEvidencePath) {
            Assert-RegularFile -Path $successEvidencePath
        }
        Move-Item -LiteralPath $stagedEvidencePath -Destination $successEvidencePath -Force -ErrorAction Stop
    }
    catch {
        $primaryFailure = $true
    }
    finally {
        if (Test-Path -LiteralPath $evidenceStagingRoot) {
            Remove-ExactOwnedTree -Path $evidenceStagingRoot -ExpectedParent $artifactRoot
        }
    }
}

if ($primaryFailure -or $cleanupFailures.Count -ne 0) {
    if ($cleanupFailures.Count -ne 0) {
        Set-CleanupFailurePoint
    }

    $failureEvidence = [ordered]@{
        Stage = $failureStage
        Code = $failureCode
    }
    try {
        New-RegularDirectory -Path (Join-Path $repositoryRoot ".artifacts")
        New-RegularDirectory -Path $artifactRoot
        Write-JsonAtomically -Value $failureEvidence -DestinationPath $failureEvidencePath
    }
    catch {
        throw "DPAPI user-boundary smoke failed and stable failure evidence could not be written."
    }

    throw "DPAPI user-boundary smoke failed at $failureStage ($failureCode)."
}

Assert-RegularFile -Path $successEvidencePath
Write-Host "DPAPI CurrentUser real-account boundary smoke passed."
