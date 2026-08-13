# IPTV Suite — Phase 0 Mimari Raporu

**Tarih:** 2026-08-09

**Belge durumu:** Windows yönü kararlaştırıldı; playback, secure-storage layout ve Samsung kararları spike koşullu

**Ürün adı:** “IPTV Suite” yalnız doğrulanmamış iç codename'dir.

## Yönetici özeti

Windows MVP için önerilen stack **C# / .NET 10 LTS + WinUI 3 + güncel stable Windows App SDK + framework-dependent MSIX**'tir. Uygulama tek process'li modular monolith olacak; presentation, application, domain ve infrastructure sınırları korunacaktır. Windows UI kararı playback motorundan ayrıdır.

Geniş IPTV compatibility için birincil playback adayı **non-GPL libVLC + LibVLCSharp.WinUI**'dır; ancak WinUI surface, codec corpus, uzun süreli stabilite, x64/ARM64, MSIX/Store ve exact lisans envanteri M10 spike'ında geçmeden production kararı değildir. Windows native media API'leri temiz baseline/fallback'tir, fakat temiz OS'te HEVC/AC-3/E-AC-3 ve HEVC-in-TS kapsamı geniş ürün sözünü karşılamaz.

Platformlar aynı UI/player implementation'ını paylaşmayacaktır. Samsung ayrı Tizen Web + AVPlay, Android Kotlin + Media3, Apple Swift + AVFoundation/AVKit yönündedir. Paylaşım; versioned terminology, contracts, error codes ve sentetik test vectors ile sınırlıdır. İlk organizasyon modeli, platform sınırları kesin bir monorepo'dur.

M1 bootstrap, M2 test/quality altyapısı ve M3 domain/validation mühendislik kabulleri tamamlanmıştır. M2 exact-SDK local iki-run gate'i ile commit `79cf619c6683fa9c4213846455e376fb1b0cb11c` üzerindeki [hosted run `31327398270`](https://github.com/serkankaracan/iptv-suite/actions/runs/31327398270) quality, signed packaged-host ve coordinator işlerini geçmiştir. M3 local gate'i safe source configuration, domain invariant, stable key/error, redaction ve catalog/HLS karar contract'larıyla 105/105 testi iki koşuda geçmiştir. M4'ün Application secret-store contract'ı, typed initialization sonucu, bounded stale-temp cleanup'ı ve Windows CurrentUser DPAPI adapter'ı uygulanmış; local foundation gate 135/135 testi iki koşuda geçmiştir. M4 milestone'u packaged lifecycle, wrong-user, source deletion/record reconciliation ve 50k kararına kadar açıktır; network/provider, parser, database, feature UI ve playback implementation'ı hâlâ yoktur.

## A. Gereksinim analizi

### A.1 Ürün ve güven sınırı

Ürün, kullanıcının kendi yetkili hesabını veya uzak playlist'ini doğrudan kendi cihazından sağlayıcıya bağlayan bir player'dır:

```text
User
  -> platform-native app
       -> user-selected provider / playlist / logo host
       -> local protected secrets + local catalog/cache
       -> platform-specific player

No IPTV catalog service
No developer media proxy/backend
No DRM/access-control bypass
```

Provider, playlist, logo ve stream tamamen güvenilmeyen dış girdidir. “Kullanıcının verdiği URL” güvenli URL demek değildir. Uygulamanın geliştirici backend'i olmaması gizlilik yüzeyini küçültür; local credential, kanal tercihleri ve provider'a doğrudan iletişim için güvenlik/privacy yükümlülüğünü ortadan kaldırmaz.

### A.2 MVP fonksiyonel zinciri

1. Xtream-compatible veya remote M3U/M3U8 source ekleme.
2. Local validation ve güvenli, bounded connection probe.
3. Download/API fetch, incremental parse, normalize ve atomik katalog activation.
4. Category/channel query, virtualized UI ve kontrollü logo cache.
5. Channel selection, playback open ve temel kontroller.
6. Buffering, timeout, cancellation, bounded reconnect ve güvenli hata mesajı.
7. Source deletion ile credential, locator, snapshot ve cache temizliği.

VOD, Series, EPG/XMLTV, local-file import, recording, download, cloud backend, analytics, reklam ve DRM işi M16 sonrasıdır.

### A.3 Medya terimleri

