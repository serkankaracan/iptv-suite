# Domain ve veri contract'ları

**Durum:** M3 Windows contract implementation completed; M4 source-draft protected-create dilimi `IN PROGRESS`; kalıcı metadata/provider/parser davranışı specification

**ADR-008 successor disposition (2026-08-29):** M3/M5/M7 HTTPS-only kabulleri tarihsel olarak korunur. Güncel policy, yalnız açık UI onaylı `RemotePlaylist` için query/token taşıyabilen HTTP locator'ı; aynı `SafeEndpoint` origin'indeki initial HTTP channel'ı ve URI/scheme validation'dan geçen HTTPS channel'ı kabul eder. Açık-onaylı HTTP source persistence'ı provider sırasındaki ilk 50.000 geçerli entry ile bounded kalır; tail doğrulanıp atlanır ve sonuç görünür uyarı taşır. HTTPS/direct 50.001 fail-closed kalır. Xtream HTTP, user-info, HTTP logo, cross-origin HTTP playlist redirect ve HTTPS→HTTP downgrade reddedilir. Native player alt-kaynak/redirect origin enforcement'ı ile successor hosted acceptance `UNVERIFIED`dır; native boşluk release blocker'dır.

**ADR-009 / post-MVP disposition (2026-08-29):** Yukarıdaki Xtream HTTP reddi M1–M16 tarihsel sınırıdır. M17'den itibaren Xtream-compatible HTTP, Remote M3U'dan ayrı exact source/configuration/origin consent'i ve kalıcı warning ile kabul edilmiştir; cleartext credential/API/playback ve native alt-kaynak/redirect riski release blocker'dır. Home/Source Manager/count foundation M17, `Movie` + seek-capable VOD M18, `Series`/`Season`/`Episode` M19 kapsamıdır. EPG hâlâ specification-only'dir. Xtream wire davranışı `UNVERIFIED / proprietary compatibility family`dir; [Post-MVP brief](../product/POST_MVP_CONTENT_EXPERIENCE_BRIEF.md), [ADR-009](../adr/ADR-009-xtream-cleartext-http-compatibility.md).

**Tarih:** 2026-08-09

**Kapsam:** Windows Live TV MVP terminology + M17–M19 post-MVP content contract; milestone başlamadan gelecek kavramlar yalnız specification

## 1. Tasarım kuralları

Windows M3 implementation'ı bu belgedeki Live TV terminology'sinin ilk saf `net10.0` karşılığını içerir. Source adı 100, locator 4096, username 256 ve password 1024 Unicode scalar ile sınırlıdır. `SafeEndpoint`, opaque references, stable errors, source/snapshot/category/channel invariant'ları ve content-based catalog/HLS karar contract'ı test edilmiştir. M4'te source-draft girdisini validate edip typed protected-store create sonucundaki opaque reference'ı in-memory `ValidatedSourceDraft`'a bağlayan dar Application operation'ı eklenmiştir. Network, durable source metadata/configuration persistence, provider mapping, incremental parser ve database bu uygulama iddiasının dışındadır.

- Domain dili provider endpoint'ini veya player library'sini değil, ürünü anlatır.
- Provider payload'ı güvenilmeyen external contract'tır. Adapter sınırında çevrilir; doğrudan presentation veya persistence'e geçmez.
- Credential, cookie ve playback locator sıradan string değildir. Domain'de yalnız opaque reference bulunur; secret authorized operation'dan hemen önce güvenli sınırda çözülür.
- ID'ler app-owned, typed ve source-scoped'dur. Display name ve provider sırası identity değildir.
- Missing/malformed/provider-specific alan normal input durumudur. Normalization warning üretir; bilinmeyen bilgiyi uydurmaz.
- Production code yalnız active milestone kavramlarını alır. M1–M16 Live TV tarihi geriye dönük genişletilmez; M17 `ContentSection`/counts/Source Manager foundation, M18 `Movie`, M19 `Series`/`Season`/`Episode` üretim kapsamını açar. EPG ayrı milestone'a kadar specification-only kalır.

