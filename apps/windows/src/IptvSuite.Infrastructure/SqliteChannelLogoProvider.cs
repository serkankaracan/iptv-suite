using System.Text;
using System.Runtime.Versioning;
using IptvSuite.Application;
using IptvSuite.Domain;
using Microsoft.Data.Sqlite;

namespace IptvSuite.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class SqliteChannelLogoProvider : IChannelLogoProvider
{
    public const int MaximumLogoBytes = 512 * 1024;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static readonly byte[] JpegSignature = [0xff, 0xd8, 0xff];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _databasePath;
    private readonly SqliteCatalogDatabase _database;
    private readonly IHttpTransport _transport;

    public SqliteChannelLogoProvider(string databasePath, IHttpTransport transport)
    {
        _database = new SqliteCatalogDatabase(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async ValueTask<ChannelLogoImage?> LoadAsync(SourceId sourceId, ChannelId channelId, CancellationToken cancellationToken = default)
    {
        if (sourceId.IsEmpty || channelId.IsEmpty) throw new ArgumentException("Exact source and channel identifiers are required.");
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        LogoBinding? binding = await ReadBindingAsync(sourceId, channelId, cancellationToken).ConfigureAwait(false);
        if (binding is null) return null;
        var reader = new SqliteCatalogLocatorReader(_databasePath);
        CatalogLocatorReadResult locator = await reader.ReadAsync(sourceId, channelId, ProtectedValuePurpose.ChannelLogoLocator, binding.Reference, cancellationToken).ConfigureAwait(false);
        if (!locator.IsSuccess) return null;
        using SecretLease lease = locator.Lease!;
        string text;
        try { text = StrictUtf8.GetString(lease.Value.Span); }
        catch (DecoderFallbackException) { return null; }
        DomainResult<PreparedRemotePlaylistSourceDraft> prepared = SourceConfigurationValidator.PrepareRemotePlaylist("Channel logo", text);
        if (!prepared.IsSuccess || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return null;
        SafeEndpoint endpoint = prepared.Value!.SafeEndpoint;
        if (!string.Equals(endpoint.Scheme, binding.Scheme, StringComparison.Ordinal) ||
            !string.Equals(endpoint.Host, binding.Host, StringComparison.Ordinal) ||
            endpoint.Port != binding.Port) return null;
        using HttpTransportRequest request = HttpTransportRequest.Create(uri, endpoint, MaximumLogoBytes);
        HttpTransportResult response = await _transport.GetAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess) return null;
        using HttpResponseLease responseLease = response.Response!;
        return responseLease.Content.Length is > 0 and <= MaximumLogoBytes &&
               TryIdentify(responseLease.Content.Span, out ChannelLogoFormat format)
            ? new ChannelLogoImage(responseLease.Content.ToArray(), format)
            : null;
    }

    private async ValueTask<LogoBinding?> ReadBindingAsync(SourceId sourceId, ChannelId channelId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.logo_reference, s.endpoint_scheme, s.endpoint_host, s.endpoint_port FROM channels c
            JOIN sources s ON s.active_snapshot_id = c.snapshot_id
            WHERE s.source_id = $source
              AND s.status = $ready
              AND c.channel_id = $channel
              AND c.logo_reference IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$source", sourceId.Value.ToString("N"));
        command.Parameters.AddWithValue("$ready", (int)ContentSourceStatus.Ready);
        command.Parameters.AddWithValue("$channel", channelId.Value.ToString("N"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3)) return null;
        string value = reader.GetString(0);
        DomainResult<ProtectedLocatorReference> parsed = ProtectedLocatorReference.Parse(value);
        return parsed.IsSuccess ? new LogoBinding(parsed.Value!, reader.GetString(1), reader.GetString(2), reader.GetInt32(3)) : null;
    }

    private static bool TryIdentify(ReadOnlySpan<byte> content, out ChannelLogoFormat format)
    {
        if (content.Length >= 8 && content[..8].SequenceEqual(PngSignature)) { format = ChannelLogoFormat.Png; return true; }
        if (content.Length >= 3 && content[..3].SequenceEqual(JpegSignature)) { format = ChannelLogoFormat.Jpeg; return true; }
        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content.Slice(8, 4).SequenceEqual("WEBP"u8)) { format = ChannelLogoFormat.WebP; return true; }
        format = default;
        return false;
    }

    private sealed record LogoBinding(ProtectedLocatorReference Reference, string Scheme, string Host, int Port);
}
