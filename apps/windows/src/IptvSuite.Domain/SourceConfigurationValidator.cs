using System.Buffers;
using System.Globalization;
using System.Text;

namespace IptvSuite.Domain;

public static class SourceConfigurationValidator
{
    public const int MaxDisplayNameUnicodeScalars = 100;
    public const int MaxLocatorUnicodeScalars = 4096;
    public const int MaxUsernameUnicodeScalars = 256;
    public const int MaxPasswordUnicodeScalars = 1024;

    public static DomainResult<PreparedXtreamSourceDraft> PrepareXtream(
        string? displayName,
        string? locator,
        string? username,
        string? password) =>
        PrepareXtreamCore(
            displayName,
            locator,
            username,
            password,
            allowInsecureHttp: false);

    public static DomainResult<PreparedXtreamSourceDraft> PrepareXtreamAllowingInsecureHttp(
        string? displayName,
        string? locator,
        string? username,
        string? password) =>
        PrepareXtreamCore(
            displayName,
            locator,
            username,
            password,
            allowInsecureHttp: true);

    private static DomainResult<PreparedXtreamSourceDraft> PrepareXtreamCore(
        string? displayName,
        string? locator,
        string? username,
        string? password,
        bool allowInsecureHttp)
    {
        DomainResult<string> normalizedName = ValidateDisplayName(displayName);
        if (!normalizedName.IsSuccess)
        {
            return DomainResult.Failure<PreparedXtreamSourceDraft>(normalizedName.Error!);
        }

        DomainResult<SafeEndpoint> endpoint = ValidateWebLocator(
            locator,
            rejectUserInfo: true,
            allowInsecureHttp);
        if (!endpoint.IsSuccess)
        {
            return DomainResult.Failure<PreparedXtreamSourceDraft>(endpoint.Error!);
        }

        DomainErrorCode? credentialError = ValidateCredentials(username, password);
        if (credentialError is not null)
        {
            return DomainResult.Failure<PreparedXtreamSourceDraft>(credentialError.Value);
        }

        return DomainResult.Success(
            new PreparedXtreamSourceDraft(
                normalizedName.Value!,
                endpoint.Value!,
                allowsInsecureTransport: string.Equals(
                    endpoint.Value!.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.Ordinal)));
    }

    public static DomainResult<PreparedRemotePlaylistSourceDraft> PrepareRemotePlaylist(
        string? displayName,
        string? locator) =>
        PrepareRemotePlaylistCore(
            displayName,
            locator,
            allowInsecureHttp: false,
            rejectUserInfo: false);

    public static DomainResult<PreparedRemotePlaylistSourceDraft>
        PrepareRemotePlaylistAllowingInsecureHttp(
            string? displayName,
            string? locator) =>
        PrepareRemotePlaylistCore(
            displayName,
            locator,
            allowInsecureHttp: true,
            rejectUserInfo: true);

    private static DomainResult<PreparedRemotePlaylistSourceDraft> PrepareRemotePlaylistCore(
        string? displayName,
        string? locator,
        bool allowInsecureHttp,
        bool rejectUserInfo)
    {
        DomainResult<string> normalizedName = ValidateDisplayName(displayName);
        if (!normalizedName.IsSuccess)
        {
            return DomainResult.Failure<PreparedRemotePlaylistSourceDraft>(normalizedName.Error!);
        }

        DomainResult<SafeEndpoint> endpoint = ValidateWebLocator(
            locator,
            rejectUserInfo,
            allowInsecureHttp);
        if (!endpoint.IsSuccess)
        {
            return DomainResult.Failure<PreparedRemotePlaylistSourceDraft>(endpoint.Error!);
        }

        return DomainResult.Success(
            new PreparedRemotePlaylistSourceDraft(normalizedName.Value!, endpoint.Value!));
    }

