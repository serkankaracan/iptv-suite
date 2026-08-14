# IPTV Suite — Phase 0 Mimari Raporu

**Tarih:** 2026-08-09

**Belge durumu:** Windows yönü kararlaştırıldı; protected-catalog comparative gate'i kapandı; production SQLite transaction yerleşimi, playback ve Samsung kararları kendi milestone'larında açık

**Ürün adı:** “IPTV Suite” yalnız doğrulanmamış iç codename'dir.

## Yönetici özeti

Windows MVP için önerilen stack **C# / .NET 10 LTS + WinUI 3 + güncel stable Windows App SDK + framework-dependent MSIX**'tir. Uygulama tek process'li modular monolith olacak; presentation, application, domain ve infrastructure sınırları korunacaktır. Windows UI kararı playback motorundan ayrıdır.

Geniş IPTV compatibility için birincil playback adayı **non-GPL libVLC + LibVLCSharp.WinUI**'dır; ancak WinUI surface, codec corpus, uzun süreli stabilite, x64/ARM64, MSIX/Store ve exact lisans envanteri M10 spike'ında geçmeden production kararı değildir. Windows native media API'leri temiz baseline/fallback'tir, fakat temiz OS'te HEVC/AC-3/E-AC-3 ve HEVC-in-TS kapsamı geniş ürün sözünü karşılamaz.

Platformlar aynı UI/player implementation'ını paylaşmayacaktır. Samsung ayrı Tizen Web + AVPlay, Android Kotlin + Media3, Apple Swift + AVFoundation/AVKit yönündedir. Paylaşım; versioned terminology, contracts, error codes ve sentetik test vectors ile sınırlıdır. İlk organizasyon modeli, platform sınırları kesin bir monorepo'dur.

M1 bootstrap, M2 test/quality altyapısı ve M3 domain/validation mühendislik kabulleri tamamlanmıştır. M2 exact-SDK local iki-run gate'i ile commit `79cf619c6683fa9c4213846455e376fb1b0cb11c` üzerindeki [hosted run `31327398270`](https://github.com/serkankaracan/iptv-suite/actions/runs/31327398270) quality, signed packaged-host ve coordinator işlerini geçmiştir. M3 local gate'i safe source configuration, domain invariant, stable key/error, redaction ve catalog/HLS karar contract'larıyla 105/105 testi iki koşuda geçmiştir. M4'ün Application secret-store contract'ı, typed initialization sonucu, bounded stale-temp cleanup'ı ve Windows CurrentUser DPAPI adapter'ı uygulanmıştır. [Hosted run `31735655363`](https://github.com/serkankaracan/iptv-suite/actions/runs/31735655363), commit `6ac235f49ffd297196189e5e9441ad0c9e04f116` için source draft'ın pre-validation→protected create→store-issued reference attach operation'ı, fail-closed packaged composition ve 1.024 startup temp-aday cap'i dahil tarihsel v1 store snapshot'ında architecture 12, unit 114 ve integration 27 olmak üzere 153/153 testi iki koşuda; signed package smoke'unu ve required coordinator'ı geçmiştir. Sanitized package evidence o committeki v1 dizini için `ProtectedStoreDirectoryInitialized=true` taşır. Owner-v2 snapshot'ı 171/171; exact configuration-record deletion primitive'i dahil tarihsel local exact-SDK snapshot architecture 13, unit 137 ve integration 33 olmak üzere 183/183 testi iki koşuda geçmiştir. DPAPI user-boundary harness'ı ve statik contract guard'ları dahil güncel local worktree gate'i exact SDK `10.0.302`, Debug/Release x64 build'lerde 0 warning/error; architecture 16, unit 151 ve integration 34 olmak üzere 201/201 testi iki identical `Passed` setiyle; fixture determinism, sentinel fail/recovery, scanner CLI self-test ve artifact canary kontrolleriyle geçmiştir. Local summary `commitSha=null` taşıdığı için local sonuç tek başına commit-bound hosted/package kanıtı değildir; full gate harness'ı derler ve statik isolation/security sözleşmesini denetler, gerçek-user executable'ını veya protected-catalog `Decision` modunu çalıştırmaz. 2026-08-14 GitHub Actions UI kaydı, run number `#15`in commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` için 3/3 işi ve üç artifact'ı başarıyla tamamladığını gösterir; bu tarihsel kayıt ilk v2 quality/package/same-version lifecycle geçişini workflow/UI düzeyinde **VERIFIED** yapar. Aynı tarihli run number `#16` UI kaydı, exact configuration-record deletion primitive'ini içeren commit `3e806edd9a9482b87cccbf000ac8a81823562f6a` için 3/3 işi yeşil, üç artifact ve `8m24s` toplam süre gösterir; bu takip sonucu da yalnız workflow/UI düzeyinde **VERIFIED**dir. Sonraki run number `#18` UI kaydı, signed `0.0.1.0 → 0.0.2.0` disposable test-family update lane'ini ve package output-directory düzeltmesini içeren commit `bfe5960d5bc911e21ba1e1e1675deb575400e531` için 3/3 işi yeşil, üç artifact ve `9m16s` toplam süre gösterir; commit-bound package job'u lifecycle betiğini zorunlu çalıştırdığı için update lane hosted execution'ı workflow/UI düzeyinde **VERIFIED**dır. Run number `#21` UI kaydı, channel deletion primitive'ini içeren commit `0ef0da5793dc4fd1f4e83dbe38cd49d7a7ccb56f` için 3/3 işi yeşil, üç artifact ve `9m25s` toplam süre gösterir; commit-bound hosted execution workflow/UI düzeyinde **VERIFIED**dır. GitHub database run ID'leri, job log/test sayıları ve indirilen artifact JSON/ZIP/içerik/tam digest bağları doğrulanmamıştır. Protected-catalog comparative benchmark'ı kapanmıştır; immutable cross-file container production için `NO-GO`dur. M4 source-lifecycle/coordinator/reconciliation ve farklı-user hosted execution nedeniyle `IN PROGRESS` kalır. Resolved-origin M5'in; security-reviewed aynı-SQLite-transaction production yerleşimi ve end-to-end/crash kanıtı M8'in; production PFN lifecycle/repair/identity/Store kabulü M15'in kapsamındadır. Network/provider, parser, database, feature UI ve playback implementation'ı hâlâ yoktur.

