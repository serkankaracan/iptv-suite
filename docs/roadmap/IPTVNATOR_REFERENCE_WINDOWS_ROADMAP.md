# IPTVnator referanslı Windows özellik genişleme yol haritası

**Belge durumu:** `APPROVED DIRECTION / MILESTONES PLANNED / IMPLEMENTATION NOT STARTED`

**Tarih:** 2026-08-29

**Ad durumu:** “IPTV Suite” yalnızca doğrulanmamış iç codename'dir.

**Milestone bağı:** M20–M36. Bu belge M1–M19'un tarihsel durumunu, M15/M16 release blocker'larını veya M17–M19 acceptance sonucunu değiştirmez.

## Karar özeti

IPTVnator `29ca94aa4371e9a71e24ccac4c529bd62f6fabd5` snapshot'ı; arayüz, navigation, source yönetimi, katalog, EPG, playback ve desktop yardımcı özellikleri açısından salt okunur incelenmiştir [S142][S143]. Kullanıcı bu incelemeden çıkan Windows özellik yönünü yeni milestone'lar halinde ilerletmeyi onaylamıştır.

Hedef, IPTVnator'ın marka veya ekranlarını kopyalamak değildir. Hedef; kanıtlanmış kullanım modellerini mevcut C#/.NET/WinUI mimarisinde, özgün görsel kimlik ve bu repository'nin güvenlik sınırlarıyla yeniden uygulamaktır.

- IPTVnator kaynak kodu MIT lisanslıdır. Kaynak kodun veya önemli bir bölümünün taşınması halinde copyright ve MIT izin metni korunmalıdır [S144].
- IPTVnator adı, logosu, icon'u ve artwork'ü yazılım lisansının dışında tutulmuştur. Ad, ürün/app/Store markası veya kimliği olarak; görsel varlıklar ise asset olarak kullanılmaz ve benzer marka izlenimi oluşturulmaz. Faktüel doküman atfı ile zorunlu lisans bildirimi korunur [S145].
- Bu aşamada IPTVnator kodu, dependency'si, görsel asset'i veya fixture'ı IPTV Suite'e alınmamıştır.
- Electron/Angular mimarisi taşınmaz. Her özellik Windows-native capability, package, Store, accessibility, security ve lifecycle koşullarıyla yeniden tasarlanır.
- “Tüm özellikler” Windows ürünündeki anlamlı kullanıcı yeteneklerini kapsar. macOS/Linux paketleri, Electron renderer'ı ve Docker/PWA doğrudan Windows feature parity sayılmaz; M36'da ayrı platform kararı olarak ele alınır.

## Kanıt ve iddia sınırı

- **VERIFIED:** Aşağıdaki özellik repo içindeki route/UI, service/store ve test veya mimari belgeyle gözlenmiştir.
- **INFERENCE:** Benzer kullanıcı değerinin WinUI üzerinde hangi milestone'a ayrılması gerektiğine ilişkin mühendislik kararıdır.
- **UNVERIFIED:** Özelliğin IPTV Suite'te çalışacağı, provider uyumluluğu, codec desteği, Store kabulü, içerik hakkı veya hukuki sonucu henüz kanıtlanmamıştır.

IPTVnator'ın feature varlığı IPTV Suite için dependency seçimi veya implementation onayı değildir. Her milestone kendi Product Brief/ADR/risk/evidence zincirini tamamlamadan production kapsamına girmez.

## İncelenen özellik envanteri ve hedefi

