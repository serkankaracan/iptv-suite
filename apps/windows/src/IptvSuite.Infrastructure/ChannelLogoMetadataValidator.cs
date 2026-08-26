using System.Buffers.Binary;
using IptvSuite.Application;

namespace IptvSuite.Infrastructure;

internal static class ChannelLogoMetadataValidator
{
    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    internal static bool TryValidate(
        ReadOnlyMemory<byte> contentMemory,
        HttpResponseMediaType mediaType,
        int maximumDimension,
        long maximumPixels,
        out ChannelLogoFormat format,
        out int width,
        out int height)
    {
        ReadOnlySpan<byte> content = contentMemory.Span;
        format = default;
        width = 0;
        height = 0;

        bool identified = mediaType switch
        {
            HttpResponseMediaType.Png =>
                TryReadPng(content, out width, out height) && SetFormat(ChannelLogoFormat.Png, out format),
            HttpResponseMediaType.Jpeg =>
                TryReadJpeg(content, out width, out height) && SetFormat(ChannelLogoFormat.Jpeg, out format),
            HttpResponseMediaType.WebP =>
                TryReadWebP(content, out width, out height) && SetFormat(ChannelLogoFormat.WebP, out format),
            _ => false,
        };

        if (!identified || width <= 0 || height <= 0 ||
            width > maximumDimension || height > maximumDimension ||
            (long)width * height > maximumPixels)
        {
            format = default;
            width = 0;
            height = 0;
            return false;
        }

        return true;
    }

    private static bool SetFormat(ChannelLogoFormat value, out ChannelLogoFormat format)
    {
        format = value;
        return true;
    }

    private static bool TryReadPng(ReadOnlySpan<byte> content, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (content.Length < 33 ||
            !content[..8].SequenceEqual(PngSignature) ||
            BinaryPrimitives.ReadUInt32BigEndian(content.Slice(8, 4)) != 13 ||
            !content.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        uint unsignedWidth = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(16, 4));
        uint unsignedHeight = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(20, 4));
        if (unsignedWidth > int.MaxValue || unsignedHeight > int.MaxValue)
        {
            return false;
        }

        width = (int)unsignedWidth;
        height = (int)unsignedHeight;
        return true;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> content, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (content.Length < 4 || content[0] != 0xff || content[1] != 0xd8)
        {
            return false;
        }

        int offset = 2;
        while (offset < content.Length)
        {
            if (content[offset++] != 0xff)
            {
                return false;
            }

            while (offset < content.Length && content[offset] == 0xff)
            {
                offset++;
            }

            if (offset >= content.Length)
            {
                return false;
            }

            byte marker = content[offset++];
            if (marker is 0x00 or 0xd9 or 0xda)
            {
                return false;
            }

            if (marker is 0x01 or >= 0xd0 and <= 0xd8)
            {
                continue;
            }

            if (offset > content.Length - 2)
            {
                return false;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(content.Slice(offset, 2));
            if (segmentLength < 2 || segmentLength > content.Length - offset)
            {
                return false;
            }

            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 7)
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(content.Slice(offset + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(content.Slice(offset + 5, 2));
                return true;
            }

            offset += segmentLength;
        }

        return false;
    }

    private static bool IsStartOfFrame(byte marker) => marker is
        0xc0 or 0xc1 or 0xc2 or 0xc3 or
        0xc5 or 0xc6 or 0xc7 or
        0xc9 or 0xca or 0xcb or
        0xcd or 0xce or 0xcf;

    private static bool TryReadWebP(ReadOnlySpan<byte> content, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (content.Length < 20 ||
            !content[..4].SequenceEqual("RIFF"u8) ||
            !content.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return false;
        }

        uint riffLength = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(4, 4));
        if ((ulong)riffLength + 8UL != (ulong)content.Length)
        {
            return false;
        }

        uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(16, 4));
        if ((ulong)chunkLength > (ulong)(content.Length - 20))
        {
            return false;
        }

        ReadOnlySpan<byte> chunkKind = content.Slice(12, 4);
        if (chunkKind.SequenceEqual("VP8X"u8))
        {
            if (chunkLength < 10 || content.Length < 30)
            {
                return false;
            }

            width = 1 + ReadUInt24LittleEndian(content.Slice(24, 3));
            height = 1 + ReadUInt24LittleEndian(content.Slice(27, 3));
            return true;
        }

        if (chunkKind.SequenceEqual("VP8L"u8))
        {
            if (chunkLength < 5 || content.Length < 25 || content[20] != 0x2f)
            {
                return false;
            }

            byte first = content[21];
            byte second = content[22];
            byte third = content[23];
            byte fourth = content[24];
            width = 1 + first + ((second & 0x3f) << 8);
            height = 1 + (second >> 6) + (third << 2) + ((fourth & 0x0f) << 10);
            return true;
        }

        if (chunkKind.SequenceEqual("VP8 "u8))
        {
            if (chunkLength < 10 || content.Length < 30 ||
                !content.Slice(23, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
            {
                return false;
            }

            width = BinaryPrimitives.ReadUInt16LittleEndian(content.Slice(26, 2)) & 0x3fff;
            height = BinaryPrimitives.ReadUInt16LittleEndian(content.Slice(28, 2)) & 0x3fff;
            return true;
        }

        return false;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> value) =>
        value[0] | (value[1] << 8) | (value[2] << 16);
}
