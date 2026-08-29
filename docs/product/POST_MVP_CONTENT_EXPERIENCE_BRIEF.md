# IPTV Suite — Post-MVP içerik deneyimi Product Brief

**Belge durumu:** `APPROVED SCOPE / IMPLEMENTATION AND ACCEPTANCE PENDING`

**Tarih:** 2026-08-29

**Ad durumu:** “IPTV Suite” yalnızca doğrulanmamış iç codename'dir.

**Milestone bağı:** M17–M19. Bu belge M1–M16'nın tarihsel durumunu değiştirmez; ADR-008 sonrasındaki M15/M16 successor ledger'ları yeni hosted kabul üretilene kadar stale kalır.

## Karar özeti

Kullanıcı 2026-08-29 tarihinde aşağıdaki post-MVP yönünü açıkça onaylamıştır:

- Ana içerik ekranı; **Live TV**, **Movies** ve **Series** hedeflerini birbirinden ayıran bir Home hub olur.
- Home, seçili kaynak ve “All sources” bağlamında Live TV, Movie, Series ve toplam içerik sayılarını gösterir.
- Playlist/account ekleme ve yönetme, katalog/oynatıcı ekranında inline form veya overlay olarak gösterilmez; ayrı bir `SourceManagerPage` ve gerekirse ayrı `SourceEditorPage`/window kullanır.
- Kullanıcı kaynakları oluşturabilir, görebilir, yeniden adlandırabilir, endpoint/credential yapılandırmasını güvenli biçimde değiştirebilir, yenileyebilir ve silebilir.
- Movie ve Episode oynatımında, medya gerçekten seek edilebiliyorsa ileri/geri sarma sağlayan seekbar ile başlangıç, mevcut konum ve toplam/bitiş zamanı gösterilir.
- HTTPS varsayılan kalır. Yetkili fakat yalnız HTTP sunan Xtream-compatible hesap, Remote M3U onayından **ayrı** cleartext risk onayıyla eklenebilir. Bu karar [ADR-009](../adr/ADR-009-xtream-cleartext-http-compatibility.md) ile sınırlandırılır.

Bu kayıt ürün yönü ve mühendislik kapsamıdır; gerçek provider uyumluluğu, Microsoft Store kabulü, hukuki kabul veya release evidence değildir.

## Kullanıcı problemi

Mevcut Live TV ekranı kaynak ekleme formu, katalog, filtreler ve player'ı aynı yüzeyde toplar. Bu düzen:

- uygulamanın ana girişini ve içerik türlerini anlaşılmaz kılar;
- kaynak ekleme/düzenleme/silme işlerini katalog gezintisiyle karıştırır;
- generic M3U içindeki Live/VOD/Series benzeri kayıtları güvenilir bir içerik modeli sanma riskini doğurur;
- sonlu Movie/Episode medyasında süre ve seek kontrolü sağlamaz.

Yeni deneyim bu sorunu navigation, domain ve playback capability sınırlarını ayrı tutarak çözer.

## Bilgi mimarisi

### Home

`HomePage`, uygulama açılışındaki ürün hub'ıdır. En az şu hedefleri gösterir:

| Hedef | Sayı | Davranış |
|---|---|---|
| Live TV | `LiveChannelCount` | `LiveTvPage` açılır. |
| Movies | `MovieCount` | `MoviesPage` açılır. |
| Series | `SeriesCount` | `SeriesPage` açılır. |
| Total | `LiveChannelCount + MovieCount + SeriesCount` | Bilgi amaçlıdır; Episode sayısı ayrıca toplam içine katılmaz. |
| Sources | kaynak sayısı ve durum özeti | `SourceManagerPage` açılır. |

Sayı sözleşmesi:

- “Channel count” yalnız Live TV kayıtlarının sayısıdır.
- “Total content” Live TV + Movie + Series kök kayıtlarının toplamıdır; season ve episode'ları yeniden saymaz.
- “All sources” görünümünde kayıtlar source-scoped sayılır; farklı kaynaklardaki benzer adlar sessizce deduplicate edilmez.
- Yalnız latest active complete snapshot authoritative'dir. Sync sırasında son bilinen sayı `stale` etiketiyle gösterilebilir; hiç başarılı snapshot yoksa sayı uydurulmaz ve `Unavailable` gösterilir.
- Remote M3U, güvenilir type metadata vermiyorsa Movie/Series sınıflandırması üretmez. İsim, category, URL path veya dosya uzantısından Movie/Series tahmini yapılmaz.

