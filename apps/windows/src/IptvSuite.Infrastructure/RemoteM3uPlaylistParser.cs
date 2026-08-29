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
        bool entryLimitReached,
        string? hlsLocator = null)
    {
        ContentKind = contentKind;
        Entries = entries;
        ProcessedEntryCount = processedEntryCount;
        SkippedEntryCount = skippedEntryCount;
        EntryLimitReached = entryLimitReached;
        HlsLocator = hlsLocator;
    }

    internal PlaylistContentKind ContentKind { get; }
    internal IReadOnlyList<RemoteM3uEntry> Entries { get; }
    internal int ProcessedEntryCount { get; }
    internal int SkippedEntryCount { get; }
    internal bool EntryLimitReached { get; }
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
    ValueTask<DomainResult<bool>> BeginAsync(
        ContentSource source,
        string? entityTag,
        DateTimeOffset? lastModified,
        CancellationToken cancellationToken);

    ValueTask<DomainResult<bool>> CompleteAsync(
        RemoteM3uParseResult parseResult,
        CancellationToken cancellationToken);

    ValueTask AbortAsync(CancellationToken cancellationToken);
}

internal static class RemoteM3uPlaylistParser
{
    internal const int MaximumEntries = 50_000;
    internal const int MaximumLineCharacters = 64 * 1024;
    internal const int MaximumMetadataValueCharacters = 4_096;
    internal const int MaximumLocatorCharacters = 4_096;
    internal const int MaximumTotalCharacters = 128 * 1024 * 1024;

    internal static async ValueTask<DomainResult<RemoteM3uParseResult>> ParseAsync(
        Stream content,
        Uri finalPlaylistUri,
        CancellationToken cancellationToken = default)
        => await ParseCoreAsync(
            content,
            finalPlaylistUri,
            configuredSourceEndpoint: null,
            sink: null,
            cancellationToken)
            .ConfigureAwait(false);