## 2. Önerilen terminology

| İstenen ad | Önerilen ad | Phase | Anlam |
|---|---|---|---|
| `IptvAccount` | `ContentSource` aggregate; secret alanda `XtreamCredentials` | MVP | Kullanıcının tanımladığı Xtream-compatible account veya remote playlist. URL-only source için “account” doğru değildir. |
| `Playlist` | `PlaylistSnapshot` | MVP | Başarılı bir source synchronization sonucunun hash/version/provenance içeren immutable görünümü. |
| `Category` | `ChannelCategory` | MVP | Live TV için source-scoped grouping. Generic Category belirsizdir. |
| `Channel` | `LiveChannel` | MVP | Normalize edilmiş linear live stream kaydı. |
| `Favorite` | `FavoriteChannel` | MVP | Source-scoped stable channel identity'ye bağlı kullanıcı tercihi. |
| `PlaybackHistory` | `RecentlyPlayedChannel` | Sonra | Bounded recency; Live TV'de completion progress anlamlı değildir. |
| `ContentSection` | `ContentSection` | M17 | `LiveTv / Movies / Series` closed navigation/count ayrımı; provider path veya category değildir. |
| `ContentCounts` | `ContentCounts` | M17 | Source-scoped authoritative Live/Movie/Series kök sayıları ve onların toplamı. |
| `Movie` | `Movie` | M18 | Bağımsız, sonlu VOD title ve typed playback identity. |
| `Series` | `Series` | M19 | Episodic VOD aggregate; kendisi oynatılmaz. |
| `Season` | `Season` | M19 | Series içindeki source-scoped grouping. |
| `Episode` | `Episode` | M19 | Sonlu ve oynatılabilir episodic item. |
| `EpgProgramme` | `EpgProgramme` | Future spec | Bir LiveChannel üzerindeki time-bounded programme. XMLTV terminology ile uyumlu British spelling korunur. |

`ContentSource` aggregate root'tur: display metadata ve synchronization lifecycle'ını sahiplenir; secret değerler platform-protected storage'da kalır. `PlaylistSnapshot` atomic refresh sağlar: query ya eski complete snapshot'ı ya yeni complete snapshot'ı görür, yarım import'u görmez.

## 3. MVP model taslakları

Bu tablolar language-neutral contract'tır; code-generation talimatı değildir.

### 3.1 ContentSource

| Alan | Type/constraint | Not |
|---|---|---|
| `id` | `SourceId` | App-generated opaque ID. |
| `configurationId` | `SourceConfigurationId` | Her source configuration sürümü için app-generated semantic owner ID; protected credential/locator binding'inin parçası. |
| `displayName` | trimmed text, 1–100 char | User-visible; identity değil. |
| `kind` | `XtreamCompatible` veya `RemotePlaylist` | MVP closed set. |
| `safeEndpoint` | `SafeEndpoint` | Yalnız normalized `http`/`https` scheme + IDNA host + effective port; user-info/query/fragment yok. HTTP yalnız ADR-008 Remote M3U veya M17 ADR-009 Xtream exact-configuration policy'sinde geçerlidir; consent türleri ayrıdır. |
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
| `SourceId`, `SourceConfigurationId`, `SnapshotId`, `CategoryId`, `ChannelId` | Opaque typed ID, aggregate/configuration/channel sınırları arası yanlış karşılaştırmayı engeller. |
| `ChannelStableKey` | Source-scoped deterministic identity; algorithm versioned. |
| `SafeEndpoint` | Yalnız normalized `http`/`https` scheme, IDNA host ve effective port; diagnostics/UI için raw secret'tan ayrı. Scheme origin equality'nin parçasıdır. |
| `SecretReference` | Secret'ı reveal veya stringify edemeyen opaque lookup token. |
| `ProtectedLocatorReference` | Encrypted stream/logo locator'a opaque reference. |
| `ProviderItemKey` | Locator biçimini reddeden bounded provider playback identifier; M3U identity metadata'sından ayrıdır. |
| `ContentHash` | Cache equality için fixed-size validated digest. |
| `DomainError` | Stable code + retryability + resource key; M3 arbitrary context yüzeyi taşımaz, original exception infrastructure-only kalır. |
| `PlaybackCapability` | Protocol/container/codecs/tracks/player/OS/device sonucu; global support boolean'ı değil. |

