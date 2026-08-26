#requires -Version 5.1

Set-StrictMode -Version Latest

$script:windowsWackMaximumReportBytes = 16MB
$script:windowsWackMaximumToolBytes = 512MB
$script:windowsWackMaximumProcessOutputBytes = 4MB
$script:windowsWackMaximumXmlNodes = 250000
$script:windowsWackMaximumXmlDepth = 128
$script:windowsWackMaximumXmlAttributes = 128

if ($null -eq ('IptvSuite.WindowsWack.BoundedProcessFileCapture' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace IptvSuite.WindowsWack
{
    public sealed class BoundedProcessFileCaptureResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public bool StandardOutputExceeded { get; set; }
        public bool StandardErrorExceeded { get; set; }
        public bool CaptureFailed { get; set; }
        public long StandardOutputLength { get; set; }
        public long StandardErrorLength { get; set; }
    }

    public static class BoundedProcessFileCapture
    {
        private sealed class CaptureState
        {
            internal Process Process;
            internal int ExpectedStop;
            internal int StandardOutputExceeded;
            internal int StandardErrorExceeded;
            internal int CaptureFailed;
        }

        private static void Stop(CaptureState state)
        {
            Interlocked.Exchange(ref state.ExpectedStop, 1);
            try
            {
                if (!state.Process.HasExited)
                {
                    state.Process.Kill();
                }
            }
            catch
            {
            }
        }

        private static void CopyBounded(
            Stream source,
            FileStream destination,
            int maximumBytes,
            CaptureState state,
            bool standardOutput)
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (true)
                {
                    int read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        destination.Flush();
                        return;
                    }

                    int remaining = maximumBytes - checked((int)destination.Length);
                    if (read > remaining)
                    {
                        if (remaining > 0)
                        {
                            destination.Write(buffer, 0, remaining);
                        }
                        if (standardOutput)
                        {
                            Interlocked.Exchange(ref state.StandardOutputExceeded, 1);
                        }
                        else
                        {
                            Interlocked.Exchange(ref state.StandardErrorExceeded, 1);
                        }
                        destination.Flush();
                        Stop(state);
                        return;
                    }
                    destination.Write(buffer, 0, read);
                }
            }
            catch
            {
                if (Interlocked.CompareExchange(ref state.ExpectedStop, 0, 0) == 0)
                {
                    Interlocked.Exchange(ref state.CaptureFailed, 1);
                    Stop(state);
                }
            }
            finally
            {
                Array.Clear(buffer, 0, buffer.Length);
            }
        }

        public static BoundedProcessFileCaptureResult Run(
            string filePath,
            string arguments,
            string workingDirectory,
            int timeoutMilliseconds,
            string standardOutputPath,
            string standardErrorPath,
            int maximumStandardOutputBytes,
            int maximumStandardErrorBytes)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = filePath;
            startInfo.Arguments = arguments;
            startInfo.WorkingDirectory = workingDirectory;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            using (FileStream standardOutput = new FileStream(
                standardOutputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            using (FileStream standardError = new FileStream(
                standardErrorPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                if (!process.Start())
                {
                    throw new InvalidOperationException("The process did not start.");
                }

                CaptureState state = new CaptureState();
                state.Process = process;
                Thread outputReader = new Thread(delegate()
                {
                    CopyBounded(
                        process.StandardOutput.BaseStream,
                        standardOutput,
                        maximumStandardOutputBytes,
                        state,
                        true);
                });
                Thread errorReader = new Thread(delegate()
                {
                    CopyBounded(
                        process.StandardError.BaseStream,
                        standardError,
                        maximumStandardErrorBytes,
                        state,
                        false);
                });
                outputReader.IsBackground = true;
                errorReader.IsBackground = true;
                outputReader.Start();
                errorReader.Start();

                bool timedOut = !process.WaitForExit(timeoutMilliseconds);
                if (timedOut)
                {
                    Stop(state);
                    process.WaitForExit(10000);
                }

                bool outputJoined = outputReader.Join(10000);
                bool errorJoined = errorReader.Join(10000);
                if (!outputJoined || !errorJoined)
                {
                    Interlocked.Exchange(ref state.CaptureFailed, 1);
                    Stop(state);
                    outputReader.Join(1000);
                    errorReader.Join(1000);
                }

                int exitCode = -1;
                try
                {
                    if (process.HasExited)
                    {
                        exitCode = process.ExitCode;
                    }
                    else
                    {
                        Interlocked.Exchange(ref state.CaptureFailed, 1);
                    }
                }
                catch
                {
                    Interlocked.Exchange(ref state.CaptureFailed, 1);
                }

                BoundedProcessFileCaptureResult result = new BoundedProcessFileCaptureResult();
                result.ExitCode = exitCode;
                result.TimedOut = timedOut;
                result.StandardOutputExceeded =
                    Interlocked.CompareExchange(ref state.StandardOutputExceeded, 0, 0) != 0;
                result.StandardErrorExceeded =
                    Interlocked.CompareExchange(ref state.StandardErrorExceeded, 0, 0) != 0;
                result.CaptureFailed =
                    Interlocked.CompareExchange(ref state.CaptureFailed, 0, 0) != 0;
                result.StandardOutputLength = standardOutput.Length;
                result.StandardErrorLength = standardError.Length;
                return result;
            }
        }
    }
}
"@
}

