# Domain ve veri contract'ları

**Durum:** M3 Windows contract implementation completed; M4 source-draft protected-create dilimi `IN PROGRESS`; kalıcı metadata/provider/parser davranışı specification

**Tarih:** 2026-08-09

**Kapsam:** Windows Live TV MVP terminology; gelecek kavramlar yalnız specification

## 1. Tasarım kuralları

Windows M3 implementation'ı bu belgedeki Live TV terminology'sinin ilk saf `net10.0` karşılığını içerir. Source adı 100, locator 4096, username 256 ve password 1024 Unicode scalar ile sınırlıdır. `SafeEndpoint`, opaque references, stable errors, source/snapshot/category/channel invariant'ları ve content-based catalog/HLS karar contract'ı test edilmiştir. M4'te source-draft girdisini validate edip typed protected-store create sonucundaki opaque reference'ı in-memory `ValidatedSourceDraft`'a bağlayan dar Application operation'ı eklenmiştir. Network, durable source metadata/configuration persistence, provider mapping, incremental parser ve database bu uygulama iddiasının dışındadır.

- Domain dili provider endpoint'ini veya player library'sini değil, ürünü anlatır.
- Provider payload'ı güvenilmeyen external contract'tır. Adapter sınırında çevrilir; doğrudan presentation veya persistence'e geçmez.
- Credential, cookie ve playback locator sıradan string değildir. Domain'de yalnız opaque reference bulunur; secret authorized operation'dan hemen önce güvenli sınırda çözülür.
- ID'ler app-owned, typed ve source-scoped'dur. Display name ve provider sırası identity değildir.
- Missing/malformed/provider-specific alan normal input durumudur. Normalization warning üretir; bilinmeyen bilgiyi uydurmaz.
- Production code yalnız MVP kavramlarını alır. Movie, Series ve EPG aşağıda ad tutarlılığı için tanımlıdır; M16 öncesi scaffold edilmez.

## 2. Önerilen terminology

| İstenen ad | Önerilen ad | Phase | Anlam |
|---|---|---|---|
| `IptvAccount` | `ContentSource` aggregate; secret alanda `XtreamCredentials` | MVP | Kullanıcının tanımladığı Xtream-compatible account veya remote playlist. URL-only source için “account” doğru değildir. |
| `Playlist` | `PlaylistSnapshot` | MVP | Başarılı bir source synchronization sonucunun hash/version/provenance içeren immutable görünümü. |
| `Category` | `ChannelCategory` | MVP | Live TV için source-scoped grouping. Generic Category belirsizdir. |
| `Channel` | `LiveChannel` | MVP | Normalize edilmiş linear live stream kaydı. |
| `Favorite` | `FavoriteChannel` | MVP | Source-scoped stable channel identity'ye bağlı kullanıcı tercihi. |
| `PlaybackHistory` | `RecentlyPlayedChannel` | Sonra | Bounded recency; Live TV'de completion progress anlamlı değildir. |
| `Movie` | `Movie` | Future spec | Bağımsız VOD title. |
| `Series` | `Series` | Future spec | Episodic VOD aggregate. |
| `Season` | `Season` | Future spec | Series grouping. |
| `Episode` | `Episode` | Future spec | Playable episodic item. |
| `EpgProgramme` | `EpgProgramme` | Future spec | Bir LiveChannel üzerindeki time-bounded programme. XMLTV terminology ile uyumlu British spelling korunur. |

`ContentSource` aggregate root'tur: display metadata ve synchronization lifecycle'ını sahiplenir; secret değerler platform-protected storage'da kalır. `PlaylistSnapshot` atomic refresh sağlar: query ya eski complete snapshot'ı ya yeni complete snapshot'ı görür, yarım import'u görmez.

## 3. MVP model taslakları

Bu tablolar language-neutral contract'tır; code-generation talimatı değildir.

### 3.1 ContentSource