Bu type'ların serialization ve string-format davranışı explicit olmalıdır. M3'te opaque reference ve stable error JSON contract'ları explicit'tir; aggregate, typed ID, `SafeEndpoint` ve stable-key için varsayılan `System.Text.Json` round-trip bir persistence contract'ı değildir ve M8 mapping kararı öncesi kullanılmaz. Secret ref veya untrusted display string diagnostic scope'a otomatik girmez.

### 4.1 M4 source-draft protected-create sınırı

`SourceDraftProtectionService`, public caller'dan gelen non-empty `SourceId` ve source-kind girdilerini store mutation'ından önce domain validation'dan geçirir. Xtream için full locator+username+password, remote playlist için full locator; strict UTF-8, versioned ve length-prefixed bounded byte payload'a encode edilir. Her yeni draft fresh `SourceConfigurationId` üretir; payload yalnız bu ID'den türeyen `ProtectedRecordOwner` ile typed `ISecretStore.CreateCredentialsAsync` veya `CreateLocatorAsync(RemotePlaylistLocator)` call'ına verilir. Store-issued reference, exact `SourceId` ve configuration owner ID başarılı `ValidatedSourceDraft` sonucuna birlikte bağlanır. `ContentSource` bu draft'tan kimliği, adı ve configuration'ı birlikte alır; ayrı caller-supplied source/configuration owner ile yeniden eşleme yüzeyi yoktur. Temporary byte buffer `finally` içinde best-effort sıfırlanır; store failure safe `StorageUnavailable` domain sonucuna map edilir.

Doğrudan await edilen başarılı çağrıda store commit'inden sonra cancellation yeniden gözlenmez ve issued reference caller'a sonuçla döner. Bu sınır; caller'ın sonucu gözlememesi/terk etmesi, retry veya aynı `SourceId` ile duplicate create, process crash/OOM, durable metadata persistence, update/delete ya da orphan reconciliation atomikliği sağlamaz. Store envelope v2; source, purpose, semantic owner kind/ID ve opaque reference kind/ID'yi entropy, filename digest ve encrypted envelope context'ine birlikte bağlar. Source credential/remote playlist owner'ı `SourceConfigurationId`, stream/logo locator owner'ı `ChannelId`dir; same-source/same-purpose cross-owner read/update fail-closed, yanlış-owner delete doğru kayda dokunmadan idempotent success'tir. Bu owner integrity context'idir, authorization principal değildir. Internal source-payload decoder v1 exact magic/version, pozitif big-endian length, exact total/no-trailing-data, strict UTF-8, control/whitespace ve Unicode-scalar sınırlarını doğrular; yalnız numeric slice layout döndürür, managed string/URI veya ikinci plaintext buffer üretmez. Unknown version, wrong-kind, bütün truncation noktaları, length overflow ve malformed UTF-8 local contract matrisinde fail-closed'dur. Decoder public resolve contract'ı değildir; decoded locator origin equality ve operation-lifetime politikası hâlâ M5/lifecycle gate'ini bekler. Source formu/UI wiring'i de uygulanmamıştır.

## 5. Provider adapter sınırı

Adapter gerçek provider variability'sini çözdüğü için gerekli abstraction'dır. Her entity için generic repository/interface oluşturulmaz.

### 5.1 Application-facing contract

Conceptual operations:

