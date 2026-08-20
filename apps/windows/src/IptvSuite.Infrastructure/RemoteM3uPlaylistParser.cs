using System.Globalization;
using System.Text;
using IptvSuite.Domain;

namespace IptvSuite.Infrastructure;

internal sealed class RemoteM3uEntry
{
    internal RemoteM3uEntry(
        string locator,
        string name,
        string? tvgId,
        string? tvgName,
        string? logo,
        string? groupTitle,
        int? number,
        ChannelNormalizationWarnings warnings)
    {
        Locator = locator;
        Name = name;
        TvgId = tvgId;
        TvgName = tvgName;
        Logo = logo;
        GroupTitle = groupTitle;
        Number = number;
        Warnings = warnings;
    }

    internal string Locator { get; }
    internal string Name { get; }
    internal string? TvgId { get; }
    internal string? TvgName { get; }
    internal string? Logo { get; }
    internal string? GroupTitle { get; }
    internal int? Number { get; }
    internal ChannelNormalizationWarnings Warnings { get; }

    public override string ToString() => "[REMOTE-M3U-ENTRY]";
}

internal sealed class RemoteM3uParseResult
{
    internal RemoteM3uParseResult(
        PlaylistContentKind contentKind,
        IReadOnlyList<RemoteM3uEntry> entries,
        int processedEntryCount,
        int skippedEntryCount,
        string? hlsLocator = null)
    {
        ContentKind = contentKind;
        Entries = entries;
        ProcessedEntryCount = processedEntryCount;
        SkippedEntryCount = skippedEntryCount;
        HlsLocator = hlsLocator;
    }

    internal PlaylistContentKind ContentKind { get; }
    internal IReadOnlyList<RemoteM3uEntry> Entries { get; }
    internal int ProcessedEntryCount { get; }
    internal int SkippedEntryCount { get; }
    internal string? HlsLocator { get; }

    public override string ToString() => $"[REMOTE-M3U-RESULT:{ProcessedEntryCount}]";
}

internal interface IRemoteM3uEntrySink
{
    ValueTask<DomainResult<bool>> WriteAsync(
        RemoteM3uEntry entry,
        CancellationToken cancellationToken);
}

internal interface IRemoteM3uImportSink : IRemoteM3uEntrySink
{
    ValueTask<DomainResult<bool>> BeginAsync(ContentSource source, CancellationToken cancellationToken);

    ValueTask<DomainResult<bool>> CompleteAsync(
        RemoteM3uParseResult parseResult,
        CancellationToken cancellationToken);

    ValueTask AbortAsync(CancellationToken cancellationToken);
}

internal static class RemoteM3uPlaylistParser
{
    internal const int MaximumEntries = 50_000;
    internal const int MaximumLineCharacters = 8_192;
    internal const int MaximumMetadataValueCharacters = 4_096;
    internal const int MaximumLocatorCharacters = 4_096;
    internal const int MaximumTotalCharacters = 32 * 1024 * 1024;

    internal static async ValueTask<DomainResult<RemoteM3uParseResult>> ParseAsync(
        Stream content,
        Uri finalPlaylistUri,
        CancellationToken cancellationToken = default)
        => await ParseCoreAsync(content, finalPlaylistUri, sink: null, cancellationToken)
            .ConfigureAwait(false);