| Alan | Type/constraint | Not |
|---|---|---|
| `id` | `SourceId` | App-generated opaque ID. |
| `displayName` | trimmed text, 1–100 char | User-visible; identity değil. |
| `kind` | `XtreamCompatible` veya `RemotePlaylist` | MVP closed set. |
| `safeEndpoint` | `SafeEndpoint` | Yalnız scheme + IDNA host + effective port; user-info/query/fragment yok. |
| `secretReference` | Source kind'a göre `SecretReference` veya `ProtectedLocatorReference` | Xtream full locator+username+password kaydına ya da remote-playlist full locator kaydına typed opaque referans; plaintext taşımaz. |
| `status` | `Draft / Testing / Syncing / Ready / Failed / Disabled / DeletionPending` | Transient progress yüzdesi persist edilmez. |
| `activeSnapshotId` | optional `SnapshotId` | Yalnız successful atomic import sonrası değişir. |
| `createdAt`, `updatedAt` | UTC instant | Contract boundary'de ISO 8601. |
| `lastSuccessfulSyncAt` | optional UTC instant | Failed partial import'tan türetilmez. |
| `lastErrorCode` | optional `SourceErrorCode` | Sanitized domain code; exception text yok. |

### 3.2 PlaylistSnapshot

| Alan | Type/constraint | Not |
|---|---|---|
| `id` | `SnapshotId` | App-generated. |
| `sourceId` | `SourceId` | Required parent. |
| `retrievedAt` | UTC instant | Response byte'ları tamamlandığında. |
| `contentHash` | SHA-256 digest | Streaming hesaplanır; cache equality için. Raw locator/hash loglanmaz. |
| `etag`, `lastModified` | optional text/instant | Untrusted ve length-bound HTTP validator. |
| `parserVersion`, `normalizationVersion`, `schemaVersion` | positive integer | Cache invalidation/migration. |
| `itemCount`, `warningCount` | non-negative integer | Operational count. |
| `state` | `Importing / Complete / Rejected` | Yalnız Complete active olabilir. |

### 3.3 ChannelCategory

| Alan | Type/constraint | Not |
|---|---|---|
| `id` | `CategoryId` | App-owned, source-scoped. |
| `snapshotId` | `SnapshotId` | Immutable snapshot membership. |
| `providerKey` | optional bounded text | Original provider key; global unique değil. |
| `normalizedName` | non-empty text | Unicode-normalized ve trimmed. |
| `sortOrder` | integer | Güvenilir provider order, yoksa deterministic import order. |
| `isSynthetic` | boolean | App-generated “Uncategorized” key. Display label localization ile gelir. |

### 3.4 LiveChannel

| Alan | Type/constraint | Not |
|---|---|---|
| `id` | `ChannelId` | Snapshot record ID. |
| `stableKey` | `ChannelStableKey` | Refresh boyunca favorite reconciliation; source-scoped. |
| `snapshotId` | `SnapshotId` | Required. |
| `categoryId` | `CategoryId` | Missing group synthetic category'ye map edilir. |
| `providerKey` | optional bounded text | Identity metadata; örneğin M3U `tvg-id`. Tek başına playback kaynağı değildir. |
| `providerPlaybackKey` | optional typed `ProviderItemKey` | Xtream-compatible item key; locator biçimi kabul etmez ve `ToString` değeri açmaz. |
| `name` | non-empty display text | Control removed, length-bound. |
| `number` | optional positive integer | Geçerliyse provider/M3U number. |
| `logoReference` | optional opaque cache/locator ref | UI raw remote URL görmez. |
| `streamReference` | optional `ProtectedLocatorReference` | Raw stream URL log/view state'e girmez. `providerPlaybackKey` ile tam olarak biri bulunur. |
| `containerHint` | optional enum | `Hls` veya `MpegTs` gibi hint; codec support kanıtı değildir. |
| `isAdultHint` | optional boolean | Provider hint; policy ayrı product decision. |
| `normalizationWarnings` | bounded flag set | Invalid number, missing group, duplicate provider ID gibi. |

