using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using IptvSuite.Application;
using IptvSuite.Domain;
using IptvSuite.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IptvSuite.IntegrationTests;

[TestClass]
public sealed class XtreamProviderClientTests
{
    [TestMethod]
    public async Task ProtectedCredentialsProduceOnlyAccountAndLiveCatalogRequests()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new ScriptedTransport(
            """{"user_info":{"auth":1}}""",
            """[{"category_id":"1","category_name":"News"}]""",
            """[{"stream_id":"7","name":"Synthetic","category_id":"1","direct_source":"https://ignored.invalid"}]""");
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamLiveCatalog> result = await client.LoadLiveCatalogAsync(source);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Value!.Categories.Items);
        Assert.HasCount(1, result.Value.Streams.Items);
        CollectionAssert.AreEqual(
            new[] { string.Empty, "get_live_categories", "get_live_streams" },
            transport.Actions);
        Assert.IsTrue(transport.AllRequestsUsedHttps);
        Assert.IsTrue(transport.AllRequestsUsedPlayerApi);
        Assert.IsTrue(transport.AllRequestsContainedEncodedSyntheticCredentials);
        Assert.AreEqual("[XTREAM-LIVE-CATALOG]", result.Value.ToString());
    }

    [TestMethod]
    public async Task BodyLevelAuthenticationFailureStopsBeforeLiveEndpoints()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new ScriptedTransport("""{"user_info":{"auth":"0"}}""");
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamLiveCatalog> result = await client.LoadLiveCatalogAsync(source);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.AuthenticationRejected, result.Error!.Code);
        CollectionAssert.AreEqual(new[] { string.Empty }, transport.Actions);
    }

    [TestMethod]
    public async Task HttpAuthenticationFailureMapsSafelyAndStopsBeforeParsing()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var transport = new ScriptedTransport(HttpTransportResult.Failed(
            HttpTransportFailure.AuthenticationRejected,
            HttpTransportRetryability.Never,
            401));
        var client = new XtreamProviderClient(store, transport);

        DomainResult<XtreamLiveCatalog> result = await client.LoadLiveCatalogAsync(source);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DomainErrorCode.AuthenticationRejected, result.Error!.Code);
        Assert.AreEqual("[DOMAIN-RESULT:AuthenticationRejected]", result.ToString());
    }

    [TestMethod]
    public async Task CancellationIsPreservedAndCredentialLeaseIsZeroed()
    {
        using var store = new CredentialMemoryStore();
        ContentSource source = await CreateSourceAsync(store);
        var client = new XtreamProviderClient(store, new CancellingTransport());

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await client.LoadLiveCatalogAsync(source));
        Assert.IsFalse(store.LastIssuedLeaseMemory.IsEmpty);
        Assert.IsTrue(store.LastIssuedLeaseMemory.Span.IndexOfAnyExcept((byte)0) < 0);
    }

    private static async Task<ContentSource> CreateSourceAsync(CredentialMemoryStore store)
    {
        SourceId sourceId = SourceId.Generate();
        var protection = new SourceDraftProtectionService(store);
        DomainResult<ValidatedSourceDraft> draft = await protection.ProtectXtreamAsync(
            sourceId,
            "Synthetic source",
            "https://fixture.invalid/provider",
            "synthetic-user",
            "synthetic password");
        Assert.IsTrue(draft.IsSuccess);
        DateTimeOffset now = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
        DomainResult<ContentSource> source = ContentSource.Create(
            draft.Value,
            ContentSourceStatus.Testing,
            now,
            now);
        Assert.IsTrue(source.IsSuccess);
        return source.Value!;
    }

    private sealed class ScriptedTransport : IHttpTransport
    {
        private static readonly PropertyInfo RequestUriProperty = typeof(HttpTransportRequest)
            .GetProperty("RequestUri", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly Queue<HttpTransportResult> _responses;

        internal ScriptedTransport(params string[] bodies)
            : this(bodies.Select(body => HttpTransportResult.Success(
                200,
                HttpResponseLease.CopyFrom(Encoding.UTF8.GetBytes(body)))).ToArray())
        {
        }

        internal ScriptedTransport(params HttpTransportResult[] responses)
        {
            _responses = new Queue<HttpTransportResult>(responses);
        }

        internal List<string> Actions { get; } = [];

        internal bool AllRequestsUsedHttps { get; private set; } = true;

        internal bool AllRequestsUsedPlayerApi { get; private set; } = true;

        internal bool AllRequestsContainedEncodedSyntheticCredentials { get; private set; } = true;

        public ValueTask<HttpTransportResult> GetAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri = (Uri)RequestUriProperty.GetValue(request)!;
            AllRequestsUsedHttps &= uri.Scheme == Uri.UriSchemeHttps;
            AllRequestsUsedPlayerApi &= uri.AbsolutePath == "/provider/player_api.php";
            Dictionary<string, string> query = ParseQuery(uri.Query);
            Actions.Add(query.TryGetValue("action", out string? action) ? action : string.Empty);
            AllRequestsContainedEncodedSyntheticCredentials &=
                query.GetValueOrDefault("username") == "synthetic-user" &&
                query.GetValueOrDefault("password") == "synthetic password";
            return ValueTask.FromResult(_responses.Dequeue());
        }

        private static Dictionary<string, string> ParseQuery(string query) => query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair.Length == 2 ? pair[1] : string.Empty),
                StringComparer.Ordinal);
    }

    private sealed class CancellingTransport : IHttpTransport
    {
        public ValueTask<HttpTransportResult> GetAsync(
            HttpTransportRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<HttpTransportResult>(new OperationCanceledException(cancellationToken));
    }

    private sealed class CredentialMemoryStore : ISecretStore, IDisposable
    {
        private byte[]? _payload;
        private SecretReference? _reference;

        internal ReadOnlyMemory<byte> LastIssuedLeaseMemory { get; private set; }

        public ValueTask<SecretReferenceCreationResult> CreateCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _payload = value.ToArray();
            _reference = SecretReference.Parse($"secret-ref-v1:{Guid.NewGuid():N}").Value!;
            return ValueTask.FromResult(SecretReferenceCreationResult.Succeeded(_reference));
        }

        public ValueTask<SecretStoreReadResult> ReadCredentialsAsync(
            SourceId sourceId,
            ProtectedRecordOwner owner,
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_payload is null || !reference.Equals(_reference))
            {
                return ValueTask.FromResult(SecretStoreReadResult.Failed(
                    SecretStoreFailure.ProtectedRecordUnavailable));
            }

            SecretLease lease = SecretLease.CopyFrom(_payload);
            LastIssuedLeaseMemory = lease.Value;
            return ValueTask.FromResult(SecretStoreReadResult.Succeeded(lease));
        }

        public void Dispose()
        {
            if (_payload is not null)
            {
                CryptographicOperations.ZeroMemory(_payload);
                _payload = null;
            }
        }

        public ValueTask<ProtectedLocatorReferenceCreationResult> CreateLocatorAsync(
            SourceId sourceId, ProtectedValuePurpose purpose, ProtectedRecordOwner owner,
            ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreReadResult> ReadLocatorAsync(
            SourceId sourceId, ProtectedValuePurpose purpose, ProtectedRecordOwner owner,
            ProtectedLocatorReference reference, CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreOperationResult> UpdateCredentialsAsync(
            SourceId sourceId, ProtectedRecordOwner owner, SecretReference reference,
            ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreOperationResult> UpdateLocatorAsync(
            SourceId sourceId, ProtectedValuePurpose purpose, ProtectedRecordOwner owner,
            ProtectedLocatorReference reference, ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreOperationResult> DeleteCredentialsAsync(
            SourceId sourceId, ProtectedRecordOwner owner, SecretReference reference,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<SecretStoreOperationResult> DeleteLocatorAsync(
            SourceId sourceId, ProtectedValuePurpose purpose, ProtectedRecordOwner owner,
            ProtectedLocatorReference reference, CancellationToken cancellationToken = default) => throw Unused();

        private static InvalidOperationException Unused() =>
            new("The test does not permit this secret-store operation.");
    }
}