function Fail-WindowsWack {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[A-Za-z][A-Za-z0-9]+$')]
        [string]$Code
    )

    throw "WindowsWack:$Code"
}

function Assert-WindowsWackCondition {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Code
    )

    if (-not $Condition) {
        Fail-WindowsWack -Code $Code
    }
}

function Test-WindowsWackStableError {
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    return $ErrorRecord.Exception.Message -clike 'WindowsWack:*'
}

function ConvertTo-WindowsWackFullPath {
    param(
        [Parameter(Mandatory)]
        [object]$Path,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $values = @($Path)
    Assert-WindowsWackCondition `
        ($values.Count -eq 1 -and $values[0] -is [string] -and
            -not [string]::IsNullOrWhiteSpace([string]$values[0])) `
        $Code
    $provider = $null
    $drive = $null
    try {
        $providerPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
            [string]$values[0],
            [ref]$provider,
            [ref]$drive)
    }
    catch {
        Fail-WindowsWack -Code $Code
    }
    Assert-WindowsWackCondition `
        ($null -ne $provider -and $provider.Name -ceq 'FileSystem') `
        $Code
    try {
        $fullPath = [System.IO.Path]::GetFullPath($providerPath)
    }
    catch {
        Fail-WindowsWack -Code $Code
    }
    Assert-WindowsWackCondition ([System.IO.Path]::IsPathRooted($fullPath)) $Code
    return $fullPath
}