### 3.5 FavoriteChannel

| Alan | Type/constraint | Not |
|---|---|---|
| `sourceId` | `SourceId` | Identity'nin parçası. |
| `channelStableKey` | `ChannelStableKey` | Identity'nin parçası. |
| `createdAt` | UTC instant | Required. |
| `lastKnownName` | bounded text | Display fallback; identity değil. |

Refresh favorite'ı eşleyemezse onu benzer isimli kanala sessizce bağlamak yerine bir retention window boyunca unresolved tutar.

## 4. Value object ve güven sınırları

| Value object | Çözdüğü problem/invariant |
|---|---|
| `SourceId`, `SnapshotId`, `CategoryId`, `ChannelId` | Opaque typed ID, aggregate'ler arası yanlış karşılaştırmayı engeller. |
| `ChannelStableKey` | Source-scoped deterministic identity; algorithm versioned. |
| `SafeEndpoint` | Yalnız normalized scheme, IDNA host ve port; diagnostics/UI için raw secret'tan ayrı. |
| `SecretReference` | Secret'ı reveal veya stringify edemeyen opaque lookup token. |
| `ProtectedLocatorReference` | Encrypted stream/logo locator'a opaque reference. |
| `ProviderItemKey` | Locator biçimini reddeden bounded provider playback identifier; M3U identity metadata'sından ayrıdır. |
| `ContentHash` | Cache equality için fixed-size validated digest. |
| `DomainError` | Stable code + retryability + resource key; M3 arbitrary context yüzeyi taşımaz, original exception infrastructure-only kalır. |
| `PlaybackCapability` | Protocol/container/codecs/tracks/player/OS/device sonucu; global support boolean'ı değil. |

Bu type'ların serialization ve string-format davranışı explicit olmalıdır. M3'te opaque reference ve stable error JSON contract'ları explicit'tir; aggregate, typed ID, `SafeEndpoint` ve stable-key için varsayılan `System.Text.Json` round-trip bir persistence contract'ı değildir ve M8 mapping kararı öncesi kullanılmaz. Secret ref veya untrusted display string diagnostic scope'a otomatik girmez.

### 4.1 M4 source-draft protected-create sınırı

`SourceDraftProtectionService`, public caller'dan gelen non-empty `SourceId` ve source-kind girdilerini store mutation'ından önce domain validation'dan geçirir. Xtream için full locator+username+password, remote playlist için full locator; strict UTF-8, versioned ve length-prefixed bounded byte payload'a encode edilir. Payload yalnız typed `ISecretStore.CreateCredentialsAsync` veya `CreateLocatorAsync(RemotePlaylistLocator)` call'ına verilir; store-issued reference ve onu üreten exact `SourceId` başarılı `ValidatedSourceDraft` sonucuna bağlanır. `ContentSource` bu draft'tan kimliği, adı ve configuration'ı birlikte alır; ayrı bir caller-supplied `SourceId` ile yeniden eşleme yüzeyi yoktur. Temporary byte buffer `finally` içinde best-effort sıfırlanır; store failure safe `StorageUnavailable` domain sonucuna map edilir.

Doğrudan await edilen başarılı çağrıda store commit'inden sonra cancellation yeniden gözlenmez ve issued reference caller'a sonuçla döner. Bu sınır; caller'ın sonucu gözlememesi/terk etmesi, retry veya aynı `SourceId` ile duplicate create, process crash/OOM, durable metadata persistence, update/delete ya da orphan reconciliation atomikliği sağlamaz. Payload implementation'ı şu an encode-only'dir; decoder/round-trip, unknown version, malformed length, trailing data ve migration compatibility contract'ı henüz yoktur. Source formu/UI wiring'i de uygulanmamıştır.

## 5. Provider adapter sınırı

Adapter gerçek provider variability'sini çözdüğü için gerekli abstraction'dır. Her entity için generic repository/interface oluşturulmaz.

### 5.1 Application-facing contract

Conceptual operations:

- `ValidateConfiguration`: yalnız local syntactic/security checks; network yok.
- `ProtectSourceDraft`: local validation sonrası typed protected create ve store-issued opaque reference'lı `ValidatedSourceDraft`; durable aggregate persistence değil.
- `ProbeConnection`: bounded, cancellable capability/auth probe.
- `OpenCatalogStream`: forward-only provider record/playlist byte stream + safe response metadata.
- `ResolvePlaybackSource`: seçilen `LiveChannel` için `SecretReference`'ı just-in-time çözer; lifetime tek playback attempt.

Her operation cancellation alır ve `DomainError` taşıyan discriminated result döndürür; raw provider exception dönmez. Retry application policy'sidir. Adapter insecure TLS veya alternate credential'a sessiz fallback yapmaz.

### 5.2 Xtream-compatible adapter

- “Xtream-compatible” compatibility family'dir; specification değildir.
- Endpoint path, auth status, booleans-as-string/number, missing category, duplicate stream ID ve extension farklı olabilir.
- Endpoint construction ve provider DTO adapter içinde kalır. Domain `player_api.php` veya provider JSON field adını bilmez.
- MVP yalnız account probe, Live category ve Live stream çağrılarını yapar; VOD/Series/EPG indirmez.
- Stable key için unique provider stream ID tercih edilir, source ile scope edilir ve collision tespit edilir.
- Contract tests sentetik local fake server kullanır; gerçek response/credential kaydedilmez.

### 5.3 Remote M3U adapter

- MVP yalnız explicit `https` remote playlist kabul eder. Credential-bearing `http` security hard gate'te reddedilir; anonymous HTTP ayrı ADR ister.
- Local file import gelecek file-picker flow'udur.
- `ResponseHeadersRead`, response/line/item budget ve cancellation kullanılır.
- `HEAD` tek connection test değildir; birçok media server bunu yanlış uygular. Bounded GET probe baseline'dır.
- Redirect manual ve sınırlıdır. Authentication/cookie/query credential farklı origin'e forward edilmez; downgrade reddedilir.
- Content sniffing Extended M3U channel catalog ile HLS master/media manifest'i ayırır. Valid HLS manifest, catalog satırları gibi parse edilmez; source display name ile tek `LiveChannel` içeren snapshot'a route edilir ve original manifest protected playback locator olur.

## 6. Incremental M3U pipeline

```text
bounded byte stream
  -> BOM/encoding detector
  -> incremental line tokenizer
  -> directive parser (#EXTM3U, #EXTINF, selected attributes)
  -> entry assembler (metadata + next playable locator)
  -> structural validator + scheme/security policy
  -> normalizer + stable-key builder
  -> protected-locator writer + batched SQLite staging
  -> snapshot validation
  -> atomic activation
```

Kurallar:

1. Download'ın tamamı parse için tek string yapılmaz.
2. UTF-8, UTF-8 BOM, CRLF/LF ve yalnız corpus kanıtıyla seçilecek legacy fallback test edilir; fallback warning üretir.
3. `#EXTINF` sonraki non-empty/non-comment locator ile eşleşir. Orphan metadata/locator malformed entry'dir.
4. En az `tvg-id`, `tvg-name`, `tvg-logo`, `group-title` ve common number hint tanınır. Attribute adı case-insensitive, value Unicode normalization sonrası korunur.
5. Unknown directive/attribute davranış için ignored, diagnostics için bounded count'tur; raw unbounded payload tutulmaz.
6. Text/line/attribute/item/total byte maksimumları vardır. NUL/control, oversized line veya budget breach policy'ye göre reject/quarantine olur.
7. Relative locator final authorized playlist URI'ına resolve edilir. `file`, `javascript`, `data`, `ftp`, `smb` ve unknown scheme reddedilir. Parser locator'ı identity metadata olan `providerKey`/`tvg-id` alanına map etmez.
8. Bilinen token key görünmese bile locator sensitive kabul edilir; persistence `ProtectedLocatorReference` kullanır.
9. Parse/staging bounded aralıklarla cancellation check eder. Cancellation active snapshot'ı değiştirmez.
10. HLS master/media manifest channel catalog değildir. Content sniffing onu single-stream source yoluna route eder; malformed veya desteklenmeyen manifest typed `UnsupportedPlaylistFormat` sonucu verir.