| Katman | Örnek | Ne değildir |
|---|---|---|
| Catalog/playlist format | Extended M3U | Tek başına streaming protocol veya codec değildir. |
| Streaming protocol/manifest | HLS / M3U8 | Her `.m3u8` dosyasının kanal kataloğu olduğu anlamına gelmez. |
| Transport/container | MPEG-TS, fMP4 | Video/audio codec değildir. |
| Video codec | H.264/AVC, H.265/HEVC | Hangi protocol/container içinde geldiğini söylemez. |
| Audio codec | AAC, AC-3, E-AC-3 | Her OS/cihazda decoder bulunduğunu garanti etmez. |
| Player capability | Motor + build + OS + device + driver + stream tuple | Global “VLC/Windows bunu destekler” boolean'ı değildir. |

Bir stream için destek iddiası `protocol + container + video codec + audio codec + track özellikleri + player build + OS + donanım/driver` tuple'ı ve test sonucu olarak tutulur.

### A.4 Non-functional öncelikler ve hard gate'ler

Öncelik sırası playback kalitesi, stabilite, performans, mağaza uyumu, sürdürülebilirlik ve native UX'tir. Maksimum kod paylaşımı hedef değildir.

Şunlar puanla telafi edilemez:

- Tier A playback zincirinin çalışmaması;
- uygulanabilir Store/MSIX yolunun olmaması;
- commercial dağıtımla çelişen exact license/build;
- abandon edilmiş veya security patch üretmeyen dependency;
- güvenli credential/locator storage'ın mümkün olmaması.

50.000+ kanal tasarım girdisidir. Download/network süresi; parser, persistence ve UI ölçümünden ayrı raporlanır.

## B. Platform stratejisi

### B.1 Net kararlar

| Platform | UI/application yönü | Playback | Paylaşım | Durum |
|---|---|---|---|---|
| Windows | C#/.NET 10 + WinUI 3 | Proposed libVLC adapter; native baseline | Windows içi domain/application C# | UI Accepted, player Proposed |
| Samsung TV | Ayrı Tizen Web `.wgt`, 2018/Tizen 4+ | AVPlay | Contract/test vectors | Proposed; business + device gate |
| Android | Kotlin native | Media3 ExoPlayer | Contract/test vectors | Gelecek Proposed |
| iOS | Swift native | AVPlayer/AVKit | Apple Swift Package + ortak specs | Gelecek Proposed |
| macOS | Swift native | AVPlayer/AVKit | Apple Swift Package + ortak specs | Gelecek Proposed |
| tvOS/Android TV/webOS | İlgili platform UI/player | Platform player önce | Specs | Backlog; yeni ADR gerekir |

Windows **native Windows uygulaması** olmalıdır. Cross-platform UI framework'ü Samsung'u çözmez ve player/lifecycle adapter'larını ortadan kaldırmaz. Windows ve macOS aynı application/UI implementation'ını paylaşmamalıdır; sadece platform bağımsız davranış specification'ı paylaşmalıdır.

Android/iOS/macOS için tek ortak teknoloji bugün gerçek değer sağlamaz. Apple hedefleri kendi içinde Swift package paylaşabilir; Android ayrı Kotlin/Media3 kalır. Gelecekte ölçülen bakım ekonomisi varsa yeniden değerlendirilir.

Samsung tamamen ayrı projedir ve TV'ye özgü remote, focus, lifecycle, AVPlay ve WidgetData adapter'ları vardır. TV platformlarında önce üreticinin player API'si kullanılır; custom FFmpeg/WASM ancak corpus başarısızlığı ve fonlanmış ihtiyaçla açılır.

Android yönü Kotlin + güncel stable Media3 ExoPlayer'dır; araştırma baseline'ı 1.10.1'dir. HLS TS/fMP4 desteği resmî olsa da sample decode cihazın platform codec'lerine bağlıdır; FFmpeg extension varsayılan değildir. Apple yönü Swift + AVFoundation/AVKit'tir. AVPlayer HLS'i resmî olarak destekler; direct raw TS ve provider-quirk kapsamı **UNVERIFIED** olup physical iPhone/iPad/Mac corpus ister. Bu gelecek platform version'ları implementation başında yeniden doğrulanır.

### B.2 Paylaşılacak ve paylaşılmayacak şeyler

**Paylaşılır:**

- versioned terminology ve schema;
- provider contract örnekleri;
- M3U parsing/normalization specification'ı;
- golden, sentetik ve hakları temiz fixtures;
- domain error registry;
- playback corpus manifesti ve capability result schema;
- risk, policy ve ADR kayıtları.