- `ValidateConfiguration`: yalnız local syntactic/security checks; network yok.
- `ProtectSourceDraft`: local validation sonrası typed protected create ve store-issued opaque reference'lı `ValidatedSourceDraft`; durable aggregate persistence değil.
- `ProbeConnection`: bounded, cancellable capability/auth probe.
- `OpenCatalogStream`: forward-only provider record/playlist byte stream + safe response metadata.
- `ResolvePlaybackSource`: seçilen `LiveChannel` için `SecretReference`'ı just-in-time çözer; lifetime tek playback attempt.
- `GetContentCounts`: M17 source-scoped authoritative Live/Movie/Series root counts; unknown/unavailable sayı uydurmaz.
- `OpenMovieCatalog`: M18 typed, paged/bounded Movie records; endpoint DTO/path'i dışarı vermez.
- `OpenSeriesCatalog` / `GetSeriesDetails`: M19 typed Series/Season/Episode graph; bounded depth/item ve deterministic ordering sonucu.
- `ResolveVodPlaybackSource`: yalnız typed `Movie` veya `Episode` için exact source/configuration/provider item binding'inden kısa ömürlü locator lease'i.

Her operation cancellation alır ve `DomainError` taşıyan discriminated result döndürür; raw provider exception dönmez. Retry application policy'sidir. Adapter insecure TLS veya alternate credential'a sessiz fallback yapmaz.

### 5.2 Xtream-compatible adapter

- “Xtream-compatible” compatibility family'dir; specification değildir.
- Endpoint path, auth status, booleans-as-string/number, missing category, duplicate stream ID ve extension farklı olabilir.
- Endpoint construction ve provider DTO adapter içinde kalır. Domain `player_api.php` veya provider JSON field adını bilmez.
- M1–M16 yalnız account probe, Live category ve Live stream çağrılarını yapar; bu tarihsel kabul VOD/Series içermez. M17 typed content counts/classification foundation'ını, M18 Movie, M19 Series/Season/Episode operations'ını ayrı ayrı açar; EPG indirilmez.
- Stable key için unique provider stream ID tercih edilir, source ile scope edilir ve collision tespit edilir.
- Contract tests sentetik local fake server kullanır; gerçek response/credential kaydedilmez.
- Content kind provider'ın typed operation'ından gelir. Display name, category, URL path veya extension heuristic'iyle `LiveChannel`/`Movie`/`Series` üretilmez; Remote M3U güvenilir type metadata vermiyorsa Live-only kalır.
- Endpoint/action/DTO/playback path'i resmi/normative standard olarak doğrulanmamıştır; versioned `UNVERIFIED / proprietary` compatibility profile'dır. Unknown variant safe error verir, cookie/header workaround registry'si oluşturmaz.
- HTTPS default'tur. M17 ADR-009 HTTP Xtream'i yalnız Remote M3U'dan ayrı exact `SourceId + SourceConfigurationId + SafeEndpoint` consent'iyle açar. Replace fresh owner/consent ister; redirect, HTTP image, cross-origin HTTP locator ve Authorization/Cookie/Referer fallback'i yasaktır.
- M17 Xtream account/category/top-level-list/selected-series-info response sınırları sırasıyla `64 KiB / 1 MiB / 64 MiB / 16 MiB`, request süresi 15 saniyedir. Xtream-only `64 MiB` factory tavanı genel HTTP'nin `4 MiB` sınırına taşınmaz. Response/decompressed bytes, JSON depth/string/array/item ve total catalog hiçbir durumda unbounded değildir.

### 5.3 Remote M3U adapter