| Özellik ailesi | IPTVnator'da gözlenen kapsam | IPTV Suite hedefi | Disposition |
|---|---|---|---|
| Workspace ve görsel sistem | Kalıcı global rail, provider-context panel, route-aware header/search, adaptive drawer, light/dark theme, ayar bölümleri | Özgün WinUI shell ve adaptive design system | M20 |
| Kişisel durum | Per-source/global favorites, recent, watch history, resume, watched state | Source-scoped identity, retention ve kullanıcı kontrollü temizleme | M21 |
| Dashboard | Continue Watching hero/rail, live favorites, recent live, favorite VOD, recent sources ve configurable rails | Yerel veriden hızlı, boş rail gizleyen Home dashboard | M22 |
| Arama ve katalog ergonomisi | Global/scoped search, category search/sort/hide/order, paging, numeric channel select, command palette | Native indexed search ve erişilebilir katalog komutları | M23 |
| Kaynak biçimleri ve taşınabilirlik | M3U URL/file/text, Xtream, Stalker, auto-detect, refresh, source headers, backup/restore/reset | Güvenli progressive editor, local import, refresh policy ve versioned backup | M24; Stalker bölümü M29 sonrası |
| Oynatıcı kontrolleri | Engine-neutral controls; seek, audio/subtitle/quality, external subtitle, speed, aspect, PiP, fullscreen, shortcut ve auto-hide | Capability-driven tek WinUI kontrol sözleşmesi | M25 |
| EPG | XMLTV source yönetimi, mapping, timeline/list, multi-channel grid, program search ve provider EPG fallback | Bounded XMLTV import, now/next ve TV guide | M26 |
| Catch-up, archive ve radio | M3U/Xtream catch-up variants, geçmiş program activation, dedicated radio player | Provider-capability gated archive/timeshift ve audio experience | M27 |
| Playback uyumluluğu | HTML5/Video.js/ArtPlayer, Shaka/HLS/mpegts; external MPV/VLC; experimental embedded MPV; structured recovery | Native Tier A'yı koruyan diagnostics, explicit fallback ve 4K/HDR matrix | M28 |
| Stalker/Ministra | Portal discovery, MAC/device identity, session/watchdog, Live/Radio/VOD/Series/EPG | Ayrı proprietary compatibility adapter ve secure session lifecycle | M29 |
| VOD zengin deneyim | Browse/detail/watch, recently added, season/episode, watched, up-next, auto-continue ve resume | M18/M19 üstüne detay ve episodic UX | M30 |
| Metadata ve keşif | Opt-in TMDB plot/cast/crew/trailer/rating/artwork, actor/similar/trending | Opt-in, cached, privacy-bounded metadata enrichment | M31 |
| Çoklu-kaynak VOD | Film identity matching, source picker/pin/probe, opt-in failover, ortak resume | Açık eşleşme güveni ve kullanıcı kontrollü source switch | M32 |
| İndirme/offline | Movie/episode queue; pause/resume/cancel/retry, Range recovery, offline detail, play/reveal/remove | Yetkili VOD için managed offline library | M33 |
| Canlı kayıt | M3U/Xtream/Stalker live recording, EPG snapshot, interrupted-file recovery ve recording library | Ayrı recording lifecycle ve rights/storage gate | M34 |
| LAN uzaktan kumanda | Mobile web remote; channel, number, volume ve now-playing status | Authenticated, paired, local-only companion control | M35 |
| Platform ve update modeli | Electron Windows/macOS/Linux, PWA/Docker, app updater ve wake lock | Windows delivery mapping; companion/PWA için yalnız feasibility kararı | M36 |

IPTVnator'daki DASH ClearKey/KODIPROP yolu gözlenmiştir; DRM veya erişim kontrolünü aşan hiçbir davranış bu roadmap'e alınmamıştır. DRM-protected kaynak desteği ayrıca hukuk, platform API'si ve içerik yetkisiyle onaylanmadıkça kapsam dışıdır.

## Özgün WinUI deneyim ilkeleri