### İçerik sayfaları

- `LiveTvPage`: mevcut Live TV category/search/page ve linear playback deneyimini taşır.
- `MoviesPage`: source/category/search/page ile bağımsız Movie kataloğu; seçim Movie detayına veya doğrudan VOD playback'e gider.
- `SeriesPage`: source/category/search/page ile Series kökleri; `SeriesDetailPage` season ve episode'ları gösterir.
- Sayfalar aynı playback coordinator'ını kullanabilir; UI state ve query state birbirine sızmaz.

### Kaynak yönetimi

`SourceManagerPage`, Home/Live/Movies/Series/player sayfalarından ayrı bir navigation destination'dır. Aynı shell window kullanılırsa ayrı `Page` zorunludur; kaynak formu katalog/player üzerine bindirilmez. Create/edit akışı yalnız Source Manager bağlamındaki ayrı `SourceEditorPage`, dialog veya ayrı window içinde olabilir.

| İşlem | Product contract |
|---|---|
| List | Display name, source kind, safe scheme/host/port, sync state, last successful sync, içerik sayıları ve HTTP warning gösterilir; raw query/username/password gösterilmez. |
| Create | Local validation → gerekli risk onayları → protected staging → bounded probe/sync → durable commit sırası kullanılır. |
| Rename | Yalnız display metadata değişir; secret veya stable source identity değişmez. |
| Replace configuration | Yeni endpoint/credential fresh configuration owner altında stage edilir. Validation/sync başarısızsa eski çalışan configuration ve active snapshot korunur. Başarıda atomik swap ve eski secret cleanup yapılır. HTTP Xtream için fresh consent gerekir. |
| Refresh | Mevcut protected configuration kısa ömürlü lease ile çözülür; başarısız/cancelled refresh eski active snapshot'ı değiştirmez. |
| Delete | Açık confirmation sonrasında admission kapanır; playback/import drain edilir; secret, bütün content snapshot'ları, user-state ve owned cache idempotent reconciliation ile kaldırılır. |

Kaynak adı veya credential değiştirmek “yeni source” gibi görünmemelidir; fakat güvenlik owner'ı ve atomic replacement kuralları korunur. Kısmi CRUD başarısı sessiz bırakılmaz; `DeletionPending` veya safe failure state görünür olur.

## İçerik modeli

- `LiveChannel`: linear canlı akış; default olarak sonlu duration veya seek sözü vermez.
- `Movie`: bağımsız, sonlu VOD title ve bir typed playback reference.
- `Series`: oynatılabilir locator taşımaz; season/episode aggregate root'udur.
- `Season`: bir Series içindeki source-scoped grouping'dir.
- `Episode`: sonlu VOD item ve typed playback reference taşır.

Xtream-compatible endpoint/path/JSON alanları domain değildir. Adapter bunları versioned, bounded `LiveChannel`, `Movie`, `Series`, `Season` ve `Episode` sonuçlarına çevirir. Xtream endpoint davranışı public/formal bir standart olarak doğrulanmamıştır; `UNVERIFIED / proprietary compatibility family` sayılır. Gerçek provider response'u veya credential repository fixture'ına alınmaz.

## VOD playback ve seek deneyimi

Seekbar yalnız typed `Movie` veya `Episode` session'ında ve player capability'si izin verdiğinde etkindir:

1. Media açıldıktan sonra `MediaPlaybackSession.CanSeek == true` olmalıdır.
2. `NaturalDuration` pozitif ve sonlu olmalıdır; varsa `GetSeekableRanges()` sonucu hedefi ayrıca sınırlar.
3. Sol etiket başlangıç `00:00`, hareket eden etiket current `Position`, sağ etiket total/end `NaturalDuration` gösterir.
4. Slider `0..NaturalDuration` aralığındadır. Kullanıcı sürüklerken player position update'i thumb'ı geri çekmez; commit edilen hedef seekable aralığa clamp edilir.
5. `SeekCompleted`, stale session/generation kontrolünden sonra UI'yı gerçek position ile uzlaştırır. Source/session değişmişse eski event yeni medya durumunu değiştiremez.
6. Position gösterimi bounded cadence ile güncellenir; yüksek frekanslı `Position` binding'i UI thread'e yük bindirecek biçimde kullanılmaz.
7. Duration bilinmiyorsa veya `CanSeek == false` ise seek disabled/hidden olur; sahte duration ya da seek başarı durumu gösterilmez.

