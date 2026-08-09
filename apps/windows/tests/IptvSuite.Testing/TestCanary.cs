using System.Text;

namespace IptvSuite.Testing;

public sealed class TestCanary
{
    public const string Marker = "IPTVSUITE_TEST_ONLY_CANARY_V1";

    private readonly string _value;

    private TestCanary(string runScope, string caseId)
    {
        _value = $"{Marker}::{runScope}::{caseId}::NOT_A_REAL_CREDENTIAL";
    }

    public static TestCanary Create(string runScope, string caseId)
    {
        ValidateSegment(runScope, nameof(runScope));
        ValidateSegment(caseId, nameof(caseId));
        return new TestCanary(runScope, caseId);
    }

    public void WriteTo(Stream stream, TestCanaryEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes = GetBytes(encoding);
        stream.Write(bytes, 0, bytes.Length);
    }

    public override string ToString() => "[TEST-CANARY]";

    internal IReadOnlyList<(TestCanaryEncoding Encoding, byte[] Bytes)> GetSearchPatterns()
    {
        return
        [
            (TestCanaryEncoding.Utf8, GetBytes(TestCanaryEncoding.Utf8)),
            (TestCanaryEncoding.Utf16LittleEndian, GetBytes(TestCanaryEncoding.Utf16LittleEndian)),
            (TestCanaryEncoding.Utf16BigEndian, GetBytes(TestCanaryEncoding.Utf16BigEndian)),
            (TestCanaryEncoding.UriEscapedUtf8, GetBytes(TestCanaryEncoding.UriEscapedUtf8)),
            (TestCanaryEncoding.Base64Utf8, GetBytes(TestCanaryEncoding.Base64Utf8)),
            (TestCanaryEncoding.MarkerUtf8, Encoding.UTF8.GetBytes(Marker)),
            (TestCanaryEncoding.MarkerUtf16LittleEndian, Encoding.Unicode.GetBytes(Marker)),
            (TestCanaryEncoding.MarkerUtf16BigEndian, Encoding.BigEndianUnicode.GetBytes(Marker)),
            (TestCanaryEncoding.MarkerBase64Utf8Prefix, GetMarkerBase64Prefix()),
        ];
    }

    private static byte[] GetMarkerBase64Prefix()
    {
        int stablePrefixLength = Marker.Length - (Marker.Length % 3);
        string stablePrefix = Marker[..stablePrefixLength];
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(stablePrefix)));
    }

    private byte[] GetBytes(TestCanaryEncoding encoding)
    {
        return encoding switch
        {
            TestCanaryEncoding.Utf8 => Encoding.UTF8.GetBytes(_value),
            TestCanaryEncoding.Utf16LittleEndian => Encoding.Unicode.GetBytes(_value),
            TestCanaryEncoding.Utf16BigEndian => Encoding.BigEndianUnicode.GetBytes(_value),
            TestCanaryEncoding.UriEscapedUtf8 => Encoding.UTF8.GetBytes(Uri.EscapeDataString(_value)),
            TestCanaryEncoding.Base64Utf8 => Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(_value))),
            TestCanaryEncoding.MarkerUtf8 => Encoding.UTF8.GetBytes(Marker),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unsupported canary encoding."),
        };
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Length > 64 || value.Any(character =>
                !(character is >= 'A' and <= 'Z' ||
                  character is >= '0' and <= '9' ||
                  character is '_' or '-')))
        {
            throw new ArgumentException("Canary segments must use 1-64 uppercase ASCII letters, digits, '_' or '-'.", parameterName);
        }
    }
}

public enum TestCanaryEncoding
{
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,
    UriEscapedUtf8,
    Base64Utf8,
    MarkerUtf8,
    MarkerUtf16LittleEndian,
    MarkerUtf16BigEndian,
    MarkerBase64Utf8Prefix,
    Path,
}
