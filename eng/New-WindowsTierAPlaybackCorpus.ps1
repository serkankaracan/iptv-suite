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

    & $ffmpeg `
        -hide_banner -loglevel error -nostdin -y `
        -i $directPath -map '0:v:0' -map '0:a:0' -c copy `
        -hls_time 2 -hls_list_size 0 -hls_playlist_type vod `
        -hls_segment_filename (Join-Path $stagingRoot 'hls-%03d.ts') `
        (Join-Path $stagingRoot 'hls.m3u8')
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

    $playlist = Get-Content -LiteralPath (Join-Path $stagingRoot 'hls.m3u8') -Raw -Encoding utf8
    if ($playlist -notmatch '#EXT-X-ENDLIST' -or $playlist -match '(?i)(https?://|file:|\\|\.\.)') {
        throw 'The HLS fixture playlist must be finite and contain only local relative segment names.'
    }
    $segments = @(Get-ChildItem -LiteralPath $stagingRoot -File -Filter 'hls-*.ts' | Sort-Object Name)
    if ($segments.Count -ne 4) { throw "Expected four HLS-TS segments, received $($segments.Count)." }

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
    $manifestJson = $manifest | ConvertTo-Json -Depth 8
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