Microsoft'un media playback rehberi built-in transport controls'ün seeking/progress desteği verdiğini; `MediaPlaybackSession` ise `CanSeek`, `Position`, `NaturalDuration`, seekable ranges ve `SeekCompleted` yüzeylerini sağlar [S139][S140][S141]. Bu API varlığı belirli bir provider stream'inin seek edilebilir olduğunu kanıtlamaz.

Live TV'de timeshift/catch-up ayrıca uygulanmadıkça seekbar kapalıdır ve UI `Live` durumu gösterir. Live stream'in geçici duration/seekable-range raporlaması VOD sınıflandırması değildir.

## HTTP Xtream kullanıcı deneyimi

HTTP Xtream izni Remote M3U checkbox'ı değildir:

- Kaynak türü `XtreamCompatible` ve scheme `http` ise, ilk ağ isteğinden önce Xtream'e özgü ayrı onay gerekir.
- Metin username/password ile API catalog ve playback request'lerinin ağda şifrelenmeden okunabileceğini veya değiştirilebileceğini açıkça söyler.
- Onay exact source configuration'a bağlanır; endpoint veya credential replace işleminde yeniden alınır.
- Kaynak var olduğu sürece Source Manager ve ilgili content bağlamında kalıcı `Cleartext HTTP` warning görünür.
- İptal veya onaysız durum hiçbir probe/catalog/playback request'i üretmez.
- HTTPS'e geçiş onayı gerektirmez; TLS doğrulaması hiçbir durumda kapatılmaz.

At-rest DPAPI/AES koruması cleartext transit güvenliği sağlamaz. Public release, Store/privacy/legal ve native player alt-kaynak/redirect kontrolü ayrı hard gate'tir.

## Milestone sırası

1. **M17:** Home hub, authoritative counts, ayrı Source Manager CRUD ve HTTP Xtream admission/foundation.
2. **M18:** Movie catalog/detail/playback, VOD seekbar ve zaman göstergeleri.
3. **M19:** Series → Season → Episode navigation ve Episode playback/seek.

M17 gerekli content-kind/count temelini kurar; M18 başlamadan Movie playback type'ı, M19 başlamadan Series/Season/Episode production graph'ı eklenmez.

## Kapsam dışı

- EPG/XMLTV, catch-up, Live TV timeshift ve recording.
- Download/offline copy, DRM veya erişim kontrolü aşma.
- Multi-screen, PVR scheduler, cloud sync/backend, analytics ve reklam.
- Generic M3U için URL/category/name heuristic'iyle Movie/Series ayrımı.
- Cross-source content deduplication.
- Persisted continue-watching/watch-history; retention/privacy kararı verilmeden eklenmez.
- Provider-specific cookie/header workaround registry'si.

## Başarı ölçütleri

- Home ve dört sayı sentetik authoritative snapshot ile deterministik ve source-scoped sonuç verir.
- Kaynak ekleme/düzenleme ekranı katalog/player sayfasında görünmez; keyboard/UIA navigation ile erişilebilir ayrı destination'dır.
- Create/replace/delete CRUD fault/cancel/restart testleri old-or-new sonucu verir ve secret/orphan bırakmaz.
- HTTP Xtream ağ çağrısı ayrı explicit consent olmadan çıkmaz; warning restart sonrasında kaybolmaz; raw credential/URL hiçbir UI/log/artifact'ta görünmez.
- Movie/Episode finite sentetik corpus'unda start/current/end değerleri doğru, seek ileri/geri çalışır ve stale seek event'i yeni session'ı değiştiremez.
- Live TV session'ında timeshift yoksa seek kapalıdır.
- Xtream variant, hostile payload, response/item/depth/size budget, source deletion ve canary testleri geçer.
- Exact SDK locked build, deterministic quality gate, signed package journey ve fiziksel Windows playback/accessibility kontrolleri milestone evidence'ına bağlanır.

## İlişkili kararlar

- [ADR-008 — Remote M3U cleartext HTTP compatibility](../adr/ADR-008-remote-m3u-cleartext-http-compatibility.md)
- [ADR-009 — Xtream cleartext HTTP compatibility](../adr/ADR-009-xtream-cleartext-http-compatibility.md)
- [Windows roadmap M17–M19](../roadmap/WINDOWS_MVP_ROADMAP.md)
- [Domain and Data Contracts](../architecture/DOMAIN_AND_DATA_CONTRACTS.md)
- [Security and Privacy Baseline](../security/SECURITY_AND_PRIVACY_BASELINE.md)
