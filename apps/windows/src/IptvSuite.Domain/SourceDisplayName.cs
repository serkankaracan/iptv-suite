namespace IptvSuite.Domain;

public readonly record struct SourceDisplayName
{
    private SourceDisplayName(string value) => Value = value;

    public string Value { get; }

    public static DomainResult<SourceDisplayName> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DomainResult.Failure<SourceDisplayName>(
                DomainErrorCode.SourceNameRequired);
        }

        if (!DomainTextValidation.TryInspect(value, out _) ||
            DomainTextValidation.ContainsControlCharacter(value))
        {
            return DomainResult.Failure<SourceDisplayName>(DomainErrorCode.SourceNameInvalid);
        }

        string normalized = value.Trim().Normalize(System.Text.NormalizationForm.FormC);
        if (!DomainTextValidation.TryInspect(normalized, out int scalarCount))
        {
            return DomainResult.Failure<SourceDisplayName>(DomainErrorCode.SourceNameInvalid);
        }

        if (scalarCount > SourceConfigurationValidator.MaxDisplayNameUnicodeScalars)
        {
            return DomainResult.Failure<SourceDisplayName>(DomainErrorCode.SourceNameTooLong);
        }

        return DomainResult.Success(new SourceDisplayName(normalized));
    }

    public override string ToString() => "[SOURCE-DISPLAY-NAME]";
}
