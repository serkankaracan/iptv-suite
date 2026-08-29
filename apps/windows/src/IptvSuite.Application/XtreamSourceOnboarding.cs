using System.Diagnostics;

namespace IptvSuite.Application;

[DebuggerDisplay("[XTREAM-SOURCE-ONBOARDING-RESULT]")]
public sealed class XtreamSourceOnboardingResult
{
    internal XtreamSourceOnboardingResult(
        SourceId sourceId,
        ContentCatalogCounts counts,
        bool previousConfigurationCleanupPending = false)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A source identifier is required.", nameof(sourceId));
        }

        SourceId = sourceId;
        Counts = counts ?? throw new ArgumentNullException(nameof(counts));
        PreviousConfigurationCleanupPending = previousConfigurationCleanupPending;
    }

    public SourceId SourceId { get; }

    public ContentCatalogCounts Counts { get; }

    public bool PreviousConfigurationCleanupPending { get; }

    public override string ToString() => "[XTREAM-SOURCE-ONBOARDING-RESULT]";
}

public sealed class XtreamSourceOnboardingService
{
    private readonly ISecretStore _secretStore;
    private readonly IXtreamCatalogImportService _importer;
    private readonly TimeProvider _timeProvider;

    public XtreamSourceOnboardingService(
        ISecretStore secretStore,
        IXtreamCatalogImportService importer,
        TimeProvider timeProvider)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ValueTask<DomainResult<XtreamSourceOnboardingResult>> AddAsync(
        string? displayName,
        string? locator,
        string? username,
        string? password,
        CancellationToken cancellationToken = default) =>
        AddCoreAsync(
            displayName,
            locator,
            username,
            password,
            allowInsecureHttp: false,
            replacementSource: null,
            cancellationToken);

    public ValueTask<DomainResult<XtreamSourceOnboardingResult>> AddFromM3uUrlAsync(
        string? displayName,
        string? m3uLocator,
        CancellationToken cancellationToken = default) =>
        AddFromM3uUrlCoreAsync(
            displayName,
            m3uLocator,
            allowInsecureHttp: false,
            replacementSource: null,
            cancellationToken);

    public ValueTask<DomainResult<XtreamSourceOnboardingResult>>
        AddAllowingInsecureHttpAsync(
            string? displayName,
            string? locator,
            string? username,
            string? password,
            CancellationToken cancellationToken = default) =>
        AddCoreAsync(
            displayName,
            locator,
            username,
            password,
            allowInsecureHttp: true,
            replacementSource: null,
            cancellationToken);

    public ValueTask<DomainResult<XtreamSourceOnboardingResult>>
        AddFromM3uUrlAllowingInsecureHttpAsync(
            string? displayName,
            string? m3uLocator,
            CancellationToken cancellationToken = default) =>
        AddFromM3uUrlCoreAsync(
            displayName,
            m3uLocator,
            allowInsecureHttp: true,
            replacementSource: null,
            cancellationToken);

    public ValueTask<DomainResult<XtreamSourceOnboardingResult>> ReplaceAsync(
        ContentSource source,
        string? displayName,
        string? locator,
        string? username,
        string? password,
        CancellationToken cancellationToken = default) =>
        AddCoreAsync(
            displayName,
            locator,
            username,
            password,
            allowInsecureHttp: false,
            replacementSource: source,
            cancellationToken);

    public ValueTask<DomainResult<XtreamSourceOnboardingResult>> ReplaceFromM3uUrlAsync(
        ContentSource source,
        string? displayName,
        string? m3uLocator,
        CancellationToken cancellationToken = default) =>
        AddFromM3uUrlCoreAsync(
            displayName,
            m3uLocator,
            allowInsecureHttp: false,
            replacementSource: source,
            cancellationToken);

    public ValueTask<DomainResult<XtreamSourceOnboardingResult>>
        ReplaceAllowingInsecureHttpAsync(
            ContentSource source,
            string? displayName,
            string? locator,
            string? username,
            string? password,
            CancellationToken cancellationToken = default) =>
        AddCoreAsync(
            displayName,
            locator,
            username,
            password,
            allowInsecureHttp: true,
            replacementSource: source,
            cancellationToken);

