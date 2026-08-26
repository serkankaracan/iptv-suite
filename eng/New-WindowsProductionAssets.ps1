[CmdletBinding(DefaultParameterSetName = 'Generate')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Generate')]
    [ValidateNotNullOrEmpty()]
    [string] $OutputRoot,

    [Parameter(Mandatory = $true, ParameterSetName = 'Verify')]
    [ValidateNotNullOrEmpty()]
    [string] $VerifyRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$generatorSource = @'
using System;
using System.Collections.Generic;
using System.IO;

namespace IptvSuite.WindowsProductionAssets
{
    public static class AssetGeneratorV1
    {
        private static readonly uint[] Crc32Table = CreateCrc32Table();

        public static byte[] CreatePng(int width, int height, int style)
        {
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
            {
                throw new ArgumentOutOfRangeException("width", "Asset dimensions must be between 1 and 4096 pixels.");
            }

            if (style < 0 || style > 3)
            {
                throw new ArgumentOutOfRangeException("style");
            }

            byte[] pixels = new byte[checked(width * height * 4)];
            DrawNeutralPlaceholder(pixels, width, height, style);

            int stride = checked(width * 4);
            byte[] scanlines = new byte[checked((stride + 1) * height)];
            for (int y = 0; y < height; y++)
            {
                int destinationOffset = y * (stride + 1);
                scanlines[destinationOffset] = 0;
                Buffer.BlockCopy(pixels, y * stride, scanlines, destinationOffset + 1, stride);
            }

            byte[] imageHeader = new byte[13];
            WriteUInt32BigEndian(imageHeader, 0, checked((uint)width));
            WriteUInt32BigEndian(imageHeader, 4, checked((uint)height));
            imageHeader[8] = 8;
            imageHeader[9] = 6;
            imageHeader[10] = 0;
            imageHeader[11] = 0;
            imageHeader[12] = 0;

            using (MemoryStream output = new MemoryStream())
            {
                byte[] signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
                output.Write(signature, 0, signature.Length);
                WritePngChunk(output, new byte[] { 73, 72, 68, 82 }, imageHeader);
                WritePngChunk(output, new byte[] { 73, 68, 65, 84 }, CreateDeterministicZlibStream(scanlines));
                WritePngChunk(output, new byte[] { 73, 69, 78, 68 }, new byte[0]);
                return output.ToArray();
            }
        }

        public static byte[] CreatePngFrameIcon(int[] frameSizes)
        {
            if (frameSizes == null || frameSizes.Length == 0 || frameSizes.Length > 32)
            {
                throw new ArgumentException("An ICO must contain between 1 and 32 frames.", "frameSizes");
            }

            List<byte[]> frames = new List<byte[]>(frameSizes.Length);
            for (int index = 0; index < frameSizes.Length; index++)
            {
                int size = frameSizes[index];
                if (size <= 0 || size > 256)
                {
                    throw new ArgumentOutOfRangeException("frameSizes", "ICO frame sizes must be between 1 and 256 pixels.");
                }

                frames.Add(CreatePng(size, size, 0));
            }

            using (MemoryStream output = new MemoryStream())
            {
                WriteUInt16LittleEndian(output, 0);
                WriteUInt16LittleEndian(output, 1);
                WriteUInt16LittleEndian(output, checked((ushort)frames.Count));

                uint imageOffset = checked((uint)(6 + (16 * frames.Count)));
                for (int index = 0; index < frames.Count; index++)
                {
                    int size = frameSizes[index];
                    output.WriteByte(size == 256 ? (byte)0 : checked((byte)size));
                    output.WriteByte(size == 256 ? (byte)0 : checked((byte)size));
                    output.WriteByte(0);
                    output.WriteByte(0);
                    WriteUInt16LittleEndian(output, 1);
                    WriteUInt16LittleEndian(output, 32);
                    WriteUInt32LittleEndian(output, checked((uint)frames[index].Length));
                    WriteUInt32LittleEndian(output, imageOffset);
                    imageOffset = checked(imageOffset + (uint)frames[index].Length);
                }

                for (int index = 0; index < frames.Count; index++)
                {
                    byte[] frame = frames[index];
                    output.Write(frame, 0, frame.Length);
                }

                return output.ToArray();
            }
        }

        private static void DrawNeutralPlaceholder(byte[] pixels, int width, int height, int style)
        {
            bool transparent = style == 2 || style == 3;
            bool lightForeground = style == 3;

            if (!transparent)
            {
                FillRectangle(pixels, width, height, 0, 0, width, height, 24, 34, 44, 255);
            }

            int side = Math.Min(width, height);
            int motifWidth = Math.Max(8, (side * 58) / 100);
            int motifHeight = Math.Max(8, (side * 58) / 100);
            int left = (width - motifWidth) / 2;
            int top = (height - motifHeight) / 2;
            int border = Math.Max(1, side / 24);

            byte frameRed = lightForeground ? (byte)236 : (transparent ? (byte)47 : (byte)194);
            byte frameGreen = lightForeground ? (byte)241 : (transparent ? (byte)61 : (byte)205);
            byte frameBlue = lightForeground ? (byte)245 : (transparent ? (byte)72 : (byte)215);
            FillRectangle(pixels, width, height, left, top, motifWidth, motifHeight, frameRed, frameGreen, frameBlue, 255);

            byte panelRed = transparent ? (byte)0 : (byte)38;
            byte panelGreen = transparent ? (byte)0 : (byte)52;
            byte panelBlue = transparent ? (byte)0 : (byte)65;
            byte panelAlpha = transparent ? (byte)0 : (byte)255;
            FillRectangle(
                pixels,
                width,
                height,
                left + border,
                top + border,
                Math.Max(1, motifWidth - (2 * border)),
                Math.Max(1, motifHeight - (2 * border)),
                panelRed,
                panelGreen,
                panelBlue,
                panelAlpha);

            int innerWidth = Math.Max(3, motifWidth - (4 * border));
            int innerHeight = Math.Max(3, motifHeight - (4 * border));
            int innerLeft = left + (motifWidth - innerWidth) / 2;
            int innerTop = top + (motifHeight - innerHeight) / 2;
            int barWidth = Math.Max(1, innerWidth / 7);
            int gap = Math.Max(1, barWidth);
            int barsWidth = (3 * barWidth) + (2 * gap);
            int barsLeft = innerLeft + Math.Max(0, (innerWidth - barsWidth) / 2);
            int bottom = innerTop + innerHeight;
            int[] heights = new int[]
            {
                Math.Max(1, (innerHeight * 42) / 100),
                Math.Max(1, (innerHeight * 68) / 100),
                Math.Max(1, (innerHeight * 86) / 100)
            };

            byte[,] darkColors = new byte[,]
            {
                { 86, 129, 153 },
                { 132, 157, 174 },
                { 205, 215, 223 }
            };
            byte[,] lightColors = new byte[,]
            {
                { 170, 195, 210 },
                { 207, 220, 229 },
                { 244, 247, 249 }
            };

            for (int index = 0; index < 3; index++)
            {
                byte red = lightForeground ? lightColors[index, 0] : darkColors[index, 0];
                byte green = lightForeground ? lightColors[index, 1] : darkColors[index, 1];
                byte blue = lightForeground ? lightColors[index, 2] : darkColors[index, 2];
                int x = barsLeft + (index * (barWidth + gap));
                FillRectangle(pixels, width, height, x, bottom - heights[index], barWidth, heights[index], red, green, blue, 255);
            }

            int markerHeight = Math.Max(1, border);
            int markerWidth = Math.Max(1, innerWidth / 3);
            FillRectangle(
                pixels,
                width,
                height,
                innerLeft,
                innerTop,
                markerWidth,
                markerHeight,
                frameRed,
                frameGreen,
                frameBlue,
                255);
        }

        private static void FillRectangle(
            byte[] pixels,
            int canvasWidth,
            int canvasHeight,
            int x,
            int y,
            int rectangleWidth,
            int rectangleHeight,
            byte red,
            byte green,
            byte blue,
            byte alpha)
        {
            int startX = Math.Max(0, x);
            int startY = Math.Max(0, y);
            int endX = Math.Min(canvasWidth, x + rectangleWidth);
            int endY = Math.Min(canvasHeight, y + rectangleHeight);

            for (int row = startY; row < endY; row++)
            {
                int pixelOffset = checked(((row * canvasWidth) + startX) * 4);
                for (int column = startX; column < endX; column++)
                {
                    pixels[pixelOffset] = red;
                    pixels[pixelOffset + 1] = green;
                    pixels[pixelOffset + 2] = blue;
                    pixels[pixelOffset + 3] = alpha;
                    pixelOffset += 4;
                }
            }
        }

        private static byte[] CreateDeterministicZlibStream(byte[] input)
        {
            using (MemoryStream output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x01);

                BitWriter writer = new BitWriter(output);
                writer.WriteBits(1, 1);
                writer.WriteBits(1, 2);

                int[] lastPositionByHash = new int[65536];
                for (int index = 0; index < lastPositionByHash.Length; index++)
                {
                    lastPositionByHash[index] = -1;
                }

                int position = 0;
                while (position < input.Length)
                {
                    int matchLength = 0;
                    int matchDistance = 0;

                    if (position + 2 < input.Length)
                    {
                        int hash = ComputeThreeByteHash(input, position);
                        int candidate = lastPositionByHash[hash];
                        lastPositionByHash[hash] = position;

                        if (candidate >= 0)
                        {
                            int distance = position - candidate;
                            if (distance <= 32768)
                            {
                                int maximumLength = Math.Min(258, input.Length - position);
                                int length = 0;
                                while (length < maximumLength && input[candidate + length] == input[position + length])
                                {
                                    length++;
                                }

                                if (length >= 3)
                                {
                                    matchLength = length;
                                    matchDistance = distance;
                                }
                            }
                        }
                    }

                    if (matchLength >= 3)
                    {
                        WriteLength(writer, matchLength);
                        WriteDistance(writer, matchDistance);

                        int updateEnd = Math.Min(position + matchLength, input.Length - 2);
                        for (int updatePosition = position + 1; updatePosition < updateEnd; updatePosition++)
                        {
                            lastPositionByHash[ComputeThreeByteHash(input, updatePosition)] = updatePosition;
                        }

                        position += matchLength;
                    }
                    else
                    {
                        WriteFixedSymbol(writer, input[position]);
                        position++;
                    }
                }

                WriteFixedSymbol(writer, 256);
                writer.FlushFinalByte();

                uint adler32 = ComputeAdler32(input);
                WriteUInt32BigEndian(output, adler32);
                return output.ToArray();
            }
        }

        private static int ComputeThreeByteHash(byte[] input, int offset)
        {
            uint hash = input[offset];
            hash = ((hash * 251U) + input[offset + 1]) & 0xFFFFU;
            hash = ((hash * 251U) + input[offset + 2]) & 0xFFFFU;
            return checked((int)hash);
        }

        private static void WriteLength(BitWriter writer, int length)
        {
            int[] bases = new int[]
            {
                3, 4, 5, 6, 7, 8, 9, 10,
                11, 13, 15, 17,
                19, 23, 27, 31,
                35, 43, 51, 59,
                67, 83, 99, 115,
                131, 163, 195, 227,
                258
            };
            int[] extraBits = new int[]
            {
                0, 0, 0, 0, 0, 0, 0, 0,
                1, 1, 1, 1,
                2, 2, 2, 2,
                3, 3, 3, 3,
                4, 4, 4, 4,
                5, 5, 5, 5,
                0
            };

            for (int index = 0; index < bases.Length; index++)
            {
                int maximum = index == bases.Length - 1
                    ? 258
                    : bases[index] + ((1 << extraBits[index]) - 1);
                if (length <= maximum)
                {
                    WriteFixedSymbol(writer, 257 + index);
                    if (extraBits[index] > 0)
                    {
                        writer.WriteBits(checked((uint)(length - bases[index])), extraBits[index]);
                    }

                    return;
                }
            }

            throw new InvalidOperationException("The DEFLATE match length was outside the supported range.");
        }

        private static void WriteDistance(BitWriter writer, int distance)
        {
            int[] bases = new int[]
            {
                1, 2, 3, 4,
                5, 7, 9, 13,
                17, 25, 33, 49,
                65, 97, 129, 193,
                257, 385, 513, 769,
                1025, 1537, 2049, 3073,
                4097, 6145, 8193, 12289,
                16385, 24577
            };
            int[] extraBits = new int[]
            {
                0, 0, 0, 0,
                1, 1, 2, 2,
                3, 3, 4, 4,
                5, 5, 6, 6,
                7, 7, 8, 8,
                9, 9, 10, 10,
                11, 11, 12, 12,
                13, 13
            };

            for (int index = 0; index < bases.Length; index++)
            {
                int maximum = bases[index] + ((1 << extraBits[index]) - 1);
                if (distance <= maximum)
                {
                    writer.WriteBits(ReverseBits(checked((uint)index), 5), 5);
                    if (extraBits[index] > 0)
                    {
                        writer.WriteBits(checked((uint)(distance - bases[index])), extraBits[index]);
                    }

                    return;
                }
            }

            throw new InvalidOperationException("The DEFLATE match distance was outside the supported range.");
        }

        private static void WriteFixedSymbol(BitWriter writer, int symbol)
        {
            uint code;
            int bitCount;
            if (symbol <= 143)
            {
                code = checked((uint)(0x30 + symbol));
                bitCount = 8;
            }
            else if (symbol <= 255)
            {
                code = checked((uint)(0x190 + (symbol - 144)));
                bitCount = 9;
            }
            else if (symbol <= 279)
            {
                code = checked((uint)(symbol - 256));
                bitCount = 7;
            }
            else if (symbol <= 287)
            {
                code = checked((uint)(0xC0 + (symbol - 280)));
                bitCount = 8;
            }
            else
            {
                throw new ArgumentOutOfRangeException("symbol");
            }

            writer.WriteBits(ReverseBits(code, bitCount), bitCount);
        }

        private static uint ReverseBits(uint value, int bitCount)
        {
            uint reversed = 0;
            for (int bit = 0; bit < bitCount; bit++)
            {
                reversed = (reversed << 1) | (value & 1U);
                value >>= 1;
            }

            return reversed;
        }

        private sealed class BitWriter
        {
            private readonly Stream output;
            private uint pendingBits;
            private int pendingBitCount;

            public BitWriter(Stream output)
            {
                this.output = output;
            }

            public void WriteBits(uint value, int bitCount)
            {
                if (bitCount < 0 || bitCount > 16)
                {
                    throw new ArgumentOutOfRangeException("bitCount");
                }

                uint mask = bitCount == 0 ? 0U : (1U << bitCount) - 1U;
                pendingBits |= (value & mask) << pendingBitCount;
                pendingBitCount += bitCount;

                while (pendingBitCount >= 8)
                {
                    output.WriteByte((byte)(pendingBits & 0xFF));
                    pendingBits >>= 8;
                    pendingBitCount -= 8;
                }
            }

            public void FlushFinalByte()
            {
                if (pendingBitCount > 0)
                {
                    output.WriteByte((byte)(pendingBits & 0xFF));
                    pendingBits = 0;
                    pendingBitCount = 0;
                }
            }
        }

        private static uint ComputeAdler32(byte[] input)
        {
            const uint modulus = 65521;
            uint first = 1;
            uint second = 0;
            for (int index = 0; index < input.Length; index++)
            {
                first = (first + input[index]) % modulus;
                second = (second + first) % modulus;
            }

            return (second << 16) | first;
        }

        private static void WritePngChunk(Stream output, byte[] type, byte[] data)
        {
            WriteUInt32BigEndian(output, checked((uint)data.Length));
            output.Write(type, 0, type.Length);
            output.Write(data, 0, data.Length);

            uint crc = 0xFFFFFFFFU;
            crc = UpdateCrc32(crc, type);
            crc = UpdateCrc32(crc, data);
            WriteUInt32BigEndian(output, crc ^ 0xFFFFFFFFU);
        }

        private static uint UpdateCrc32(uint crc, byte[] data)
        {
            for (int index = 0; index < data.Length; index++)
            {
                int tableIndex = checked((int)((crc ^ data[index]) & 0xFF));
                crc = Crc32Table[tableIndex] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] CreateCrc32Table()
        {
            uint[] table = new uint[256];
            for (uint value = 0; value < table.Length; value++)
            {
                uint remainder = value;
                for (int bit = 0; bit < 8; bit++)
                {
                    remainder = (remainder & 1) == 1
                        ? 0xEDB88320U ^ (remainder >> 1)
                        : remainder >> 1;
                }

                table[value] = remainder;
            }

            return table;
        }

        private static void WriteUInt16LittleEndian(Stream output, ushort value)
        {
            output.WriteByte((byte)(value & 0xFF));
            output.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static void WriteUInt32LittleEndian(Stream output, uint value)
        {
            output.WriteByte((byte)(value & 0xFF));
            output.WriteByte((byte)((value >> 8) & 0xFF));
            output.WriteByte((byte)((value >> 16) & 0xFF));
            output.WriteByte((byte)((value >> 24) & 0xFF));
        }

        private static void WriteUInt32BigEndian(Stream output, uint value)
        {
            output.WriteByte((byte)((value >> 24) & 0xFF));
            output.WriteByte((byte)((value >> 16) & 0xFF));
            output.WriteByte((byte)((value >> 8) & 0xFF));
            output.WriteByte((byte)(value & 0xFF));
        }

        private static void WriteUInt32BigEndian(byte[] destination, int offset, uint value)
        {
            destination[offset] = (byte)((value >> 24) & 0xFF);
            destination[offset + 1] = (byte)((value >> 16) & 0xFF);
            destination[offset + 2] = (byte)((value >> 8) & 0xFF);
            destination[offset + 3] = (byte)(value & 0xFF);
        }
    }
}
'@

if (-not ('IptvSuite.WindowsProductionAssets.AssetGeneratorV1' -as [type])) {
    Add-Type -TypeDefinition $generatorSource -Language CSharp
}

function Get-Sha256Hex {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]] $Bytes
    )

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha256.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Test-ByteSequenceEqual {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]] $Expected,

        [Parameter(Mandatory = $true)]
        [byte[]] $Actual
    )

    if ($Expected.Length -ne $Actual.Length) {
        return $false
    }

    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Expected[$index] -ne $Actual[$index]) {
            return $false
        }
    }

    return $true
}