    internal static async ValueTask<DomainResult<RemoteM3uParseResult>> ParseToSinkAsync(
        Stream content,
        Uri finalPlaylistUri,
        IRemoteM3uEntrySink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return await ParseCoreAsync(content, finalPlaylistUri, sink, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<DomainResult<RemoteM3uParseResult>> ParseCoreAsync(
        Stream content,
        Uri finalPlaylistUri,
        IRemoteM3uEntrySink? sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(finalPlaylistUri);

        if (!IsSafeAbsoluteHttpsUri(finalPlaylistUri))
        {
            return Unsupported();
        }

        List<RemoteM3uEntry>? entries = sink is null ? [] : null;
        var tvgIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        int processedEntryCount = 0;
        int skipped = 0;
        int totalCharacters = 0;
        PendingMetadata? pending = null;
        bool hlsSeen = false;
        bool hlsMasterSeen = false;
        bool hlsMediaSeen = false;

        try
        {
            using var reader = new StreamReader(
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4_096,
                leaveOpen: true);

            string? firstLine = await ReadBoundedLineAsync(reader, cancellationToken).ConfigureAwait(false);
            if (firstLine is null)
            {
                return Unsupported();
            }

            totalCharacters = firstLine.Length;
            ReadOnlySpan<char> header = firstLine.AsSpan();
            if (!header.IsEmpty && header[0] == '\uFEFF')
            {
                header = header[1..];
            }

            if (!header.Trim().SequenceEqual("#EXTM3U"))
            {
                return Unsupported();
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line = await ReadBoundedLineAsync(reader, cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                totalCharacters = checked(totalCharacters + line.Length);
                if (totalCharacters > MaximumTotalCharacters)
                {
                    return Unsupported();
                }

                ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
                if (trimmed.IsEmpty)
                {
                    continue;
                }

                if (trimmed.StartsWith("#EXT-X-", StringComparison.Ordinal))
                {
                    if (processedEntryCount > 0)
                    {
                        return Unsupported();
                    }

                    hlsSeen = true;
                    pending = null;
                    hlsMasterSeen |= IsHlsMasterDirective(trimmed);
                    hlsMediaSeen |= IsHlsMediaDirective(trimmed);
                    continue;
                }

                if (hlsSeen)
                {
                    continue;
                }

                if (trimmed.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    pending = TryParseMetadata(trimmed[8..], out PendingMetadata metadata)
                        ? metadata
                        : null;
                    if (pending is null)
                    {
                        skipped++;
                    }

                    continue;
                }

                if (trimmed[0] == '#')
                {
                    continue;
                }

                if (pending is null)
                {
                    skipped++;
                    continue;
                }

                if (!TryResolveLocator(finalPlaylistUri, trimmed, out string locator))
                {
                    skipped++;
                    pending = null;
                    continue;
                }

                if (processedEntryCount == MaximumEntries)
                {
                    return Unsupported();
                }

                PendingMetadata completed = pending.Value;
                if (completed.TvgId is not null && !tvgIdentifiers.Add(completed.TvgId))
                {
                    completed = completed with
                    {
                        Warnings = completed.Warnings |
                            ChannelNormalizationWarnings.DuplicateProviderIdentifier,
                    };
                }

                RemoteM3uEntry entry = completed.ToEntry(locator);
                if (sink is null)
                {
                    entries!.Add(entry);
                }
                else
                {
                    DomainResult<bool> write = await sink.WriteAsync(entry, cancellationToken)
                        .ConfigureAwait(false);
                    if (!write.IsSuccess)
                    {
                        return DomainResult.Failure<RemoteM3uParseResult>(write.Error!);
                    }
                }

                processedEntryCount++;
                pending = null;
            }
        }
        catch (DecoderFallbackException)
        {
            return Unsupported();
        }
        catch (OverflowException)
        {
            return Unsupported();
        }
        catch (OperationCanceledException)
        {
            return DomainResult.Failure<RemoteM3uParseResult>(DomainErrorCode.OperationCancelled);
        }

        if (hlsSeen)
        {
            if (hlsMasterSeen == hlsMediaSeen)
            {
                return Unsupported();
            }

            return DomainResult.Success(new RemoteM3uParseResult(
                hlsMasterSeen
                    ? PlaylistContentKind.HlsMasterManifest
                    : PlaylistContentKind.HlsMediaManifest,
                [],
                processedEntryCount: 0,
                skippedEntryCount: 0,
                finalPlaylistUri.AbsoluteUri));
        }

        return DomainResult.Success(new RemoteM3uParseResult(
            PlaylistContentKind.ExtendedM3uCatalog,
            entries ?? [],
            processedEntryCount,
            skipped));
    }

    private static async ValueTask<string?> ReadBoundedLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is not null && line.Length > MaximumLineCharacters)
        {
            throw new DecoderFallbackException("Playlist line exceeds the bounded parser contract.");
        }

        return line;
    }

    private static bool TryParseMetadata(ReadOnlySpan<char> value, out PendingMetadata metadata)
    {
        metadata = default;
        int comma = FindUnquotedComma(value);
        if (comma < 0)
        {
            return false;
        }

        ReadOnlySpan<char> attributes = value[..comma];
        ReadOnlySpan<char> displayName = value[(comma + 1)..].Trim();
        if (!TryBounded(displayName, LiveChannel.MaximumNameLength, required: true, out string? name))
        {
            return false;
        }

        string? tvgId = null;
        string? tvgName = null;
        string? logo = null;
        string? group = null;
        int? number = null;
        ChannelNormalizationWarnings warnings = ChannelNormalizationWarnings.None;

        int cursor = attributes.IndexOf(' ');
        cursor = cursor < 0 ? attributes.Length : cursor + 1;
        while (cursor < attributes.Length)
        {
            while (cursor < attributes.Length && char.IsWhiteSpace(attributes[cursor])) cursor++;
            int equals = attributes[cursor..].IndexOf('=');
            if (equals < 1) break;
            equals += cursor;
            ReadOnlySpan<char> key = attributes[cursor..equals].Trim();
            cursor = equals + 1;
            if (cursor >= attributes.Length || attributes[cursor] != '"') break;
            int closing = attributes[(cursor + 1)..].IndexOf('"');
            if (closing < 0) break;
            ReadOnlySpan<char> attributeValue = attributes.Slice(cursor + 1, closing);
            cursor += closing + 2;

            if (!TryBounded(attributeValue, MaximumMetadataValueCharacters, required: false, out string? normalized))
            {
                return false;
            }

            if (key.Equals("tvg-id", StringComparison.OrdinalIgnoreCase)) tvgId ??= normalized;
            else if (key.Equals("tvg-name", StringComparison.OrdinalIgnoreCase)) tvgName ??= normalized;
            else if (key.Equals("tvg-logo", StringComparison.OrdinalIgnoreCase)) logo ??= normalized;
            else if (key.Equals("group-title", StringComparison.OrdinalIgnoreCase)) group ??= normalized;
            else if (key.Equals("tvg-chno", StringComparison.OrdinalIgnoreCase) && normalized is not null)
            {
                if (int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                    number = parsed;
                else
                    warnings |= ChannelNormalizationWarnings.InvalidNumber;
            }
        }

        if (group is null) warnings |= ChannelNormalizationWarnings.MissingGroup;
        metadata = new PendingMetadata(name!, tvgId, tvgName, logo, group, number, warnings);
        return true;
    }

    private static int FindUnquotedComma(ReadOnlySpan<char> value)
    {
        bool quoted = false;
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '"') quoted = !quoted;
            else if (value[index] == ',' && !quoted) return index;
        }

        return -1;
    }