Run `#18`den sonra lifecycle harness schema v3'e genişletilmiştir: updated disposable package için reset + fresh create ve canlı protected state varken exact current-user uninstall + aynı updated MSIX reinstall + ikinci fresh create/delete zinciri implemented durumdadır. Run number `#19`, commit `71dbb0cf23467d978674976796e2bbceca01494a` için broad `PackageReset` stage'inde fail olmuştur; bu historical stage invocation ile registration/identity/manifest postcondition'larını ayırmadığı ve HRESULT'ı göstermediği için `#19` native reset sonucu `UNVERIFIED`dır. Takip implementation'ı bu üç aşamayı ayrı stable stage/code'lara böler, raw diagnostic yerine allowlist'li deployment failure `Code`u üretir ve yalnız `0x80073D00`, `0x80073D02`, `0x80073D05` için toplam en fazla üç bounded retry uygular [S93]. Bu retry politikası `INFERENCE`dır; run `#20` UI'ı retry'ın kullanılıp kullanılmadığını göstermez. Takip commit'i `6f30e3fa92f329c1fa26333c591edb5148261cec` için run number `#20` 3/3 işi yeşil, üç artifact ve `8m14s` toplam süreyle tamamlandığından hardening ile schema-v3 reset/live-state uninstall/reinstall zincirinin hosted execution'ı workflow/UI düzeyinde **VERIFIED**dır. Artifact içeriği bağımsız doğrulanmamıştır; `#19` tarihsel failure ve `#18` update **VERIFIED** kaydı korunur. Comparative benchmark kapanmıştır; M4 source-lifecycle/coordinator/reconciliation ve farklı-user hosted execution nedeniyle açık kalır. M5 resolved-origin, M8 production SQLite transaction/end-to-end/crash ve M15 production PFN lifecycle/repair/identity/Store işleri ayrı milestone kapsamındadır.

Run number `#24` UI kaydı, yalnız documentation değişikliği olan commit `207455a54d2d7ac9b6b5c1ce8eb5e29bbee0c383` için 3/3 yeşil iş, üç artifact ve `9m02s` toplam süre gösterir. Bu hosted kayıt protected-catalog adayının Smoke veya `Decision` execution kanıtı değildir.