**Paylaşılmaz:**

- UI tree/view-model;
- player nesnesi veya callback tipi;
- platform secret store;
- platform lifecycle/input/focus;
- Store signing/release implementation;
- sırf tekrar kullanım yüzdesi için C#/Kotlin/Swift/TypeScript runtime code.

## C. Windows teknoloji karşılaştırması ve kararı

### C.1 Yöntem

Kullanıcı tarafından verilen ağırlıklar değişmeden kullanılmıştır. UI tablosundaki “Playback” puanı codec kapsamı değil, native video surface/interop ve motoru framework'ten ayırabilme riskidir. Puanlar 1–5 arası **INFERENCE**; toplam `sum(weight × score / 5)` formülüdür. Yakın puan tek başına hard gate kararı değildir.

| Aday | Playback 25 | Bakım 15 | Store/lisans 15 | Perf 10 | Native UX 10 | Security 10 | Test 5 | Ekip 5 | Share 5 | Toplam /100 | Sonuç |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| WinUI 3 / WASDK | 4 | 4 | 5 | 4 | 5 | 4 | 4 | 4 | 2 | **83** | Seçildi |
| WPF / .NET 10 | 4 | 4 | 5 | 4 | 3 | 4 | 5 | 5 | 2 | **81** | Birincil UI fallback |
| Avalonia 12 | 3 | 4 | 4 | 4 | 3 | 3 | 4 | 4 | 5 | **72** | Gelecek contingency |
| .NET MAUI 10 | 3 | 3 | 4 | 3 | 3 | 3 | 3 | 4 | 4 | **65** | Stratejik ret |
| Flutter | 2 | 4 | 4 | 4 | 2 | 3 | 4 | 4 | 5 | **65** | Ret |
| Electron | 2 | 4 | 4 | 2 | 2 | 2 | 4 | 5 | 5 | **60** | Ret |

### C.2 Seçim

**Accepted:** WinUI 3 + C# + .NET 10 LTS + current stable Windows App SDK + packaged, framework-dependent MSIX.

Neden:

- Microsoft'ın güncel Windows-only UI yönü ve Store/MSIX için kısa resmî yol;
- Fluent/native API, accessibility, DPI/windowing ve virtualization erişimi;
- .NET LTS ile domain/application kodu;
- packaged app identity, Store runtime servicing ve controlled update;
- player'ı app-owned adapter arkasında seçebilme.

Dezavantajlar:

- Windows App SDK servicing penceresi .NET LTS'den kısadır;
- WinUI UI automation ve native surface/lifecycle olgunluğu spike ister;
- Windows/macOS UI paylaşımı yoktur;
- ARM64 ancak native player chain geçerse sunulur.

WPF aktif ve güçlü fallback'tir; deprecated değildir. WinUI surface hard gate'i geçmez, aynı adapter WPF'de geçerse ADR-001 yeniden açılır. MAUI Windows'ta WinUI'a iner ama Tizen'i çözmez ve ek abstraction getirir. Avalonia güçlü testability sunar; v12'de Tizen paketini kaldırmış ve native surface airspace riski taşır. Flutter'ın resmî Windows video çözümü ve Tizen hedefi yoktur. Electron, native UX/footprint ve remote-content security yükü getirir.

### C.3 OS, architecture ve Store baseline

- M1 baseline: .NET SDK 10.0.302 / runtime 10.0.10 LTS, WASDK stable 2.3.1, Windows SDK BuildTools 10.0.26100.8249. M2'de SDK resolution `global.json` ile `rollForward: disable` ve `allowPrerelease: false` olarak exact hale getirilmiştir.
- Preview/Experimental dependency yoktur.
- Engineering baseline: x64 Windows 11 build 10.0.26100+. Nihai product support alt sınırı M15 market/Store/clean-device verisiyle yeniden doğrulanır.
- x64 ilk hedef; ARM64 yalnız bütün native DLL/plugin ve performance gate'leri geçerse.
- Store-first MSIX; framework-dependent paket. Greenfield EXE/MSI updater sahipliği alınmaz.
- WACK, clean install/update/uninstall ve Partner Center private preflight M15 hard gate'idir.
- Install directory salt okunur kabul edilir; DB/cache/log/player plugin yazımları app-data alanındadır.
- Marka doğrulanmadan kalıcı public package identity/store listing kilitlenmez.

## D. Video player karşılaştırması ve kararı

### D.1 Karar matrisi