1. **Aynı zihinsel model, farklı ürün dili:** Global navigation, bağlamsal kategori paneli, header ve içerik yüzeyi korunabilir; kesin ölçüler, renkler, icon sırası, route adları ve görseller kopyalanmaz.
2. **Kaynak yönetimi ayrı kalır:** M17'nin `SourceManagerPage` ayrımı korunur. Source formu katalog/player üstüne overlay olarak geri dönmez.
3. **Progressive disclosure:** Kullanıcı önce M3U URL/file/text, Xtream veya ileride Stalker yöntemini seçer; yalnız gerekli alanlar açılır. Credential alanları maskeli ve erişilebilir olur.
4. **Adaptive, sabit breakpoint kopyası değil:** Wide/standard/compact durumları effective pixel ve kullanılabilir içerik tabanıyla belirlenir. Compact görünümde context pane drawer olur; player'ın ikincil kontrolleri `More` menüsüne taşınır.
5. **Capability-driven player:** UI, player'ın o session için gerçekten desteklediği komutu gösterir. Sahte 4K, seek, PiP, track, recording veya timeshift vaadi üretilmez.
6. **Remote artwork untrusted'dır:** Poster, logo ve backdrop bounded fetch/decode/cache politikasından geçer. Placeholder, icon, demo artwork ve fixture'lar özgün ve provenance kayıtlı olur.
7. **Erişilebilirlik feature sonu işi değildir:** Keyboard, gamepad/remote-benzeri focus, Narrator/UIA, high contrast, reduced motion ve %200 text scale her milestone acceptance'ına girer.

## Bağımlılık sırası

| Dalga | Milestone'lar | Başlama koşulu |
|---|---|---|
| A — UI ve kullanıcı durumu | M20 → M21 → M22; M23 | M17–M19 manuel kabul sapmaları triage edilmiş olmalı |
| B — Source ve player foundation | M24, M25 | M17 CRUD/crash-intent; M18/M19 playback identity invariant'ları korunmalı |
| C — Live bilgi ve uyumluluk | M26 → M27; M28; M29 | Network/EPG/player/provider ADR'leri ve sentetik harness'lar hazır olmalı |
| D — VOD zenginleştirme | M30 → M31 → M32 | M18/M19 seek/hierarchy acceptance'ı tamamlanmalı |
| E — Yerel medya ve kontrol | M33 → M34; M35 | Rights/storage/path ve authenticated LAN threat model'leri kabul edilmeli |
| F — Teslimat kararı | M36 | Önceki feature setinin Windows release etkisi ölçülmüş olmalı |

Bağımsız dallar paralel tasarlanabilir; bir sonraki milestone'a geçmek öncekinin release completion'ı anlamına gelmez. Production kodu yalnız aktif milestone'un modellerini alır.

## M20 — Özgün adaptive workspace shell ve design system

**Durum:** `IN PROGRESS / SHELL FOUNDATION IMPLEMENTED / ACCEPTANCE PENDING`

**Implementation checkpoint — 2026-08-29:** Windows istemcisinde bağımsız
“Signal Slate” koyu tema token'ları, `NavigationView` tabanlı açık/compact navigation,
adaptive Home yerleşimi ve mevcut Live TV/Movie/Series/Sources destination'larını
koruyan yeni shell uygulanmıştır. Favorites, TV guide, Continue Watching ve Downloads
yalnız açıkça `coming soon` / `planned feature` olarak görünen etkisiz presentation
placeholder'larıdır; veri modeli, persistence, network veya capability iddiası oluşturmaz.
IPTVnator kodu, CSS'i, ekran görüntüsü, adı, logo/icon/artwork'ü veya medya asset'i
taşınmamıştır. Bu checkpoint yeni dependency ya da güvenlik ödünü eklememiş; mevcut
credential masking, consent ve source ayrımı sözleşmelerini değiştirmemiştir.

Light/system/high-contrast token matrisi, settings surface, context pane/drawer,
320–2560 effective-pixel tam matrisi, text-scale/Narrator ve packaged navigation smoke
henüz tamamlanmadığından M20 acceptance açık kalır. M21/M22 kullanıcı durumu ve gerçek
dashboard rail implementation'ı başlamış sayılmaz.

### Kapsam

