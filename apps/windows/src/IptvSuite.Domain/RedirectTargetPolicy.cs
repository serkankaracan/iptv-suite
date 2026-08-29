using System.Diagnostics;
using System.Text.Json.Serialization;

namespace IptvSuite.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<RedirectOriginRelation>))]
public enum RedirectOriginRelation
{
    SameOrigin,
    CrossOrigin,
}

[JsonConverter(typeof(JsonStringEnumConverter<RedirectCredentialPolicy>))]
public enum RedirectCredentialPolicy
{
    PreserveForSameOrigin,
    Strip,
}

[DebuggerDisplay("[REDIRECT-TARGET-ASSESSMENT]")]
public sealed class RedirectTargetAssessment
{
    internal RedirectTargetAssessment(
        SafeEndpoint targetEndpoint,
        RedirectOriginRelation originRelation,
        RedirectCredentialPolicy credentialPolicy)
    {
        ArgumentNullException.ThrowIfNull(targetEndpoint);
        TargetEndpoint = targetEndpoint;
        OriginRelation = originRelation;
        CredentialPolicy = credentialPolicy;
    }

    public SafeEndpoint TargetEndpoint { get; }

    public RedirectOriginRelation OriginRelation { get; }

    public RedirectCredentialPolicy CredentialPolicy { get; }

    public override string ToString() => "[REDIRECT-TARGET-ASSESSMENT]";
}

public static class RedirectTargetPolicy
{
    public static DomainResult<RedirectTargetAssessment> Evaluate(
        SafeEndpoint sourceEndpoint,
        string? redirectTarget)
    {
        ArgumentNullException.ThrowIfNull(sourceEndpoint);

        bool sourceUsesHttp = string.Equals(
            sourceEndpoint.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.Ordinal);
        DomainResult<SafeEndpoint> target = SourceConfigurationValidator.ValidateWebLocator(
            redirectTarget,
            rejectUserInfo: true,
            allowInsecureHttp: sourceUsesHttp);
        if (!target.IsSuccess)
        {
            return DomainResult.Failure<RedirectTargetAssessment>(target.Error!);
        }

        bool sameOrigin = sourceEndpoint.Equals(target.Value);
        if (sourceUsesHttp &&
            string.Equals(target.Value!.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
            !sameOrigin)
        {
            return DomainResult.Failure<RedirectTargetAssessment>(
                DomainErrorCode.InsecureTransportRejected);
        }

        RedirectTargetAssessment assessment = new(
            target.Value!,
            sameOrigin ? RedirectOriginRelation.SameOrigin : RedirectOriginRelation.CrossOrigin,
            sameOrigin ? RedirectCredentialPolicy.PreserveForSameOrigin : RedirectCredentialPolicy.Strip);
        return DomainResult.Success(assessment);
    }
}