- M3/M5/M7 tarihsel baseline'i yalnız explicit `https` remote playlist kabul eder. ADR-008 successor'ında HTTPS varsayılan kalır; scheme-specific açık UI onayıyla `http` Remote M3U locator query/token taşıyabilir, user-info taşıyamaz.
- Remote M3U network body HTTP/HTTPS ayrımı olmadan ayrı `128 MiB` decompressed-response ve iki dakikalık total request bütçesiyle stream edilir; genel HTTP `4 MiB` sözleşmesi değişmez ve Xtream'in ayrı endpoint tavanları bu factory'ye yayılmaz. Onboarding her iki scheme için preliminary full-body probe yapmaz; validation — HTTP için ayrıca açık onay — ve protected staging sonrasında tek bounded streaming import isteği kullanır. Yalnız açık `NotCommitted` sonuç exact staging kaydını siler.
- `#EXTM3U` token'ından whitespace ile ayrılan header öznitelikleri kabul edilir; bitişik lookalike token reddedilir. Incremental reader'ın fiziksel satır hard cap'i `65.536` UTF-16 kod birimidir. Bu cap semantik field cap'i değildir: locator ve generic metadata değeri en çok `4.096`, `tvg-id` `512`, channel/group name `256` kod birimidir; bu semantik sınırı aşan entry tekil olarak atlanır.
- Persistence en çok 50.000 geçerli entry kabul eder. Açık-onaylı HTTP source 50.001+ entry taşıdığında ilk 50.000 provider sırasıyla yazılır, tail sink'e yazılmadan bounded stream sonuna kadar doğrulanır, skipped count warning toplamına ve `EntryLimitReached` sonucuna bağlanır. HTTPS/direct parser 50.001'de fail-closed kalır. `ProcessedEntryCount == 0` katalog sonucu commit edilemez.
- Remote M3U failure contract'ı generic provider detail string'i taşımaz. Response-address, header, UTF-8, fiziksel satır (`PlaylistLineLimitExceeded`), decoded toplam (`PlaylistTotalLimitExceeded`), entry (`PlaylistEntryLimitExceeded`), structure, no-usable, URL/origin-policy ve HLS-manifest durumları append-only stable error code'lardır; localized UI bu kodları çözer. Raw locator, host, query, playlist satırı/body veya exception message sonuç/JSON/diagnostic yüzeyine girmez.
- Local file import gelecek file-picker flow'udur.
- `ResponseHeadersRead`, response/line/item budget ve cancellation kullanılır.
- `HEAD` tek connection test değildir; birçok media server bunu yanlış uygular. Bounded GET probe baseline'dır.
- HTTP request `Authorization`, `Cookie` veya `Referer` taşımaz. Full locator protected-at-rest kalır; cleartext locator/query, playlist response'u ve HTTP channel trafiği transit sırasında görünür/değiştirilebilir.
- Redirect manual ve sınırlıdır. Same-origin HTTP redirect kabul edilebilir; cross-origin HTTP redirect ve HTTPS→HTTP downgrade reddedilir. HTTP→HTTPS target yeniden doğrulanarak upgrade olabilir.
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
6. Fiziksel satır incremental olarak en çok `65.536` UTF-16 kod birimidir; daha uzun satır `PlaylistLineLimitExceeded` ile fail-closed olur. Locator/generic metadata için `4.096` ve mevcut daha dar ID/name/group semantik sınırları korunur. Decoded toplam `128 Mi` UTF-16 kod birimi, decompressed response `128 MiB` ve 50.000 entry bütçeleri bağımsızdır; toplam ve entry aşımı kendi typed kodunu üretir.
7. Relative locator final authorized playlist URI'ına resolve edilir. HTTP source'taki initial HTTP locator yalnız source'un exact scheme + IDNA host + effective port `SafeEndpoint` origin'iyle aynıysa; HTTPS locator initial URI/scheme validation'dan geçerse kabul edilir. HTTPS-source→HTTP, cross-origin initial HTTP, HTTP logo, `file`, `javascript`, `data`, `ftp`, `smb` ve unknown scheme reddedilir. Parser locator'ı identity metadata olan `providerKey`/`tvg-id` alanına map etmez. Bu admission native `MediaSource` sonrasındaki HLS/media alt-kaynak veya player redirect'ine same-origin/address enforcement sağlamaz.
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
| `RemoteResourceNotFound` | Uzak kaynak bulunamadı. | Automatic retry yok. |
| `RemoteRequestRejected` | Uzak sunucu isteği kalıcı olarak reddetti. | Input veya policy değişmeden retry yok. |
| `RequestRateLimited` | Uzak sunucu istek hızını sınırladı. | `Retry-After` sınırı içinde bounded retry. |
| `RemoteServiceUnavailable` | Uzak servis geçici olarak kullanılamıyor. | Bounded transient retry. |
| `RemoteResponseTooLarge` | Uzak yanıt güvenli boyut sınırını aştı. | Contract değişmeden retry yok. |
| `InsecureTransportRejected` | Bu kaynak güvenli bağlantı kullanmıyor. | Policy değişmeden retry yok. |
| `PlaybackStartFailed` | Yayın başlatılamadı. | Capability/error'a göre bounded. |
| `StreamInterrupted` | Yayın kesildi. | Reconnect policy'ye girer. |
| `PlaybackNetworkFailed` | Native oynatıcı güvenli enum ile ağ hatası bildirdi. | Bounded transient retry. |
| `PlaybackSourceUnsupported` | Native oynatıcı source/media türünü kullanamadı; exact codec nedeni kanıtlanmış sayılmaz. | Otomatik retry yok. |
| `PlaybackDecodingFailed` | Native decode başarısız oldu; eksik codec, uyumsuz encoding veya bozuk akış birbirinden kesin ayrılmaz. | Otomatik retry yok. |
| `ReconnectExhausted` | Yeniden bağlanma başarısız oldu. | User action gerekir. |
| `StorageUnavailable` | Yerel veriye erişilemedi. | State safe ise retry. |
| `OperationCancelled` | İşlem iptal edildi. | Normal control flow; toast gerekmez. |

