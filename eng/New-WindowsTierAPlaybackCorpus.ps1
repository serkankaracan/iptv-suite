[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $FfmpegDirectory,

    [Parameter(Mandatory)]
    [string] $FfmpegArchive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $repositoryRoot '.artifacts\m10-tier-a-corpus'
$stagingRoot = Join-Path $artifactRoot ('staging-' + [Guid]::NewGuid().ToString('N'))
$outputRoot = Join-Path $repositoryRoot 'apps\windows\tests\fixtures\playback\tier-a'
$ffmpeg = Join-Path ([System.IO.Path]::GetFullPath($FfmpegDirectory)) 'ffmpeg.exe'
$ffprobe = Join-Path ([System.IO.Path]::GetFullPath($FfmpegDirectory)) 'ffprobe.exe'
$expectedToolVersion = 'n9.0.1-6-g9d4ca21220-20260820'
$expectedArchiveSha256 = '73d64c702162aaa5eaa8f36c21921f95cb351d737bf89c0557d773cd8cf091a9'
$expectedFiles = @(
    'direct-h264-aac.ts',
    'hls.m3u8',
    'hls-000.ts',
    'hls-001.ts',
    'hls-002.ts',
    'hls-003.ts',
    'fixture-manifest.json'
)

foreach ($tool in @($ffmpeg, $ffprobe)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw "Required corpus generator tool is missing: $tool"
    }
}
$archivePath = [System.IO.Path]::GetFullPath($FfmpegArchive)
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or
    (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expectedArchiveSha256) {
    throw "FFmpeg generator archive does not match expected SHA-256 $expectedArchiveSha256."
}

$versionOutput = @(& $ffmpeg -version 2>&1)
$versionExitCode = $LASTEXITCODE
$versionLine = [string]$versionOutput[0]
if ($versionExitCode -ne 0 -or $versionLine.IndexOf($expectedToolVersion, [StringComparison]::Ordinal) -lt 0) {
    throw "Expected FFmpeg generator $expectedToolVersion, received '$versionLine'."
}

function Get-FirstPacketMetadata {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $StreamSelector
    )

    $packetProbeOutput = @()
    $packetProbeExitCode = -1
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $packetProbeOutput = @(& $ffprobe `
            -v error -select_streams $StreamSelector -read_intervals '%+#1' `
            -show_entries 'packet=pts_time,dts_time,flags' -of json `
            $Path 2>&1)
        $packetProbeExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($packetProbeExitCode -ne 0) {
        throw "FFprobe failed to inspect the first $StreamSelector packet in $([IO.Path]::GetFileName($Path))."
    }

    $packetProbeText = [string]::Join("`n", [string[]]@(
        $packetProbeOutput | ForEach-Object { [string]$_ }))
    $packetProbe = $packetProbeText | ConvertFrom-Json
    $packets = @($packetProbe.packets)
    $ptsTime = 0.0
    $dtsTime = 0.0
    if ($packets.Count -ne 1 -or
        -not [double]::TryParse(
            [string]$packets[0].pts_time,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$ptsTime) -or
        -not [double]::TryParse(
            [string]$packets[0].dts_time,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$dtsTime) -or
        [double]::IsNaN($ptsTime) -or
        [double]::IsInfinity($ptsTime) -or
        [double]::IsNaN($dtsTime) -or
        [double]::IsInfinity($dtsTime)) {
        throw "The first $StreamSelector packet metadata in $([IO.Path]::GetFileName($Path)) is ambiguous."
    }

    [pscustomobject]@{
        PtsTime = $ptsTime
        DtsTime = $dtsTime
        Flags = [string]$packets[0].flags
    }
}

function Get-PacketTimeline {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $StreamSelector
    )

    $timelineProbeOutput = @()
    $timelineProbeExitCode = -1
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $timelineProbeOutput = @(& $ffprobe `
            -v error -select_streams $StreamSelector `
            -show_entries 'stream=time_base:packet=pts,dts,duration,flags' -of json `
            $Path 2>&1)
        $timelineProbeExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($timelineProbeExitCode -ne 0) {
        throw "FFprobe failed to inspect the $StreamSelector packet timeline in $([IO.Path]::GetFileName($Path))."
    }

    $timelineProbeText = [string]::Join("`n", [string[]]@(
        $timelineProbeOutput | ForEach-Object { [string]$_ }))
    $timelineProbe = $timelineProbeText | ConvertFrom-Json
    $streams = @($timelineProbe.streams)
    $packets = @($timelineProbe.packets)
    if ($streams.Count -ne 1 -or
        [string]$streams[0].time_base -notmatch '^\d+/\d+$' -or
        $packets.Count -eq 0) {
        throw "The $StreamSelector packet timeline in $([IO.Path]::GetFileName($Path)) is ambiguous."
    }

    $rows = @($packets | ForEach-Object {
        $pts = 0L
        $dts = 0L
        $duration = 0L
        $flags = [string]$_.flags
        if (-not [long]::TryParse(
                [string]$_.pts,
                [Globalization.NumberStyles]::Integer,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$pts) -or
            -not [long]::TryParse(
                [string]$_.dts,
                [Globalization.NumberStyles]::Integer,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$dts) -or
            -not [long]::TryParse(
                [string]$_.duration,
                [Globalization.NumberStyles]::Integer,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$duration) -or
            [string]::IsNullOrWhiteSpace($flags)) {
            throw "The $StreamSelector packet timeline in $([IO.Path]::GetFileName($Path)) contains an invalid row."
        }

        "$pts|$dts|$duration|$flags"
    })

    [pscustomobject]@{
        TimeBase = [string]$streams[0].time_base
        Rows = [string[]]$rows
    }
}