## 7. Normalization ve identity

### 7.1 Text

- Surrounding Unicode whitespace trim, control char removal ve Unicode NFC.
- Display casing korunur; search için ayrı culture-aware folded/indexed value.
- Empty channel name validation error'dır; “Channel 1” gibi uydurma ad üretilmez.
- Empty category source-scoped synthetic `Uncategorized` key'e gider; display label presentation tarafından localized edilir.

### 7.2 Category mapping

- Explicit unique provider category ID tercih edilir.
- M3U'da normalized `group-title`; blank ise synthetic category.
- Aynı display name fakat farklı provider ID internal olarak ayrı kalır; gelecekte explicit merge rule olmadan birleştirilmez.

### 7.3 Stable channel key algorithm v2

1. Non-empty ve source içinde unique provider stream ID: hash `sourceId + provider-kind + provider-id`.
2. Değilse non-empty unique `tvg-id`: hash `sourceId + m3u-tvg-id`.
3. Değilse: hash `sourceId + normalized-name + normalized-group + canonical-locator-fingerprint`.
4. Collision'da deterministic occurrence discriminator + warning.

Locator fingerprint secure boundary içinde hesaplanır ve loglanmaz. Volatile credential yalnız structure güvenle biliniyorsa fingerprint'ten çıkarılır; aksi halde full protected canonical locator katılır. Algorithm version persist edilir; migration favorite reconciliation ölçer.

V2, doğrulanmamış codename'i hash domain'inden çıkarıp ürün-adından bağımsız `CHANNEL-STABLE-KEY` domain'i kullanır. Bu pre-release değişiklik v1 geliştirme golden'ını bilinçli olarak geçersiz kılar; yayımlanmış kullanıcı verisi veya v1→v2 migration kabulü yoktur.

### 7.4 Duplicate

- Aynı stable input ile exact repeated entry snapshot içinde collapse edilir ve count tutulur.
- Aynı name, farklı provider ID/locator ayrı channel'dır.
- Conflicting locator'lı duplicate provider ID retained + collision warning; adapter sessiz seçim yapmaz.

## 8. Persistence contract

SQLite normalized query metadata ve snapshot state'i tutar. Secret material protected blob olarak opaque ID ile referans edilir; plaintext credential/locator kolon, index, WAL, migration log veya debug dump'ta bulunmaz.

Minimum logical tables:

- `content_sources`
- `playlist_snapshots`
- `channel_categories`
- `live_channels`
- `protected_locators`
- `favorite_channels`
- `schema_migrations`
- `sync_runs` — yalnız sanitized code/count/duration

Gerekli index'ler:

- source + active snapshot;
- snapshot + category + normalized sort/search;
- source + stable key;
- favorites by source.

Import bounded transaction batch'leriyle staging snapshot'a yazar. Count, foreign key, protected-reference ve integrity checks geçince tek transaction `activeSnapshotId` değiştirir. Eski snapshot activation critical path dışında prune edilir.

Cache key: `sourceId + contentHash + parserVersion + normalizationVersion + schemaVersion`. ETag/Last-Modified download'ı atlatabilir ama content validation yerine geçmez. Failed/cancelled import cache state'i ilerletmez.

## 9. Domain error taxonomy