Result random operation ID, safe source ID ve stage taşır. UI'a raw URL, username, response body, untrusted media title, header veya exception stack taşımaz.

Windows native playback adapter'ı yalnız `MediaPlayerFailedEventArgs.Error` içindeki
`NetworkError`, `SourceNotSupported` ve `DecodingError` enum değerlerini yukarıdaki
canonical kodlara çevirir. `ErrorMessage`, `ExtendedErrorCode`, HRESULT, URL veya provider
metni domain/presentation sınırını geçmez; `Unknown`, `Aborted`, source-open ve media-ended
olayları mevcut playback-phase fallback contract'ını korur. Connectivity hint yalnız açık
network allowlist'indeki canonical hatalarda gösterilir.

## 10. Platformlar arası paylaşım contract'ı

Player object veya UI view-model yerine specification paylaşılır:

- gerçekten interchange gereken yerde versioned JSON Schema/OpenAPI-like DTO;
- terminology ve error-code registry;
- M3U normalization rules ve golden synthetic fixtures;
- secret'ı commit öncesi değiştirilmiş provider contract fixtures;
- playback compatibility corpus manifest + expected capability result schema;
- migration/version rules.

Windows C# domain/parser kodu ileride başka .NET target'ta ancak ölçülmüş faydayla reuse edilir. Tizen TypeScript/JavaScript, Android Kotlin ve Apple Swift aynı versioned behavior'ı shared test vectors'a karşı uygular. Playback adapter her platforma özeldir.

## 11. Post-MVP içerik ilişkileri ve VOD playback contract'ı

```text
Series 1 --- * Season 1 --- * Episode
Movie  -------------------------- playable VOD asset
LiveChannel 1 --- * EpgProgramme
ContentSource 1 --- * (LiveChannel | Movie | Series)
```