Puanlar araştırma kanıtlarından üretilen **INFERENCE**'tır; benchmark değildir.

| Aday | Playback 25 | Bakım 15 | Store/lisans 15 | Perf/HW 10 | Native UX 10 | Security 10 | Debug 5 | Ekip 5 | Share 5 | Toplam | Gate |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| libVLC / LibVLCSharp | 4.5 | 4.0 | 3.0 | 4.0 | 3.5 | 3.5 | 4.0 | 4.0 | 4.5 | **78.0** | Conditional |
| Windows native | 2.0 | 5.0 | 4.5 | 5.0 | 5.0 | 4.5 | 3.5 | 4.5 | 2.0 | **77.5** | **Fail: geniş codec/container** |
| mpv/libmpv | 5.0 | 3.0 | 2.0 | 4.5 | 2.5 | 3.0 | 4.5 | 2.5 | 4.5 | **71.5** | Fail: build/package ownership |
| FFmpeg custom | 5.0 | 2.5 | 2.5 | 4.5 | 2.0 | 3.0 | 5.0 | 2.0 | 5.0 | **71.0** | Fail: MVP engineering scope |
| Framework media wrapper | 2.0 | 3.5 | 4.0 | 4.5 | 3.5 | 4.0 | 2.5 | 4.0 | 4.0 | **67.0** | Backend gate'ini miras alır |

### D.2 Proposed seçim

M10 başlangıç seti:

- LibVLCSharp.WinUI 3.10.0;
- version-compatible LibVLCSharp core 3.10.0; 3.10.1 upgrade ayrı hücre;
- VideoLAN.LibVLC.Windows 3.0.23.1 **non-GPL**;
- `IPlaybackEngine` arkasında engine type sızıntısı olmayan adapter.

Seçim nedeni; native API'den geniş format ihtimali, FFmpeg custom'dan düşük lifecycle sahipliği ve mpv'den daha hazır first-party .NET/Windows paketidir. Dezavantajları yaklaşık 128 MB native paket, plugin/SBOM/CVE sahipliği, WinUI surface belirsizliği ve codec patent incelemesidir.

Windows native `MediaPlayer`:

- HLS ve H.264/TS + AAC için güçlü baseline;
- OS ile serviced, en küçük attack/package surface;
- fakat HEVC optional, HEVC-in-TS güncel matriste garanti değil ve AC-3 temiz Win11 24H2'de dahil değildir; E-AC-3 temiz OS garantisi yoktur.

Dolayısıyla broad IPTV hard gate'ini geçmez. Ürün H.264/AAC HLS'ye daralırsa yeniden primary olabilir.

FFmpeg teknik coverage'i geçirir ama player değildir; clock, A/V sync, render, audio, buffer, lifecycle ve recovery ürünün sorumluluğu olur. mpv coverage'i güçlüdür; production Windows binary, .NET/WinUI control ve controlled LGPL dependency seti ekibe kalır. Her ikisi de fallback/revisit'tir.

### D.3 Compatibility spike

M10 en fazla küçük, throwaway fakat ölçümlü bir harness'tir; production player implementation değildir. Tam matrix ve go/no-go [ADR-002](../adr/ADR-002-windows-playback-engine.md) ile [Quality Strategy](../quality/QUALITY_AND_PERFORMANCE_STRATEGY.md)'dedir.

Özet hard gate:

- Tier A: HLS-TS ve direct TS + H.264/AAC yüzde 100;
- HLS-fMP4, HEVC, AC-3/E-AC-3, multi-track ve subtitle capability sonuçları;
- Intel/AMD/NVIDIA, x64 ve hedefleniyorsa gerçek ARM64;
- resize/fullscreen/DPI/multi-monitor/lifecycle;
- bounded error/reconnect/cancel;
- 100 switch + 8 saat soak;
- Release MSIX/WACK/private preflight;
- GPL plugin sıfır, exact SBOM/source/notices;
- redacted diagnostics;
- codec patent ve redistribution için uzman hukuk gate'i.

## E. Uygulama mimarisi

### E.1 Katmanlar ve modüller

- **Presentation:** WinUI view, navigation, resources, feature-scoped MVVM state, UI command ve accessibility.
- **Application:** use-case orchestration, concurrency/cancellation, retry policy, atomic workflow ve domain result mapping.
- **Domain:** terminology, value objects, invariants, normalization, stable error/capability contract.
- **Infrastructure:** provider/HTTP, M3U record stream, SQLite, protected storage, images, logging, player ve Windows packaging integration.