Run number `#25` UI kaydı, `test(windows): spike protected catalog layout` başlıklı commit `786151cef6c60de5e34009ebb6643ec804490ee9` için 3/3 işi yeşil, üç artifact ve `8m32s` toplam süre gösterir. Commit-bound normal workflow protected-catalog projesini solution'ın Debug/Release x64 build'lerinde derlemiş; architecture guard'ları production dependency graph'ından ve normal workflow invocation'ından ayrımı denetlemiş, package smoke da çıkarılmış production MSIX'te `IptvSuite.ProtectedCatalogSpike*` denylist'ini uygulamıştır. Hosted compile/isolation/package-exclusion sonucu workflow/UI düzeyinde **VERIFIED**dır; aday executable'ı `Smoke` veya `Decision` modunda çalıştırılmamış ve candidate evidence yüklenmemiştir. Job log/test sayıları ile artifact JSON/ZIP/içerik/tam digest bağları bağımsız doğrulanmamıştır. Bu kayıt production persistence seçimi değildir; M4 `IN PROGRESS`, ADR-003 `Proposed` kalır.

M4 farklı-user boundary bileşeni production graph'ından ayrı `IptvSuite.DpapiUserBoundaryHarness` + elevated controller'dır. Harness plain `net10.0`, framework-dependent x64, nonpackable/nonpublishable test executable'ıdır; yalnız Domain/Application/Infrastructure/Testing'e ve doğrudan mevcut `ProtectedData` paketine bağlıdır. Solution Debug/Release x64 build'i projeyi derler fakat deploy etmez; production MSIX payload denylist'i assembly adını exact reddeder. Creator → farklı SID'li non-admin child → creator üç-aşamalı protokol raw `CurrentUser` DPAPI reddini, production adapter'ın `ProtectedRecordUnavailable` + null-lease davranışını, child'ın kendi raw/adapter round-trip'ini ve creator kaydının değişmeden kalmasını doğrular. Controller target security context/profile için `CreateProcessWithLogonW(LOGON_WITH_PROFILE)` kullanır; `LOGON_NETCREDENTIALS_ONLY` yasaktır [S107–S111]. Sanitized evidence exact allowlist'teki sabit metadata, exact SDK, clean commit SHA, controller script SHA-256, staged harness assembly SHA-256 ve Boolean sonuçları taşır; credential/payload/reference/path/user/SID/protected-record digest taşımaz ve account/profile/workspace cleanup başarının parçasıdır. Kod/static dependency ve workflow sözleşmesi `IMPLEMENTED`, hosted execution **UNVERIFIED**dır; local OS-user lane bu çalışmada yürütülmemiştir. Bu ayrım M4 raw/adapter user boundary'sidir; production package registration/PFN/Store identity altında ikinci-user ve clean-machine kabulü M15'tedir. M4 `IN PROGRESS`, ADR-003 `Proposed` kalır.

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