- Global navigation rail/pane, route-aware header, source switcher, main content ve optional context pane.
- Wide/standard/compact layout; compact context drawer, focus trap/inert eşdeğeri ve focus restore.
- Light, dark, system ve high-contrast token'ları; typography, spacing, card, selection, skeleton, progress ve empty-state bileşenleri.
- Resource-backed localization altyapısı, locale-aware tarih/saat/sayı biçimi ve per-locale truncation/text-scale kontrolü. İlk kabul mevcut Türkçe/İngilizceyi kilitler; ek diller yalnız insan incelemeli çeviri ve aynı accessibility gate'iyle açılır.
- Bölümlenmiş Settings page; başlangıç görünümü ve son uygun görünümü geri yükleme.
- Özgün icon/placeholder/visual assets ve attribution/provenance manifesti.

### Acceptance

- 320–2560 effective-pixel aralığında içerik erişilemez olmaz; keyboard/Narrator focus kaybolmaz.
- Source Manager ve catalog/player ayrı destination olarak kalır.
- IPTVnator marka, logo, artwork, birebir renk/ölçü veya screenshot türevi bulunmaz.
- UI performance, contrast, text scale, reduced-motion ve packaged navigation smoke geçer.

## M21 — Favorites, history, watched ve resume state

**Durum:** `PLANNED / NOT STARTED`

- Live/Movie/Series/Episode için per-source ve global favorites.
- Recently viewed, watched/unwatched, Movie/Episode resume position ve season-level toplu watched işlemi.
- Stable typed identity; refresh/replace/delete old-or-new state ve source silmede deterministic cleanup.
- Ayrı retention süresi, “clear all/selected”, privacy açıklaması ve backup participation kararı.
- Acceptance: cross-source ID çakışması, stale position, delete/restart, bounded history ve kullanıcı temizliği testleri.

## M22 — Home dashboard ve continue-watching rails

**Durum:** `PLANNED / NOT STARTED`

- Continue Watching hero, continue rail, live favorites, recent live, favorite Movies/Series ve recent Sources.
- Empty rail auto-hide, rail bazlı loading/skeleton, `Manage all` navigation ve persisted show/hide settings.
- İlk sürüm yalnız local authoritative M21/M17–M19 verisini kullanır; TMDB rails M31'e kadar gösterilmez.
- Acceptance: kısmi source failure tüm dashboard'u bloklamaz; cards keyboard/UIA erişilebilir; 50k katalog dashboard'u eager materialize etmez.

## M23 — Global keşif, kategori yönetimi ve komut paleti

**Durum:** `PLANNED / NOT STARTED`

- Tüm source'larda typed global search; source/section scoped search ve URL/navigation state.
- Category search, server/A–Z/Z–A sıralama, show/hide ve kullanıcı sırası; refresh yeni category'yi görünür ekler.
- Paging/items-per-page, recently added, channel-number selection ve favorites ordering.
- Current view / current source / global gruplu command palette ve son kullanılan bounded komutlar.
- Acceptance: source switch eski query/category sonucunu göstermez; hidden state refresh'te korunur; keyboard/focus ve indexed-query bütçeleri geçer.

## M24 — Source import, refresh ve güvenli backup/restore

**Durum:** `PLANNED / NOT STARTED`

- Remote URL yanında local `.m3u/.m3u8` file ve bounded raw-text import; input'tan safe source-kind önerisi.
- Source list search/type filter/sort ve explicit reorder; source kimliği sıralamadan bağımsız kalır.
- Startup/manual auto-refresh, stale policy, cancellation/progress ve last-good snapshot korunması.
- Gerekiyorsa source-scoped `User-Agent`/`Referer`/`Origin` için ayrı allowlist ve exact-origin güvenlik kararı; arbitrary Cookie/Authorization registry'si yoktur.
- Versioned backup manifesti; cache ve ephemeral token hariç portable source/user state; import merge/create/skip/fail özeti ve dry-run.
- Backup varsayılan olarak secret içermez. Secret-inclusive export ancak ayrı ADR ile authenticated encryption, passphrase UX, memory wipe ve recovery sonucu kabul edilirse açılır; plaintext credential backup yoktur.
- Acceptance: hostile file/manifest, symlink/reparse/path, oversized text, refresh crash/restart, old-version import ve secret canary testleri.
- Generic M3U içeriği ad/category/URL extension heuristic'iyle Movie veya Series'e çevrilmez; güvenilir typed provider contract yoksa M17'nin Live-only dürüstlük kuralı korunur.