    public ValueTask<DomainResult<XtreamSourceOnboardingResult>>
        ReplaceFromM3uUrlAllowingInsecureHttpAsync(
            ContentSource source,
            string? displayName,
            string? m3uLocator,
            CancellationToken cancellationToken = default) =>
        AddFromM3uUrlCoreAsync(
            displayName,
            m3uLocator,
            allowInsecureHttp: true,
            replacementSource: source,
            cancellationToken);

    private ValueTask<DomainResult<XtreamSourceOnboardingResult>>
        AddFromM3uUrlCoreAsync(
            string? displayName,
            string? m3uLocator,
            bool allowInsecureHttp,
            ContentSource? replacementSource,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BootstrapParseResult parsed = ParseM3uBootstrap(
            displayName,
            m3uLocator,
            allowInsecureHttp);
        if (!parsed.IsSuccess)
        {
            return ValueTask.FromResult(
                DomainResult.Failure<XtreamSourceOnboardingResult>(parsed.ErrorCode));
        }

        XtreamM3uBootstrap bootstrap = parsed.Value!;
        m3uLocator = null;
        return AddCoreAsync(
            displayName,
            bootstrap.ServerLocator,
            bootstrap.Username,
            bootstrap.Password,
            allowInsecureHttp,
            replacementSource,
            cancellationToken);
    }