M4'ün ilk gerçek Application orchestration'ı `SourceDraftProtectionService`'tir: pure validation store mutation'ından önce tamamlanır; fresh `SourceConfigurationId` semantic owner'ı ve bounded/versioned secret payload typed `ISecretStore` create operation'ına verilir; başarılı commit'ten dönen exact opaque reference, owner ve onu üreten `SourceId` aynı draft'a bağlanır, temporary buffer sıfırlanır. `ContentSource` kimlik/ad/configuration üçlüsünü bu draft'tan birlikte alır. `SourceConfigurationProtectedRecordDeletionService`, yalnız durable `DeletionPending` olduğu varsayılan aggregate'ten exact configuration owner/purpose/reference tuple'ını türetip o tek credential veya remote-playlist locator kaydını idempotent siler; ayrı key parçaları kabul etmez. `SourceChannelProtectedRecordDeletionService` ise aynı source ile authoritative persistence'tan yüklenmesi gereken `PlaylistSnapshot` + `LiveChannel` zincirini kabul eder; source→snapshot, snapshot→channel ve stable-key→source ilişkilerini doğrulayıp `ChannelId` owner ile varsa protected stream'i, ardından optional logo kaydını exact/idempotent siler. İlk başarılı channel delete commit'inden sonra newly-requested cancellation ikinci exact delete'i durdurmaz; kısmi failure retry'ı, store yeniden başarılı olduğunda aynı zincirin sonuca yakınsamasına izin verir. Bu ilişki kontrolü provenance veya authorization kurmaz. DPAPI store envelope v2; source, purpose, owner kind/ID ve reference kind/ID'yi entropy, dosya adı ve encrypted envelope context'ine birlikte bağlar. Caller abandonment/retry, duplicate create, process crash/OOM, source status persistence, admission/in-flight drain, bütün channel'ların enumeration'ı, metadata/cache cleanup, orphan reconciliation ve source-wide coordinator kanıtlanmaz. Internal decoder source payload v1'i exact magic/version/length/UTF-8/scalar sözleşmesiyle doğrular ve yalnız numeric slice metadata'sı üretir. Public resolve consumer'ı M5 origin policy ve deletion/in-flight gate tamamlanana kadar açılmaz. Channel primitive, DPAPI user-boundary statik contract'ı dahil güncel local `201/201 ×2` worktree gate'inde ve ayrıca commit `0ef0da5793dc4fd1f4e83dbe38cd49d7a7ccb56f` üzerindeki run number `#21` ile workflow/UI düzeyinde hosted **VERIFIED**dır; local gate gerçek-user executable'ını çalıştırmamış, artifact içeriği bağımsız doğrulanmamıştır.

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
- Packaged composition root'ta `ApplicationData.LocalCachePath\ProtectedStore\v2`; tek factory call, retained store, initialization failure'da pencere öncesi fail-closed ve fallback yasağı. V1 pre-release namespace otomatik okunmaz/silinmez; owner metadata'sı olmadan migration yapılmaz.
- Startup stale-temp cleanup için 1.024 aday cap'i; aşımda cleanup mutation'ı yapmadan fail-closed. Bu aynı-user candidate-fill DoS'unu engellemez.
- Credential Locker 20 kayıt/roaming/bulk locator nedeniyle primary değil.
- Commit `fc96a211171d1e4f5e5f02174da6c565ef2d59bb` üzerindeki 5k/10k/20k/50k × 20 local `Decision` evidence'ında 50k create p95 `151.447 s` ve create allocation p95 `668.007 MiB`dir. `INFERENCE`: per-record DPAPI bulk channel-locator layout'ı tek başına 50k normalize + protected persistence + index için birleşik ≤3 s süre bütçesini ve bütün-import ≤150 MiB allocation bütçesini karşılamadığı için reddedilmiştir; bu source-credential kararı değildir. Ayrıntı: [M4 secret-store Decision evidence](../quality/M4_SECRET_STORE_DECISION_EVIDENCE.md).
- Ayrı `IptvSuite.ProtectedCatalogSpike`, source/snapshot başına immutable big-endian/footerless container'da fresh DEK'i CurrentUser DPAPI ile sarıp kayıtları AES-256-GCM ile koruyan test-only adaydır. Clean/eligible commit `7cd5bf739712a5cbdcf47634275a113faab8c48e` üzerindeki 5k–50k × 20 Decision'da bütün sample/hash/aggregate kontrolleri, 22 reader Boolean'ı, fault/cancellation, canary ve cleanup **VERIFIED**dır. 50k p95 create+activate `173,128 ms`, allocation `34,676 MiB`, boundary working-set delta `15,855 MiB`, aynı-process reopen+unwrap `13,231 ms`, 256-read `1,695 ms`, snapshot delete `2,321 ms`, disk `16,022 MiB` ve staging cancellation `5,570 ms`dir; post-request mutation/artifact sayıları `0`dır. Baseline'a göre create `874,766×`, allocation `19,264×` iyileşmiştir. `209.828` byte summary SHA-256 `4a219e802dcff959644bda42489f0ee5231be63df1eb37d034e2ed16ff0859de`dir. Runner threshold uygulamaz; component'in referans bütçelerin altında kalması comparative benchmark gate'ini kapatan **INFERENCE**dır, end-to-end PASS değildir. Cross-file container production için **NO-GO**; security-reviewed yön DPAPI-wrapped per-snapshot DEK, per-row AES-GCM ve active snapshot'ı aynı SQLite transaction domain'inde tutmaktır. Bu M8 yönü `Proposed`/unimplemented; M4 `IN PROGRESS`, ADR-003 `Proposed` kalır. Ayrıntı: [M4 protected-catalog Decision evidence](../quality/M4_PROTECTED_CATALOG_DECISION_EVIDENCE.md).
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