$iconFrameSizes = [int[]] @(256, 128, 64, 48, 32, 16)
$assetDefinitions = @(
    [pscustomobject] @{
        Path = 'apps/windows/src/IptvSuite.Windows/Assets/AppIcon.ico'
        MediaType = 'image/vnd.microsoft.icon'
        Width = $null
        Height = $null
        FrameSizes = $iconFrameSizes
        Bytes = [IptvSuite.WindowsProductionAssets.AssetGeneratorV1]::CreatePngFrameIcon($iconFrameSizes)
    },
    [pscustomobject] @{
        Path = 'apps/windows/src/IptvSuite.Windows/Assets/SplashScreen.scale-200.png'
        MediaType = 'image/png'
        Width = 1240
        Height = 600
        FrameSizes = [int[]] @()
        Bytes = [IptvSuite.WindowsProductionAssets.AssetGeneratorV1]::CreatePng(1240, 600, 1)
    },
    [pscustomobject] @{
        Path = 'apps/windows/src/IptvSuite.Windows/Assets/Square150x150Logo.scale-200.png'
        MediaType = 'image/png'
        Width = 300
        Height = 300
        FrameSizes = [int[]] @()
        Bytes = [IptvSuite.WindowsProductionAssets.AssetGeneratorV1]::CreatePng(300, 300, 0)
    },
    [pscustomobject] @{
        Path = 'apps/windows/src/IptvSuite.Windows/Assets/Square44x44Logo.scale-200.png'
        MediaType = 'image/png'
        Width = 88
        Height = 88
        FrameSizes = [int[]] @()
        Bytes = [IptvSuite.WindowsProductionAssets.AssetGeneratorV1]::CreatePng(88, 88, 0)
    },
    [pscustomobject] @{
        Path = 'apps/windows/src/IptvSuite.Windows/Assets/Square44x44Logo.targetsize-24_altform-unplated.png'
        MediaType = 'image/png'
        Width = 24
        Height = 24
        FrameSizes = [int[]] @()
        Bytes = [IptvSuite.WindowsProductionAssets.AssetGeneratorV1]::CreatePng(24, 24, 2)
    },
    [pscustomobject] @{
        Path = 'apps/windows/src/IptvSuite.Windows/Assets/Square44x44Logo.targetsize-48_altform-lightunplated.png'
        MediaType = 'image/png'
        Width = 48
        Height = 48
        FrameSizes = [int[]] @()
        Bytes = [IptvSuite.WindowsProductionAssets.AssetGeneratorV1]::CreatePng(48, 48, 3)
    },
    [pscustomobject] @{
        Path = 'apps/windows/src/IptvSuite.Windows/Assets/StoreLogo.png'
        MediaType = 'image/png'
        Width = 50
        Height = 50
        FrameSizes = [int[]] @()
        Bytes = [IptvSuite.WindowsProductionAssets.AssetGeneratorV1]::CreatePng(50, 50, 0)
    },
    [pscustomobject] @{
        Path = 'apps/windows/src/IptvSuite.Windows/Assets/Wide310x150Logo.scale-200.png'
        MediaType = 'image/png'
        Width = 620
        Height = 300
        FrameSizes = [int[]] @()
        Bytes = [IptvSuite.WindowsProductionAssets.AssetGeneratorV1]::CreatePng(620, 300, 1)
    }
)