Bu bir modular monolith'tir. Microservice/backend yoktur. CQRS/event sourcing/global immutable store yoktur. Abstraction yalnız değişen dış sistem veya test clock/player/transport gibi gerçek seam için vardır.

### E.2 State management

- View-model yalnız presentation state taşır.
- Source import ve playback ayrı explicit state machine'lerdir.
- Her async operation bir operation/session ID ile “stale callback cannot win” kuralına uyar.
- User cancel, source delete ve channel switch eski HTTP/import/player işini sonlandırır.
- Tek aktif playback session vardır.
- 50.000 channel global observable list olarak kopyalanmaz; indexed, windowed query ve virtualization kullanılır.

### E.3 Networking

- Uzun ömürlü/typed `HttpClient`; DNS yenileme için bounded `PooledConnectionLifetime`.
- `ResponseHeadersRead`, connect/request timeout, cancellation, decompression ve byte/line/item budget.
- Redirect manuel, sınırlı ve loop-safe; credential/header/cookie cross-origin forward edilmez.
- Cookies default kapalı; ancak contract corpus ihtiyacı kanıtlarsa source-scoped isolated container.
- Retry yalnız idempotent/safe GET ve transient DNS/connect/408/429/seçilmiş 5xx için, jitter ve total-time budget ile.
- Auth rejection, validation, TLS ve parse hatası otomatik retry edilmez.
- Sertifika doğrulaması veya hostname check kapatılamaz; HTTPS→HTTP downgrade reddedilir.
- Credential-bearing request için HTTPS hard gate. Anonymous HTTP media desteği ancak explicit source policy, kullanıcı uyarısı ve mağaza/security review ile ayrı karar olur.

### E.4 Provider ve parser

`XtreamCompatibleSourceAdapter` ve `RemoteM3uSourceAdapter`, provider payload'ını domain'e çeviren gerçek variability sınırlarıdır. Xtream bir standart kabul edilmez; string/number boolean, missing category, duplicate ID, endpoint ve response varyasyonları contract fixture'larıyla izlenir. MVP yalnız Live TV çağrılarını kullanır.

M3U pipeline:

```text
bounded response bytes
 -> encoding/BOM
 -> incremental line tokenizer
 -> directive/attribute parser
 -> entry assembler
 -> validation + scheme/security policy
 -> normalization + stable key
 -> protected locator + batched SQLite staging
 -> snapshot checks
 -> atomic activation
```

Complete download tek string'e çevrilmez. Unknown directives bounded warning olarak sayılır; raw payload loglanmaz. `.m3u8` uzantısı katalog kararı değildir; içerik sniffing HLS manifest ile channel catalog'u ayırır. Valid HLS master/media manifest catalog parser'a sokulmaz; source display name ile tek `LiveChannel` snapshot'ına route edilir ve manifest locator'ı protected kalır.

### E.5 Persistence, cache ve images

- SQLite normalized metadata, index ve snapshot state.
- DPAPI CurrentUser/LOCAL=user ile protected source secret ve M3U locator.
- Credential Locker 20 kayıt/roaming/bulk locator nedeniyle primary değil.
- 50k DPAPI layout M4 benchmark koşullu; gerekirse reviewed envelope encryption/vetted encrypted DB.
- Failed/cancelled import eski active snapshot'ı değiştirmez.
- Content hash + ETag/Last-Modified + parser/normalization/schema version tekrar parse kararını verir.
- Image URL untrusted ve sensitive'dir; UI yalnız opaque reference/cache state görür.
- Image fetch yalnız http(s), credential/cookie/referer yok, redirect/origin/address policy uygulanır.
- MIME, byte, pixel/dimension, decode ve concurrency sınırı; placeholder ve cancellation.
- Provisional: network concurrency 4, memory cache 32 MiB, device-local LRU disk cache 200 MiB. M14 ölçümüyle değişir.
- Loopback/link-local/private destination logo fetch'i default reddedilir; source explicit private origin olarak yapılandırılmışsa aynı-origin policy ayrıca değerlendirilir.

### E.6 Logging, errors, configuration, localization

- Structured event + operation ID + safe source ID + stage + stable `DomainError`.
- URI user-info, bütün query values (allowlist ile güvenli olduğu kanıtlanmayan), Authorization/Cookie ve provider response redacted.
- Kullanıcı exception/type/stack görmez; localized action-oriented message görür.
- Engine log line'ları protocol/error code gibi parse edilmez; gerekirse bounded, redacted debug attachment.
- Analytics ve üçüncü taraf crash SDK ilk MVP'de yoktur.
- Strongly typed, secretsiz config; runtime secret environment/appsettings'e girmez.
- English resource keys/fields, Türkçe dahil localized display strings. Domain error code locale'den bağımsızdır.