New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
try {
    $directPath = Join-Path $stagingRoot 'direct-h264-aac.ts'
    & $ffmpeg `
        -hide_banner -loglevel error -nostdin -y `
        -f lavfi -i 'testsrc2=size=640x360:rate=25' `
        -f lavfi -i 'sine=frequency=1000:sample_rate=48000' `
        -t 8 `
        -map '0:v:0' -map '1:a:0' `
        -c:v libx264 -profile:v high -level:v 4.0 -pix_fmt yuv420p `
        -preset medium -threads 1 -g 50 -keyint_min 50 -sc_threshold 0 -b:v 800k `
        -c:a aac -profile:a aac_low -b:a 96k -ar 48000 -ac 2 `
        -muxdelay 0 -muxpreload 0 -mpegts_flags resend_headers `
        -f mpegts $directPath
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg failed to create the direct Tier A fixture.' }

    $playlistPath = Join-Path $stagingRoot 'hls.m3u8'
    & $ffmpeg `
        -hide_banner -loglevel error -nostdin -y `
        -i $directPath -map '0:v:0' -map '0:a:0' -c copy `
        -muxdelay 0 -muxpreload 0 `
        -hls_time 2 -hls_list_size 0 -hls_playlist_type vod `
        -hls_flags independent_segments `
        -hls_segment_filename (Join-Path $stagingRoot 'hls-%03d.ts') `
        $playlistPath
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg failed to create the HLS-TS Tier A fixture.' }

    $probeJson = & $ffprobe -v error -show_entries 'stream=index,codec_name,profile,pix_fmt,width,height,r_frame_rate,sample_rate,channels' -show_entries 'format=format_name,duration' -of json $directPath
    if ($LASTEXITCODE -ne 0) { throw 'FFprobe failed to inspect the direct Tier A fixture.' }
    $probe = $probeJson | ConvertFrom-Json
    $video = @($probe.streams | Where-Object codec_name -eq 'h264')
    $audio = @($probe.streams | Where-Object codec_name -eq 'aac')
    if ($video.Count -ne 1 -or $audio.Count -ne 1) { throw 'Tier A fixture must contain exactly one H.264 and one AAC stream.' }
    if ($video[0].profile -ne 'High' -or $video[0].pix_fmt -ne 'yuv420p' -or
        $video[0].width -ne 640 -or $video[0].height -ne 360 -or $video[0].r_frame_rate -ne '25/1') {
        throw 'Tier A video tuple is not H.264 High, 640x360, yuv420p at 25 fps.'
    }
    if ($audio[0].profile -ne 'LC' -or $audio[0].sample_rate -ne '48000' -or $audio[0].channels -ne 2) {
        throw 'Tier A audio tuple is not AAC-LC, 48 kHz stereo.'
    }
    if ($probe.format.format_name -notmatch 'mpegts') { throw 'Tier A direct fixture is not MPEG-TS.' }

    $playlist = Get-Content -LiteralPath $playlistPath -Raw -Encoding utf8
    if ($playlist -notmatch '#EXT-X-ENDLIST' -or $playlist -match '(?i)(https?://|file:|\\|\.\.)') {
        throw 'The HLS fixture playlist must be finite and contain only local relative segment names.'
    }
    $playlistLines = @(Get-Content -LiteralPath $playlistPath -Encoding utf8)
    $playlistVersionLines = @($playlistLines | Where-Object {
        ([string]$_).StartsWith('#EXT-X-VERSION:', [StringComparison]::Ordinal)
    })
    $independentSegmentLines = @($playlistLines | Where-Object {
        ([string]$_).StartsWith('#EXT-X-INDEPENDENT-SEGMENTS', [StringComparison]::Ordinal)
    })
    $independentSegmentIndex = -1
    $firstExtInfIndex = -1
    for ($lineIndex = 0; $lineIndex -lt $playlistLines.Count; $lineIndex++) {
        if ($independentSegmentIndex -lt 0 -and
            [string]$playlistLines[$lineIndex] -ceq '#EXT-X-INDEPENDENT-SEGMENTS') {
            $independentSegmentIndex = $lineIndex
        }
        if ($firstExtInfIndex -lt 0 -and
            ([string]$playlistLines[$lineIndex]).StartsWith('#EXTINF:', [StringComparison]::Ordinal)) {
            $firstExtInfIndex = $lineIndex
        }
    }
    if ($playlistVersionLines.Count -ne 1 -or
        [string]$playlistVersionLines[0] -cne '#EXT-X-VERSION:6') {
        throw 'The HLS fixture playlist must declare exact version 6 once.'
    }
    if ($independentSegmentLines.Count -ne 1 -or
        [string]$independentSegmentLines[0] -cne '#EXT-X-INDEPENDENT-SEGMENTS' -or
        $independentSegmentIndex -lt 0 -or
        $firstExtInfIndex -lt 0 -or
        $independentSegmentIndex -ge $firstExtInfIndex) {
        throw 'The HLS fixture playlist must declare exactly one independent-segments tag before media.'
    }
    $segments = @(Get-ChildItem -LiteralPath $stagingRoot -File -Filter 'hls-*.ts' | Sort-Object Name)
    if ($segments.Count -ne 4) { throw "Expected four HLS-TS segments, received $($segments.Count)." }
    $directFirstVideoPacket = Get-FirstPacketMetadata -Path $directPath -StreamSelector 'v:0'
    $directFirstAudioPacket = Get-FirstPacketMetadata -Path $directPath -StreamSelector 'a:0'
    for ($segmentIndex = 0; $segmentIndex -lt $segments.Count; $segmentIndex++) {
        $segment = $segments[$segmentIndex]
        $firstVideoPacket = Get-FirstPacketMetadata -Path $segment.FullName -StreamSelector 'v:0'
        $expectedVideoPtsTime = $directFirstVideoPacket.PtsTime + (2.0 * $segmentIndex)
        $expectedVideoDtsTime = $directFirstVideoPacket.DtsTime + (2.0 * $segmentIndex)
        if (-not $firstVideoPacket.Flags.StartsWith('K', [StringComparison]::Ordinal)) {
            throw "The first video packet in $($segment.Name) is not a key frame."
        }
        if ([Math]::Abs($firstVideoPacket.PtsTime - $expectedVideoPtsTime) -gt 0.000001 -or
            [Math]::Abs($firstVideoPacket.DtsTime - $expectedVideoDtsTime) -gt 0.000001) {
            throw "The first video packet timeline in $($segment.Name) is not aligned with the direct fixture."
        }
        if ($segmentIndex -eq 0) {
            $firstAudioPacket = Get-FirstPacketMetadata -Path $segment.FullName -StreamSelector 'a:0'
            if ([Math]::Abs($firstAudioPacket.PtsTime - $directFirstAudioPacket.PtsTime) -gt 0.000001 -or
                [Math]::Abs($firstAudioPacket.DtsTime - $directFirstAudioPacket.DtsTime) -gt 0.000001) {
                throw 'The first HLS audio packet timeline is not aligned with the direct fixture.'
            }
        }

        $previousErrorActionPreference = $ErrorActionPreference
        try {
            # PowerShell 5.1 represents a native process's normal stderr stream as
            # NativeCommandError records. Capture FFmpeg's trace without weakening
            # the script-wide stop policy; the exact native exit code remains fatal.
            $ErrorActionPreference = 'Continue'
            $traceOutput = @(& $ffmpeg `
                -hide_banner -loglevel info -nostdin `
                -i $segment.FullName -map '0:v:0' -c:v copy `
                -bsf:v trace_headers -frames:v 1 -f null 'NUL' 2>&1)
            $traceExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        $traceText = [string]::Join("`n", [string[]]@($traceOutput | ForEach-Object { [string]$_ }))
        if ($traceExitCode -ne 0 -or
            $traceText -notmatch '(?m)^\[trace_headers[^\]]*\][^\r\n]*nal_unit_type[^\r\n]*=[ \t]*7[ \t]*\r?$' -or
            $traceText -notmatch '(?m)^\[trace_headers[^\]]*\][^\r\n]*nal_unit_type[^\r\n]*=[ \t]*8[ \t]*\r?$' -or
            $traceText -notmatch '(?m)^\[trace_headers[^\]]*\][^\r\n]*nal_unit_type[^\r\n]*=[ \t]*5[ \t]*\r?$') {
            throw "The first video access unit in $($segment.Name) does not contain SPS, PPS, and IDR NAL units."
        }
    }

    foreach ($streamSelector in @('v:0', 'a:0')) {
        $directTimeline = Get-PacketTimeline -Path $directPath -StreamSelector $streamSelector
        $hlsTimeline = Get-PacketTimeline -Path $playlistPath -StreamSelector $streamSelector
        if ($directTimeline.TimeBase -cne $hlsTimeline.TimeBase -or
            $directTimeline.Rows.Count -ne $hlsTimeline.Rows.Count -or
            [string]::Join("`n", $directTimeline.Rows) -cne [string]::Join("`n", $hlsTimeline.Rows)) {
            throw "The HLS $streamSelector packet timeline changed during segmentation."
        }
    }

    $elementaryStreamChecks = @(
        [pscustomobject]@{ Name = 'H.264'; Map = '0:v:0'; Format = 'h264'; Extension = 'h264' }
        [pscustomobject]@{ Name = 'AAC'; Map = '0:a:0'; Format = 'adts'; Extension = 'aac' }
    )
    foreach ($elementaryStreamCheck in $elementaryStreamChecks) {
        $directElementaryPath = Join-Path $stagingRoot ".direct-parity.$($elementaryStreamCheck.Extension)"
        $hlsElementaryPath = Join-Path $stagingRoot ".hls-parity.$($elementaryStreamCheck.Extension)"
        try {
            & $ffmpeg `
                -hide_banner -loglevel error -nostdin -y `
                -i $directPath -map $elementaryStreamCheck.Map -c copy `
                -f $elementaryStreamCheck.Format $directElementaryPath
            if ($LASTEXITCODE -ne 0) {
                throw "FFmpeg failed to extract the direct $($elementaryStreamCheck.Name) parity stream."
            }
            & $ffmpeg `
                -hide_banner -loglevel error -nostdin -y `
                -i $playlistPath -map $elementaryStreamCheck.Map -c copy `
                -f $elementaryStreamCheck.Format $hlsElementaryPath
            if ($LASTEXITCODE -ne 0) {
                throw "FFmpeg failed to extract the HLS $($elementaryStreamCheck.Name) parity stream."
            }
            $directElementary = Get-Item -LiteralPath $directElementaryPath
            $hlsElementary = Get-Item -LiteralPath $hlsElementaryPath
            if ($directElementary.Length -ne $hlsElementary.Length -or
                (Get-FileHash -LiteralPath $directElementaryPath -Algorithm SHA256).Hash -cne
                    (Get-FileHash -LiteralPath $hlsElementaryPath -Algorithm SHA256).Hash) {
                throw "The HLS $($elementaryStreamCheck.Name) elementary stream changed during segmentation."
            }
        }
        finally {
            Remove-Item -LiteralPath $directElementaryPath, $hlsElementaryPath -Force -ErrorAction SilentlyContinue
        }
    }

    $mediaFiles = @(
        Get-Item -LiteralPath $directPath
        Get-Item -LiteralPath (Join-Path $stagingRoot 'hls.m3u8')
        $segments
    )
    $manifest = [ordered]@{
        SchemaVersion = 1
        FixtureId = 'iptvsuite-tier-a-synthetic-v1'
        Rights = [ordered]@{
            CopyrightOwner = 'IPTV Suite contributors'
            License = 'CC0-1.0'
            Provenance = 'Generated exclusively from FFmpeg lavfi testsrc2 and sine sources; no captured or third-party media.'
        }
        Generator = [ordered]@{
            Tool = 'FFmpeg BtbN Windows build linked by ffmpeg.org'
            Version = $expectedToolVersion
            ArchiveSha256 = $expectedArchiveSha256
            GeneratorScript = 'eng/New-WindowsTierAPlaybackCorpus.ps1'
        }
        Capability = [ordered]@{
            Tier = 'A'
            Video = 'H.264 High, yuv420p, 640x360, 25fps'
            Audio = 'AAC-LC, 48kHz, stereo'
            DirectContainer = 'MPEG-TS'
            AdaptiveContainer = 'HLS VOD with MPEG-TS segments'
            DurationSeconds = 8
        }
        Files = @($mediaFiles | ForEach-Object {
            [ordered]@{
                Path = $_.Name
                SizeBytes = $_.Length
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
    }
    $manifestJson = ($manifest | ConvertTo-Json -Depth 8).Replace("`r`n", "`n")
    [System.IO.File]::WriteAllText(
        (Join-Path $stagingRoot 'fixture-manifest.json'),
        $manifestJson,
        [System.Text.UTF8Encoding]::new($false))

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $unexpected = @(Get-ChildItem -LiteralPath $outputRoot -File | Where-Object Name -notin $expectedFiles)
    if ($unexpected.Count -ne 0) { throw 'Refusing to update a fixture directory containing unexpected files.' }
    foreach ($expectedFile in $expectedFiles) {
        $source = Join-Path $stagingRoot $expectedFile
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Expected generated file is missing: $expectedFile" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $outputRoot $expectedFile) -Force
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

Write-Host "Generated deterministic Tier A playback corpus: $outputRoot"
