using System.Buffers;
using System.Globalization;
using System.Text;

namespace IptvSuite.Domain;

internal static class DomainText
{
    private static readonly string[] LocatorSchemes =
    [
        "data",
        "file",
        "ftp",
        "ftps",
        "http",
        "https",
        "javascript",
        "rtmp",
        "rtmps",
        "rtsp",
        "rtsps",
        "sftp",
        "smb",
    ];

    public static bool TryNormalizeRequired(
        string? value,
        int maximumLength,
        out string normalized)
    {
        normalized = string.Empty;
        if (value is null || !IsWellFormedAndFreeOfControlCharacters(value))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = value.Trim().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (candidate.Length == 0 || !HasAtMostUnicodeScalars(candidate, maximumLength))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    public static bool TryNormalizeOptional(
        string? value,
        int maximumLength,
        out string? normalized)
    {
        normalized = null;
        if (value is null)
        {
            return true;
        }

        if (!IsWellFormedAndFreeOfControlCharacters(value))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = value.Trim().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (candidate.Length == 0)
        {
            return true;
        }

        if (!HasAtMostUnicodeScalars(candidate, maximumLength))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    public static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool isHexadecimal = character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F';
            if (!isHexadecimal)
            {
                return false;
            }
        }

        normalized = value.ToUpperInvariant();
        return true;
    }

    public static bool TryNormalizeRequiredProviderIdentifier(
        string? value,
        int maximumLength,
        out string normalized)
    {
        if (!TryNormalizeRequired(value, maximumLength, out normalized) ||
            LooksLikeLocator(normalized))
        {
            normalized = string.Empty;
            return false;
        }

        return true;
    }

    private static bool IsWellFormedAndFreeOfControlCharacters(string value)
    {
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int consumed);
            if (status != OperationStatus.Done || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }

    private static bool HasAtMostUnicodeScalars(string value, int maximumLength)
    {
        int scalarCount = 0;
        foreach (Rune _ in value.EnumerateRunes())
        {
            scalarCount++;
            if (scalarCount > maximumLength)
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeLocator(string value)
    {
        ReadOnlySpan<char> candidate = value.AsSpan();
        if (StartsWithDoublePathSeparator(candidate))
        {
            return true;
        }

        int colonIndex = candidate.IndexOf(':');
        if (colonIndex <= 0 || !IsUriScheme(candidate[..colonIndex]))
        {
            return false;
        }

        ReadOnlySpan<char> suffix = candidate[(colonIndex + 1)..];
        if (StartsWithDoublePathSeparator(suffix))
        {
            return true;
        }

        ReadOnlySpan<char> scheme = candidate[..colonIndex];
        foreach (string locatorScheme in LocatorSchemes)
        {
            if (scheme.Equals(locatorScheme, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithDoublePathSeparator(ReadOnlySpan<char> value) =>
        value.Length >= 2 &&
        value[0] is '/' or '\\' &&
        value[1] is '/' or '\\';

    private static bool IsUriScheme(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !IsAsciiLetter(value[0]))
        {
            return false;
        }

        foreach (char character in value[1..])
        {
            if (!IsAsciiLetter(character) &&
                !char.IsAsciiDigit(character) &&
                character is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