function Resolve-WindowsWackArtifactRoot {
    param(
        [Parameter(Mandatory)]
        [object]$ArtifactRoot
    )

    $fullPath = ConvertTo-WindowsWackFullPath `
        -Path $ArtifactRoot `
        -Code 'ArtifactRootInvalid'
    try {
        $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    }
    catch {
        Fail-WindowsWack -Code 'ArtifactRootInvalid'
    }
    Assert-WindowsWackCondition `
        ($item -is [System.IO.DirectoryInfo] -and $item.PSIsContainer) `
        'ArtifactRootInvalid'
    Assert-WindowsWackCondition `
        (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        'ArtifactRootInvalid'
    return $item
}

function Assert-WindowsWackPathWithinRoot {
    param(
        [Parameter(Mandatory)]
        [System.IO.DirectoryInfo]$Root,

        [Parameter(Mandatory)]
        [string]$CandidatePath,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $rootPath = $Root.FullName.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $candidate = ConvertTo-WindowsWackFullPath -Path $CandidatePath -Code $Code
    $prefix = $rootPath + [System.IO.Path]::DirectorySeparatorChar
    Assert-WindowsWackCondition `
        ($candidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) `
        $Code
    $relative = $candidate.Substring($prefix.Length)
    Assert-WindowsWackCondition `
        (-not [string]::IsNullOrWhiteSpace($relative) -and $relative.IndexOf(':') -lt 0) `
        $Code

    $current = $candidate
    while ($true) {
        if ([System.IO.File]::Exists($current) -or [System.IO.Directory]::Exists($current)) {
            try {
                $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            }
            catch {
                Fail-WindowsWack -Code $Code
            }
            Assert-WindowsWackCondition `
                (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
                $Code
        }

        $parent = [System.IO.Path]::GetDirectoryName($current)
        Assert-WindowsWackCondition (-not [string]::IsNullOrWhiteSpace($parent)) $Code
        if ([string]::Equals(
                $parent,
                $rootPath,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        Assert-WindowsWackCondition `
            ($parent.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) `
            $Code
        $current = $parent
    }
    return $candidate
}

function Get-WindowsWackSha256ForBytes {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes
    )

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace(
            '-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-WindowsWackSha256ForStream {
    param(
        [Parameter(Mandatory)]
        [System.IO.Stream]$Stream
    )

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($Stream))).Replace(
            '-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Read-WindowsWackReportBytes {
    param(
        [Parameter(Mandatory)]
        [object]$ReportPath,

        [Parameter(Mandatory)]
        [object]$ArtifactRoot
    )

    $root = Resolve-WindowsWackArtifactRoot -ArtifactRoot $ArtifactRoot
    $candidate = Assert-WindowsWackPathWithinRoot `
        -Root $root `
        -CandidatePath $ReportPath `
        -Code 'ReportPathInvalid'
    try {
        $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
    }
    catch {
        Fail-WindowsWack -Code 'ReportPathInvalid'
    }
    Assert-WindowsWackCondition `
        ($item -is [System.IO.FileInfo] -and -not $item.PSIsContainer) `
        'ReportPathInvalid'
    Assert-WindowsWackCondition `
        (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        'ReportPathInvalid'
    if ($item.Length -eq 0) {
        Fail-WindowsWack -Code 'ReportEmpty'
    }
    if ($item.Length -gt $script:windowsWackMaximumReportBytes) {
        Fail-WindowsWack -Code 'ReportTooLarge'
    }

    $stream = $null
    $bytes = $null
    try {
        $stream = [System.IO.File]::Open(
            $item.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::None)
        if ($stream.Length -eq 0) {
            Fail-WindowsWack -Code 'ReportEmpty'
        }
        if ($stream.Length -gt $script:windowsWackMaximumReportBytes) {
            Fail-WindowsWack -Code 'ReportTooLarge'
        }
        $length = [long]$stream.Length
        $bytes = New-Object byte[] ([int]$length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            Assert-WindowsWackCondition ($read -gt 0) 'ReportReadFailed'
            $offset += $read
        }
        Assert-WindowsWackCondition ($stream.ReadByte() -eq -1) 'ReportReadFailed'
        Assert-WindowsWackCondition ($stream.Length -eq $length) 'ReportReadFailed'
        return [pscustomobject][ordered]@{
            Length = $length
            Bytes = $bytes
        }
    }
    catch {
        if ($null -ne $bytes) {
            [Array]::Clear($bytes, 0, $bytes.Length)
        }
        if (Test-WindowsWackStableError -ErrorRecord $_) {
            throw
        }
        Fail-WindowsWack -Code 'ReportReadFailed'
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Assert-WindowsWackReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$ReportPath,

        [Parameter(Mandatory)]
        [object]$ArtifactRoot
    )

    $raw = Read-WindowsWackReportBytes `
        -ReportPath $ReportPath `
        -ArtifactRoot $ArtifactRoot
    $reportSha256 = Get-WindowsWackSha256ForBytes -Bytes $raw.Bytes
    $memory = $null
    $reader = $null
    try {
        $settings = New-Object System.Xml.XmlReaderSettings
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersFromEntities = 0
        $settings.MaxCharactersInDocument = $script:windowsWackMaximumReportBytes
        $settings.IgnoreComments = $false
        $settings.IgnoreProcessingInstructions = $false
        $settings.IgnoreWhitespace = $false

        $memory = New-Object System.IO.MemoryStream(,$raw.Bytes)
        $reader = [System.Xml.XmlReader]::Create($memory, $settings)
        $elementNames = New-Object 'System.Collections.Generic.List[string]'
        $testFrames = @{}
        $activeResult = $null
        $seenRoot = $false
        $closedRoot = $false
        $overallResult = $null
        $partialRun = $null
        $latestVersion = $null
        [int]$nodeCount = 0
        [int]$testCount = 0
        [int]$passedTestCount = 0
        [int]$failedTestCount = 0
        [int]$otherTestCount = 0

        while ($reader.Read()) {
            $nodeCount++
            Assert-WindowsWackCondition `
                ($nodeCount -le $script:windowsWackMaximumXmlNodes) `
                'ReportXmlBoundsExceeded'
            Assert-WindowsWackCondition `
                ($reader.Depth -le $script:windowsWackMaximumXmlDepth) `
                'ReportXmlBoundsExceeded'

            if ($reader.NodeType -eq [System.Xml.XmlNodeType]::Element) {
                Assert-WindowsWackCondition `
                    ($reader.AttributeCount -le $script:windowsWackMaximumXmlAttributes) `
                    'ReportXmlBoundsExceeded'
                Assert-WindowsWackCondition `
                    ($elementNames.Count -eq $reader.Depth) `
                    'ReportXmlInvalid'

                $elementKey = $reader.LocalName + [char]0 + $reader.NamespaceURI
                if (-not $seenRoot) {
                    Assert-WindowsWackCondition `
                        ($reader.Depth -eq 0 -and $reader.LocalName -ceq 'REPORT' -and
                            [string]::IsNullOrEmpty($reader.NamespaceURI) -and
                            [string]::IsNullOrEmpty($reader.Prefix)) `
                        'ReportRootInvalid'
                    $seenRoot = $true
                    Assert-WindowsWackCondition `
                        ($reader.MoveToAttribute('OVERALL_RESULT')) `
                        'OverallResultMissing'
                    $overallResult = [string]$reader.Value
                    $reader.MoveToElement() | Out-Null
                    Assert-WindowsWackCondition `
                        ($reader.MoveToAttribute('PARTIAL_RUN')) `
                        'PartialRunMissing'
                    $partialRun = [string]$reader.Value
                    $reader.MoveToElement() | Out-Null
                    if ($reader.MoveToAttribute('LATEST_VERSION')) {
                        $latestVersion = [string]$reader.Value
                        $reader.MoveToElement() | Out-Null
                    }
                }
                elseif ($reader.Depth -eq 0) {
                    Fail-WindowsWack -Code 'ReportRootInvalid'
                }

                if ($null -ne $activeResult -and
                    $reader.Depth -gt [int]$activeResult.Depth) {
                    $activeResult.Simple = $false
                }

                $parentKey = if ($reader.Depth -gt 0) {
                    $elementNames[$reader.Depth - 1]
                }
                else {
                    $null
                }
                if ($reader.LocalName -ceq 'TEST' -and
                    [string]::IsNullOrEmpty($reader.NamespaceURI)) {
                    $testCount++
                    $testFrames[[int]$reader.Depth] = [pscustomobject]@{
                        ResultCount = 0
                        ResultValue = $null
                    }
                }
                if ($reader.LocalName -ceq 'RESULT' -and
                    [string]::IsNullOrEmpty($reader.NamespaceURI) -and
                    $parentKey -ceq ('TEST' + [char]0) -and
                    $testFrames.ContainsKey([int]$reader.Depth - 1)) {
                    $activeResult = [pscustomobject]@{
                        Depth = [int]$reader.Depth
                        TestDepth = [int]$reader.Depth - 1
                        Text = New-Object System.Text.StringBuilder
                        Simple = $true
                    }
                }

                if ($reader.IsEmptyElement) {
                    if ($null -ne $activeResult -and
                        [int]$activeResult.Depth -eq $reader.Depth) {
                        $frame = $testFrames[[int]$activeResult.TestDepth]
                        $frame.ResultCount++
                        $frame.ResultValue = ''
                        $activeResult = $null
                    }
                    if ($reader.LocalName -ceq 'TEST' -and
                        [string]::IsNullOrEmpty($reader.NamespaceURI)) {
                        $frame = $testFrames[[int]$reader.Depth]
                        if ($frame.ResultCount -eq 1 -and $frame.ResultValue -ceq 'PASS') {
                            $passedTestCount++
                        }
                        elseif ($frame.ResultCount -eq 1 -and $frame.ResultValue -ceq 'FAIL') {
                            $failedTestCount++
                        }
                        else {
                            $otherTestCount++
                        }
                        $testFrames.Remove([int]$reader.Depth)
                    }
                    if ($reader.Depth -eq 0) {
                        $closedRoot = $true
                    }
                }
                else {
                    $elementNames.Add($elementKey)
                }
                continue
            }

            if ($reader.NodeType -eq [System.Xml.XmlNodeType]::Text -or
                $reader.NodeType -eq [System.Xml.XmlNodeType]::CDATA -or
                $reader.NodeType -eq [System.Xml.XmlNodeType]::Whitespace -or
                $reader.NodeType -eq [System.Xml.XmlNodeType]::SignificantWhitespace) {
                if ($null -ne $activeResult) {
                    if ($reader.Depth -ne ([int]$activeResult.Depth + 1) -or
                        ($activeResult.Text.Length + $reader.Value.Length) -gt 64) {
                        $activeResult.Simple = $false
                    }
                    elseif ($activeResult.Simple) {
                        $activeResult.Text.Append($reader.Value) | Out-Null
                    }
                }
                continue
            }

            if ($reader.NodeType -eq [System.Xml.XmlNodeType]::EndElement) {
                $elementKey = $reader.LocalName + [char]0 + $reader.NamespaceURI
                Assert-WindowsWackCondition `
                    ($elementNames.Count -eq ($reader.Depth + 1) -and
                        $elementNames[$reader.Depth] -ceq $elementKey) `
                    'ReportXmlInvalid'

                if ($null -ne $activeResult -and
                    [int]$activeResult.Depth -eq $reader.Depth -and
                    $reader.LocalName -ceq 'RESULT' -and
                    [string]::IsNullOrEmpty($reader.NamespaceURI)) {
                    $frame = $testFrames[[int]$activeResult.TestDepth]
                    $frame.ResultCount++
                    if ($activeResult.Simple) {
                        $frame.ResultValue = $activeResult.Text.ToString().Trim()
                    }
                    else {
                        $frame.ResultValue = $null
                    }
                    $activeResult = $null
                }

                if ($reader.LocalName -ceq 'TEST' -and
                    [string]::IsNullOrEmpty($reader.NamespaceURI)) {
                    $frame = $testFrames[[int]$reader.Depth]
                    if ($frame.ResultCount -eq 1 -and $frame.ResultValue -ceq 'PASS') {
                        $passedTestCount++
                    }
                    elseif ($frame.ResultCount -eq 1 -and $frame.ResultValue -ceq 'FAIL') {
                        $failedTestCount++
                    }
                    else {
                        $otherTestCount++
                    }
                    $testFrames.Remove([int]$reader.Depth)
                }
                $elementNames.RemoveAt($elementNames.Count - 1)
                if ($reader.Depth -eq 0) {
                    $closedRoot = $true
                }
            }
        }

        Assert-WindowsWackCondition `
            ($seenRoot -and $closedRoot -and $elementNames.Count -eq 0 -and
                $testFrames.Count -eq 0 -and $null -eq $activeResult) `
            'ReportXmlInvalid'
        if ($overallResult -ceq 'FAIL') {
            Fail-WindowsWack -Code 'OverallResultFailed'
        }
        if ($overallResult -ceq 'WARNING') {
            Fail-WindowsWack -Code 'OverallResultWarning'
        }
        Assert-WindowsWackCondition ($overallResult -ceq 'PASS') 'OverallResultUnknown'
        if ($partialRun -ceq 'TRUE') {
            Fail-WindowsWack -Code 'PartialRunDetected'
        }
        Assert-WindowsWackCondition ($partialRun -ceq 'FALSE') 'PartialRunUnknown'
        if ($null -ne $latestVersion) {
            if ($latestVersion -ceq 'FALSE') {
                Fail-WindowsWack -Code 'LatestVersionFalse'
            }
            Assert-WindowsWackCondition ($latestVersion -ceq 'TRUE') 'LatestVersionUnknown'
        }

        return [pscustomobject][ordered]@{
            ReportLength = [long]$raw.Length
            ReportSha256 = $reportSha256
            OverallResult = 'PASS'
            PartialRun = 'FALSE'
            LatestVersion = if ($null -eq $latestVersion) { $null } else { 'TRUE' }
            TestCount = $testCount
            PassedTestCount = $passedTestCount
            FailedTestCount = $failedTestCount
            OtherTestCount = $otherTestCount
        }
    }
    catch {
        if (Test-WindowsWackStableError -ErrorRecord $_) {
            throw
        }
        Fail-WindowsWack -Code 'ReportXmlInvalid'
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        if ($null -ne $memory) {
            $memory.Dispose()
        }
        if ($null -ne $raw -and $null -ne $raw.Bytes) {
            [Array]::Clear($raw.Bytes, 0, $raw.Bytes.Length)
        }
    }
}

function Resolve-WindowsWackTool {
    $programFilesX86 = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFilesX86)
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        Fail-WindowsWack -Code 'ToolUnavailable'
    }
    $programFilesRoot = ConvertTo-WindowsWackFullPath `
        -Path $programFilesX86 `
        -Code 'ToolUnavailable'
    try {
        $rootItem = Get-Item -LiteralPath $programFilesRoot -Force -ErrorAction Stop
    }
    catch {
        Fail-WindowsWack -Code 'ToolUnavailable'
    }
    Assert-WindowsWackCondition `
        ($rootItem -is [System.IO.DirectoryInfo] -and $rootItem.PSIsContainer -and
            ($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        'ToolInvalid'

    $expectedPath = [System.IO.Path]::GetFullPath((Join-Path $rootItem.FullName `
        'Windows Kits\10\App Certification Kit\appcert.exe'))
    $candidate = Assert-WindowsWackPathWithinRoot `
        -Root $rootItem `
        -CandidatePath $expectedPath `
        -Code 'ToolInvalid'
    Assert-WindowsWackCondition `
        ([string]::Equals(
            $candidate,
            $expectedPath,
            [System.StringComparison]::OrdinalIgnoreCase)) `
        'ToolInvalid'
    try {
        $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
    }
    catch {
        Fail-WindowsWack -Code 'ToolUnavailable'
    }
    Assert-WindowsWackCondition `
        ($item -is [System.IO.FileInfo] -and -not $item.PSIsContainer -and
            ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        'ToolInvalid'
    Assert-WindowsWackCondition `
        ($item.Length -gt 0 -and $item.Length -le $script:windowsWackMaximumToolBytes) `
        'ToolInvalid'

    $stream = $null
    try {
        $stream = [System.IO.File]::Open(
            $item.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        Assert-WindowsWackCondition `
            ($stream.Length -eq $item.Length -and $stream.Length -gt 0 -and
                $stream.Length -le $script:windowsWackMaximumToolBytes) `
            'ToolInvalid'
        $sha256 = Get-WindowsWackSha256ForStream -Stream $stream
        $stream.Position = 0
        $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($item.FullName)
        $version = [string]::Format(
            [System.Globalization.CultureInfo]::InvariantCulture,
            '{0}.{1}.{2}.{3}',
            $versionInfo.FileMajorPart,
            $versionInfo.FileMinorPart,
            $versionInfo.FileBuildPart,
            $versionInfo.FilePrivatePart)
        Assert-WindowsWackCondition `
            ($version -cmatch '\A[0-9]{1,10}(?:\.[0-9]{1,10}){3}\z') `
            'ToolInvalid'
        return [pscustomobject][ordered]@{
            File = $item
            Stream = $stream
            Version = $version
            Length = [long]$stream.Length
            Sha256 = $sha256
        }
    }
    catch {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        if (Test-WindowsWackStableError -ErrorRecord $_) {
            throw
        }
        Fail-WindowsWack -Code 'ToolInvalid'
    }
}

function ConvertTo-WindowsWackQuotedArgument {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    Assert-WindowsWackCondition `
        ($Value.IndexOf([char]0) -lt 0 -and $Value.IndexOf('"') -lt 0 -and
            $Value.IndexOf("`r") -lt 0 -and $Value.IndexOf("`n") -lt 0) `
        'ProcessArgumentsInvalid'
    return '"' + $Value + '"'
}

function Invoke-WindowsWackBoundedProcess {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$ToolFile,

        [Parameter(Mandatory)]
        [string]$Arguments,

        [Parameter(Mandatory)]
        [int]$TimeoutMilliseconds,

        [Parameter(Mandatory)]
        [string]$StandardOutputPath,

        [Parameter(Mandatory)]
        [string]$StandardErrorPath,

        [Parameter(Mandatory)]
        [ValidateSet('Reset', 'Test', 'Finalize')]
        [string]$Phase
    )

    Assert-WindowsWackCondition ($TimeoutMilliseconds -gt 0) ($Phase + 'Timeout')
    try {
        $capture = [IptvSuite.WindowsWack.BoundedProcessFileCapture]::Run(
            $ToolFile.FullName,
            $Arguments,
            $ToolFile.DirectoryName,
            $TimeoutMilliseconds,
            $StandardOutputPath,
            $StandardErrorPath,
            $script:windowsWackMaximumProcessOutputBytes,
            $script:windowsWackMaximumProcessOutputBytes)
    }
    catch {
        Fail-WindowsWack -Code ($Phase + 'StartFailed')
    }
    if ($capture.TimedOut) {
        Fail-WindowsWack -Code ($Phase + 'Timeout')
    }
    if ($capture.StandardOutputExceeded -or $capture.StandardErrorExceeded) {
        Fail-WindowsWack -Code ($Phase + 'OutputTooLarge')
    }
    Assert-WindowsWackCondition (-not $capture.CaptureFailed) ($Phase + 'CaptureFailed')
    return [pscustomobject][ordered]@{
        ExitCode = [int]$capture.ExitCode
        TimedOut = $false
    }
}