## M25 — Birleşik gelişmiş player controls

**Durum:** `PLANNED / NOT STARTED`

- Engine-neutral `PlayerCapabilities + PlayerState + PlayerCommands` sözleşmesi.
- Play/pause/stop, live/VOD timeline, volume/mute, fullscreen auto-hide/title, PiP, playback speed ve aspect presets.
- Audio/subtitle/quality track seçimi; kullanıcı seçtiği local SRT/VTT dosyası, encoding, delay, size/color. ASS yalnız seçilen engine güvenle desteklerse.
- Keyboard: Space/K, F, M, arrows; popover/focus/typing ownership ve compact overflow.
- Acceptance: unsupported capability görünmez/disabled; stale player event yeni session'ı değiştirmez; external subtitle path capability-bound ve log/redaction-safe olur.

## M26 — XMLTV EPG, now/next ve TV guide

**Durum:** `PLANNED / NOT STARTED`

- Global ve source-scoped XMLTV source CRUD; refresh/progress/freshness/clear.
- Bounded streaming XML parse, timezone normalization, channel mapping CRUD ve manual search.
- Channel row now/next/progress; selected-channel timeline/list ve multi-channel EPG grid/date navigation.
- Xtream EPG ile XMLTV tercih/fallback sözleşmesi; Stalker adapter M29'da eklenir.
- Acceptance: SSRF/DNS rebinding/redirect/downgrade, decompression/entity/size/depth/time budgets, DST/timezone, stale cache ve cancellation matrisi.

## M27 — Radio, catch-up/archive ve live timeshift

**Durum:** `PLANNED / NOT STARTED`

- `radio=true`/provider radio için dedicated audio surface, artwork aura yerine özgün bounded visual treatment ve shared volume/shortcuts.
- M3U catch-up metadata ile Xtream archive capability'sinden geçmiş program activation.
- Live, archive ve local/provider timeshift state'lerinin açık ayrımı; seekbar yalnız gerçek seekable range için.
- Local timeshift buffer ancak storage quota, encryption/retention, crash cleanup ve içerik hakkı kararıyla açılır.
- Acceptance: live edge, start-over, seek bounds, timezone, expired archive, provider variant ve source deletion testleri.

## M28 — Playback diagnostics, fallback ve 4K/HDR uyumluluk matrisi

**Durum:** `PLANNED / NOT STARTED`

- Safe diagnostic sınıfları: network, source unsupported, decoder/track, policy ve unknown; raw URL/header/native message yok.
- Kullanıcı kontrollü recovery: retry, alternative format/source, installed external player veya approved native fallback. Otomatik gizli engine switch yoktur.
- Credential-bearing locator process command line'ına veya başka uygulamanın görünür history/log yüzeyine konmaz. Güvenli handoff yoksa external-player eylemi o session'da sunulmaz.
- HLS, MPEG-TS, MP4/VOD, DASH ve gerekli audio/subtitle tuple'ları için sentetik matrix; 4K/HEVC/Main10/HDR yalnız cihaz+decoder+container doğrulanırsa etiketlenir.
- MPV/LibVLC/FFmpeg veya başka bundled engine ayrı ADR, exact binary-to-source/SBOM/notices, codec/patent ve Store gate'i olmadan eklenmez. IPTVnator'ın experimental embedded MPV kodu/binary'si taşınmaz.
- DRM/access-control bypass, TLS validation disable ve certificate “accept all” kesin kapsam dışıdır.

## M29 — Stalker/Ministra compatibility adapter

**Durum:** `PLANNED / DECISION-GATED`