    internal static ValidatedSourceDraft CompleteXtream(
        SourceId sourceId,
        SourceConfigurationId configurationId,
        PreparedXtreamSourceDraft prepared,
        SecretReference credentialsReference)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A non-empty source identifier is required.", nameof(sourceId));
        }

        if (configurationId.IsEmpty)
        {
            throw new ArgumentException(
                "A non-empty source configuration identifier is required.",
                nameof(configurationId));
        }

        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(credentialsReference);
        var configuration = new XtreamSourceConfiguration(
            configurationId,
            prepared.SafeEndpoint,
            credentialsReference,
            prepared.AllowsInsecureTransport);
        return new ValidatedSourceDraft(sourceId, prepared.NormalizedDisplayName, configuration);
    }

    internal static ValidatedSourceDraft CompleteRemotePlaylist(
        SourceId sourceId,
        SourceConfigurationId configurationId,
        PreparedRemotePlaylistSourceDraft prepared,
        ProtectedLocatorReference locatorReference)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A non-empty source identifier is required.", nameof(sourceId));
        }

        if (configurationId.IsEmpty)
        {
            throw new ArgumentException(
                "A non-empty source configuration identifier is required.",
                nameof(configurationId));
        }

        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(locatorReference);
        var configuration = new RemotePlaylistSourceConfiguration(
            configurationId,
            prepared.SafeEndpoint,
            locatorReference);
        return new ValidatedSourceDraft(sourceId, prepared.NormalizedDisplayName, configuration);
    }

    internal static DomainResult<SafeEndpoint> ValidateHttpsLocator(string? locator, bool rejectUserInfo)
        => ValidateWebLocator(locator, rejectUserInfo, allowInsecureHttp: false);

    internal static DomainResult<SafeEndpoint> ValidateWebLocator(
        string? locator,
        bool rejectUserInfo,
        bool allowInsecureHttp)
    {
        if (string.IsNullOrWhiteSpace(locator))
        {
            return DomainResult.Failure<SafeEndpoint>(DomainErrorCode.EndpointRequired);
        }

        if (!DomainTextValidation.TryInspect(locator, out int scalarCount) ||
            DomainTextValidation.ContainsControlCharacter(locator))
        {
            return DomainResult.Failure<SafeEndpoint>(DomainErrorCode.EndpointMalformed);
        }

        if (scalarCount > MaxLocatorUnicodeScalars)
        {
            return DomainResult.Failure<SafeEndpoint>(DomainErrorCode.EndpointTooLong);
        }

        Uri? uri;
        try
        {
            if (!Uri.TryCreate(locator, UriKind.Absolute, out uri) || uri is null)
            {
                return DomainResult.Failure<SafeEndpoint>(DomainErrorCode.EndpointMalformed);
            }
        }
        catch (UriFormatException)
        {
            return DomainResult.Failure<SafeEndpoint>(DomainErrorCode.EndpointMalformed);
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return DomainResult.Failure<SafeEndpoint>(DomainErrorCode.EndpointSchemeUnsupported);
        }

        bool isHttps = string.Equals(
            uri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !allowInsecureHttp)
        {
            return DomainResult.Failure<SafeEndpoint>(DomainErrorCode.InsecureTransportRejected);
        }

        if ((rejectUserInfo || !isHttps) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            return DomainResult.Failure<SafeEndpoint>(DomainErrorCode.EndpointUserInfoNotAllowed);
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            return DomainResult.Failure<SafeEndpoint>(DomainErrorCode.EndpointFragmentNotAllowed);
        }

        return SafeEndpoint.TryCreate(uri, out SafeEndpoint? endpoint)
            ? DomainResult.Success(endpoint!)
            : DomainResult.Failure<SafeEndpoint>(DomainErrorCode.EndpointMalformed);
    }

    private static DomainResult<string> ValidateDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return DomainResult.Failure<string>(DomainErrorCode.SourceNameRequired);
        }

        if (!DomainTextValidation.TryInspect(displayName, out _) ||
            DomainTextValidation.ContainsControlCharacter(displayName))
        {
            return DomainResult.Failure<string>(DomainErrorCode.SourceNameInvalid);
        }

        string normalizedName = displayName.Trim().Normalize(NormalizationForm.FormC);
        if (normalizedName.Length == 0)
        {
            return DomainResult.Failure<string>(DomainErrorCode.SourceNameRequired);
        }

        if (!DomainTextValidation.TryInspect(normalizedName, out int normalizedScalarCount) ||
            normalizedScalarCount > MaxDisplayNameUnicodeScalars)
        {
            return DomainResult.Failure<string>(DomainErrorCode.SourceNameTooLong);
        }

        return DomainResult.Success(normalizedName);
    }

    private static DomainErrorCode? ValidateCredentials(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return DomainErrorCode.UsernameRequired;
        }

        if (string.IsNullOrEmpty(password))
        {
            return DomainErrorCode.PasswordRequired;
        }

        if (!DomainTextValidation.TryInspect(username, out int usernameScalars) ||
            !DomainTextValidation.TryInspect(password, out int passwordScalars) ||
            DomainTextValidation.ContainsControlCharacter(username) ||
            DomainTextValidation.ContainsControlCharacter(password))
        {
            return DomainErrorCode.CredentialInvalid;
        }

        return usernameScalars > MaxUsernameUnicodeScalars ||
            passwordScalars > MaxPasswordUnicodeScalars
            ? DomainErrorCode.CredentialTooLong
            : null;
    }
}

internal static class DomainTextValidation
{
    internal static bool TryInspect(string value, out int scalarCount)
    {
        ArgumentNullException.ThrowIfNull(value);

        scalarCount = 0;
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(remaining, out _, out int consumed);
            if (status != OperationStatus.Done)
            {
                scalarCount = 0;
                return false;
            }

            scalarCount++;
            remaining = remaining[consumed..];
        }

        return true;
    }

    internal static bool ContainsControlCharacter(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                return true;
            }
        }

        return false;
    }
}