- M17 `ContentSection` ve `ContentCounts` foundation'ını açar. `liveChannelCount`, `movieCount` ve `seriesCount` non-negative authoritative root count'tur; `totalContentCount` yalnız bu üçünün checked toplamıdır. Season/Episode total'a katılmaz. `All sources` source-scoped kayıtları toplar ve cross-source dedupe yapmaz.
- M18 `Movie`yi source/snapshot/category/stable-key/name/optional metadata + exactly-one typed provider playback reference olarak açar. Raw locator taşımaz.
- M19 `Series` → `Season` → `Episode` graph'ını açar. Series oynatılmaz; Episode exact source/snapshot/series/season identity ve typed provider playback reference taşır. Missing/duplicate provider order deterministic bounded normalization warning'idir; hierarchy uydurmak için isim/path heuristic'i kullanılmaz.
- M18/M19 tamamlanmadan ortak `PlayableAsset` abstraction'ı çıkarılmaz. Yalnız Movie/Episode davranışı ve lifecycle'ı gerçekten aynıysa en küçük ortak contract'a refactor edilir.
- `EpgProgramme` source channel mapping, start/end UTC, title/description ve provenance içerir. Time-zone interpretation future XMLTV boundary işidir.
- `RecentlyPlayedChannel`, continue-watching ve richer history implementation'dan önce explicit retention/privacy kararı ister; M17–M19 kapsamında değildir.

### 11.1 Source Manager old-or-new contract'ı

- Source list UI yalnız safe endpoint scheme/host/port, state, last-success ve counts gösterir; raw username/password/query/full locator göstermez.
- Rename yalnız display metadata mutation'ıdır.
- Endpoint/credential replace fresh `SourceConfigurationId` altında protected staging yapar. Validation/sync başarısız veya cancelled olursa eski active configuration/snapshot kalır; success'te configuration pointer ve content snapshot'ı atomik olarak yeni complete generation'a geçer, eski secret/consent idempotent cleanup'a girer.
- HTTP Xtream replace fresh ADR-009 consent ister; eski configuration consent'i devredilemez.
- Delete yeni admission'ı kapatır, in-flight catalog/playback'i drain eder, durable `DeletionPending` üzerinden secret + consent + bütün content graph + user-state/cache cleanup'ına yakınsar.

`IMPLEMENTED LOCALLY / ACCEPTANCE PENDING — 2026-08-29`: Configuration replace yeni source yaratmaz; mevcut `SourceId` korunur. Yeni configuration ile complete snapshot aynı SQLite activation transaction'ında görünür olur veya rollback ile eski configuration/snapshot birlikte kalır. Transaction, önceki opaque configuration tuple'ını `source_configuration_retirements` journal'ına da ekler; startup ve replace-sonrası reconciler exact owner/reference kaydını idempotent silip journal satırını kaldırır. Açıkça `NotCommitted` biten Remote M3U/Xtream importu staged kaydı siler; SQLite activation'a girdikten sonraki cancellation/fault `Indeterminate` kalır ve başarı gibi gösterilmez.

`UNVERIFIED / M17 ACCEPTANCE BLOCKER`: Protected-store create tamamlanıp SQLite activation/journal başlamadan process termination/OOM oluşursa yeni staged configuration kaydı authoritative DB referansı taşımadan kalabilir. Store enumeration olmadığı için mevcut retirement journal bu pre-activation kaydı restart'ta bulamaz. Crash-injection + restart orphan reconciliation bu aralığı kapatmadan “replace orphan bırakmaz” kriteri tamamlanmış sayılmaz.

### 11.2 VOD zaman ve seek contract'ı

`VodPlaybackTimeline` domain/provider DTO değildir; Application'ın current playback session capability görünümüdür:

| Alan | Kural |
|---|---|
| `sessionId`, `generation` | Her event/command exact current Movie/Episode session'a bağlanır. |
| `start` | Sabit `TimeSpan.Zero`; UI `00:00` gösterir. |
| `position` | Clamp edilmiş current `MediaPlaybackSession.Position`; bounded cadence ile yayımlanır. |
| `duration` | Pozitif ve finite `NaturalDuration` ise değer; aksi halde unknown. |
| `canSeek` | Yalnız typed Movie/Episode + native `CanSeek` + known duration + geçerli target/range. |
| `seekableRanges` | Varsa native range'lerin validated/bounded kopyası; yoksa known duration içinde target clamp. |
| `isUserScrubbing` | True iken background position event'i UI thumb'ını override etmez. |