### E.7 Dependency injection ve testability

Composition root'ta Microsoft.Extensions DI benzeri minimal container uygun olabilir; M1'de gerçek composition ihtiyacı olmadığı için DI paketi eklenmemiştir. Exact paket ilk gerçek adapter wiring milestone'unda stable sürüm ve ihtiyaçla seçilir. Source adapter, transport, clock, secret store, catalog store, image fetch/cache ve player seam'leri injection alır. “Her sınıfa interface” yoktur.

Test sınırları:

- pure domain/parser/unit;
- local fake HTTP + SQLite + protected-store integration;
- provider contract;
- 5k–50k performance;
- fake ve real player adapter contract;
- packaged UI automation;
- gerçek device/driver/store;
- 8/24 saat playback soak.

M2 concrete graph'ı production graph'ından tek yönlü ayrıdır:

```text
IptvSuite.ArchitectureTests -> MSTest
IptvSuite.UnitTests --------> IptvSuite.Testing + MSTest
IptvSuite.IntegrationTests -> IptvSuite.Testing + MSTest
IptvSuite.Testing ----------> Microsoft.Extensions.TimeProvider.Testing
IptvSuite.SecretStoreSpike -> Application + Domain + Infrastructure + Testing
                              Microsoft.AspNetCore.App (test-only Kestrel)

Production projects -X-> any test project
```

`IptvSuite.Testing`; deterministic fake time, scripted in-process transport, M2 in-memory test secret store, passive player double, loopback Kestrel, guarded temp directory, timeout, canary scan ve fixture generator içerir. Bu M2 fake'leri application/domain port'u tanımlamaz ve production tarafından referans alınmaz. M4-specific fake ve gerçek DPAPI senaryoları `IptvSuite.IntegrationTests` içindedir; test projesi Application/Infrastructure'a yalnız test yönünde referans verir. `IptvSuite.SecretStoreSpike` yalnız opt-in Release x64 ölçüm executable'ıdır; production package veya normal quality/CI runtime'ına girmez. Unit pure Application/Domain ve harness davranışını; integration loopback/temp/DPAPI contract davranışını; architecture dependency allowlist'ini doğrular. Signed installed MSIX smoke ayrı, seri ve elevated interactive host lane'idir.

Generator `1.0.0` / algorithm `1` / seed `20260809` ile byte-identical sentetik record ve SHA-256/provenance manifesti üretir. Corpus gerçek provider/account/credential/playlist/medya içermez. Internal `LicenseRef-IPTVSuite-Synthetic-Test-Only` durumu `UNVERIFIED` olduğundan public paylaşım hakkı değildir. Canary de gerçek secret değil, bilinen marker sızıntısını kanıtlayan test girdisidir; genel credential/redaction kanıtı sayılmaz.

Local `Invoke-WindowsQualityGate.ps1`; exact SDK locked restore, Debug/Release x64 build, full suite'in ayrı TRX dizinlerinde iki ardışık yeşil koşusu ve eşit sonuç seti, fixture SHA-256 eşitliği, armed sentinel non-zero/recovery ve artifact canary kontrollerini birleştirir. Hosted workflow bütün PR, `merge_group`, `main` push ve manual dispatch olaylarında exact SDK ile bu gate'i çalıştırır; required check'in path filter nedeniyle `Pending` kalmaması için üst seviye path filter yoktur. Quality sonrasındaki package job'u signed x64 MSIX'i hedefli test-payload/canary sızıntısına karşı inceler, kurar, görünür AUMID launch yapar, normal kapatır ve exact cleanup uygular. `always()` coordinator'ı quality fail veya package skip/fail durumunu tek `Required Windows gate` sonucuna taşır.