- Portal endpoint discovery, normalized MAC/device identity, optional account fields ve explicit provider-profile editor.
- Handshake/token/profile/watchdog lifecycle; token ve resolved playback link yalnız memory/short protected lease içinde.
- Live, Radio, VOD, Series ve provider EPG typed mapping; favorites/recent/user state integration.
- Proprietary dialect versioning, bounded request/response, exact-origin header/cookie scope ve no-workaround-registry policy.
- Acceptance yalnız sentetik Stalker mock server ile yapılır; gerçek MAC/account/response capture repository veya artifact'a girmez.

## M30 — Zengin Movie/Series detail ve episodic playback

**Durum:** `PLANNED / NOT STARTED`

- Browse ↔ detail ↔ watch iki durumlu flow, backdrop/poster fallback ve playback stage.
- Recently added, rating/year/genre metadata, season tabs, episode watched/resume, previous/next ve Up Next.
- Auto-next varsayılan açık olmaz; aynı Series/Season/session identity içinde kullanıcı tercihine bağlıdır.
- M18/M19 typed identity/seek/natural-completion ve source replacement invariant'ları korunur.

## M31 — Opt-in metadata enrichment ve keşif

**Durum:** `PLANNED / DECISION-GATED`

- TMDB veya seçilecek provider için kullanıcı API key'i, açık opt-in ve local protected storage.
- Confidence-scored match; düşük güven provider metadata'sını sessizce değiştirmez.
- Plot, cast/crew, trailer, rating, artwork, actor, similar ve trending rails; locale-aware cache.
- Data minimization, retention/purge, request attribution, image policy, rate limit ve third-party privacy disclosure.
- Provider terms/attribution/logo kullanım koşulları ve API redistribution sonucu onaylanmadan release edilmez.

## M32 — Çoklu-kaynak VOD seçimi ve failover

**Durum:** `PLANNED / DECISION-GATED`

- Aynı Movie için explicit source alternatives; identity confidence ve neden eşleştiği kullanıcıya görünür.
- User pin, bounded availability probe, current position'ı taşıyan manual switch.
- Auto-failover yalnız opt-in; sonsuz retry, credential cross-source reuse veya sessiz düşük-güven eşleşmesi yok.
- Cross-source dedup M17 Home count sözleşmesini geriye dönük değiştirmez; yalnız playback alternative layer'ında çalışır.

## M33 — Managed VOD/episode download ve offline library

**Durum:** `PLANNED / RIGHTS + STORAGE GATED`

- Movie/episode queue, pause/resume/cancel/retry, Range/validator/overlap verification ve bounded reconnect.
- OS picker ile authorized download root; no-overwrite, atomic `.part` finalization, startup recovery ve disk quota/free-space davranışı.
- Offline detail/series grouping, play/reveal/remove ve missing-file recovery.
- DRM, ephemeral authenticated locator, provider prohibition veya kullanıcı yetkisi belirsizse download gösterilmez.
- Acceptance: path traversal/reparse, disk-full, network loss, validator mismatch, process kill, source deletion ve file-retention tests.

## M34 — Live recording library

**Durum:** `PLANNED / RIGHTS + PLAYER GATED`

- Recording yalnız player capability ve kullanıcı yetkisi açıkken; start/stop, elapsed/size ve route/source change auto-stop.
- Başlangıçta channel/source/now-program snapshot; program sınırları için EPG enrichment.
- Ayrı recordings persistence; completed/interrupted/failed; crash sonrası playable partial'ı dürüstçe koruma.
- Recording, download queue ile aynı UI hub'ı paylaşabilir fakat retry/resume semantics'i paylaşmaz.
- Acceptance: flush/finalization race, PID/session reuse, missing/dead network folder, source delete, disk full ve managed-file authorization.

## M35 — Eşleştirilmiş yerel ağ uzaktan kumanda

**Durum:** `PLANNED / SECURITY GATED`

