using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class XtreamProviderJsonParserTests
{
    [TestMethod]
    public void AccountStatusAcceptsTolerantBooleanScalarsAndRejectsBodyLevelAuthenticationFailure()
    {
        DomainResult<XtreamAccountStatus> accepted =
            XtreamProviderJsonParser.ParseAccountStatus(Utf8("""{"user_info":{"auth":"1"}}"""));
        DomainResult<XtreamAccountStatus> rejected =
            XtreamProviderJsonParser.ParseAccountStatus(Utf8("""{"user_info":{"auth":0}}"""));

        Assert.IsTrue(accepted.IsSuccess);
        Assert.IsTrue(accepted.Value!.IsAuthenticated);
        Assert.AreEqual("[XTREAM-ACCOUNT-STATUS]", accepted.Value.ToString());
        Assert.IsFalse(rejected.IsSuccess);
        Assert.AreEqual(DomainErrorCode.AuthenticationRejected, rejected.Error!.Code);
    }

    [TestMethod]
    public void AccountStatusRequiresTheBoundedUserInfoAuthenticationShape()
    {
        foreach (ReadOnlyMemory<byte> item in new[]
                 {
                     Utf8("{}"),
                     Utf8("""{"user_info":null}"""),
                     Utf8("""{"user_info":{"auth":"unknown"}}"""),
                 })
        {
            DomainResult<XtreamAccountStatus> result = XtreamProviderJsonParser.ParseAccountStatus(item);
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, result.Error!.Code);
        }
    }

    [TestMethod]
    public void CategoriesAcceptStringNumberAndBooleanIdentifiersAndIgnoreUnknownFields()
    {
        DomainResult<XtreamProviderPage<XtreamCategoryInput>> result =
            XtreamProviderJsonParser.ParseCategories(Utf8("""
                [
                  {"category_id":"10","category_name":" News ","unknown":{"nested":true}},
                  {"category_id":20,"category_name":"Sports"},
                  {"category_id":true,"category_name":"Synthetic"}
                ]
                """));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(3, result.Value!.Items);
        Assert.AreEqual("News", result.Value.Items[0].Name);
        Assert.AreEqual("20", result.Value.Items[1].ProviderIdentifier);
        Assert.AreEqual("true", result.Value.Items[2].ProviderIdentifier);
    }

    [TestMethod]
    public void DuplicateAndMissingCategoryIdentifiersHaveVisibleDeterministicPolicy()
    {
        DomainResult<XtreamProviderPage<XtreamCategoryInput>> result =
            XtreamProviderJsonParser.ParseCategories(Utf8("""
                [
                  {"category_id":"10","category_name":"First"},
                  {"category_id":"10","category_name":"Duplicate"},
                  {"category_name":"Missing"},
                  null
                ]
                """));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Value!.Items);
        Assert.AreEqual(1, result.Value.DuplicateIdentifierCount);
        Assert.AreEqual(2, result.Value.SkippedItemCount);
    }

    [TestMethod]
    public void LiveStreamsNormalizeScalarVariantsWithoutRetainingUnknownFields()
    {
        DomainResult<XtreamProviderPage<XtreamStreamInput>> result =
            XtreamProviderJsonParser.ParseLiveStreams(Utf8("""
                [
                  {
                    "stream_id":42,
                    "name":" Synthetic Live ",
                    "category_id":7,
                    "num":"12",
                    "container_extension":"m3u8",
                    "is_adult":"1",
                    "direct_source":"https://must-not-be-retained.invalid/secret"
                  }
                ]
                """));

        Assert.IsTrue(result.IsSuccess);
        XtreamStreamInput stream = result.Value!.Items.Single();
        Assert.AreEqual("42", stream.ProviderPlaybackKey.Value);
        Assert.AreEqual("Synthetic Live", stream.Name);
        Assert.AreEqual("7", stream.CategoryIdentifier);
        Assert.AreEqual(12, stream.Number);
        Assert.AreEqual("m3u8", stream.ContainerExtension);
        Assert.AreEqual(true, stream.IsAdultHint);
        Assert.AreEqual("[XTREAM-STREAM-INPUT]", stream.ToString());
    }

    [TestMethod]
    public void MalformedNonArrayAndOversizedDocumentsFailWithStableTypedError()
    {
        ReadOnlyMemory<byte>[] cases =
        [
            Utf8("{"),
            Utf8("{}"),
            new byte[HttpTransportLimits.MaximumAllowedResponseBytes + 1],
        ];

        foreach (ReadOnlyMemory<byte> item in cases)
        {
            DomainResult<XtreamProviderPage<XtreamStreamInput>> result =
                XtreamProviderJsonParser.ParseLiveStreams(item);
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, result.Error!.Code);
        }
    }

    [TestMethod]
    public void DeclaredItemBudgetsRejectExcessiveLiveArrays()
    {
        string categories = string.Concat(
            "[",
            string.Join(',', Enumerable.Repeat("{}", XtreamProviderJsonParser.MaximumCategoryCount + 1)),
            "]");
        string streams = string.Concat(
            "[",
            string.Join(',', Enumerable.Repeat("{}", XtreamProviderJsonParser.MaximumStreamCount + 1)),
            "]");

        DomainResult<XtreamProviderPage<XtreamCategoryInput>> categoryResult =
            XtreamProviderJsonParser.ParseCategories(Utf8(categories));
        DomainResult<XtreamProviderPage<XtreamStreamInput>> streamResult =
            XtreamProviderJsonParser.ParseLiveStreams(Utf8(streams));

        Assert.IsFalse(categoryResult.IsSuccess);
        Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, categoryResult.Error!.Code);
        Assert.IsFalse(streamResult.IsSuccess);
        Assert.AreEqual(DomainErrorCode.UnsupportedPlaylistFormat, streamResult.Error!.Code);
    }

    [TestMethod]
    public void MaximumLiveStreamBudgetProducesAllTypedPlaybackKeys()
    {
        var document = new StringBuilder(XtreamProviderJsonParser.MaximumStreamCount * 48);
        document.Append('[');
        for (int index = 0; index < XtreamProviderJsonParser.MaximumStreamCount; index++)
        {
            if (index > 0)
            {
                document.Append(',');
            }

            document.Append("{\"stream_id\":")
                .Append(index + 1)
                .Append(",\"name\":\"Synthetic\"}");
        }

        document.Append(']');
        byte[] input = Encoding.UTF8.GetBytes(document.ToString());
        Assert.IsLessThan(HttpTransportLimits.MaximumAllowedResponseBytes, input.Length);

        DomainResult<XtreamProviderPage<XtreamStreamInput>> result =
            XtreamProviderJsonParser.ParseLiveStreams(input);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(XtreamProviderJsonParser.MaximumStreamCount, result.Value!.Items);
        Assert.AreEqual("1", result.Value.Items[0].ProviderPlaybackKey.Value);
        Assert.AreEqual(
            XtreamProviderJsonParser.MaximumStreamCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.Value.Items[^1].ProviderPlaybackKey.Value);
    }

    private static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
