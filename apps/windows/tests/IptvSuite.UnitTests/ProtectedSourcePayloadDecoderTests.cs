using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class ProtectedSourcePayloadDecoderTests
{
    private const int MagicSize = 8;
    private const int VersionOffset = MagicSize;
    private const int FirstLengthOffset = VersionOffset + 1;
    private const int CredentialsHeaderSize = 21;
    private const int RemotePlaylistHeaderSize = 13;

    [TestMethod]
    public void EncoderAndDecoderRoundTripExactUtf8FieldSlices()
    {
        string locator = "https://fixtures.invalid/private/ç?token=synthetic";
        string username = "kullanıcı";
        string password = " synthetic password ";
        byte[] credentials = EncodeCredentials(locator, username, password);
        byte[] remote = EncodeRemotePlaylist(locator);

        try
        {
            Assert.IsTrue(TryDecodeCredentials(credentials, out LayoutSnapshot credentialLayout));
            AssertFieldEquals(credentials, credentialLayout.LocatorOffset, credentialLayout.LocatorLength, locator);
            AssertFieldEquals(credentials, credentialLayout.UsernameOffset, credentialLayout.UsernameLength, username);
            AssertFieldEquals(credentials, credentialLayout.PasswordOffset, credentialLayout.PasswordLength, password);

            Assert.IsTrue(TryDecodeRemotePlaylist(remote, out LayoutSnapshot remoteLayout));
            AssertFieldEquals(remote, remoteLayout.LocatorOffset, remoteLayout.LocatorLength, locator);
            Assert.IsFalse(TryDecodeCredentials(remote, out _));
            Assert.IsFalse(TryDecodeRemotePlaylist(credentials, out _));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentials);
            CryptographicOperations.ZeroMemory(remote);
        }
    }

    [TestMethod]
    public void WrongMagicVersionTruncationAndTrailingDataFailClosed()
    {
        byte[] credentials = EncodeCredentials(
            "https://fixtures.invalid/api",
            "synthetic-user",
            "synthetic-password");
        byte[] remote = EncodeRemotePlaylist("https://fixtures.invalid/catalog.m3u");

        try
        {
            AssertAllTruncationsFail(credentials, credentialsPayload: true);
            AssertAllTruncationsFail(remote, credentialsPayload: false);

            VerifyMutationFails(credentials, credentialsPayload: true, value => value[0] ^= 0x40);
            VerifyMutationFails(credentials, credentialsPayload: true, value => value[VersionOffset] = 0);
            VerifyMutationFails(credentials, credentialsPayload: true, value => value[VersionOffset] = 2);
            VerifyMutationFails(remote, credentialsPayload: false, value => value[0] ^= 0x40);
            VerifyMutationFails(remote, credentialsPayload: false, value => value[VersionOffset] = 0);
            VerifyMutationFails(remote, credentialsPayload: false, value => value[VersionOffset] = 2);

            byte[] credentialsWithTrailingByte = [.. credentials, 0x7F];
            byte[] remoteWithTrailingByte = [.. remote, 0x7F];
            try
            {
                Assert.IsFalse(TryDecodeCredentials(credentialsWithTrailingByte, out _));
                Assert.IsFalse(TryDecodeRemotePlaylist(remoteWithTrailingByte, out _));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(credentialsWithTrailingByte);
                CryptographicOperations.ZeroMemory(remoteWithTrailingByte);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentials);
            CryptographicOperations.ZeroMemory(remote);
        }
    }

    [TestMethod]
    public void NonPositiveOverflowingAndInconsistentLengthsFailWithoutThrowing()
    {
        byte[] credentials = EncodeCredentials(
            "https://fixtures.invalid/api",
            "synthetic-user",
            "synthetic-password");
        byte[] remote = EncodeRemotePlaylist("https://fixtures.invalid/catalog.m3u");

        try
        {
            int[] invalidLengths = [0, -1, int.MaxValue];
            foreach (int invalidLength in invalidLengths)
            {
                for (int fieldIndex = 0; fieldIndex < 3; fieldIndex++)
                {
                    int lengthOffset = FirstLengthOffset + (fieldIndex * sizeof(int));
                    VerifyMutationFails(
                        credentials,
                        credentialsPayload: true,
                        value => BinaryPrimitives.WriteInt32BigEndian(
                            value.AsSpan(lengthOffset, sizeof(int)),
                            invalidLength));
                }

                VerifyMutationFails(
                    remote,
                    credentialsPayload: false,
                    value => BinaryPrimitives.WriteInt32BigEndian(
                        value.AsSpan(FirstLengthOffset, sizeof(int)),
                        invalidLength));
            }

            VerifyMutationFails(
                credentials,
                credentialsPayload: true,
                value => BinaryPrimitives.WriteInt32BigEndian(
                    value.AsSpan(FirstLengthOffset, sizeof(int)),
                    1));
            VerifyMutationFails(
                remote,
                credentialsPayload: false,
                value => BinaryPrimitives.WriteInt32BigEndian(
                    value.AsSpan(FirstLengthOffset, sizeof(int)),
                    1));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentials);
            CryptographicOperations.ZeroMemory(remote);
        }
    }

    [TestMethod]
    public void InvalidUtf8ControlsAndCredentialWhitespaceFailClosed()
    {
        byte[][] payloads =
        [
            BuildRemotePayload([0xC0, 0xAF]),
            BuildRemotePayload(Encoding.UTF8.GetBytes("https://fixtures.invalid/\0catalog")),
            BuildCredentialsPayload(
                Encoding.UTF8.GetBytes("https://fixtures.invalid/api"),
                [0xED, 0xA0, 0x80],
                Encoding.UTF8.GetBytes("password")),
            BuildCredentialsPayload(
                Encoding.UTF8.GetBytes("https://fixtures.invalid/api"),
                Encoding.UTF8.GetBytes("   "),
                Encoding.UTF8.GetBytes("password")),
            BuildCredentialsPayload(
                Encoding.UTF8.GetBytes("https://fixtures.invalid/api"),
                Encoding.UTF8.GetBytes("username"),
                Encoding.UTF8.GetBytes("pass\nword")),
        ];

        try
        {
            Assert.IsFalse(TryDecodeRemotePlaylist(payloads[0], out _));
            Assert.IsFalse(TryDecodeRemotePlaylist(payloads[1], out _));
            Assert.IsFalse(TryDecodeCredentials(payloads[2], out _));
            Assert.IsFalse(TryDecodeCredentials(payloads[3], out _));
            Assert.IsFalse(TryDecodeCredentials(payloads[4], out _));

            byte[] whitespacePassword = BuildCredentialsPayload(
                Encoding.UTF8.GetBytes("https://fixtures.invalid/api"),
                Encoding.UTF8.GetBytes("username"),
                Encoding.UTF8.GetBytes("   "));
            try
            {
                Assert.IsTrue(TryDecodeCredentials(whitespacePassword, out _));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(whitespacePassword);
            }
        }
        finally
        {
            foreach (byte[] payload in payloads)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    [TestMethod]
    public void UnicodeScalarLimitsAreInclusiveAndFailAboveTheBoundary()
    {
        byte[][] payloads =
        [
            BuildRemotePayload(Encoding.UTF8.GetBytes(new string('l', 4096))),
            BuildRemotePayload(Encoding.UTF8.GetBytes(new string('l', 4097))),
            BuildCredentialsPayload(
                Encoding.UTF8.GetBytes("https://fixtures.invalid/api"),
                Encoding.UTF8.GetBytes(new string('u', 256)),
                Encoding.UTF8.GetBytes(new string('p', 1024))),
            BuildCredentialsPayload(
                Encoding.UTF8.GetBytes("https://fixtures.invalid/api"),
                Encoding.UTF8.GetBytes(new string('u', 257)),
                Encoding.UTF8.GetBytes("password")),
            BuildCredentialsPayload(
                Encoding.UTF8.GetBytes("https://fixtures.invalid/api"),
                Encoding.UTF8.GetBytes("username"),
                Encoding.UTF8.GetBytes(new string('p', 1025))),
        ];

        try
        {
            Assert.IsTrue(TryDecodeRemotePlaylist(payloads[0], out _));
            Assert.IsFalse(TryDecodeRemotePlaylist(payloads[1], out _));
            Assert.IsTrue(TryDecodeCredentials(payloads[2], out _));
            Assert.IsFalse(TryDecodeCredentials(payloads[3], out _));
            Assert.IsFalse(TryDecodeCredentials(payloads[4], out _));
        }
        finally
        {
            foreach (byte[] payload in payloads)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    [TestMethod]
    public void DecoderAndLayoutsRemainInternalAndExposeOnlyNumericOffsets()
    {
        Type decoder = GetApplicationType("IptvSuite.Application.ProtectedSourcePayloadDecoder");
        Type credentialLayout = GetApplicationType("IptvSuite.Application.XtreamSourcePayloadLayout");
        Type locatorLayout = GetApplicationType("IptvSuite.Application.RemotePlaylistPayloadLayout");

        Assert.IsFalse(decoder.IsPublic);
        Assert.IsFalse(credentialLayout.IsPublic);
        Assert.IsFalse(locatorLayout.IsPublic);
        Assert.IsTrue(credentialLayout.GetProperties().All(property => property.PropertyType == typeof(int)));
        Assert.IsTrue(locatorLayout.GetProperties().All(property => property.PropertyType == typeof(int)));
    }

    private static void AssertAllTruncationsFail(byte[] payload, bool credentialsPayload)
    {
        for (int length = 0; length < payload.Length; length++)
        {
            byte[] truncated = payload.AsSpan(0, length).ToArray();
            try
            {
                bool decoded = credentialsPayload
                    ? TryDecodeCredentials(truncated, out _)
                    : TryDecodeRemotePlaylist(truncated, out _);
                Assert.IsFalse(decoded, $"A truncated payload of length {length} was accepted.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(truncated);
            }
        }
    }

    private static void VerifyMutationFails(
        byte[] source,
        bool credentialsPayload,
        Action<byte[]> mutate)
    {
        byte[] candidate = source.ToArray();
        try
        {
            mutate(candidate);
            bool decoded = credentialsPayload
                ? TryDecodeCredentials(candidate, out _)
                : TryDecodeRemotePlaylist(candidate, out _);
            Assert.IsFalse(decoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    private static byte[] EncodeCredentials(string locator, string username, string password) =>
        InvokeEncoder("EncodeXtreamSourceCredentials", locator, username, password);

    private static byte[] EncodeRemotePlaylist(string locator) =>
        InvokeEncoder("EncodeRemotePlaylistLocator", locator);

    private static byte[] InvokeEncoder(string methodName, params object?[] parameters)
    {
        Type encoder = GetApplicationType("IptvSuite.Application.ProtectedSourcePayloadEncoder");
        MethodInfo method = encoder.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The protected source payload encoder method is unavailable.");
        return method.Invoke(null, parameters) as byte[]
            ?? throw new InvalidOperationException("The protected source payload encoder returned no payload.");
    }

    private static bool TryDecodeCredentials(byte[] payload, out LayoutSnapshot layout) =>
        InvokeDecoder("TryDecodeXtream", payload, out layout);

    private static bool TryDecodeRemotePlaylist(byte[] payload, out LayoutSnapshot layout) =>
        InvokeDecoder("TryDecodeRemotePlaylist", payload, out layout);

    private static bool InvokeDecoder(string methodName, byte[] payload, out LayoutSnapshot layout)
    {
        Type decoder = GetApplicationType("IptvSuite.Application.ProtectedSourcePayloadDecoder");
        MethodInfo method = decoder.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The protected source payload decoder method is unavailable.");
        object?[] parameters = [new ReadOnlyMemory<byte>(payload), null];
        bool decoded = method.Invoke(null, parameters) is true;
        layout = decoded
            ? ReadLayout(parameters[1] ?? throw new InvalidOperationException("A decoded layout is required."))
            : default;
        return decoded;
    }

    private static LayoutSnapshot ReadLayout(object value)
    {
        Type type = value.GetType();
        return new LayoutSnapshot(
            GetInt32(type, value, "LocatorOffset"),
            GetInt32(type, value, "LocatorLength"),
            GetInt32(type, value, "UsernameOffset"),
            GetInt32(type, value, "UsernameLength"),
            GetInt32(type, value, "PasswordOffset"),
            GetInt32(type, value, "PasswordLength"));
    }

    private static int GetInt32(Type type, object value, string propertyName) =>
        type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value) is int result
            ? result
            : 0;

    private static Type GetApplicationType(string fullName) =>
        typeof(SourceDraftProtectionService).Assembly.GetType(fullName, throwOnError: true)!;

    private static void AssertFieldEquals(
        byte[] payload,
        int offset,
        int length,
        string expected)
    {
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        try
        {
            Assert.IsTrue(payload.AsSpan(offset, length).SequenceEqual(expectedBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    private static byte[] BuildRemotePayload(byte[] locator)
    {
        byte[] payload = GC.AllocateUninitializedArray<byte>(RemotePlaylistHeaderSize + locator.Length);
        "SRCLOC01"u8.CopyTo(payload);
        payload[VersionOffset] = 1;
        BinaryPrimitives.WriteInt32BigEndian(
            payload.AsSpan(FirstLengthOffset, sizeof(int)),
            locator.Length);
        locator.CopyTo(payload, RemotePlaylistHeaderSize);
        return payload;
    }

    private static byte[] BuildCredentialsPayload(byte[] locator, byte[] username, byte[] password)
    {
        byte[] payload = GC.AllocateUninitializedArray<byte>(
            CredentialsHeaderSize + locator.Length + username.Length + password.Length);
        "SRCRED01"u8.CopyTo(payload);
        payload[VersionOffset] = 1;
        BinaryPrimitives.WriteInt32BigEndian(
            payload.AsSpan(FirstLengthOffset, sizeof(int)),
            locator.Length);
        BinaryPrimitives.WriteInt32BigEndian(
            payload.AsSpan(FirstLengthOffset + sizeof(int), sizeof(int)),
            username.Length);
        BinaryPrimitives.WriteInt32BigEndian(
            payload.AsSpan(FirstLengthOffset + (2 * sizeof(int)), sizeof(int)),
            password.Length);
        int offset = CredentialsHeaderSize;
        locator.CopyTo(payload, offset);
        offset += locator.Length;
        username.CopyTo(payload, offset);
        offset += username.Length;
        password.CopyTo(payload, offset);
        return payload;
    }

    private readonly record struct LayoutSnapshot(
        int LocatorOffset,
        int LocatorLength,
        int UsernameOffset,
        int UsernameLength,
        int PasswordOffset,
        int PasswordLength);
}