- Varsayılan kapalı local companion server; random high port, explicit network-scope seçimi ve firewall UX.
- QR ile kısa ömürlü pairing, device-bound revocable token, CSRF/origin checks, rate limiting ve bounded status payload.
- Control channel confidentiality, integrity ve replay protection sağlar; düz HTTP bearer token modeli acceptance değildir. Exact transport/pairing protokolü ADR'de seçilir.
- Channel up/down/number, volume/mute, play/pause ve safe now-playing/EPG özeti; credential/locator/log yok.
- `0.0.0.0` üzerinde kimlik doğrulamasız HTTP kontrol yüzeyi kabul edilmez. Pairing secret URL query/log/history içine konmaz.
- Acceptance: adjacent-LAN unauthorized request, replay, brute force, malformed input, sleep/resume, network change ve revoke tests.

## M36 — Windows delivery mapping ve companion/PWA feasibility

**Durum:** `PLANNED / ADR ONLY`

- Electron auto-updater davranışı kopyalanmaz; MSIX/Store/App Installer update ownership'ı, rollback ve release notes UX'i Windows modeline eşlenir.
- Playback wake lock/power request yalnız aktif session lifetime'ında ve guaranteed cleanup ile değerlendirilir.
- Self-hosted web/PWA, macOS ve Linux bu Windows roadmap'in implementation kapsamı değildir. İstenirse threat model, backend/proxy ownership, credential boundary, deployment/support maliyeti ve ayrı repository/platform stratejisi için ADR üretilir.
- M36 acceptance bir platform kararıdır; PWA/Docker/backend scaffold'ı üretmek acceptance değildir.

## Ortak hard gate'ler

Her milestone aşağıdakileri sağlamadan `completed` olamaz:

1. Tamamen sentetik, lisansı/provenance'i kayıtlı fixture ve loopback service; gerçek provider/account/playlist yok.
2. Raw credential, full URL, Authorization/Cookie/MAC/pairing secret veya media locator için UI/log/TRX/artifact canary scan.
3. Endpoint, redirect, DNS/address, timeout, decompressed-size, item/depth/string ve cancellation budget'ları.
4. Locked restore; Debug/Release x64 build; unit/integration/architecture; iki deterministik quality turu.
5. Signed MSIX packaged journey, UIA/Narrator/keyboard, physical-device/player matrix ve feature'a özgü soak/fault planı.
6. Yeni dependency/native binary/service için exact license, notice, SBOM, source correspondence, CVE ve Store/legal incelemesi.
7. M15/M16 acceptance ledger'ları source/package drift üreten her production değişiklikten sonra exact successor artifact'larla yenilenir; local feature PASS bunların yerine geçmez.

## Açık kararlar

- M20 uygulamasından önce özgün visual direction ve üç adaptive wireframe kullanıcıyla kabul edilecek.
- M24 backup secret modeli için plaintext dışındaki recovery UX'i ayrıca seçilecek.
- M27 local timeshift'in gerçekten ürün kapsamına girip girmediği rights/storage kararıyla belirlenecek.
- M28 ek playback engine ihtiyacı native Tier A sentetik/gerçek cihaz gap'i ölçülmeden varsayılmayacak.
- M29 Stalker kapsamı, kullanıcı tarafından yetkili hesap modeli ve Store/legal review ile doğrulanacak.
- M31 metadata provider'ı ve attribution/privacy metni ayrıca onaylanacak.
- M33/M34 download/recording için hedef ülke, provider terms ve içerik hakları uzman incelemesi yapılacak.
- M35 pairing/transport threat model'i ayrı ADR olmadan implementation'a geçmeyecek.

## İlişkili belgeler

- [Windows MVP roadmap](./WINDOWS_MVP_ROADMAP.md)
- [Post-MVP content experience brief](../product/POST_MVP_CONTENT_EXPERIENCE_BRIEF.md)
- [Security and Privacy Baseline](../security/SECURITY_AND_PRIVACY_BASELINE.md)
- [Risk Register](../risks/RISK_REGISTER.md)
- [Research Sources](../research/SOURCES.md)