    private static bool IsHlsMasterDirective(ReadOnlySpan<char> line) =>
        IsDirective(line, "#EXT-X-STREAM-INF") ||
        IsDirective(line, "#EXT-X-I-FRAME-STREAM-INF") ||
        IsDirective(line, "#EXT-X-MEDIA");

    private static bool IsHlsMediaDirective(ReadOnlySpan<char> line) =>
        IsDirective(line, "#EXT-X-TARGETDURATION") ||
        IsDirective(line, "#EXT-X-MEDIA-SEQUENCE") ||
        IsDirective(line, "#EXT-X-ENDLIST") ||
        IsDirective(line, "#EXT-X-KEY") ||
        IsDirective(line, "#EXT-X-MAP");

    private static bool IsDirective(ReadOnlySpan<char> line, string directive) =>
        line.StartsWith(directive, StringComparison.Ordinal) &&
        (line.Length == directive.Length || line[directive.Length] == ':');

    private static bool TryResolveLocator(Uri baseUri, ReadOnlySpan<char> value, out string locator)
    {
        locator = string.Empty;
        if (value.Length is 0 or > MaximumLocatorCharacters ||
            !Uri.TryCreate(baseUri, value.ToString(), out Uri? resolved) ||
            !IsSafeAbsoluteHttpsUri(resolved))
        {
            return false;
        }

        locator = resolved.AbsoluteUri;
        return true;
    }

    private static bool IsSafeAbsoluteHttpsUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool TryBounded(
        ReadOnlySpan<char> value,
        int maximumCharacters,
        bool required,
        out string? normalized)
    {
        normalized = null;
        value = value.Trim();
        if (value.IsEmpty) return !required;
        if (value.Length > maximumCharacters) return false;
        foreach (char character in value)
        {
            if (char.IsControl(character) || char.IsSurrogate(character)) return false;
        }

        normalized = value.ToString().Normalize(NormalizationForm.FormC);
        return normalized.Length <= maximumCharacters;
    }

    private static DomainResult<RemoteM3uParseResult> Unsupported() =>
        DomainResult.Failure<RemoteM3uParseResult>(DomainErrorCode.UnsupportedPlaylistFormat);

    private readonly record struct PendingMetadata(
        string Name,
        string? TvgId,
        string? TvgName,
        string? Logo,
        string? GroupTitle,
        int? Number,
        ChannelNormalizationWarnings Warnings)
    {
        internal RemoteM3uEntry ToEntry(string locator) =>
            new(locator, Name, TvgId, TvgName, Logo, GroupTitle, Number, Warnings);
    }
}