$root = if ($PSCmdlet.ParameterSetName -eq 'Generate') { $OutputRoot } else { $VerifyRoot }
$resolvedRoot = [System.IO.Path]::GetFullPath($root)

if ($PSCmdlet.ParameterSetName -eq 'Generate') {
    [System.IO.Directory]::CreateDirectory($resolvedRoot) | Out-Null
}
elseif (-not [System.IO.Directory]::Exists($resolvedRoot)) {
    throw "The verification root does not exist: $resolvedRoot"
}

$result = [System.Collections.Generic.List[object]]::new()
foreach ($asset in $assetDefinitions) {
    $relativeNativePath = $asset.Path.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $targetPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($resolvedRoot, $relativeNativePath))
    $expectedPrefix = $resolvedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $targetPath.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The asset path escaped the selected root: $($asset.Path)"
    }

    if ($PSCmdlet.ParameterSetName -eq 'Generate') {
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($targetPath)) | Out-Null
        [System.IO.File]::WriteAllBytes($targetPath, $asset.Bytes)
    }
    else {
        if (-not [System.IO.File]::Exists($targetPath)) {
            throw "The expected generated asset is missing: $($asset.Path)"
        }

        $actualBytes = [System.IO.File]::ReadAllBytes($targetPath)
        if (-not (Test-ByteSequenceEqual -Expected $asset.Bytes -Actual $actualBytes)) {
            throw "The generated asset does not match the deterministic recipe: $($asset.Path)"
        }
    }

    $result.Add([pscustomobject] @{
            Path = $asset.Path
            MediaType = $asset.MediaType
            Width = $asset.Width
            Height = $asset.Height
            FrameSizes = [int[]] $asset.FrameSizes
            Length = $asset.Bytes.Length
            Sha256 = Get-Sha256Hex -Bytes $asset.Bytes
        })
}

$canonicalLines = foreach ($asset in $result) {
    $dimensions = if ($asset.FrameSizes.Count -gt 0) {
        'frames=' + (($asset.FrameSizes | ForEach-Object { $_.ToString([System.Globalization.CultureInfo]::InvariantCulture) }) -join ',')
    }
    else {
        'size=' + $asset.Width.ToString([System.Globalization.CultureInfo]::InvariantCulture) + 'x' + $asset.Height.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }

    $asset.Path + '|' + $asset.MediaType + '|' + $dimensions + '|' +
        $asset.Length.ToString([System.Globalization.CultureInfo]::InvariantCulture) + '|' + $asset.Sha256
}

$canonicalBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(($canonicalLines -join "`n") + "`n")
$canonicalAssetSetSha256 = Get-Sha256Hex -Bytes $canonicalBytes
$operation = if ($PSCmdlet.ParameterSetName -eq 'Generate') { 'generated' } else { 'verified' }
Write-Host "Windows production assets $operation deterministically."
$result | Select-Object Path, MediaType, Width, Height, FrameSizes, Length, Sha256
Write-Host "Canonical asset-set SHA-256: $canonicalAssetSetSha256"
