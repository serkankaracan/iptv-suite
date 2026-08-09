using System.Text;

namespace IptvSuite.Domain;

public enum PlaylistContentKind
{
    Unknown,
    ExtendedM3uCatalog,
    HlsMasterManifest,
    HlsMediaManifest,
}

public static class PlaylistKindClassifier
{
    public const int MaxPrefixCharacters = 65_536;
    public const int MaxLineCharacters = 8_192;
    public const int MaxInspectedLines = 512;

    private const string ExtendedM3uHeader = "#EXTM3U";
    private const string ExtendedInfoTag = "#EXTINF";

    private static readonly string[] MasterManifestTags =
    [
        "#EXT-X-STREAM-INF",
        "#EXT-X-I-FRAME-STREAM-INF",
        "#EXT-X-MEDIA",
        "#EXT-X-SESSION-DATA",
        "#EXT-X-SESSION-KEY",
        "#EXT-X-CONTENT-STEERING",
    ];

    private static readonly string[] MediaManifestTags =
    [
        "#EXT-X-TARGETDURATION",
        "#EXT-X-MEDIA-SEQUENCE",
        "#EXT-X-DISCONTINUITY-SEQUENCE",
        "#EXT-X-ENDLIST",
        "#EXT-X-PLAYLIST-TYPE",
        "#EXT-X-I-FRAMES-ONLY",
        "#EXT-X-KEY",
        "#EXT-X-MAP",
        "#EXT-X-BYTERANGE",
        "#EXT-X-DISCONTINUITY",
        "#EXT-X-PROGRAM-DATE-TIME",
        "#EXT-X-DATERANGE",
        "#EXT-X-GAP",
        "#EXT-X-PART-INF",
        "#EXT-X-SERVER-CONTROL",
        "#EXT-X-PART",
        "#EXT-X-PRELOAD-HINT",
        "#EXT-X-RENDITION-REPORT",
        "#EXT-X-SKIP",
    ];

    public static DomainResult<PlaylistContentKind> Classify(string? contentPrefix)
    {
        if (contentPrefix is null ||
            contentPrefix.Length is 0 or > MaxPrefixCharacters)
        {
            return Unsupported();
        }

        string normalizedPrefix;
        try
        {
            normalizedPrefix = contentPrefix.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return Unsupported();
        }

        if (normalizedPrefix.Length is 0 or > MaxPrefixCharacters)
        {
            return Unsupported();
        }

        ReadOnlySpan<char> prefix = normalizedPrefix.AsSpan();
        if (prefix[0] == '\uFEFF')
        {
            prefix = prefix[1..];
        }

        if (prefix.IsEmpty || ContainsDisallowedControl(prefix))
        {
            return Unsupported();
        }

        bool headerSeen = false;
        bool extendedInfoSeen = false;
        bool hlsTagSeen = false;
        bool masterTagSeen = false;
        bool mediaTagSeen = false;
        int inspectedLines = 0;
        int lineStart = 0;

        while (lineStart < prefix.Length)
        {
            if (++inspectedLines > MaxInspectedLines)
            {
                return Unsupported();
            }

            ReadOnlySpan<char> remaining = prefix[lineStart..];
            int lineBreakIndex = remaining.IndexOfAny('\r', '\n');
            ReadOnlySpan<char> line = lineBreakIndex < 0
                ? remaining
                : remaining[..lineBreakIndex];

            if (line.Length > MaxLineCharacters)
            {
                return Unsupported();
            }

            line = line.Trim();
            if (!line.IsEmpty)
            {
                if (!headerSeen)
                {
                    if (!IsExtendedM3uHeader(line))
                    {
                        return Unsupported();
                    }

                    headerSeen = true;
                }
                else
                {
                    extendedInfoSeen |= IsDirective(line, ExtendedInfoTag);

                    if (line.StartsWith("#EXT-X-", StringComparison.Ordinal))
                    {
                        hlsTagSeen = true;
                        masterTagSeen |= MatchesAnyDirective(line, MasterManifestTags);
                        mediaTagSeen |= MatchesAnyDirective(line, MediaManifestTags);
                    }
                }
            }

            if (lineBreakIndex < 0)
            {
                break;
            }

            lineStart += lineBreakIndex + 1;
            if (prefix[lineStart - 1] == '\r' &&
                lineStart < prefix.Length &&
                prefix[lineStart] == '\n')
            {
                lineStart++;
            }
        }

        if (!headerSeen || (masterTagSeen && mediaTagSeen))
        {
            return Unsupported();
        }

        if (masterTagSeen)
        {
            return DomainResult.Success(PlaylistContentKind.HlsMasterManifest);
        }

        if (mediaTagSeen)
        {
            return DomainResult.Success(PlaylistContentKind.HlsMediaManifest);
        }

        if (!hlsTagSeen && extendedInfoSeen)
        {
            return DomainResult.Success(PlaylistContentKind.ExtendedM3uCatalog);
        }

        return Unsupported();
    }

    private static bool ContainsDisallowedControl(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (character == '\uFEFF' ||
                (char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAnyDirective(ReadOnlySpan<char> line, string[] directives)
    {
        foreach (string directive in directives)
        {
            if (IsDirective(line, directive))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExtendedM3uHeader(ReadOnlySpan<char> line)
    {
        if (!line.StartsWith(ExtendedM3uHeader, StringComparison.Ordinal))
        {
            return false;
        }

        return line.Length == ExtendedM3uHeader.Length ||
            char.IsWhiteSpace(line[ExtendedM3uHeader.Length]);
    }

    private static bool IsDirective(ReadOnlySpan<char> line, string directive)
    {
        if (!line.StartsWith(directive, StringComparison.Ordinal))
        {
            return false;
        }

        return line.Length == directive.Length ||
            line[directive.Length] == ':' ||
            char.IsWhiteSpace(line[directive.Length]);
    }

    private static DomainResult<PlaylistContentKind> Unsupported() =>
        DomainResult.Failure<PlaylistContentKind>(DomainErrorCode.UnsupportedPlaylistFormat);
}