- Seek command locator/time text değil, exact session/generation + bounded target taşır.
- `SeekCompleted`, position/duration/state callback'leri exact current context değilse düşer.
- `CanSeek == false`, unknown/zero duration veya boş range fake success üretmez; seek UI disabled/hidden olur.
- Live TV session'ı native duration/range bildirse bile timeshift/catch-up milestone'u olmadan seek-capable VOD sayılmaz.
- Microsoft API yüzeyleri `CanSeek`, `Position`, `NaturalDuration`, seekable ranges ve `SeekCompleted` sağlar; bu, provider medyasının gerçekten seek edilebilir olduğu garantisi değildir [S139][S140][S141].

## 12. Açık doğrulama kalemleri

- **VERIFIED / INFERENCE:** 50.000 protected locator için file-per-record DPAPI baseline ölçülmüş ve bulk channel-locator kullanımı için reddedilmiştir; test-only protected-catalog comparative `Decision` da tamamlanmıştır. Production same-SQLite-transaction yerleşimi ile parser + normalize + index end-to-end 50k kanıtı M8'de hâlâ `UNVERIFIED`dır.
- **UNVERIFIED:** Legacy M3U encoding prevalence. Sentetik ve lawfully contributed sanitized corpus ile karar verilir.
- **UNVERIFIED:** Representative provider refresh'te stable-key survival. Favorite reconciliation oranı contract corpus'ta ölçülür.
- **UNVERIFIED:** Herhangi bir provider'ın cookie persistence gerektirip gerektirmediği. Default disabled; evidence ile isolated source handling.
- **ACCEPTED ENGINEERING / UNVERIFIED RELEASE:** ADR-008, açık UI onaylı Remote M3U HTTP locator/query ve initial same-origin HTTP channel compatibility'sini dar kapsamda kabul eder. Cleartext transit ile native HLS/media alt-kaynak/player-redirect origin/address enforcement boşluğu yüksek residual'dır ve release blocker'dır; gerçek provider evreni, Store/privacy/hukuk ve successor hosted kabulü `UNVERIFIED`dır.
- **ACCEPTED PRODUCT/ENGINEERING / UNVERIFIED RELEASE:** ADR-009 HTTP Xtream'i M17 için ayrı exact-configuration consent ile açar. Username/password/API/playback cleartext riski, native subresource/redirect enforcement, Store/privacy/hukuk ve gerçek provider compatibility'si release blocker'dır.
- **UNVERIFIED:** Xtream Live/VOD/Series endpoint/action/DTO/path çeşitliliği, güvenilir item count ve Series graph davranışı; yalnız versioned sentetik contract corpus'u ile ölçülür.
- **UNVERIFIED:** Physical Windows device/provider tuple'larında Movie/Episode `CanSeek`/duration/range davranışı. M18/M19 capability matrix'i olmadan genel seek sözü verilmez.

## İlişkili belgeler

- [Architecture Report](ARCHITECTURE_REPORT.md)
- [ADR-008 — Remote M3U cleartext HTTP compatibility](../adr/ADR-008-remote-m3u-cleartext-http-compatibility.md)
- [ADR-009 — Xtream cleartext HTTP compatibility](../adr/ADR-009-xtream-cleartext-http-compatibility.md)
- [Post-MVP Content Experience Product Brief](../product/POST_MVP_CONTENT_EXPERIENCE_BRIEF.md)
- [ADR-003 — Persistence and secure storage](../adr/ADR-003-local-persistence-and-secure-storage.md)
- [Security Baseline](../security/SECURITY_AND_PRIVACY_BASELINE.md)
- [Quality Strategy](../quality/QUALITY_AND_PERFORMANCE_STRATEGY.md)
