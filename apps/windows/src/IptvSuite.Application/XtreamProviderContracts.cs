using System.Diagnostics;

namespace IptvSuite.Application;

[DebuggerDisplay("[XTREAM-ACCOUNT-STATUS]")]
public sealed record XtreamAccountStatus(bool IsAuthenticated)
{
    public override string ToString() => "[XTREAM-ACCOUNT-STATUS]";
}

[DebuggerDisplay("[XTREAM-PROVIDER-PAGE]")]
public sealed class XtreamProviderPage<T>
{
    internal XtreamProviderPage(IReadOnlyList<T> items, int skippedItemCount, int duplicateIdentifierCount)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        SkippedItemCount = skippedItemCount;
        DuplicateIdentifierCount = duplicateIdentifierCount;
    }

    public IReadOnlyList<T> Items { get; }

    public int SkippedItemCount { get; }

    public int DuplicateIdentifierCount { get; }

    public override string ToString() => "[XTREAM-PROVIDER-PAGE]";
}

[DebuggerDisplay("[XTREAM-CATEGORY-INPUT]")]
public sealed record XtreamCategoryInput(string ProviderIdentifier, string Name)
{
    public override string ToString() => "[XTREAM-CATEGORY-INPUT]";
}

[DebuggerDisplay("[XTREAM-STREAM-INPUT]")]
public sealed record XtreamStreamInput(
    string ProviderIdentifier,
    string Name,
    string? CategoryIdentifier,
    int? Number,
    string? ContainerExtension,
    bool? IsAdultHint)
{
    public override string ToString() => "[XTREAM-STREAM-INPUT]";
}