Composition root M4'te secret-store factory'sini doğrudan ve tek kez wire eder; başarılı store'u app lifetime boyunca tutar, typed initialization failure'da UI oluşturmadan fail-closed olur ve fallback yaratmaz. Bu tek dependency için DI paketi eklenmemiştir. Gelecekte birden fazla gerçek adapter aynı lifecycle/composition ihtiyacını kanıtlarsa stable minimal container ayrı dependency kararıyla eklenebilir. Source adapter, transport, clock, secret store, catalog store, image fetch/cache ve player seam'leri injection alır. “Her sınıfa interface” yoktur.

Test sınırları:

- pure domain/parser/unit;
- local fake HTTP + SQLite + protected-store integration;
- provider contract;
- test-only protected-catalog 5k–50k comparative performance ve M8 production SQLite end-to-end performance;
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
IptvSuite.ProtectedCatalogSpike -> Testing + ProtectedData

Production projects -X-> any test project
```

`IptvSuite.Testing`; deterministic fake time, scripted in-process transport, M2 in-memory test secret store, passive player double, loopback Kestrel, guarded temp directory, timeout, canary scan ve fixture generator içerir. Bu M2 fake'leri application/domain port'u tanımlamaz ve production tarafından referans alınmaz. M4-specific fake ve gerçek DPAPI senaryoları `IptvSuite.IntegrationTests` içindedir; test projesi Application/Infrastructure'a yalnız test yönünde referans verir. `IptvSuite.SecretStoreSpike` yalnız opt-in Release x64 ölçüm executable'ıdır; production package veya normal quality/CI runtime'ına girmez. `IptvSuite.PackageLifecycleHarness` ayrı disposable identity'li, nonpublishable x64 test MSIX'i; `IptvSuite.DpapiUserBoundaryHarness` ise unpackaged, framework-dependent ve nonpublishable x64 test executable'ıdır. Production Windows projesi ikisini de referanslamaz ve production payload taraması iki assembly adını da reddeder. Unit pure Application/Domain ve harness davranışını; integration loopback/temp/DPAPI contract davranışını; architecture dependency allowlist'ini doğrular. Signed installed MSIX smoke, packaged lifecycle ve gerçek local-user DPAPI boundary ayrı elevated host lane'leridir.

`IptvSuite.ProtectedCatalogSpike` yalnız `Testing` ve mevcut `ProtectedData` paketine bağlı nonpackable/nonpublishable x64 adaydır; solution ile build edilir fakat normal quality/hosted akışta executable olarak çağrılmaz. Production Windows projesi onu referanslamaz ve production payload taraması assembly/artifact adlarını reddeder.

Generator `1.0.0` / algorithm `1` / seed `20260809` ile byte-identical sentetik record ve SHA-256/provenance manifesti üretir. Corpus gerçek provider/account/credential/playlist/medya içermez. Internal `LicenseRef-IPTVSuite-Synthetic-Test-Only` durumu `UNVERIFIED` olduğundan public paylaşım hakkı değildir. Canary de gerçek secret değil, bilinen marker sızıntısını kanıtlayan test girdisidir; genel credential/redaction kanıtı sayılmaz.

Local `Invoke-WindowsQualityGate.ps1`; exact SDK locked restore, Debug/Release x64 build, full suite'in ayrı TRX dizinlerinde iki ardışık yeşil koşusu ve eşit sonuç seti, fixture SHA-256 eşitliği, armed sentinel non-zero/recovery ve artifact canary kontrollerini birleştirir. Hosted workflow bütün PR, `merge_group`, `main` push ve manual dispatch olaylarında exact SDK ile bu gate'i çalıştırır; required check'in path filter nedeniyle `Pending` kalmaması için üst seviye path filter yoktur. Quality sonrasındaki package job'u signed x64 MSIX'i hedefli test-payload/canary sızıntısına karşı inceler, kurar, görünür AUMID launch yapar, güncel exact package-family `LocalCache\ProtectedStore\v2` directory initialization'ını/reparse-point yasağını kontrol eder, normal kapatır ve exact cleanup uygular. Başarılı evidence `ProtectedStoreDirectoryInitialized=true` taşır; bu yalnız o committe beklenen schema directory initialization kanıtıdır, DPAPI CRUD veya lifecycle kanıtı değildir. `always()` coordinator'ı quality fail veya package skip/fail durumunu tek `Required Windows gate` sonucuna taşır.

Aynı package job'undaki ikinci lane production app'e hidden test mode eklemek yerine ayrı test MSIX'ini aynı disposable identity/publisher ve signer ile iki kez üretir: baseline `0.0.1.0` ve update `0.0.2.0`. İlk exact-PID baseline activation owner-bound v2 kaydı ile DPAPI-protected control ticket'ı oluşturur; ikinci baseline process aynı ticket varken duplicate create'i reddeder. Daha yüksek sürüm normal `Add-AppxPackage` yoluyla aynı package family üzerine kurulduktan sonra üçüncü, updated-package process ticket ile baseline kaydını okur; yanlış owner'ı reddeder, doğru owner ile update/read/delete ve post-delete unavailable zincirini tamamlar. Exact `ProtectedStore` ile lifecycle run-directory canary scan'leri create, post-update ve final aşamalarında; exact package/certificate/app-data/output cleanup success evidence öncesinde yürütülür. Windows-managed package hive'ları bu tarama kanıtına dahil değildir ve test MSIX'leri upload edilmez. Scanner bulgu, operasyonel hata ve CLI contract sapmasını ayrı stable failure code'larıyla fail-closed tutar. Sanitized evidence schema v2 iki package file/version/SHA-256/signature alanını, same-signer/family, changed-full-name, update-installed, post-update read/scan ve lifecycle/cleanup sonuçlarını allowlist'ler. Run number `#15` yalnız tarihsel same-version/same-PFN/same-user process-restart lane'ini, run number `#16` ise yeni lane'den önceki exact deletion primitive commit'ini workflow/UI düzeyinde **VERIFIED** yapar. Run number `#18`, commit `bfe5960d5bc911e21ba1e1e1675deb575400e531` üzerindeki signed two-version lane hosted execution'ını workflow/UI düzeyinde **VERIFIED** yapar; evidence schema v2 artifact'ı indirilmediği için alan değerleri, ZIP içeriği ve tam hash bağı bağımsız doğrulanmamıştır. Bu lane production PFN update'i, reset/repair/uninstall-reinstall, second-user veya crash recovery kanıtı değildir.

