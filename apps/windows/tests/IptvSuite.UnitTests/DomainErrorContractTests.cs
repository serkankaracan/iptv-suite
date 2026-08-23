using System.Globalization;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class DomainErrorContractTests
{
    private static readonly ExpectedError[] ExpectedErrors =
    [
        new(DomainErrorCode.SourceNameRequired, DomainRetryability.Never, "Errors.Source.NameRequired"),
        new(DomainErrorCode.SourceNameInvalid, DomainRetryability.Never, "Errors.Source.NameInvalid"),
        new(DomainErrorCode.SourceNameTooLong, DomainRetryability.Never, "Errors.Source.NameTooLong"),
        new(DomainErrorCode.EndpointRequired, DomainRetryability.Never, "Errors.Endpoint.Required"),
        new(DomainErrorCode.EndpointMalformed, DomainRetryability.Never, "Errors.Endpoint.Malformed"),
        new(DomainErrorCode.EndpointTooLong, DomainRetryability.Never, "Errors.Endpoint.TooLong"),
        new(DomainErrorCode.EndpointSchemeUnsupported, DomainRetryability.Never, "Errors.Endpoint.SchemeUnsupported"),
        new(DomainErrorCode.InsecureTransportRejected, DomainRetryability.Never, "Errors.Endpoint.InsecureTransportRejected"),
        new(DomainErrorCode.EndpointUserInfoNotAllowed, DomainRetryability.Never, "Errors.Endpoint.UserInfoNotAllowed"),
        new(DomainErrorCode.EndpointFragmentNotAllowed, DomainRetryability.Never, "Errors.Endpoint.FragmentNotAllowed"),
        new(DomainErrorCode.UsernameRequired, DomainRetryability.Never, "Errors.Credentials.UsernameRequired"),
        new(DomainErrorCode.PasswordRequired, DomainRetryability.Never, "Errors.Credentials.PasswordRequired"),
        new(DomainErrorCode.CredentialInvalid, DomainRetryability.Never, "Errors.Credentials.Invalid"),
        new(DomainErrorCode.CredentialTooLong, DomainRetryability.Never, "Errors.Credentials.TooLong"),
        new(DomainErrorCode.SecretReferenceInvalid, DomainRetryability.Never, "Errors.SecretReference.Invalid"),
        new(DomainErrorCode.DomainInvariantViolation, DomainRetryability.Never, "Errors.Domain.InvariantViolation"),
        new(DomainErrorCode.NetworkUnreachable, DomainRetryability.Manual, "Errors.Network.Unreachable"),
        new(DomainErrorCode.AuthenticationRejected, DomainRetryability.Never, "Errors.Authentication.Rejected"),
        new(DomainErrorCode.PlaylistDownloadFailed, DomainRetryability.BoundedTransient, "Errors.Playlist.DownloadFailed"),
        new(DomainErrorCode.UnsupportedPlaylistFormat, DomainRetryability.Never, "Errors.Playlist.UnsupportedFormat"),
        new(DomainErrorCode.RequestTimedOut, DomainRetryability.BoundedTransient, "Errors.Network.RequestTimedOut"),
        new(DomainErrorCode.TlsValidationFailed, DomainRetryability.Never, "Errors.Network.TlsValidationFailed"),
        new(DomainErrorCode.PlaybackStartFailed, DomainRetryability.Manual, "Errors.Playback.StartFailed"),
        new(DomainErrorCode.StreamInterrupted, DomainRetryability.BoundedTransient, "Errors.Playback.StreamInterrupted"),
        new(DomainErrorCode.ReconnectExhausted, DomainRetryability.Manual, "Errors.Playback.ReconnectExhausted"),
        new(DomainErrorCode.StorageUnavailable, DomainRetryability.Manual, "Errors.Storage.Unavailable"),
        new(DomainErrorCode.OperationCancelled, DomainRetryability.Never, "Errors.Operation.Cancelled"),
        new(DomainErrorCode.PlaybackControlFailed, DomainRetryability.Manual, "Errors.Playback.ControlFailed"),
    ];

    [TestMethod]
    public void EveryErrorHasStableRetryAndLocalizationMetadata()
    {
        Assert.AreEqual(0, typeof(DomainError).GetConstructors().Length);

        DomainErrorCode[] codes = Enum.GetValues<DomainErrorCode>();
        CollectionAssert.AreEquivalent(ExpectedErrors.Select(item => item.Code).ToArray(), codes);
        CollectionAssert.AreEqual(
            ExpectedErrors.Select(item => item.Code).ToArray(),
            codes,
            "Domain error ordinals are persisted in catalog history and must remain append-only.");
        for (int ordinal = 0; ordinal < codes.Length; ordinal++)
        {
            Assert.AreEqual(
                ordinal,
                (int)codes[ordinal],
                $"Persisted domain error ordinal changed for {codes[ordinal]}.");
        }

        foreach (ExpectedError expected in ExpectedErrors)
        {
            DomainError actual = DomainError.Create(expected.Code);

            Assert.AreEqual(expected.Code, actual.Code);
            Assert.AreEqual(expected.Retryability, actual.Retryability);
            Assert.AreEqual(expected.ResourceKey, actual.ResourceKey);
        }

        Assert.AreEqual(
            ExpectedErrors.Length,
            ExpectedErrors.Select(item => item.ResourceKey).Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void ErrorSerializationIsStableAndCultureIndependent()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            string? baseline = null;
            foreach (string cultureName in new[] { "en-US", "tr-TR", "de-DE" })
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                string serialized = JsonSerializer.Serialize(DomainError.Create(DomainErrorCode.AuthenticationRejected));
                baseline ??= serialized;
                Assert.AreEqual(baseline, serialized);
            }

            Assert.AreEqual(
                "{\"Code\":\"AuthenticationRejected\",\"Retryability\":\"Never\",\"ResourceKey\":\"Errors.Authentication.Rejected\"}",
                baseline);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [TestMethod]
    public void FailureResultContainsNoExceptionOrUntrustedContextSurface()
    {
        DomainResult<string> result = DomainResult.Failure<string>(DomainErrorCode.EndpointMalformed);
        string serialized = JsonSerializer.Serialize(result);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Value);
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(serialized, "EndpointMalformed");
        Assert.IsFalse(serialized.Contains("Exception", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains("StackTrace", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("[DOMAIN-RESULT:EndpointMalformed]", result.ToString());
    }

    private sealed record ExpectedError(
        DomainErrorCode Code,
        DomainRetryability Retryability,
        string ResourceKey);
}