    private async ValueTask<DomainResult<XtreamSourceOnboardingResult>> AddCoreAsync(
        string? displayName,
        string? locator,
        string? username,
        string? password,
        bool allowInsecureHttp,
        ContentSource? replacementSource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (replacementSource is not null &&
            (replacementSource.Id.IsEmpty ||
             replacementSource.Status == ContentSourceStatus.DeletionPending))
        {
            return DomainResult.Failure<XtreamSourceOnboardingResult>(
                DomainErrorCode.DomainInvariantViolation);
        }

        DateTimeOffset now;
        try
        {
            now = _timeProvider.GetUtcNow();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return StorageUnavailable();
        }

        if (now == default)
        {
            return StorageUnavailable();
        }

        SourceId sourceId = replacementSource?.Id ?? SourceId.Generate();
        DomainResult<ValidatedSourceDraft> protectedDraft;
        try
        {
            var protection = new SourceDraftProtectionService(_secretStore);
            protectedDraft = allowInsecureHttp
                ? await protection.ProtectXtreamAllowingInsecureHttpAsync(
                    sourceId,
                    displayName,
                    locator,
                    username,
                    password,
                    cancellationToken).ConfigureAwait(false)
                : await protection.ProtectXtreamAsync(
                    sourceId,
                    displayName,
                    locator,
                    username,
                    password,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return StorageUnavailable();
        }

        if (!protectedDraft.IsSuccess)
        {
            return DomainResult.Failure<XtreamSourceOnboardingResult>(protectedDraft.Error!);
        }

        DomainResult<ContentSource> source = ContentSource.Create(
            protectedDraft.Value,
            ContentSourceStatus.Testing,
            now,
            now);
        if (!source.IsSuccess)
        {
            await TryDeleteProtectedConfigurationAsync(protectedDraft.Value!, now)
                .ConfigureAwait(false);
            return StorageUnavailable();
        }

        XtreamCatalogImportResult imported;
        try
        {
            imported = await _importer.ImportWithDispositionAsync(
                source.Value!,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // A storage commit exception can be indeterminate. Retaining the protected
            // configuration is safer than stranding an already committed catalog row.
            return StorageUnavailable();
        }

        if (imported is null)
        {
            return StorageUnavailable();
        }

        if (imported.Disposition == CatalogImportCommitDisposition.Committed &&
            imported.Counts is not null)
        {
            bool cleanupPending = replacementSource is not null &&
                !await TryDeleteProtectedConfigurationAsync(replacementSource, now)
                    .ConfigureAwait(false);
            return DomainResult.Success(new XtreamSourceOnboardingResult(
                sourceId,
                imported.Counts,
                cleanupPending));
        }

        if (imported.Disposition == CatalogImportCommitDisposition.Indeterminate)
        {
            return StorageUnavailable();
        }

        if (imported.Disposition != CatalogImportCommitDisposition.NotCommitted ||
            imported.Error is null)
        {
            return StorageUnavailable();
        }

        bool deleted = await TryDeleteProtectedConfigurationAsync(
            protectedDraft.Value!,
            now).ConfigureAwait(false);
        return deleted
            ? DomainResult.Failure<XtreamSourceOnboardingResult>(imported.Error)
            : StorageUnavailable();
    }

    private async ValueTask<bool> TryDeleteProtectedConfigurationAsync(
        ValidatedSourceDraft draft,
        DateTimeOffset now)
    {
        DomainResult<ContentSource> deletionPending = ContentSource.Create(
            draft,
            ContentSourceStatus.DeletionPending,
            now,
            now);
        if (!deletionPending.IsSuccess)
        {
            return false;
        }

        try
        {
            SecretStoreOperationResult deleted =
                await new SourceConfigurationProtectedRecordDeletionService(_secretStore)
                .DeleteAsync(deletionPending.Value!, CancellationToken.None)
                .ConfigureAwait(false);
            return deleted.IsSuccess;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private ValueTask<bool> TryDeleteProtectedConfigurationAsync(
        ContentSource source,
        DateTimeOffset now)
    {
        var draft = new ValidatedSourceDraft(
            source.Id,
            source.DisplayName,
            source.Configuration);
        return TryDeleteProtectedConfigurationAsync(draft, now);
    }

    private static DomainResult<XtreamSourceOnboardingResult> StorageUnavailable() =>
        DomainResult.Failure<XtreamSourceOnboardingResult>(
            DomainErrorCode.StorageUnavailable);

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private static BootstrapParseResult ParseM3uBootstrap(
        string? displayName,
        string? locator,
        bool allowInsecureHttp)
    {
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared = allowInsecureHttp
            ? SourceConfigurationValidator.PrepareRemotePlaylistAllowingInsecureHttp(
                displayName,
                locator)
            : SourceConfigurationValidator.PrepareRemotePlaylist(displayName, locator);
        if (!prepared.IsSuccess)
        {
            return BootstrapParseResult.Failure(prepared.Error!.Code);
        }

        if (!Uri.TryCreate(locator, UriKind.Absolute, out Uri? uri) ||
            uri is null ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.Equals(uri.AbsolutePath, "/get.php", StringComparison.Ordinal) ||
            !HasExactRootGetPhpPath(locator!))
        {
            return BootstrapParseResult.Failure(DomainErrorCode.EndpointMalformed);
        }

        string rawLocator = locator!;
        int queryMarker = rawLocator.IndexOf('?');
        ReadOnlySpan<char> rawQuery = queryMarker >= 0
            ? rawLocator.AsSpan(queryMarker + 1)
            : ReadOnlySpan<char>.Empty;
        if (!rawQuery.IsEmpty && rawQuery[0] == '?')
        {
            return BootstrapParseResult.Failure(DomainErrorCode.EndpointMalformed);
        }

        // type/output are deliberately ignored: they do not classify content. The
        // provider's explicit Live/VOD/Series operations remain authoritative.
        QueryParameterResult username = ReadSingleQueryParameter(rawQuery, "username");
        QueryParameterResult password = ReadSingleQueryParameter(rawQuery, "password");
        if (!username.IsValid || !password.IsValid)
        {
            return BootstrapParseResult.Failure(DomainErrorCode.EndpointMalformed);
        }

        if (string.IsNullOrWhiteSpace(username.Value))
        {
            return BootstrapParseResult.Failure(DomainErrorCode.UsernameRequired);
        }

        if (string.IsNullOrEmpty(password.Value))
        {
            return BootstrapParseResult.Failure(DomainErrorCode.PasswordRequired);
        }

        SafeEndpoint endpoint = prepared.Value!.SafeEndpoint;
        string serverLocator;
        try
        {
            var builder = new UriBuilder(endpoint.Scheme, endpoint.Host, endpoint.Port);
            serverLocator = builder.Uri.GetLeftPart(UriPartial.Authority);
        }
        catch (UriFormatException)
        {
            return BootstrapParseResult.Failure(DomainErrorCode.EndpointMalformed);
        }

        return BootstrapParseResult.Success(new XtreamM3uBootstrap(
            serverLocator,
            username.Value!,
            password.Value!));
    }

    private static QueryParameterResult ReadSingleQueryParameter(
        ReadOnlySpan<char> query,
        string expectedName)
    {
        string? value = null;
        bool found = false;
        ReadOnlySpan<char> remaining = query;
        while (!remaining.IsEmpty)
        {
            int separator = remaining.IndexOf('&');
            ReadOnlySpan<char> pair = separator >= 0 ? remaining[..separator] : remaining;
            remaining = separator >= 0 ? remaining[(separator + 1)..] : [];
            int equals = pair.IndexOf('=');
            ReadOnlySpan<char> encodedName = equals >= 0 ? pair[..equals] : pair;
            ReadOnlySpan<char> encodedValue = equals >= 0 ? pair[(equals + 1)..] : [];
            if (!TryDecodeQueryComponent(encodedName, out string? name) ||
                !TryDecodeQueryComponent(encodedValue, out string? candidate))
            {
                return QueryParameterResult.Invalid();
            }

            if (!string.Equals(name, expectedName, StringComparison.Ordinal))
            {
                continue;
            }

            if (found)
            {
                return QueryParameterResult.Invalid();
            }

            found = true;
            value = candidate;
        }

        return new QueryParameterResult(found, value);
    }

    private static bool TryDecodeQueryComponent(
        ReadOnlySpan<char> encoded,
        out string? decoded)
    {
        if (!HasWellFormedPercentEncoding(encoded))
        {
            decoded = null;
            return false;
        }

        try
        {
            decoded = Uri.UnescapeDataString(encoded.ToString().Replace('+', ' '));
            return true;
        }
        catch (UriFormatException)
        {
            decoded = null;
            return false;
        }
    }

    private static bool HasWellFormedPercentEncoding(ReadOnlySpan<char> encoded)
    {
        for (int index = 0; index < encoded.Length; index++)
        {
            if (encoded[index] != '%')
            {
                continue;
            }

            if (index + 2 >= encoded.Length ||
                !Uri.IsHexDigit(encoded[index + 1]) ||
                !Uri.IsHexDigit(encoded[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static bool HasExactRootGetPhpPath(string locator)
    {
        int schemeSeparator = locator.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 1)
        {
            return false;
        }

        int authorityStart = schemeSeparator + 3;
        int pathStart = locator.IndexOf('/', authorityStart);
        if (pathStart < 0)
        {
            return false;
        }

        int queryStart = locator.IndexOf('?', pathStart);
        int fragmentStart = locator.IndexOf('#', pathStart);
        int pathEnd = locator.Length;
        if (queryStart >= 0)
        {
            pathEnd = queryStart;
        }

        if (fragmentStart >= 0 && fragmentStart < pathEnd)
        {
            pathEnd = fragmentStart;
        }

        return locator.AsSpan(pathStart, pathEnd - pathStart)
            .SequenceEqual("/get.php".AsSpan());
    }

    [DebuggerDisplay("[XTREAM-M3U-BOOTSTRAP]")]
    private sealed class XtreamM3uBootstrap(
        string serverLocator,
        string username,
        string password)
    {
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal string ServerLocator { get; } = serverLocator;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal string Username { get; } = username;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        internal string Password { get; } = password;

        public override string ToString() => "[XTREAM-M3U-BOOTSTRAP]";
    }

    private readonly record struct BootstrapParseResult(
        XtreamM3uBootstrap? Value,
        DomainErrorCode ErrorCode)
    {
        internal bool IsSuccess => Value is not null;

        internal static BootstrapParseResult Success(XtreamM3uBootstrap value) =>
            new(value, default);

        internal static BootstrapParseResult Failure(DomainErrorCode errorCode) =>
            new(null, errorCode);
    }

    private readonly record struct QueryParameterResult(bool IsValid, string? Value)
    {
        internal static QueryParameterResult Invalid() => new(false, null);
    }
}