Schema v3 aynı test identity'sinde önce canlı record/ticket seed'i üretir ve `Reset-AppxPackage` çağrısını exact updated package full name ile yapar. Version/family/full-name kimliği değişmeden exact owned state'in manual cleanup öncesinde yok olması ve reset sonrası create'in farklı record identity üretmesi gerekir. Bu yeni state canlıyken exact current-user package preserve-data veya `-AllUsers` kullanılmadan kaldırılır; registration ve package-family app data yokluğu doğrulanır. Aynı signed `0.0.2.0` MSIX/dependency reinstall'i aynı package kimliğini geri getirmeli, boş owned state ile başlamalı ve ikinci farklı record identity üzerinden fresh create/delete'i tamamlamalıdır. Reset ile data-preserving repair ayrı platform davranışlarıdır; clean uninstall package state'i kaldırır [S91][S92]. Sanitized schema v3 yalnız reset, identity-preservation, owned-state removal, live-state uninstall, app-data removal, reinstall ve fresh-create/record-identity-change Boolean'larını ekler; path, PFN/full-name, record leaf, reference, source/owner ve kullanıcı kimliğini yayımlamaz. Architecture testi adım sırasını, reset/uninstall incelemesinden önce manual cleanup yapılmamasını, live-state önkoşulunu, preserve/all-users yasağını ve evidence denylist'ini kilitler. Run number `#20`, commit `6f30e3fa92f329c1fa26333c591edb5148261cec` için bu kodun hosted execution'ını workflow/UI düzeyinde **VERIFIED** yapmıştır; artifact alanları/ZIP/içerik/tam hash bağı bağımsız doğrulanmamıştır. Wrong-user ve source-wide lifecycle coordinator/reconciliation M4, resolved-origin M5, production PFN/repair/identity M15 sınırlarında açıktır. Bulk channel locator comparative benchmark'ı tamamlanmıştır; M8 aynı-SQLite-transaction production implementasyonu ile crash/power-loss ve end-to-end kanıtı açık kalır.

