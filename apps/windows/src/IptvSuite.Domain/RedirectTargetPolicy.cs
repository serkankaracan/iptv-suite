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

        DomainResult<SafeEndpoint> target =
            SourceConfigurationValidator.ValidateHttpsLocator(redirectTarget, rejectUserInfo: true);
        if (!target.IsSuccess)
        {
            return DomainResult.Failure<RedirectTargetAssessment>(target.Error!);
        }

        bool sameOrigin = sourceEndpoint.Equals(target.Value);
        RedirectTargetAssessment assessment = new(
            target.Value!,
            sameOrigin ? RedirectOriginRelation.SameOrigin : RedirectOriginRelation.CrossOrigin,
            sameOrigin ? RedirectCredentialPolicy.PreserveForSameOrigin : RedirectCredentialPolicy.Strip);
        return DomainResult.Success(assessment);
    }
}
