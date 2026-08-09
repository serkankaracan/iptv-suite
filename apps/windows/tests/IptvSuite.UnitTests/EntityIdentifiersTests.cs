using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.UnitTests;

[TestClass]
public sealed class EntityIdentifiersTests
{
    [TestMethod]
    [DataRow("source")]
    [DataRow("snapshot")]
    [DataRow("category")]
    [DataRow("channel")]
    public void EmptyGuidIsRejectedByEveryIdentifierFactory(string identifierKind)
    {
        DomainError? error = identifierKind switch
        {
            "source" => SourceId.Create(Guid.Empty).Error,
            "snapshot" => SnapshotId.Create(Guid.Empty).Error,
            "category" => CategoryId.Create(Guid.Empty).Error,
            "channel" => ChannelId.Create(Guid.Empty).Error,
            _ => throw new InvalidOperationException("Unknown synthetic identifier kind."),
        };

        Assert.IsNotNull(error);
        Assert.AreEqual(DomainErrorCode.DomainInvariantViolation, error.Code);
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("snapshot")]
    [DataRow("category")]
    [DataRow("channel")]
    public void NonEmptyGuidRoundTripsThroughEveryIdentifierFactory(string identifierKind)
    {
        Guid expected = Guid.Parse("7bf8586b-e9d6-4fc1-9285-50daecaf44ae");
        (bool IsSuccess, Guid Actual) result = identifierKind switch
        {
            "source" => From(SourceId.Create(expected)),
            "snapshot" => From(SnapshotId.Create(expected)),
            "category" => From(CategoryId.Create(expected)),
            "channel" => From(ChannelId.Create(expected)),
            _ => throw new InvalidOperationException("Unknown synthetic identifier kind."),
        };

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(expected, result.Actual);
    }

    [TestMethod]
    public void GeneratedIdentifiersAreNonEmpty()
    {
        Assert.IsFalse(SourceId.Generate().IsEmpty);
        Assert.IsFalse(SnapshotId.Generate().IsEmpty);
        Assert.IsFalse(CategoryId.Generate().IsEmpty);
        Assert.IsFalse(ChannelId.Generate().IsEmpty);
    }

    private static (bool IsSuccess, Guid Value) From(DomainResult<SourceId> result) =>
        (result.IsSuccess, result.Value.Value);

    private static (bool IsSuccess, Guid Value) From(DomainResult<SnapshotId> result) =>
        (result.IsSuccess, result.Value.Value);

    private static (bool IsSuccess, Guid Value) From(DomainResult<CategoryId> result) =>
        (result.IsSuccess, result.Value.Value);

    private static (bool IsSuccess, Guid Value) From(DomainResult<ChannelId> result) =>
        (result.IsSuccess, result.Value.Value);
}