Local `Decision` koşusu clean commit `fc96a211171d1e4f5e5f02174da6c565ef2d59bb` üzerinde 5k/10k/20k/50k ölçeklerinin her birini 20 tur tamamlamış; sanitized evidence ile repository/spec/runner/workload hash bağları doğrulanmıştır. 50k create p95 `151.447 s`, create allocation p95 `668.007 MiB`; cancellation request→completion p95 `1.067 ms`, post-cancellation commit üst sınırı `0`dır. Cancellation sonucu kendi ≤250 ms sınırını karşılar; restart ve 256-record read yalnız dar unpackaged problardır ve production PFN/`LocalCache`, full-read/restart, parser/normalize/index veya UI çıkarımı sağlamaz. Bu nedenle M4 `IN PROGRESS`, ADR-003 `Proposed` kalır; ayrıntı [kalıcı Decision evidence](../quality/M4_SECRET_STORE_DECISION_EVIDENCE.md) belgesindedir.

Run `#19`, bu schema-v3 zincirini yalnız broad `PackageReset` failure noktasına kadar gözlemletmiştir. Historical failure code, `Reset-AppxPackage` çağrısının kendisi ile hemen sonraki registration/identity/manifest postcondition'larını ve HRESULT sınıfını ayıramaz; bu yüzden `#19` için native reset PASS/FAIL nedeni ve aşağı akış uninstall/reinstall sonucu bilinmez. Takip kodu `PackageResetInvocation`, `PackageResetRegistrationValidation` ve `PackageResetManifestValidation` stage'lerini ayırır; raw HRESULT/message/ActivityId yayımlamadan bilinen deployment HRESULT'ını allowlist'li stable `Code`a map eder. S93'teki update-in-progress, packages-in-use ve app-data-delete hata anlamlarından türetilen mühendislik politikası (`INFERENCE`), yalnız `0x80073D00`, `0x80073D02` ve `0x80073D05` sınıflarını bounded exact-process quiescence sonrasında 500 ms/1500 ms beklemeli toplam üç denemeye kadar retry eder; diğer sınıflar retry edilmez [S93]. Run `#20` UI kaydı retry'ın tetiklenip tetiklenmediğini veya attempt sayısını göstermez. Bununla birlikte bu run, commit `6f30e3fa92f329c1fa26333c591edb5148261cec` için 3/3 yeşil iş, üç artifact ve `8m14s` toplam süre gösterir; commit-bound package işindeki zorunlu lifecycle adımı nedeniyle tanı hardening'i ile tüm schema-v3 zincirinin hosted execution'ı workflow/UI düzeyinde **VERIFIED**dır. Artifact'lar indirilmediği için allowlist alanları, ZIP içeriği ve tam hash bağı bağımsız doğrulanmamıştır. M4 `IN PROGRESS`, ADR-003 `Proposed` kalır.

Run number `#22` UI kaydı, threat-model sınırı ile production `asInvoker` / `uiAccess=false` executable manifestini içeren commit `43bf88c591bdc216d3a288ca578f90e4fad927c6` için 3/3 işi yeşil, üç artifact ve `8m23s` toplam süre gösterir. Exact architecture gate, signed production MSIX install/launch smoke'u ve disposable lifecycle adımı aynı commit-bound workflow'da geçtiğinden declarative least-privilege manifest sınırı ile packaged execution workflow/UI düzeyinde **VERIFIED**dır. Bu kanıt R26 kararının doğruluğunu veya runtime token'ın non-elevated olduğunu doğrulamaz; path/reparse yarışını atomik kapatmaz ve same-user process isolation sağlamaz. Artifact alanları, ZIP/içerik/tam digest, job log/test sayısı ve GitHub database run ID'si bağımsız doğrulanmamıştır.

