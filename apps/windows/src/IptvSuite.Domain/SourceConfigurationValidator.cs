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

    public static DomainResult<ValidatedSourceDraft> ValidateXtream(
        string? displayName,
        string? locator,
        string? username,
        string? password,
        SecretReference? credentialsReference)
    {
        DomainResult<string> normalizedName = ValidateDisplayName(displayName);
        if (!normalizedName.IsSuccess)
        {
            return DomainResult.Failure<ValidatedSourceDraft>(normalizedName.Error!);
        }

        DomainResult<SafeEndpoint> endpoint = ValidateHttpsLocator(locator, rejectUserInfo: true);
        if (!endpoint.IsSuccess)
        {
            return DomainResult.Failure<ValidatedSourceDraft>(endpoint.Error!);
        }

        DomainErrorCode? credentialError = ValidateCredentials(username, password);
        if (credentialError is not null)
        {
            return DomainResult.Failure<ValidatedSourceDraft>(credentialError.Value);
        }

        if (credentialsReference is null)
        {
            return DomainResult.Failure<ValidatedSourceDraft>(DomainErrorCode.SecretReferenceInvalid);
        }

        XtreamSourceConfiguration configuration = new(endpoint.Value!, credentialsReference);
        return DomainResult.Success(new ValidatedSourceDraft(normalizedName.Value!, configuration));
    }

    public static DomainResult<ValidatedSourceDraft> ValidateRemotePlaylist(
        string? displayName,
        string? locator,
        ProtectedLocatorReference? locatorReference)
    {
        DomainResult<string> normalizedName = ValidateDisplayName(displayName);
        if (!normalizedName.IsSuccess)
        {
            return DomainResult.Failure<ValidatedSourceDraft>(normalizedName.Error!);
        }

        DomainResult<SafeEndpoint> endpoint = ValidateHttpsLocator(locator, rejectUserInfo: false);
        if (!endpoint.IsSuccess)
        {
            return DomainResult.Failure<ValidatedSourceDraft>(endpoint.Error!);
        }

        if (locatorReference is null)
        {
            return DomainResult.Failure<ValidatedSourceDraft>(DomainErrorCode.SecretReferenceInvalid);
        }

        RemotePlaylistSourceConfiguration configuration = new(endpoint.Value!, locatorReference);
        return DomainResult.Success(new ValidatedSourceDraft(normalizedName.Value!, configuration));
    }

    internal static DomainResult<SafeEndpoint> ValidateHttpsLocator(string? locator, bool rejectUserInfo)
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

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return DomainResult.Failure<SafeEndpoint>(DomainErrorCode.InsecureTransportRejected);
        }

        if (rejectUserInfo && !string.IsNullOrEmpty(uri.UserInfo))
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
