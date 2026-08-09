using System.Diagnostics;

namespace IptvSuite.Domain;

[DebuggerDisplay("[PROVIDER-ITEM-KEY]")]
public readonly record struct ProviderItemKey
{
    public const int MaximumLength = 512;

    private readonly string? value;

    private ProviderItemKey(string value) => this.value = value;

    public string Value => value ?? string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(value);

    public static DomainResult<ProviderItemKey> Create(string? value) =>
        DomainText.TryNormalizeRequiredProviderIdentifier(value, MaximumLength, out string normalized)
            ? DomainResult.Success(new ProviderItemKey(normalized))
            : DomainResult.Failure<ProviderItemKey>(DomainErrorCode.DomainInvariantViolation);

    public override string ToString() => "[PROVIDER-ITEM-KEY]";
}