Bir local/hosted komut tanımının varlığı başarılı run kanıtı değildir. Tarihsel M2 local exact-SDK gate'i 2026-08-09'da 22 testi iki koşuda geçmiş; M3 sonrası local gate aynı gün 105 testi iki koşuda geçmiştir. M2 hosted run `31327398270` üç işi de geçirmiş; iki sanitized artifact'ın commit bağı, quality şekli, fixture hashleri, signature, capability allowlist'i ve package smoke sonuçları `14/14` assertion ile doğrulanmıştır. M4 hosted run `31735655363` önceki paragraftaki 153-test/v1 store snapshot'ını doğrular. Owner-v2 snapshot'ı 171 test, exact configuration-record deletion primitive'i dahil tarihsel snapshot 183 test; channel primitive'i dahil tarihsel local snapshot 198 test ile exact-SDK iki-run kanıtına sahiptir. Sonuncunun summary'si `commitSha=null` olduğundan local sonuç tek başına commit-bound değildir. Run number `#15`in UI kaydı commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` için ilk owner-v2/same-version lifecycle geçişindeki üç hosted işi ve üç artifact'ı başarıyla gösterir. Run number `#16`nın UI kaydı ise exact deletion primitive'ini içeren commit `3e806edd9a9482b87cccbf000ac8a81823562f6a` için 3/3 işi yeşil, üç artifact ve `8m24s` toplam süre gösterir. Run number `#18`in UI kaydı da signed `0.0.1.0 → 0.0.2.0` lane'ini içeren commit `bfe5960d5bc911e21ba1e1e1675deb575400e531` için 3/3 işi yeşil, üç artifact ve `9m16s` toplam süre gösterir; commit-bound package job'u lifecycle betiğini zorunlu çalıştırdığından update execution workflow/UI düzeyinde **VERIFIED**dır. Run number `#20` UI kaydı reset hardening'ini içeren commit `6f30e3fa92f329c1fa26333c591edb5148261cec` için 3/3 işi yeşil, üç artifact ve `8m14s` toplam süre gösterir; aynı zorunlu adım bağı schema-v3 reset/live-state uninstall/reinstall execution'ını workflow/UI düzeyinde **VERIFIED** yapar. Run number `#21` UI kaydı channel primitive'i içeren commit `0ef0da5793dc4fd1f4e83dbe38cd49d7a7ccb56f` için 3/3 işi yeşil, üç artifact ve `9m25s` toplam süre gösterir; hosted execution workflow/UI düzeyinde **VERIFIED**dır. Database run ID'leri, job log/test sayıları, downloaded artifact schema alanları ve ZIP/içerik/tam digest bağları incelenmediği için önceki M2 artifact'ının `14/14` doğrulamasıyla eşdeğer artifact-content kanıtı sayılmaz. Artifact taraması GitHub job logunu kapsamaz; log canary sonucu `UNVERIFIED` kalır. Yeşil hosted smoke yalnız çalıştığı runner ve disposable test PFN/commit için package zincirini kanıtlar; feature UI/UIA/accessibility, production PFN update/reset/repair/uninstall-reinstall, WACK/Store, second-user, gerçek provider/player/codec, genel secret yokluğu, ARM64 veya cihaz matrisi pass'i değildir. Branch policy enforcement mevcut private-repository planında etkin değildir. Ayrıntılı kalıcı M2 kaydı [M2 completion evidence](../quality/M2_COMPLETION_EVIDENCE.md) belgesindedir.

Tarihsel M2 hosted artifact'ı `ProtectedStoreDirectoryInitialized` alanını taşımaz; alan run `31735655363` packaged artifact'ında doğrulanmıştır.

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
| O2 | INFERENCE / OPEN, 2026-08-14 | Comparative gate kapandı; security-reviewed aynı-SQLite-transaction yönü production crash/recovery ve end-to-end bütçelerini karşılar mı? | [Baseline evidence](../quality/M4_SECRET_STORE_DECISION_EVIDENCE.md) + [candidate Decision evidence](../quality/M4_PROTECTED_CATALOG_DECISION_EVIDENCE.md) + M8 production implementation/fault injection/end-to-end benchmark | Security/Data / M4–M8 |
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