function Resolve-WindowsWackTestExitDisposition {
    param(
        [Parameter(Mandatory)]
        [int]$ExitCode
    )

    if ($ExitCode -eq 0) {
        return 'ReportComplete'
    }
    if ($ExitCode -eq 1) {
        return 'ReportFinalizationRequired'
    }
    if ($ExitCode -eq -1) {
        Fail-WindowsWack -Code 'TestInvalidCommandLine'
    }
    if ($ExitCode -eq -2) {
        Fail-WindowsWack -Code 'TestInfrastructureError'
    }
    if ($ExitCode -eq -3) {
        Fail-WindowsWack -Code 'TestUserInitiated'
    }
    if ($ExitCode -eq -4) {
        Fail-WindowsWack -Code 'TestInstallationError'
    }
    if ($ExitCode -eq -5) {
        Fail-WindowsWack -Code 'TestUnpackagingError'
    }
    Fail-WindowsWack -Code 'TestExitCodeUnknown'
}

function Assert-WindowsWackCommandCompleted {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Reset', 'Finalize')]
        [string]$Phase,

        [Parameter(Mandatory)]
        [int]$ExitCode
    )

    if ($ExitCode -eq 0) {
        return
    }
    if ($ExitCode -eq -1) {
        Fail-WindowsWack -Code ($Phase + 'InvalidCommandLine')
    }
    if ($ExitCode -eq -2) {
        Fail-WindowsWack -Code ($Phase + 'InfrastructureError')
    }
    if ($ExitCode -eq -3) {
        Fail-WindowsWack -Code ($Phase + 'UserInitiated')
    }
    if ($ExitCode -eq -4) {
        Fail-WindowsWack -Code ($Phase + 'InstallationError')
    }
    if ($ExitCode -eq -5) {
        Fail-WindowsWack -Code ($Phase + 'UnpackagingError')
    }
    Fail-WindowsWack -Code ($Phase + 'ExitCodeUnknown')
}

