namespace IptvSuite.Application;

internal enum ProtectedReferenceKind : byte
{
    Secret = 1,
    Locator = 2,
}

internal readonly record struct SecretStoreKey
{
    private SecretStoreKey(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedReferenceKind referenceKind,
        Guid recordIdentifier)
    {
        SourceId = sourceId;
        Purpose = purpose;
        ReferenceKind = referenceKind;
        RecordIdentifier = recordIdentifier;
    }

    internal SourceId SourceId { get; }

    internal ProtectedValuePurpose Purpose { get; }

    internal ProtectedReferenceKind ReferenceKind { get; }

    internal Guid RecordIdentifier { get; }

    internal static (SecretReference Reference, SecretStoreKey Key) IssueCredentials(SourceId sourceId)
    {
        ValidateSource(sourceId);
        SecretReference reference = SecretReference.Create();
        return (reference, new SecretStoreKey(
            sourceId,
            ProtectedValuePurpose.SourceCredentials,
            ProtectedReferenceKind.Secret,
            reference.Identifier));
    }

    internal static SecretStoreKey ForCredentials(SourceId sourceId, SecretReference reference)
    {
        ValidateSource(sourceId);
        ArgumentNullException.ThrowIfNull(reference);
        return new SecretStoreKey(
            sourceId,
            ProtectedValuePurpose.SourceCredentials,
            ProtectedReferenceKind.Secret,
            reference.Identifier);
    }

    internal static (ProtectedLocatorReference Reference, SecretStoreKey Key) IssueLocator(
        SourceId sourceId,
        ProtectedValuePurpose purpose)
    {
        ValidateSource(sourceId);
        ValidateLocatorPurpose(purpose);
        ProtectedLocatorReference reference = ProtectedLocatorReference.Create();
        return (reference, new SecretStoreKey(
            sourceId,
            purpose,
            ProtectedReferenceKind.Locator,
            reference.Identifier));
    }

    internal static SecretStoreKey ForLocator(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedLocatorReference reference)
    {
        ValidateSource(sourceId);
        ValidateLocatorPurpose(purpose);
        ArgumentNullException.ThrowIfNull(reference);
        return new SecretStoreKey(sourceId, purpose, ProtectedReferenceKind.Locator, reference.Identifier);
    }

    public override string ToString() => "[PROTECTED-STORE-KEY]";

    private static void ValidateSource(SourceId sourceId)
    {
        if (sourceId.IsEmpty)
        {
            throw new ArgumentException("A non-empty source identifier is required.", nameof(sourceId));
        }
    }

    private static void ValidateLocatorPurpose(ProtectedValuePurpose purpose)
    {
        if (purpose is ProtectedValuePurpose.SourceCredentials || !Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "A locator purpose is required.");
        }
    }
}
