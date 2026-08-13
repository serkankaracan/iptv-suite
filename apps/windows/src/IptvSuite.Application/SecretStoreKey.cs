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
        ProtectedRecordOwner owner,
        ProtectedReferenceKind referenceKind,
        Guid recordIdentifier)
    {
        SourceId = sourceId;
        Purpose = purpose;
        OwnerKind = owner.Kind;
        OwnerIdentifier = owner.Identifier;
        ReferenceKind = referenceKind;
        RecordIdentifier = recordIdentifier;
    }

    internal SourceId SourceId { get; }

    internal ProtectedValuePurpose Purpose { get; }

    internal ProtectedRecordOwnerKind OwnerKind { get; }

    internal Guid OwnerIdentifier { get; }

    internal ProtectedReferenceKind ReferenceKind { get; }

    internal Guid RecordIdentifier { get; }

    internal static (SecretReference Reference, SecretStoreKey Key) IssueCredentials(
        SourceId sourceId,
        ProtectedRecordOwner owner)
    {
        ValidateSource(sourceId);
        ValidateOwner(ProtectedValuePurpose.SourceCredentials, owner);
        SecretReference reference = SecretReference.Create();
        return (reference, new SecretStoreKey(
            sourceId,
            ProtectedValuePurpose.SourceCredentials,
            owner,
            ProtectedReferenceKind.Secret,
            reference.Identifier));
    }

    internal static SecretStoreKey ForCredentials(
        SourceId sourceId,
        ProtectedRecordOwner owner,
        SecretReference reference)
    {
        ValidateSource(sourceId);
        ValidateOwner(ProtectedValuePurpose.SourceCredentials, owner);
        ArgumentNullException.ThrowIfNull(reference);
        return new SecretStoreKey(
            sourceId,
            ProtectedValuePurpose.SourceCredentials,
            owner,
            ProtectedReferenceKind.Secret,
            reference.Identifier);
    }

    internal static (ProtectedLocatorReference Reference, SecretStoreKey Key) IssueLocator(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedRecordOwner owner)
    {
        ValidateSource(sourceId);
        ValidateLocatorPurpose(purpose);
        ValidateOwner(purpose, owner);
        ProtectedLocatorReference reference = ProtectedLocatorReference.Create();
        return (reference, new SecretStoreKey(
            sourceId,
            purpose,
            owner,
            ProtectedReferenceKind.Locator,
            reference.Identifier));
    }

    internal static SecretStoreKey ForLocator(
        SourceId sourceId,
        ProtectedValuePurpose purpose,
        ProtectedRecordOwner owner,
        ProtectedLocatorReference reference)
    {
        ValidateSource(sourceId);
        ValidateLocatorPurpose(purpose);
        ValidateOwner(purpose, owner);
        ArgumentNullException.ThrowIfNull(reference);
        return new SecretStoreKey(
            sourceId,
            purpose,
            owner,
            ProtectedReferenceKind.Locator,
            reference.Identifier);
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

    private static void ValidateOwner(ProtectedValuePurpose purpose, ProtectedRecordOwner owner)
    {
        ProtectedRecordOwnerKind expectedKind = purpose switch
        {
            ProtectedValuePurpose.SourceCredentials or ProtectedValuePurpose.RemotePlaylistLocator =>
                ProtectedRecordOwnerKind.SourceConfiguration,
            ProtectedValuePurpose.ChannelStreamLocator or ProtectedValuePurpose.ChannelLogoLocator =>
                ProtectedRecordOwnerKind.Channel,
            _ => throw new ArgumentOutOfRangeException(
                nameof(purpose),
                purpose,
                "A supported protected-value purpose is required."),
        };

        if (owner.IsEmpty || owner.Kind != expectedKind)
        {
            throw new ArgumentException(
                "The protected-record owner kind does not match the protected-value purpose.",
                nameof(owner));
        }
    }
}