function New-WindowsWackDevelopmentIdentitySummary {
    param(
        [Parameter(Mandatory)]
        [object]$Tool,

        [Parameter(Mandatory)]
        [object]$Report,

        [Parameter(Mandatory)]
        [string]$PackageSha256,

        [Parameter(Mandatory)]
        [object]$ResetResult,

        [Parameter(Mandatory)]
        [object]$TestResult,

        [object]$FinalizeResult = $null
    )

    Assert-WindowsWackCondition `
        ($PackageSha256 -cmatch '\A[0-9a-f]{64}\z') `
        'PackageSha256Invalid'
    Assert-WindowsWackCondition `
        ([string]$Tool.Version -cmatch '\A[0-9]{1,10}(?:\.[0-9]{1,10}){3}\z' -and
            [long]$Tool.Length -gt 0 -and
            [string]$Tool.Sha256 -cmatch '\A[0-9a-f]{64}\z') `
        'ToolInvalid'
    Assert-WindowsWackCondition `
        ([long]$Report.ReportLength -gt 0 -and
            [long]$Report.ReportLength -le $script:windowsWackMaximumReportBytes -and
            [string]$Report.ReportSha256 -cmatch '\A[0-9a-f]{64}\z' -and
            [string]$Report.OverallResult -ceq 'PASS' -and
            [string]$Report.PartialRun -ceq 'FALSE' -and
            ($null -eq $Report.LatestVersion -or
                [string]$Report.LatestVersion -ceq 'TRUE')) `
        'ReportSummaryInvalid'
    Assert-WindowsWackCondition `
        ([int]$Report.TestCount -ge 0 -and
            [int]$Report.PassedTestCount -ge 0 -and
            [int]$Report.FailedTestCount -ge 0 -and
            [int]$Report.OtherTestCount -ge 0 -and
            ([int]$Report.PassedTestCount + [int]$Report.FailedTestCount +
                [int]$Report.OtherTestCount) -eq [int]$Report.TestCount) `
        'ReportSummaryInvalid'
    Assert-WindowsWackCondition `
        ([int]$ResetResult.ExitCode -eq 0 -and -not [bool]$ResetResult.TimedOut) `
        'ResetFailed'
    $reportFinalizationRequired = [int]$TestResult.ExitCode -eq 1
    Assert-WindowsWackCondition `
        (([int]$TestResult.ExitCode -eq 0 -or
                $reportFinalizationRequired) -and
            -not [bool]$TestResult.TimedOut) `
        'TestFailed'
    if ($reportFinalizationRequired) {
        Assert-WindowsWackCondition `
            ($null -ne $FinalizeResult -and
                [int]$FinalizeResult.ExitCode -eq 0 -and
                -not [bool]$FinalizeResult.TimedOut) `
            'FinalizeFailed'
    }
    else {
        Assert-WindowsWackCondition ($null -eq $FinalizeResult) 'FinalizeUnexpected'
    }

    return [pscustomobject][ordered]@{
        SchemaVersion = 1
        Scope = 'DevelopmentIdentityWackPreflightOnly'
        ClosedBlocker = 'None'
        ReleaseReady = $false
        PackageSha256 = $PackageSha256
        ToolVersion = [string]$Tool.Version
        ToolLength = [long]$Tool.Length
        ToolSha256 = [string]$Tool.Sha256
        ReportLength = [long]$Report.ReportLength
        ReportSha256 = [string]$Report.ReportSha256
        OverallResult = 'PASS'
        PartialRun = 'FALSE'
        LatestVersion = if ($null -eq $Report.LatestVersion) { $null } else { 'TRUE' }
        ResetExitCode = 0
        ResetTimedOut = $false
        TestExitCode = [int]$TestResult.ExitCode
        TestTimedOut = $false
        TestCount = [int]$Report.TestCount
        PassedTestCount = [int]$Report.PassedTestCount
        FailedTestCount = [int]$Report.FailedTestCount
        OtherTestCount = [int]$Report.OtherTestCount
    }
}

