using System.Globalization;
using System.Security.Cryptography;

namespace IptvSuite.Application;

public static class DiagnosticSanitizer
{
    public static string SanitizeUri(string? rawUri) => DiagnosticRedactionPolicy.RedactUri(rawUri);

    public static string SanitizeHeader(string? headerName, string? headerValue) =>
        DiagnosticRedactionPolicy.RedactHeader(headerName, headerValue);

    public static string SanitizeUntrustedText(string? value) =>
        DiagnosticRedactionPolicy.RedactUntrustedText(value);

    public static string SanitizeException(Exception? exception)
    {
        string classification = exception switch
        {
            null => "missing",
            OperationCanceledException => "cancelled",
            UnauthorizedAccessException => "access-denied",
            CryptographicException => "data-protection",
            IOException => "storage",
            _ => "unexpected",
        };

        return $"exception;classification={classification}";
    }

    public static string SanitizeNativeResult(int resultCode) =>
        string.Create(CultureInfo.InvariantCulture, $"native;component=redacted;result={resultCode}");
}