Bir local/hosted komut tanımının varlığı başarılı run kanıtı değildir. Tarihsel M2 local exact-SDK gate'i 2026-08-09'da 22 testi iki koşuda geçmiş; M3 sonrası local gate aynı gün 105 testi iki koşuda geçmiştir. M2 hosted run `31327398270` üç işi de geçirmiş; iki sanitized artifact'ın commit bağı, quality şekli, fixture hashleri, signature, capability allowlist'i ve package smoke sonuçları `14/14` assertion ile doğrulanmıştır. Artifact taraması GitHub job logunu kapsamaz; log canary sonucu `UNVERIFIED` kalır. Yeşil hosted smoke yalnız çalıştığı runner ve commit için package zincirini kanıtlar; feature UI/UIA/accessibility, update/migration, WACK/Store, gerçek provider/player/codec, genel secret yokluğu, ARM64 veya cihaz matrisi pass'i değildir. Branch policy enforcement mevcut private-repository planında etkin değildir. Ayrıntılı kalıcı M2 kaydı [M2 completion evidence](../quality/M2_COMPLETION_EVIDENCE.md) belgesindedir.

## F. Repository stratejisi

**Accepted:** Başlangıçta platform sınırları belirgin monorepo. Phase 0'da bu path'ler scaffold edilmez:

```text
apps/windows
apps/samsung-tizen
apps/android
apps/apple
shared/contracts
shared/test-vectors
docs
tools
```

Her app'in toolchain, lockfile, player, secure-storage, signing ve release workflow'u ayrıdır. Shared alan yalnız language-neutral contract ve fixture içerir. Platform işleri mantıksal olarak ayrıdır; required-check workflow'u skip/pending bırakmamak için her PR'da sonuç üretir, ilerideki platform seçimi job içinde yapılır. Contract değişikliği consumer contract testlerini tetikler. Store secret/signing material repo dışıdır.

Multirepo tetikleri: bağımsız ekip/release, vendor erişim izolasyonu, compliance/sözleşme, repository/CI performansı veya platform devri. CODEOWNERS path-level gizlilik sağlamaz. Bölünürse contracts/test-vectors ayrı semantic-versioned repository olur.

## G. Güvenlik, mağaza ve lisans yaklaşımı

### G.1 Güvenlik

- Credentials ve full locators secret; raw URL loglanmaz.
- Platform-protected at-rest storage, least privilege, deletion/reconciliation.
- TLS validation kapatılamaz; credential-bearing HTTP yoktur.
- Playlist/logo/provider response hostile input olarak bounded parse/decode edilir.
- Native player ve image codec/parser dependency'leri attack surface'tir; SBOM/CVE/update SLA gerekir.
- Source delete; secret, protected locator, snapshot, favorites policy ve image cache'i kapsar.
- Privacy threat/model ayrıntısı [Security Baseline](../security/SECURITY_AND_PRIVACY_BASELINE.md)'dadır.

### G.2 Microsoft Store

MSIX teknik yolu **PASS**, ürün/içerik incelemesi **CONDITIONAL**:

- App bir player'dır; içerik/abonelik/provider listesi sunmaz.
- Listing/onboarding yalnız yetkili kaynak kullanımını açıklar.
- Certification için geliştiriciye ait sentetik ve hakları temiz source/account sağlanır.
- Gerçek müşteri credential'ı veya korsan provider fixture'ı kullanılmaz.
- Privacy/support URL, IARC/rating, third-party content authorization anlatımı ve reviewer notes hazırlanır.
- Exact package/native dependency WACK/private submission'dan geçer.
- Store policy release tarihinde tekrar doğrulanır.

### G.3 Samsung Store

Türkiye dahil ABD dışı dağıtım için Partner Seller, Content Manager onayı ve offline agreement P0 ticari blocker'dır. Genel BYO IPTV player kabulünü garanti eden kamu politikası bulunmamıştır. Teknik Tizen başlamadan ürün tanımıyla yazılı preflight istenir.

### G.4 Gelecek platform mağazaları

Apple tarafında privacy policy, App Privacy beyanı, third-party service/streaming content authorization, ATS ve macOS App Sandbox/outgoing-network entitlement birlikte değerlendirilir. Genel BYO player, içerik hakkı incelemesinden otomatik muaf sayılmaz. Google Play'de privacy policy ve Data Safety veri toplamayan uygulama için de hazırlanır; integrated SDK davranışı dahil edilir ve unauthorized streaming'i kolaylaştıran davranış yoktur. Her iki platformda da gerçek Store policy implementation başlangıcı ve submission tarihinde yeniden doğrulanır.

### G.5 Lisans ve codec patentleri

Üç ayrı konu karıştırılmaz:

1. OSS copyright license: libVLC/FFmpeg/mpv binary ve source dağıtım koşulları.
2. Codec patent/license: H.264, HEVC, AAC, AC-3/E-AC-3 uygulama/decoder dağıtımı.
3. Content rights: kullanıcının provider/yayın izni ve Store içerik politikası.