    internal static async ValueTask<DomainResult<RemoteM3uParseResult>> ParseForSourceAsync(
        Stream content,
        Uri finalPlaylistUri,
        SafeEndpoint configuredSourceEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuredSourceEndpoint);
        return await ParseCoreAsync(
            content,
            finalPlaylistUri,
            configuredSourceEndpoint,
            sink: null,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<DomainResult<RemoteM3uParseResult>> ParseToSinkAsync(
        Stream content,
        Uri finalPlaylistUri,
        IRemoteM3uEntrySink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return await ParseCoreAsync(
            content,
            finalPlaylistUri,
            configuredSourceEndpoint: null,
            sink,
            cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async ValueTask<DomainResult<RemoteM3uParseResult>> ParseToSinkForSourceAsync(
        Stream content,
        Uri finalPlaylistUri,
        SafeEndpoint configuredSourceEndpoint,
        IRemoteM3uEntrySink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuredSourceEndpoint);
        ArgumentNullException.ThrowIfNull(sink);
        return await ParseCoreAsync(
            content,
            finalPlaylistUri,
            configuredSourceEndpoint,
            sink,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<DomainResult<RemoteM3uParseResult>> ParseCoreAsync(
        Stream content,
        Uri finalPlaylistUri,
        SafeEndpoint? configuredSourceEndpoint,
        IRemoteM3uEntrySink? sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(finalPlaylistUri);

        if (!IsAllowedFinalPlaylistUri(finalPlaylistUri, configuredSourceEndpoint))
        {
            return FormatFailure(DomainErrorCode.PlaylistResponseAddressRejected);
        }

        List<RemoteM3uEntry>? entries = sink is null ? [] : null;
        var tvgIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        int processedEntryCount = 0;
        int skipped = 0;
        int entriesRejectedByAddressPolicy = 0;
        int totalCharacters = 0;
        bool entryLimitReached = false;
        bool truncateAtEntryLimit = string.Equals(
            configuredSourceEndpoint?.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.Ordinal);
        PendingMetadata? pending = null;
        bool discardPendingLocator = false;
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

            var lineReader = new BoundedPlaylistLineReader(reader);
            string? firstLine = await lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (firstLine is null)
            {
                return FormatFailure(DomainErrorCode.PlaylistHeaderInvalid);
            }

            totalCharacters = firstLine.Length;
            ReadOnlySpan<char> header = firstLine.AsSpan();
            if (!header.IsEmpty && header[0] == '\uFEFF')
            {
                header = header[1..];
            }

            if (!IsExtendedM3uHeader(header.Trim()))
            {
                return FormatFailure(DomainErrorCode.PlaylistHeaderInvalid);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line = await lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!TryAccumulateTotalCharacters(ref totalCharacters, line.Length))
                {
                    return FormatFailure(DomainErrorCode.PlaylistTotalLimitExceeded);
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
                        return FormatFailure(DomainErrorCode.PlaylistStructureInvalid);
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
                    if (TryParseMetadata(trimmed[8..], out PendingMetadata metadata))
                    {
                        pending = metadata;
                        discardPendingLocator = false;
                    }
                    else
                    {
                        pending = null;
                        discardPendingLocator = true;
                        skipped++;
                    }

                    continue;
                }

                if (trimmed[0] == '#')
                {
                    continue;
                }

                if (discardPendingLocator)
                {
                    discardPendingLocator = false;
                    continue;
                }

                if (pending is null)
                {
                    skipped++;
                    continue;
                }

                if (!TryResolveLocator(
                        finalPlaylistUri,
                        trimmed,
                        configuredSourceEndpoint,
                        out string locator))
                {
                    skipped++;
                    entriesRejectedByAddressPolicy++;
                    pending = null;
                    continue;
                }

                if (processedEntryCount == MaximumEntries)
                {
                    if (!truncateAtEntryLimit)
                    {
                        return FormatFailure(DomainErrorCode.PlaylistEntryLimitExceeded);
                    }

                    entryLimitReached = true;
                    skipped++;
                    pending = null;
                    continue;
                }

                PendingMetadata completed = pending.Value;
                completed = ApplyLogoPolicy(
                    completed,
                    finalPlaylistUri,
                    configuredSourceEndpoint);
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
        catch (PlaylistLineLimitExceededException)
        {
            return FormatFailure(DomainErrorCode.PlaylistLineLimitExceeded);
        }
        catch (DecoderFallbackException)
        {
            return FormatFailure(DomainErrorCode.PlaylistTextEncodingInvalid);
        }
        catch (OperationCanceledException)
        {
            return DomainResult.Failure<RemoteM3uParseResult>(DomainErrorCode.OperationCancelled);
        }

        if (hlsSeen)
        {
            if (hlsMasterSeen == hlsMediaSeen)
            {
                return FormatFailure(DomainErrorCode.PlaylistStructureInvalid);
            }

            return DomainResult.Success(new RemoteM3uParseResult(
                hlsMasterSeen
                    ? PlaylistContentKind.HlsMasterManifest
                    : PlaylistContentKind.HlsMediaManifest,
                [],
                processedEntryCount: 0,
                skippedEntryCount: 0,
                entryLimitReached: false,
                finalPlaylistUri.AbsoluteUri));
        }

        if (processedEntryCount == 0)
        {
            return FormatFailure(entriesRejectedByAddressPolicy > 0
                ? DomainErrorCode.PlaylistEntriesRejectedByAddressPolicy
                : DomainErrorCode.PlaylistNoUsableEntries);
        }

        return DomainResult.Success(new RemoteM3uParseResult(
            PlaylistContentKind.ExtendedM3uCatalog,
            entries ?? [],
            processedEntryCount,
            skipped,
            entryLimitReached));
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

            int maximumValueCharacters = GetMaximumAttributeValueCharacters(key);
            if (!TryBounded(attributeValue, maximumValueCharacters, required: false, out string? normalized))
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

    private static int GetMaximumAttributeValueCharacters(ReadOnlySpan<char> key)
    {
        if (key.Equals("tvg-id", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Min(
                LiveChannel.MaximumProviderKeyLength,
                ChannelStableKeyBuilder.MaximumProviderIdentifierLength);
        }

        return key.Equals("group-title", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(
                ChannelCategory.MaximumProviderKeyLength,
                ChannelCategory.MaximumNameLength)
            : MaximumMetadataValueCharacters;
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

    private static bool IsExtendedM3uHeader(ReadOnlySpan<char> line) =>
        line.StartsWith("#EXTM3U", StringComparison.Ordinal) &&
        (line.Length == "#EXTM3U".Length || char.IsWhiteSpace(line["#EXTM3U".Length]));

    private static bool TryResolveLocator(Uri baseUri, ReadOnlySpan<char> value, out string locator)
        => TryResolveLocator(baseUri, value, configuredSourceEndpoint: null, out locator);

    private static bool TryResolveLocator(
        Uri baseUri,
        ReadOnlySpan<char> value,
        SafeEndpoint? configuredSourceEndpoint,
        out string locator)
    {
        locator = string.Empty;
        if (value.Length is 0 or > MaximumLocatorCharacters ||
            !Uri.TryCreate(baseUri, value.ToString(), out Uri? resolved) ||
            !IsAllowedEntryUri(resolved, configuredSourceEndpoint))
        {
            return false;
        }

        locator = resolved.AbsoluteUri;
        return true;
    }

    private static bool IsSafeAbsoluteWebUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool IsAllowedFinalPlaylistUri(
        Uri uri,
        SafeEndpoint? configuredSourceEndpoint) =>
        IsSafeAbsoluteWebUri(uri) &&
        (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         IsExactConfiguredHttpOrigin(uri, configuredSourceEndpoint));

    private static bool IsAllowedEntryUri(
        Uri uri,
        SafeEndpoint? configuredSourceEndpoint) =>
        IsSafeAbsoluteWebUri(uri) &&
        (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         IsExactConfiguredHttpOrigin(uri, configuredSourceEndpoint));

    private static bool IsExactConfiguredHttpOrigin(
        Uri uri,
        SafeEndpoint? configuredSourceEndpoint)
    {
        if (configuredSourceEndpoint is null ||
            !configuredSourceEndpoint.Scheme.Equals(
                Uri.UriSchemeHttp,
                StringComparison.Ordinal) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        DomainResult<PreparedRemotePlaylistSourceDraft> prepared =
            SourceConfigurationValidator.PrepareRemotePlaylistAllowingInsecureHttp(
                "Remote playlist origin",
                uri.AbsoluteUri);
        return prepared.IsSuccess && configuredSourceEndpoint.Equals(prepared.Value!.SafeEndpoint);
    }

    private static PendingMetadata ApplyLogoPolicy(
        PendingMetadata metadata,
        Uri baseUri,
        SafeEndpoint? configuredSourceEndpoint)
    {
        if (metadata.Logo is null || configuredSourceEndpoint is null)
        {
            return metadata;
        }

        if (!Uri.TryCreate(baseUri, metadata.Logo, out Uri? resolved) ||
            !resolved.IsAbsoluteUri ||
            !string.Equals(resolved.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(resolved.UserInfo) ||
            !string.IsNullOrEmpty(resolved.Fragment))
        {
            return metadata with { Logo = null };
        }

        DomainResult<PreparedRemotePlaylistSourceDraft> prepared =
            SourceConfigurationValidator.PrepareRemotePlaylist("Channel logo", resolved.AbsoluteUri);
        return prepared.IsSuccess && configuredSourceEndpoint.Equals(prepared.Value!.SafeEndpoint)
            ? metadata with { Logo = resolved.AbsoluteUri }
            : metadata with { Logo = null };
    }

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

    private static DomainResult<RemoteM3uParseResult> FormatFailure(DomainErrorCode code) =>
        DomainResult.Failure<RemoteM3uParseResult>(code);

    private static bool TryAccumulateTotalCharacters(ref int totalCharacters, int lineCharacters)
    {
        long nextTotal = (long)totalCharacters + lineCharacters;
        if (nextTotal > MaximumTotalCharacters)
        {
            return false;
        }

        totalCharacters = (int)nextTotal;
        return true;
    }

    private sealed class PlaylistLineLimitExceededException : Exception
    {
    }

    private sealed class BoundedPlaylistLineReader(StreamReader reader)
    {
        private const int BufferSize = 4_096;
        private readonly char[] _buffer = new char[BufferSize];
        private int _bufferLength;
        private int _bufferOffset;
        private bool _skipLeadingLineFeed;

        internal async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            StringBuilder? builder = null;
            while (true)
            {
                if (!await EnsureBufferedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return builder?.ToString();
                }

                if (_skipLeadingLineFeed)
                {
                    _skipLeadingLineFeed = false;
                    if (_buffer[_bufferOffset] == '\n')
                    {
                        _bufferOffset++;
                        continue;
                    }
                }

                ReadOnlySpan<char> available = _buffer.AsSpan(
                    _bufferOffset,
                    _bufferLength - _bufferOffset);
                int terminatorOffset = available.IndexOfAny('\r', '\n');
                if (terminatorOffset < 0)
                {
                    AppendBounded(ref builder, available);
                    _bufferOffset = _bufferLength;
                    continue;
                }

                ReadOnlySpan<char> segment = available[..terminatorOffset];
                string line;
                if (builder is null)
                {
                    EnsureBounded(segment.Length);
                    line = segment.ToString();
                }
                else
                {
                    AppendBounded(ref builder, segment);
                    line = builder!.ToString();
                }

                char terminator = available[terminatorOffset];
                _bufferOffset += terminatorOffset + 1;
                if (terminator == '\r')
                {
                    if (_bufferOffset < _bufferLength)
                    {
                        if (_buffer[_bufferOffset] == '\n')
                        {
                            _bufferOffset++;
                        }
                    }
                    else
                    {
                        _skipLeadingLineFeed = true;
                    }
                }

                return line;
            }
        }

        private async ValueTask<bool> EnsureBufferedAsync(CancellationToken cancellationToken)
        {
            if (_bufferOffset < _bufferLength)
            {
                return true;
            }

            _bufferLength = await reader.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _bufferOffset = 0;
            return _bufferLength > 0;
        }

        private static void AppendBounded(
            ref StringBuilder? builder,
            ReadOnlySpan<char> value)
        {
            int currentLength = builder?.Length ?? 0;
            EnsureBounded(checked(currentLength + value.Length));
            builder ??= new StringBuilder(Math.Min(MaximumLineCharacters, BufferSize * 2));
            builder.Append(value);
        }

        private static void EnsureBounded(int length)
        {
            if (length > MaximumLineCharacters)
            {
                throw new PlaylistLineLimitExceededException();
            }
        }
    }

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