| Code | Kullanıcı anlamı | Retry class |
|---|---|---|
| `NetworkUnreachable` | Sunucuya bağlanılamadı. | Manual veya bounded transient retry. |
| `AuthenticationRejected` | Kimlik bilgileri reddedildi. | Automatic retry yok. |
| `PlaylistDownloadFailed` | Playlist indirilemedi. | Status'a bağlı bounded retry. |
| `UnsupportedPlaylistFormat` | Playlist biçimi desteklenmiyor. | Input değişmeden retry yok. |
| `RequestTimedOut` | Bağlantı zaman aşımına uğradı. | Safe read için bounded retry. |
| `TlsValidationFailed` | Güvenli bağlantı doğrulanamadı. | Insecure fallback yok. |
| `InsecureTransportRejected` | Bu kaynak güvenli bağlantı kullanmıyor. | Policy değişmeden retry yok. |
| `PlaybackStartFailed` | Yayın başlatılamadı. | Capability/error'a göre bounded. |
| `StreamInterrupted` | Yayın kesildi. | Reconnect policy'ye girer. |
| `ReconnectExhausted` | Yeniden bağlanma başarısız oldu. | User action gerekir. |
| `StorageUnavailable` | Yerel veriye erişilemedi. | State safe ise retry. |
| `OperationCancelled` | İşlem iptal edildi. | Normal control flow; toast gerekmez. |

Result random operation ID, safe source ID ve stage taşır. UI'a raw URL, username, response body, untrusted media title, header veya exception stack taşımaz.

## 10. Platformlar arası paylaşım contract'ı

Player object veya UI view-model yerine specification paylaşılır:

- gerçekten interchange gereken yerde versioned JSON Schema/OpenAPI-like DTO;
- terminology ve error-code registry;
- M3U normalization rules ve golden synthetic fixtures;
- secret'ı commit öncesi değiştirilmiş provider contract fixtures;
- playback compatibility corpus manifest + expected capability result schema;
- migration/version rules.

Windows C# domain/parser kodu ileride başka .NET target'ta ancak ölçülmüş faydayla reuse edilir. Tizen TypeScript/JavaScript, Android Kotlin ve Apple Swift aynı versioned behavior'ı shared test vectors'a karşı uygular. Playback adapter her platforma özeldir.

## 11. Gelecek specification-only ilişkiler

```text
Series 1 --- * Season 1 --- * Episode
Movie  -------------------------- playable VOD asset
LiveChannel 1 --- * EpgProgramme
ContentSource 1 --- * (LiveChannel | Movie | Series)
```

- `Movie`, `Series`, `Season`, `Episode` ve `EpgProgramme` MVP table, migration, API veya source code'u değildir.
- VOD başlarsa `PlayableAsset` yalnız Movie/Episode davranışı gerçekten örtüşüyorsa çıkarılır.
- `EpgProgramme` source channel mapping, start/end UTC, title/description ve provenance içerir. Time-zone interpretation future XMLTV boundary işidir.
- `RecentlyPlayedChannel` ve richer history implementation'dan önce explicit retention/privacy kararı ister.

## 12. Açık doğrulama kalemleri

- **UNVERIFIED:** 50.000 M3U protected locator için DPAPI throughput. M4'te ölçülmeden exact layout kabul edilmez.
- **UNVERIFIED:** Legacy M3U encoding prevalence. Sentetik ve lawfully contributed sanitized corpus ile karar verilir.
- **UNVERIFIED:** Representative provider refresh'te stable-key survival. Favorite reconciliation oranı contract corpus'ta ölçülür.
- **UNVERIFIED:** Herhangi bir provider'ın cookie persistence gerektirip gerektirmediği. Default disabled; evidence ile isolated source handling.
- **UNVERIFIED:** Anonymous HTTP media compatibility'nin ürün için gerekli olup olmadığı. Credential-bearing HTTP baseline'da reddedilir.

## İlişkili belgeler

- [Architecture Report](ARCHITECTURE_REPORT.md)
- [ADR-003 — Persistence and secure storage](../adr/ADR-003-local-persistence-and-secure-storage.md)
- [Security Baseline](../security/SECURITY_AND_PRIVACY_BASELINE.md)
- [Quality Strategy](../quality/QUALITY_AND_PERFORMANCE_STRATEGY.md)