Non-GPL/LGPL paket seçmek codec patentlerini veya content rights'ı çözmez. M10/M15'te exact binary/SBOM/notices; ticari yayın öncesi hedef ülke ve iş modeline göre uzman IP hukuk incelemesi hard gate'tir. Bu rapor hukuki görüş değildir.

### G.6 Privacy

Backend/analytics olmasa da credential ve izleme/katalog metadata'sı için privacy policy gerekir. Policy; local processing, doğrudan provider trafiği, retention/cache, deletion, support ve üçüncü taraf SDK bulunmadığını açıklamalıdır. KVKK bakımından cihaz-içi modelde yayıncının rolü/VERBİS/yurt dışı aktarımı uzman görüşü ister. Aydınlatma ve açık rıza ayrı tutulur; gereksiz rıza istenmez.

## H. Açık sorular, varsayımlar ve doğrulama spike'ları

| ID | Durum | Soru/varsayım | Kapatma yöntemi | Owner/milestone |
|---|---|---|---|---|
| O1 | UNVERIFIED | Non-GPL libVLC hedef corpus, WinUI surface ve MSIX'i geçer mi? | M10 full matrix, SBOM, WACK | Playback / M10 |
| O2 | UNVERIFIED | DPAPI-per-locator 50k performansı ve migration'ı uygun mu? | Canary + 5k–50k benchmark/fault injection | Security/Data / M4–M8 |
| O3 | UNVERIFIED | M1 engineering sınırı 10.0.26100/x64; nihai product minimumu ve ARM64 release talebi nedir? | Market/support data + clean devices | Product / M15 |
| O4 | UNVERIFIED | Microsoft Store BYO IPTV modelini kabul eder mi? | Partner Center private/pre-certification | Product/Legal / M15 |
| O5 | UNVERIFIED | Bundled codec patent/redistribution sonucu nedir? | Exact binary list + uzman hukuk görüşü | Legal / M10–M15 |
| O6 | UNVERIFIED | Samsung Partner Seller/Türkiye ve BYO IPTV review yolu açık mı? | Content Manager yazılı preflight | Business / Tizen başlamadan |
| O7 | UNVERIFIED | AVPlay ve WidgetData 2018–2026 cihazlarda yeterli mi? | Gerçek cihaz/store spike | Samsung owner |
| O8 | UNVERIFIED | Xtream variants/cookie ihtiyacı nedir? | Sentetik/lawfully sanitized contract corpus | Provider / M5–M7 |
| O9 | UNVERIFIED | Legacy M3U encoding ve stable-key başarı oranı nedir? | Versioned corpus + refresh reconciliation | Parser / M7–M8 |
| O10 | UNVERIFIED | Cihaz-içi modelde KVKK rolü ve aktarım sorumluluğu nedir? | Privacy counsel | Product/Legal / M15 |
| O11 | Assumption | İlk MVP analytics, backend ve DRM içermez. | Scope review; değişirse ADR/security reopen | Product |
| O12 | Assumption | Test verisi sentetik, third-party içeriksiz ve credentials sahtedir; public redistribution lisansı ayrıca doğrulanır. | Fixture provenance/license manifest; paylaşım öncesi legal review | Quality/Legal |
| O13 | VERIFIED / CLOSED, 2026-08-09 | M2 scaffold'u exact-SDK iki-run quality gate ve hosted packaged-smoke'u deterministik geçiyor mu? | Local sıfır exit code + minimal summary ve ephemeral TRX set karşılaştırması; hosted run `31327398270` üç green job + iki doğrulanmış sanitized artifact; exact sentinel/scanner fail-recovery | Quality / M2 |

## İlişkili kararlar

- [ADR-001 — Windows application technology](../adr/ADR-001-windows-application-technology.md)
- [ADR-002 — Windows playback engine](../adr/ADR-002-windows-playback-engine.md)
- [ADR-003 — Local persistence and secure storage](../adr/ADR-003-local-persistence-and-secure-storage.md)
- [ADR-004 — Application architecture and state](../adr/ADR-004-application-architecture-and-state-management.md)
- [ADR-005 — Repository strategy](../adr/ADR-005-repository-strategy.md)
- [ADR-006 — Samsung TV strategy](../adr/ADR-006-samsung-tv-platform-strategy.md)
- [Araştırma kaynakları](../research/SOURCES.md)
