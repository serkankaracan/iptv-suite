namespace IptvSuite.Domain;

public static class DiagnosticRedactionPolicy
{
    private static readonly string[] SecretIdentifierFragments =
    [
        "api-key",
        "apikey",
        "auth",
        "authorization",
        "cookie",
        "credential",
        "key",
        "passwd",
        "password",
        "secret",
        "signature",
        "token",
    ];

    public static string RedactUri(string? rawUri)
    {
        int inputLength = rawUri?.Length ?? 0;
        if (string.IsNullOrWhiteSpace(rawUri))
        {
            return "uri;status=missing;length=0";
        }

        if (!DomainTextValidation.TryInspect(rawUri, out int scalarCount) ||
            scalarCount > SourceConfigurationValidator.MaxLocatorUnicodeScalars ||
            DomainTextValidation.ContainsControlCharacter(rawUri))
        {
            return FormattableString.Invariant($"uri;status=invalid;length={inputLength}");
        }

        Uri? uri;
        try
        {
            if (!Uri.TryCreate(rawUri, UriKind.Absolute, out uri) || uri is null)
            {
                return FormattableString.Invariant($"uri;status=invalid;length={inputLength}");
            }
        }
        catch (UriFormatException)
        {
            return FormattableString.Invariant($"uri;status=invalid;length={inputLength}");
        }

        int effectivePort = GetEffectivePort(uri);
        (int queryCount, int secretParameterCount) = CountQueryParameters(uri.Query);
        bool userInfoRemoved = !string.IsNullOrEmpty(uri.UserInfo);
        bool fragmentRemoved = !string.IsNullOrEmpty(uri.Fragment);

        return FormattableString.Invariant($"uri;status=redacted;scheme={uri.Scheme.ToLowerInvariant()};host=redacted;port={effectivePort};path=redacted;query-values=redacted;query-count={queryCount};secret-parameter-count={secretParameterCount};userinfo-removed={userInfoRemoved};fragment-removed={fragmentRemoved}");
    }

    public static string RedactHeader(string? headerName, string? headerValue)
    {
        _ = headerValue;
        string classification = IsSecretIdentifier(headerName) ? "secret" : "other";
        return $"header;classification={classification};value=redacted";
    }

    public static string RedactUntrustedText(string? value)
    {
        int inputLength = value?.Length ?? 0;
        string status = value is null
            ? "missing"
            : DomainTextValidation.TryInspect(value, out _)
                ? "redacted"
                : "invalid-redacted";
        return FormattableString.Invariant($"text;status={status};length={inputLength}");
    }

    private static (int QueryCount, int SecretParameterCount) CountQueryParameters(string query)
    {
        if (query.Length <= 1)
        {
            return (0, 0);
        }

        int queryCount = 0;
        int secretParameterCount = 0;
        foreach (string parameter in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            queryCount++;
            int separatorIndex = parameter.IndexOf('=');
            string name = separatorIndex < 0 ? parameter : parameter[..separatorIndex];
            if (IsSecretIdentifier(name))
            {
                secretParameterCount++;
            }
        }

        return (queryCount, secretParameterCount);
    }

    private static bool IsSecretIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string decoded = value.Length > 256 ? value[..256] : value;
        for (int iteration = 0; iteration < 3; iteration++)
        {
            string next;
            try
            {
                next = Uri.UnescapeDataString(decoded);
            }
            catch (UriFormatException)
            {
                return true;
            }

            if (string.Equals(next, decoded, StringComparison.Ordinal))
            {
                break;
            }

            decoded = next;
        }

        string normalized = decoded.ToLowerInvariant();
        return SecretIdentifierFragments.Any(fragment =>
            normalized.Contains(fragment, StringComparison.Ordinal));
    }

    private static int GetEffectivePort(Uri uri)
    {
        if (!uri.IsDefaultPort)
        {
            return uri.Port;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return 443;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            ? 80
            : -1;
    }
}