function Invoke-WindowsWackDevelopmentIdentityPreflight {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$PackageFullName,

        [Parameter(Mandatory)]
        [object]$PackageSha256,

        [Parameter(Mandatory)]
        [object]$ArtifactRoot,

        [object]$TimeoutMinutes = 60
    )

    $packageNames = @($PackageFullName)
    Assert-WindowsWackCondition `
        ($packageNames.Count -eq 1 -and $packageNames[0] -is [string] -and
            [string]$packageNames[0] -cmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,511}\z') `
        'PackageFullNameInvalid'
    $packageName = [string]$packageNames[0]
    $packageHashes = @($PackageSha256)
    Assert-WindowsWackCondition `
        ($packageHashes.Count -eq 1 -and $packageHashes[0] -is [string] -and
            [string]$packageHashes[0] -cmatch '\A[0-9a-f]{64}\z') `
        'PackageSha256Invalid'
    $packageHash = [string]$packageHashes[0]
    $timeoutValues = @($TimeoutMinutes)
    Assert-WindowsWackCondition `
        ($timeoutValues.Count -eq 1 -and $timeoutValues[0] -is [int] -and
            [int]$timeoutValues[0] -ge 1 -and [int]$timeoutValues[0] -le 60) `
        'TimeoutInvalid'
    [int]$timeoutMilliseconds = [int]([long][int]$timeoutValues[0] * 60000L)

    $artifact = Resolve-WindowsWackArtifactRoot -ArtifactRoot $ArtifactRoot
    $runId = [Guid]::NewGuid().ToString('N')
    $reportPath = Join-Path $artifact.FullName ("wack-$runId-report.xml")
    $resetOutputPath = Join-Path $artifact.FullName ("wack-$runId-reset.stdout.raw")
    $resetErrorPath = Join-Path $artifact.FullName ("wack-$runId-reset.stderr.raw")
    $testOutputPath = Join-Path $artifact.FullName ("wack-$runId-test.stdout.raw")
    $testErrorPath = Join-Path $artifact.FullName ("wack-$runId-test.stderr.raw")
    $finalizeOutputPath = Join-Path $artifact.FullName ("wack-$runId-finalize.stdout.raw")
    $finalizeErrorPath = Join-Path $artifact.FullName ("wack-$runId-finalize.stderr.raw")
    $rawPaths = @(
        $reportPath,
        $resetOutputPath,
        $resetErrorPath,
        $testOutputPath,
        $testErrorPath,
        $finalizeOutputPath,
        $finalizeErrorPath)
    foreach ($rawPath in $rawPaths) {
        Assert-WindowsWackPathWithinRoot `
            -Root $artifact `
            -CandidatePath $rawPath `
            -Code 'ArtifactPathInvalid' | Out-Null
        Assert-WindowsWackCondition `
            (-not [System.IO.File]::Exists($rawPath) -and
                -not [System.IO.Directory]::Exists($rawPath)) `
            'ArtifactCollision'
    }

    $tool = $null
    $summary = $null
    $operationError = $null
    $toolReleaseFailed = $false
    # Elevation and the active interactive user session are caller-owned prerequisites.
    try {
        $tool = Resolve-WindowsWackTool
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $resetResult = Invoke-WindowsWackBoundedProcess `
            -ToolFile $tool.File `
            -Arguments 'reset' `
            -TimeoutMilliseconds $timeoutMilliseconds `
            -StandardOutputPath $resetOutputPath `
            -StandardErrorPath $resetErrorPath `
            -Phase 'Reset'
        Assert-WindowsWackCommandCompleted `
            -Phase 'Reset' `
            -ExitCode $resetResult.ExitCode

        [long]$remainingMilliseconds = [long]$timeoutMilliseconds - $clock.ElapsedMilliseconds
        if ($remainingMilliseconds -le 0) {
            Fail-WindowsWack -Code 'TestTimeout'
        }
        $testArguments = 'test -packagefullname ' +
            (ConvertTo-WindowsWackQuotedArgument -Value $packageName) +
            ' -reportoutputpath ' +
            (ConvertTo-WindowsWackQuotedArgument -Value $reportPath)
        $testResult = Invoke-WindowsWackBoundedProcess `
            -ToolFile $tool.File `
            -Arguments $testArguments `
            -TimeoutMilliseconds ([int]$remainingMilliseconds) `
            -StandardOutputPath $testOutputPath `
            -StandardErrorPath $testErrorPath `
            -Phase 'Test'
        $testDisposition = Resolve-WindowsWackTestExitDisposition `
            -ExitCode $testResult.ExitCode
        $finalizeResult = $null
        if ($testDisposition -ceq 'ReportFinalizationRequired') {
            $remainingMilliseconds = [long]$timeoutMilliseconds - $clock.ElapsedMilliseconds
            if ($remainingMilliseconds -le 0) {
                Fail-WindowsWack -Code 'FinalizeTimeout'
            }
            $finalizeArguments = 'finalizereport -reportfilepath ' +
                (ConvertTo-WindowsWackQuotedArgument -Value $reportPath)
            $finalizeResult = Invoke-WindowsWackBoundedProcess `
                -ToolFile $tool.File `
                -Arguments $finalizeArguments `
                -TimeoutMilliseconds ([int]$remainingMilliseconds) `
                -StandardOutputPath $finalizeOutputPath `
                -StandardErrorPath $finalizeErrorPath `
                -Phase 'Finalize'
            Assert-WindowsWackCommandCompleted `
                -Phase 'Finalize' `
                -ExitCode $finalizeResult.ExitCode
        }

        $report = Assert-WindowsWackReport `
            -ReportPath $reportPath `
            -ArtifactRoot $artifact.FullName
        $summary = New-WindowsWackDevelopmentIdentitySummary `
            -Tool $tool `
            -Report $report `
            -PackageSha256 $packageHash `
            -ResetResult $resetResult `
            -TestResult $testResult `
            -FinalizeResult $finalizeResult
    }
    catch {
        $operationError = $_
    }
    finally {
        if ($null -ne $tool -and $null -ne $tool.Stream) {
            try {
                $tool.Stream.Dispose()
            }
            catch {
                $toolReleaseFailed = $true
            }
        }
    }

    $cleanupFailed = $false
    foreach ($rawPath in $rawPaths) {
        try {
            if ([System.IO.File]::Exists($rawPath)) {
                [System.IO.File]::Delete($rawPath)
            }
            elseif ([System.IO.Directory]::Exists($rawPath)) {
                $cleanupFailed = $true
            }
        }
        catch {
            $cleanupFailed = $true
        }
    }
    if ($cleanupFailed) {
        Fail-WindowsWack -Code 'RawCleanupFailed'
    }
    if ($toolReleaseFailed) {
        Fail-WindowsWack -Code 'ToolReleaseFailed'
    }
    if ($null -ne $operationError) {
        if (Test-WindowsWackStableError -ErrorRecord $operationError) {
            throw $operationError
        }
        Fail-WindowsWack -Code 'PreflightFailed'
    }
    return $summary
}
