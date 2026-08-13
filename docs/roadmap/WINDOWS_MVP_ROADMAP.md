# Windows MVP yol haritası

**Tarih:** 2026-08-09

**Durum:** Phase 0 implementation plan; M1, M2 ve M3 completed, M4 foundation in progress

**Kural:** Her milestone clean checkout'tan build/test edilebilir, sentetik veriyle demo edilebilir ve geri alınabilir olmalıdır.

Bu sıra temel Live TV playback zincirini bitirmeden VOD, Series veya EPG'yi başlatmaz. Exact komutlar M1'de gerçek projeden doğrulanıp `AGENTS.md`'ye yazılacaktır; Phase 0'da uydurulmamıştır.

## M1 — Repository ve reproducible Windows bootstrap

**Implementation status:** Completed — 2026-08-09

### Amaç

Boş workspace'i, yalnız Windows için minimal ve tekrarlanabilir .NET 10/WinUI 3 solution'ına dönüştürmek.

### Kapsam

- Workspace'in intended repository root olduğunun doğrulanması ve Git metadata yoksa bu kökte initialize edilmesi.
- .NET 10 LTS ve bootstrap günündeki current stable Windows App SDK exact patch'inin pinlenmesi.
- Yalnız gerekli Windows App, Domain, Application ve Infrastructure proje sınırları; test için en küçük architecture-test projesi.
- Packaged, framework-dependent MSIX development build; yalnız local/dev geçici identity, public Store/package/brand reservation yok.
- Common build/analyzer/nullable/warnings baseline ve deterministic restore.
- Clean restore, Debug/Release x64 build, test ve local launch komutlarının README/AGENTS'e gerçek çıktıyla kaydı.

### Kapsam dışı

Provider, parser, database, secret-store implementation, playback dependency/surface, final navigation/design, CI ve Store submission.

### Acceptance criteria

- Clean checkout'ta belgelenen tek akış restore/build/test'i geçirir.
- Debug ve Release x64 build başarılıdır; packaged app açılır, sürüm/build bilgisini gösterir ve düzgün kapanır.
- Domain WinUI/Infrastructure'a; Application Presentation'a bağımlı değildir.
- Preview/Experimental dependency ve gerçek credential yoktur.
- Public marka/package identifier kilitlenmemiştir.
- Repository çalışma ağacı ve build/test komutları açıkça belgelenmiştir.

### Testler / doğrulama

- Clean NuGet cache/clean machine restore provası.
- Debug/Release x64 build ve minimal test suite.
- Dependency-boundary assertion.
- Local MSIX install/launch/uninstall smoke.
- Package/dependency listesinde stable channel ve expected architecture kontrolü.

### Ana risk

WinUI template ayrıntısının domain'e sızması veya development identity'nin yanlışlıkla kalıcı Store kimliği sayılması.

### Demo çıktısı

Clean workspace'ten belgelenen komutla derlenip kurulan, build bilgisini gösteren boş Windows shell ve yeşil test sonucu.

### Uygulama kanıtı

- Git intended kökte `main` branch ile initialize edildi; public Store identity/association yok.
- .NET SDK `10.0.302`, WASDK `2.3.1`, BuildTools `10.0.26100.8249` ve MSTest `4.3.3` stable exact lock altında.
- Build çıktısı/.git içermeyen ayrı temp workspace + tamamen boş NuGet cache üzerinde locked restore; Debug/Release x64 build sıfır warning/error.
- Architecture/toolchain/manifest suite 6/6 geçti; Domain'e geçici yasak reference eklendiğinde 1 testin fail ettiği ve geri alındığında yeşile döndüğü doğrulandı.
- Signed framework-dependent x64 MSIX üretildi; installed effective manifest yalnız `runFullTrust` ve Windows App Runtime `2.3.1` dependency'si taşıdı.
- Paket kuruldu, AUMID ile görünür build-info shell açıldı, normal window-close ile kapandı; exact package, app-data ve ephemeral certificate temizlendi.

## M2 — Test altyapısı ve quality gates

**Implementation status:** Completed, 2026-08-09 — local ve hosted acceptance PASS

### Amaç

Sonraki her milestone'u gerçek provider/internet olmadan deterministik doğrulayacak test ve fixture altyapısı kurmak.

### Kapsam

- Unit, integration, architecture ve packaged UI launch smoke test sınırları.
- Fake clock, transport, secret store ve player.
- Local HTTP fixture server ve deterministic sentetik fixture generator/provenance manifesti.
- CI restore/build/test, test artifact ve timeout; required-check sonucu üretmek için workflow bütün PR'larda çalışır ve üst seviye path filter kullanmaz.
- Secret-canary scan için test helper; production secret değil.

Mevcut scaffold; production graph'ından bağımsız `IptvSuite.Testing`, `IptvSuite.UnitTests` ve `IptvSuite.IntegrationTests` projelerini kullanır. `IptvSuite.ArchitectureTests` M1'den korunur; packaged UI launch için yeni UI automation dependency'si yerine mevcut signed MSIX smoke seri lane olarak kalır.

### Kapsam dışı

Ürün domain kuralları, provider logic, parser, gerçek player, 50k release benchmark ve public CI secrets.

### Acceptance criteria

- Failing test pipeline'ı durdurur; fresh quality-artifact kökünde aynı built test binaries'inin iki ardışık koşusu aynı test sonuç setini verir.
- Testler internet, gerçek account ve yetkisiz media olmadan çalışır.
- UI launch smoke packaged host'ta geçer.
- Fixture seed/version/provenance kaydedilir.
- Log/artifact canary taraması test edilebilir durumdadır.
- Repository M1 komutlarıyla build/test edilebilir kalır.
- Exact SDK `10.0.302`, `rollForward: disable` ve `allowPrerelease: false` ile seçilir; başka SDK veya prerelease'e sessiz fallback olmaz.
- Unit/integration/architecture suite'i ayrı TRX dizinleriyle iki ardışık koşuda aynı discovered/pass/fail sonucunu verir.
- Başarılı packaged-host run'ı yalnız çalıştığı host/commit için signed payload-inspection/install/visible-launch/normal-close/uninstall kanıtıdır; UIA, Store, update, provider/player/codec veya cihaz matrisi kanıtı olarak yorumlanmaz.

### Testler / doğrulama

- Bilerek bozulan sample assertion'ın local/CI gate'i kırması ve geri düzeltmeyle yeşile dönmesi.
- Parallel test, temp-data isolation ve timeout provası.
- Local fixture server start/stop/port cleanup.
- Test artifact'ta dummy canary detect/non-detect senaryosu.

### Ana risk

Flaky UI/network testlerinin gate güvenini azaltması veya test helper'ın production koduna sızması.

### Demo çıktısı

Unit + integration + UI smoke sonuçları ve tamamen sentetik fixture manifesti olan yeşil pipeline.

### Mevcut uygulama ve completion evidence

- **Scaffold mevcut:** Resmî `FakeTimeProvider`, scripted transport, in-memory test secret store, passive fake player, loopback-only Kestrel, guarded temp directory, timeout helper, canary scanner ve deterministic generator eklendi.
- **Fixture kaydı mevcut:** Generator `1.0.0`, algorithm version `1`, seed `20260809`, SHA-256/provenance ve internal-only `UNVERIFIED` LicenseRef specification'da sabittir; gerçek provider/account/credential/playlist/medya yoktur.
- **Test sınırı mevcut:** Unit ve integration assembly'leri method-level parallel; OS-seçimli port/temp path isolation ve canary detect/non-detect testleri tanımlıdır. Test projelerinin production tarafından referans alınmaması architecture allowlist'ine eklenmiştir.
- **Local gate PASS — 2026-08-09:** isolated exact SDK `10.0.302` ile locked restore ve Debug/Release x64 build sıfır warning/error; architecture 8, unit 9 ve integration 5 test iki ayrı koşuda aynı 22/22 `Passed` setini verdi. Sentinel exact TRX'te armed `Failed`, disarmed `Passed` oldu; scanner CLI kontamine artifact'ta exit `2`, cleanup sonrası `0` verdi. Fixture iki output root'ta byte-identical üretildi (`records` SHA-256 `1da91c57da1f704076600aab29cdd938851d75f765679ac2b79dc9cb9e908020`, manifest SHA-256 `b1f1513e786f3176c7275af927c4c93c847d0476858fe56701d2054128818438`); final artifact-file canary scan geçti. Job log canary kapsamı `UNVERIFIED` kalır.
- **Hosted gate PASS — 2026-08-09:** [Run `31327398270`](https://github.com/serkankaracan/iptv-suite/actions/runs/31327398270), commit `79cf619c6683fa9c4213846455e376fb1b0cb11c` üzerinde `Locked build and test gate`, `Packaged install and launch smoke` ve `Required Windows gate` işlerinin üçünü de başarıyla tamamladı. Sanitized quality artifact'ı iki koşuda 22/22 sonucu ve local ile aynı fixture hash'lerini; packaged-smoke artifact'ı valid signature, yalnız `runFullTrust`, payload leak gate, visible launch/normal close ve exact package cleanup sonucunu taşıdı. Package SHA-256 `e04a7db236a263641e56e6c33fac4987ca774ba7a9af1bb963632ad8d1df8a2c`; artifact/alan doğrulama kaydı [M2 completion evidence](../quality/M2_COMPLETION_EVIDENCE.md) belgesindedir.

Bu kanıtla M2 mühendislik kabulü `Completed` ve yeşil pipeline demo çıktısı elde edilmiştir. `Required Windows gate` coordinator'ı sonuç üretse de 2026-08-09 GitHub preflight'ında mevcut private-repository planı branch protection için `403` döndürmüştür; merge enforcement ancak uygun plan veya repository visibility kararı sonrasında açılabilir ve M2 PASS iddiasına dahil değildir.

## M3 — Domain terminology, validation ve safe errors

**Implementation status:** Completed — 2026-08-09; local acceptance PASS

### Amaç

Provider/UI/player'dan bağımsız, tek ve güvenli Live TV domain dilini ve input validation'ı uygulamak.

### Kapsam

- `ContentSource`, source-kind configuration, `SafeEndpoint`, `SecretReference`, `PlaylistSnapshot`, `ChannelCategory`, `LiveChannel`, `ChannelStableKey`, `DomainError` value/contract'ları.
- Xtream ve remote playlist name/URL/scheme/length/Unicode validation.
- M3U channel catalog ile HLS manifest ayrım kuralı; `.m3u8` uzantısı tek başına karar değildir.
- Error taxonomy, retryability ve localized presentation resource key'leri.
- URI/header/untrusted-text redaction pure policy'sinin ilk sürümü.

### Kapsam dışı

Network çağrısı, secret persistence, parser, DB, UI form ve playback.

### Acceptance criteria

- Empty/malformed/unsupported/oversized input deterministik typed result üretir.
- Token/user-info taşıyan locator “secret” sınıfıdır; safe endpoint raw path/query taşımaz.
- Error/UI sonucu exception type, credential veya raw provider text içermez.
- Future Movie/Series/EPG production type olarak eklenmez.
- Domain katmanı platform/infrastructure dependency almaz.
- Clean build ve tüm table-driven tests geçer.

### Testler / doğrulama

- Boundary, Unicode NFC/NFD, IDNA, IPv4/IPv6, control/NUL ve length tests.
- HTTP/HTTPS dışı scheme, redirect hedefi ve credential-bearing URL cases.
- Nested/encoded query/header redaction corpus.
- Stable error serialization/localization-key tests.
- Architecture/dependency tests.

### Ana risk

URL canonicalization'ın provider locator'ını bozması veya “safe” display/log nesnesinin secret'ı geri üretebilmesi.

### Demo çıktısı

Sentetik Xtream/M3U configuration'larının güvenli kabul/ret ve localized error-key sonucunu gösteren test report/harness.

### Uygulama kanıtı

- Plain `net10.0` Domain; typed ID'ler, immutable Live TV contract'ları, exactly-one provider-item/protected-locator playback reference, versioned/source-scoped stable key, stable error/retry/resource-key registry ve typed result içerir; project/package/framework bağımlılığı yoktur.
- Xtream ve remote playlist validation HTTPS hard gate, NFC/Unicode scalar sınırları, invalid UTF-16/control/NUL, IDNA/IPv4/IPv6 ve opaque reference kurallarını uygular. Başarılı config raw username/password/path/query/user-info/locator döndürmez.
- Pure URI/header/untrusted-text redaction ile same/cross-origin redirect target policy'si network çağrısı yapmadan doğrulanır. Bounded content classifier `.m3u`/`.m3u8` uzantısını karar girdisi almaz ve catalog/HLS master/HLS media ayrımını içerikten yapar; gerçek parser M7'ye bırakılmıştır.
- Exact SDK locked gate Debug/Release x64 build'i sıfır warning/error ile tamamladı; architecture 9, unit 91 ve integration 5 olmak üzere **105/105 test**, iki ayrı koşuda aynı `Passed` setini verdi. Sentinel fail/recovery, deterministic fixture hashleri ve artifact canary scan de geçti.
- Network/provider, DPAPI/secret persistence, database, UI form ve playback eklenmedi; bunlar M4+ kapsamında açık kalır.
- Opaque reference ile protected record/source/endpoint binding'i M3 kanıtı değildir; issuance M4'te atomikleşir, resolved locator origin equality ve ref-swap testleri M5'te hard gate olur.

## M4 — Secure credential storage ve merkezi redaction

**Implementation status:** IN PROGRESS — 2026-08-13 local foundation gate PASS; milestone acceptance pending

### Amaç

Credential ve token-bearing locator'ları plaintext persistence/log/artifact'tan çıkarmak; ADR-003'ün exact layout kararını ölçümle kapatmak.

Mevcut dilim; typed Application `ISecretStore`, zeroing `SecretLease`, merkezi sanitizer, `ProtectedData 10.0.10`/CurrentUser adapter, packaged `LocalCache` factory ve fake/gerçek contract testlerini içerir. Factory beklenen initialization hatalarını diagnostic context taşımayan typed sonuca eşler; cancellation'ı exception olarak korur. Adapter startup'ı yalnız exact current-namespace, regular ve en az 24 saatlik ciphertext temp artığını bounded biçimde temizler; `.dpapi` kayıtlarını enumerate etmez. Local exact-SDK gate architecture 9, unit 100 ve integration 26 olmak üzere 135/135 testi iki kez aynı setle geçirmiş; source/purpose/reference'a bağlı kriptografik context, restart edilen adapter instance'ı, CRUD/update/delete, işlem başlamadan iptal edilmiş çağrıların mutation yapmaması, exact stale-temp cleanup, birbirinden bağımsız kayıtların concurrent create'i, aynı süreçte iki adapter instance'ı için same-key update/read/delete sıralaması, ciphertext swap/corruption ve canary-at-rest davranışı doğrulanmıştır. Arbitrary mid-I/O cancellation/interleaving ve cross-process sıralama bu kanıtın parçası değildir; sonuç packaged process/lifecycle veya gerçek wrong-user testi de değildir.

M4'ü açık tutan hard-gate'ler: source disable + in-flight operation drain + source-wide deletion + `DeletionPending` orchestration ve startup protected-record orphan reconciliation; aynı source/purpose içindeki referansı gerçek configuration/channel/endpoint owner'ına bağlayan semantic ref-swap politikası; handle-relative path/reparse hardening veya açık threat-model kararı; installed package two-launch/update/reset/uninstall; ikinci gerçek Windows user; 5k/10k/20k/50k ölçümü ve buna bağlı ADR-003 kararı. Tamamlanıp caller'a verilmiş bir plaintext lease delete ile geriye dönük revoke edilmez; lifecycle koordinatörü yeni resolve'ları durdurup aktif operasyonların kapanmasını beklemelidir. Bu kanıtlar olmadan aşağıdaki milestone acceptance maddeleri `UNVERIFIED` kalır.

### Kapsam

- `ISecretStore` contract ve Windows DPAPI CurrentUser/LOCAL=user packaged adapter.
- Source secret create/read/update/delete, opaque reference ve restart.
- 5k/10k/20k/50k protected-locator spike; DPAPI-per-record ile reviewed alternative karşılaştırması gerekiyorsa yalnız spike.
- Central structured sanitizer; URI/header/exception/native-text adapters.
- Source deletion disable→secret delete→metadata cleanup orchestration contract'ı.
- Packaged reset/update/uninstall ve orphan-reconciliation test harness.

### Kapsam dışı

Provider login, production catalog DB, cloud sync/export, own cryptographic scheme ve Credential Locker'a bulk JSON/locator yazma.

### Acceptance criteria

- Password/token/full sensitive URL app-data, test DB, config, log, crash/support artifact'ta plaintext bulunmaz.
- Restart sonrası authorized context secret'ı çözer; wrong user/context ve deleted source çözemez.
- Delete idempotent; partial failure source'u network-disabled `DeletionPending` yapar.
- 50k protected locator layout'ı 5 saniyelik total import bütçesinde ayrılan ≤3 saniyelik persistence hedefi için ölçülmüş karara sahiptir.
- ADR-003 Accepted yapılmış ya da ölçümlü alternatif için yeni Proposed decision vardır; belirsizlik gizlenmez.
- Clean build/test ve canary scan geçer.

### Testler / doğrulama

- Fake-store shared contract + real packaged Windows integration.
- Add/read/update/delete, restart, corrupt blob, wrong context.
- App update, reset, uninstall/reinstall, provisional identity change.
- DB/file/log/artifact binary canary scan.
- 5k–50k duration/allocation/working-set/cancellation.
- Crash injection ve startup orphan reconciliation.

### Ana risk

DPAPI-per-locator'ın performans bütçesini aşması veya app identity/lifecycle'ın protected data'yı orphan etmesi.

### Demo çıktısı

Canary secret'ın kaydedilip restart sonrası okunması, her output'ta maskelenmesi, silinmesi ve artifact scan'in sıfır hit vermesi; 50k karar raporu.

## M5 — HTTP/networking ve connection probe

### Amaç

Provider ve playlist trafiği için tek, bounded, cancel edilebilir ve redacted transport oluşturmak.

### Kapsam

- Long-lived/typed HTTP client lifecycle ve connection lifetime.
- Connect/request/total timeout, cancellation, decompression ve response byte budget.
- Manual bounded redirects, origin/scheme/downgrade ve cookie policy.
- TLS validation; credential-bearing HTTPS hard gate.
- Safe GET transient retry/backoff/jitter ve `Retry-After` cap.
- Connection probe: HEAD'e güvenmeden bounded GET; domain result mapping.
- Structured safe operation metrics.

### Kapsam dışı

Xtream endpoint semantiği, full playlist parsing, stream playback/reconnect, anonymous HTTP exception ve real provider.

### Acceptance criteria

- Success, DNS/connect/TLS, 401/403, 404, 408, 429, 5xx, timeout, cancel, redirect ve oversize typed sonuç verir.
- Sertifika/hostname validation kapatılamaz; HTTPS→HTTP ve cross-origin credential forwarding reddedilir.
- Auth/TLS/validation error retry edilmez; safe transient retry attempt/total budget içindedir.
- User cancellation bounded read/pipeline'ı keser.
- Log/artifact'ta canary credential/header/query sıfırdır.
- Clean build/test ve local fixture demo geçer.

### Testler / doğrulama

- Local HTTP(S) server: delayed headers/body, disconnect, gzip/bomb, redirect loop, origin change, invalid TLS.
- Fake-clock retry schedule ve cancellation.
- Cookies-disabled/cross-source isolation assertion.
- Request headers/URI ve response exception redaction scan.

### Ana risk

Convenience retry/redirect'in credential sızdırması veya iptal edilen işlemin arkada sürmesi.

### Demo çıktısı

Sentetik endpoint'e başarılı probe ile invalid TLS, bounded timeout ve güvenli redaction hata senaryoları.

## M6 — Xtream-compatible Live TV adapter

### Amaç

Kullanıcının yetkili Xtream-compatible source'undan yalnız MVP için gereken Live TV katalog verisini almak.

### Kapsam

- Account/auth probe, live categories ve live streams endpoint construction.
- Provider DTO tolerant parsing ve domain normalization input record'ları.
- Missing/null/string-number-bool, duplicate/unknown-field policies.
- Cancellation, safe error mapping ve response budgets.
- Playback için full URL persist etmeden provider stream key + secret reference.
- Sentetik local fake Xtream service contract suite.

### Kapsam dışı

VOD, Series, EPG, catch-up, real playback, provider-specific undocumented workaround ve gerçek account.

### Acceptance criteria

- Fake service'ten Live category/channel records alınır ve deterministic result üretilir.
- 200-body auth fail, 401/403, malformed/partial/oversized response doğru sınıflanır.
- VOD/Series/EPG endpoint'lerine hiç request yapılmadığı testle kanıtlanır.
- Credential/full stream URL persist veya log edilmez.
- Duplicate/missing ID warning/error policy'si contract'ta görünürdür.
- Clean build ve tüm provider contract tests geçer.

### Testler / doğrulama

- Success/empty/partial, 200-auth-fail, 401/403/429/5xx, timeout/cancel.
- Field type/null/unknown/duplicate/category-missing variants.
- Large Live-only JSON budget ve streaming/allocation observation.
- Endpoint path/encoding ve canary secret scan.

### Ana risk

“Xtream-compatible” adının tek standard sanılması ve fixture dışı provider varyantlarının domain'i kirletmesi.

### Demo çıktısı

Geliştirici-owned fake Xtream source'tan alınan, secretsiz Live category/channel listesi ve variant contract report.

## M7 — Remote M3U/M3U8 download ve incremental parser

### Amaç

Uzak user-authorized Extended M3U channel catalog'unu bounded, incremental ve cancel edilebilir biçimde domain record'larına dönüştürmek.

### Kapsam

- HTTP response stream'den BOM/encoding ve incremental line tokenizer.
- `#EXTM3U`, `#EXTINF`, `tvg-id`, `tvg-name`, `tvg-logo`, `group-title` ve common number hints.
- Attribute/line/item/total-byte budgets; unknown/malformed policy.
- Catalog ile HLS master/media manifest sniffing ayrımı.
- Valid HLS master/media manifest'i source display name ile tek channel snapshot yoluna route etme.
- Relative URL final authorized playlist origin'e resolve; unsafe scheme reddi.
- Duplicate/collision warning ve protected locator writer boundary.
- 100–50k sentetik golden/fuzz/performance corpus.

### Kapsam dışı

Local file import, HLS segment/media parsing, EPG/XMLTV, playlist editor, DB persistence ve UI.

### Acceptance criteria

- Valid catalog stable order/records üretir; complete download tek string/materialized object graph olmaz.
- Malformed/orphan/oversized/truncated input crash/hang etmez; policy'ye göre reject/skip+warning.
- HLS master/media manifest channel catalog olarak import edilmez; valid manifest tek-channel route, malformed manifest typed error üretir.
- Cancellation p95 ≤250 ms gözlenir ve partial result active state olmaz.
- Raw locator/log secret sıfır; protected writer yalnız opaque ref döndürür.
- 50k parser stage p95 hedefi ≤2 saniye referans koşulda ölçülür.
- Clean build ve golden/property/fuzz tests geçer.

### Testler / doğrulama

- BOM/CRLF/LF/Unicode/legacy-warning; quoted/unquoted/mixed-case attributes.
- Orphan/missing/duplicate/conflicting ID, invalid scheme, relative URL, query token.
- HLS master/media fixtures.
- Oversized line/value/items, truncated stream ve injected cancellation.
- 5k/10k/20k/50k duration/allocation.

### Ana risk

Sahadaki gevşek M3U varyantlarıyla bounded güvenlik policy'si arasında compatibility kaybı.

### Demo çıktısı

Uzak sentetik M3U'dan incremental üretilen category/channel records, warning özeti ve 50k parser raporu.

## M8 — Local persistence, cache ve indexing

### Amaç

Büyük katalogları atomik refresh, hızlı restart/query ve tam delete lifecycle ile device-local saklamak.

### Kapsam

- SQLite versioned schema/migration: source, snapshot, category, channel, protected locator ref, favorite ve sanitized sync run.
- Stable identity ve gerekli source/snapshot/category/search indexes.
- Bounded batch staging, validation ve one-transaction active snapshot switch.
- Content hash + HTTP validator + parser/normalization/schema version cache key.
- Concurrent read/refresh, old snapshot pruning ve source delete/orphan reconciliation.
- WAL kullanımı yalnız ölçümle.

### Kapsam dışı

EPG/VOD/Series tables, cloud sync/backup/export, UI ve player.

### Acceptance criteria

- Restart sonrası active catalog aynı result'la sorgulanır.
- Failed/cancelled/crashed refresh önceki complete snapshot'ı bozmaz.
- Forward migration atomic ve recovery planlıdır.
- DB/WAL/SHM'de plaintext credential/full locator canary hit'i sıfırdır.
- Source delete secret/locator/snapshot/cache/favorite policy'sini idempotent tamamlar.
- 50k normalize+protected persist+index p95 ≤3 saniye, total import p95 ≤5 saniye hedefi ölçülür.
- Clean build, migration, fault ve repository integration tests geçer.

### Testler / doğrulama

- Fresh DB, N-1→N, migration failure/corruption.
- Atomic activate, concurrent read/refresh ve kill/fault injection.
- Duplicate/upsert/stable favorite reconciliation.
- Cache hit/invalidation ve old snapshot prune.
- 5k–50k duration/DB size/allocation/working set/cancel.
- Source delete ve startup orphan scan.

### Ana risk

Long transaction/index veya protected-locator yazımının import bütçesini aşması; WAL/orphan lifecycle'ın secret bırakması.

### Demo çıktısı

50k sentetik katalog importu, app restart sonrası indexed query, failed refresh'te eski snapshot'ın korunması ve deletion proof.

## M9 — Categories/channels query ve virtualized UI

### Amaç

Player eklemeden 50.000 kanalı hızlı, klavye/erişilebilirlik uyumlu biçimde gezdirmek.

### Kapsam

- Source ve category seçimi; indexed windowed channel query.
- Virtualized channel list; debounced search/filter ve deterministic sort.
- Loading, empty, failed, stale/refresh durumları.
- Keyboard/focus ve UI Automation/Narrator adları.
- Logo placeholder; görünür pencereye yakın lazy fetch, concurrency/cache/cancellation.
- Channel selection yalnız playback placeholder command üretir.

### Kapsam dışı

Gerçek video/player, final visual polish/theme, EPG grid, VOD UI ve animation sistemi.

### Acceptance criteria

- 50k data 50k UI control veya full observable copy üretmez; realized container ≤300.
- Cached first visible content p95 ≤500 ms; indexed query p95 ≤100 ms.
- Input response p95 ≤100 ms, scroll frame p95 ≤33,3 ms, dropped frame <%1 ve >200 ms UI-thread stall sıfırdır.
- Rapid scroll/filter/source-change stale image/query sonucunu göstermez.
- Bütün MVP browsing keyboard ile yapılır ve temel UIA/Narrator smoke geçer.
- Clean build, view-model, integration ve packaged UI tests geçer.

### Testler / doğrulama

- Query/view-model unit + real SQLite integration.
- Realized-container count ve scroll ETW/frame trace.
- Rapid filter/category/source cancellation/race.
- Image scheme/address/size/MIME/cache/concurrency fixtures.
- UI Automation launch, focus order, name/role ve keyboard.

### Ana risk

Collection reset, synchronous DB/image decode veya bad template'in virtualization'ı bozup UI thread'i kilitlemesi.

### Demo çıktısı

50k sentetik channel catalog'da source/category/search/scroll/logo placeholder ve keyboard navigation çalışan player'sız tarayıcı.

## M10 — Playback compatibility, license ve MSIX spike

### Amaç

Proposed libVLC setinin codec/container, WinUI surface, stabilite, lisans ve packaged deployment hard gate'lerini production adapter yazmadan kanıtlamak.

### Kapsam

- Küçük throwaway harness; LibVLCSharp.WinUI 3.10.0 + compatible core + non-GPL VideoLAN.LibVLC.Windows 3.0.23.1 başlangıç seti.
- Windows native player ile Tier A baseline karşılaştırması.
- Tier A/B/C sentetik corpus; track/aspect/hardware decode gözlemi.
- Resize/fullscreen/DPI/multi-monitor/overlay/minimize/restore/sleep-resume.
- Network fault, bounded open/stop/cancel ve rapid switch.
- x64; ARM64 yalnız native package zinciri varsa.
- Release MSIX, WACK/private preflight dry run.
- SBOM, binary/plugin-to-source, notices/license ve codec patent counsel input listesi.
- 2 saat smoke ardından 8 saat/100 switch soak.

### Kapsam dışı

Production player abstraction/UI, final reconnect UX, real provider/stream, DRM, recording ve public Store submission.

### Acceptance criteria

- Tier A HLS-TS/direct HTTPS TS + H.264/AAC referans matrix'te %100 başlar; controlled-LAN startup p95 ≤3 saniye.
- Tier B sonucu capability tuple olarak kayıtlı; marketing guarantee yapılmaz.
- Black frame/airspace/z-order/fullscreen blocker, crash/deadlock/double audio yoktur.
- Stop/switch/cancel deterministik; 100 switch ve 8 saat soak geçer; memory budget ihlali yoktur.
- HW decode Intel/AMD/NVIDIA'da raporlu, fallback davranışı belirgindir.
- Release MSIX install/launch/plugin discovery ve WACK blocker'sızdır.
- GPL plugin/binary sıfır; exact SBOM/source/notices hazırdır. Patent/redistribution açıkları hukuk gate'i olarak kayıtlıdır.
- Diagnostics canary secret hit'i sıfırdır.
- ADR-002 Accepted yapılır veya başarısız gate + fallback için yeni karar açıkça yazılır.
- Spike dışında repository build/test yeşil kalır.

### Testler / doğrulama

- [Quality Strategy](../quality/QUALITY_AND_PERFORMANCE_STRATEGY.md) Tier/device/network matrix.
- 25 rapid + toplam 100 scripted switch; lifecycle/surface recreation.
- Intel/AMD/NVIDIA ve clean Windows; x64/conditional ARM64.
- 2/8 saat metrics/handle/memory/threads.
- Packaged install/update/uninstall smoke, WACK ve package content/license scan.

### Ana risk

Teknik demo playback'in geçip exact non-GPL/MSIX/long-soak setinin başarısız olması; scope baskısıyla gate'in görmezden gelinmesi.

### Demo çıktısı

Paketlenmiş spike'ta hakları temiz Tier A yayını, device/capability sonuç matrisi, 8 saat soak özeti ve signed go/no-go ADR güncellemesi.

## M11 — Production player adapter ve temel playback

### Amaç

Accepted motoru engine type'larını üst katmanlara sızdırmadan uygulama state modeline bağlamak.

### Kapsam

- `IPlaybackEngine`: open/play/pause/stop, volume/mute, aspect, track info/select, event/state, cancellation/dispose.
- App-owned single-session state machine ve monoton session ID.
- Just-in-time protected locator resolve; raw locator UI/domain/log'a dönmez.
- Engine event → typed `DomainError`/safe diagnostics mapping.
- Fake ve real adapter için ortak contract suite.
- Channel selection'dan embedded video surface'te Tier A playback.

### Kapsam dışı

Final fullscreen/zapping polish, automatic reconnect, PiP, casting, background audio, DRM.

### Acceptance criteria

- Engine/libVLC type'ı Application/Domain'e sızmaz.
- Fake ve real adapter aynı state/cancel/dispose contract tests'i geçer.
- Rapid A→B open, stop during opening ve stale callback invalid state/session üretmez.
- Tek active session; stop/dispose sonrası ghost audio/handle leak yoktur.
- Raw credential/locator/native log sızıntısı yoktur.
- Channel list'ten Tier A sentetik stream production adapter ile başlar.
- Clean build ve bütün tests geçer.

### Testler / doğrulama

- State transition/property ve callback race tests.
- Fake engine fault injection; wrong-order/late events.
- Real fixture integration; repeated open/stop/dispose.
- Canary URI/header native log/artifact scan.
- Packaged launch/play/close smoke.

### Ana risk

Native callback/thread/resource lifetime'ın adapter sınırını delip UI state veya process kararlılığını bozması.

### Demo çıktısı

Channel list'ten seçilen sentetik Tier A yayının app-owned adapter üzerinden play/pause/stop ve safe state göstergesiyle çalışması.

## M12 — Playback UX, fullscreen, volume, channel switch ve lifecycle

### Amaç

Canlı TV için erişilebilir temel kontrolleri ve Windows window/device lifecycle'ını kararlı hale getirmek.

### Kapsam

- Play/pause/stop, volume/mute, fullscreen, aspect fit/fill.
- Keyboard shortcuts, focus/UIA/Narrator.
- Previous/next veya list selection ile channel switching; old session cancel.
- Window close, minimize/restore, display/DPI/multi-monitor.
- Sleep/resume ve audio-output device değişimi.
- Single-session ve source/profile delete sırasında player release.

### Kapsam dışı

Reconnect policy (M13), background playback, PiP, casting, recording, timeshift ve final animation.

### Acceptance criteria

- Fullscreen enter/exit güvenli ve focus geri döner; multi-monitor/DPI'da surface doğru boyutlanır.
- 25 rapid switch'te hang/crash/double audio/ghost session yok; channel switch p95 ≤3 saniye controlled LAN'da.
- Close/source-delete/sleep player resource'unu tanımlı state'e getirir; resume kullanıcı niyetini ihlal etmez.
- Kontroller keyboard/UIA ile erişilebilir; technical exception göstermez.
- Handle/memory snapshot repeated lifecycle sonunda bütçe içindedir.
- Clean packaged build ve automated/manual lifecycle matrix geçer.

### Testler / doğrulama

- UI automation play/stop/volume/fullscreen/focus.
- Window/display/DPI/minimize/sleep/audio-device manual matrix.
- 25 rapid switch ve dispose/close race.
- Handle/thread/working-set before-after.
- Safe player state/log scan.

### Ana risk

UI ve native player lifecycle'ın ayrışarak invisible video, ghost audio veya suspend/resume leak üretmesi.

### Demo çıktısı

Channel browsing → playback → volume/mute → fullscreen → rapid switch → minimize/restore → clean close uçtan uca akışı.

## M13 — Error mapping, timeout, cancellation ve reconnect

### Amaç

Geçici network/stream kesintisinde kontrollü toparlanmak; kalıcı hatada kullanıcıya güvenli ve uygulanabilir mesaj vermek.

### Kapsam

- Auth, network, HTTP, playlist, unsupported, playback, reconnect, storage domain error taxonomy'sinin uçtan uca mapping'i.
- Retryability; bounded exponential backoff + jitter + total time/attempt budget.
- Playback reconnect countdown/state, user cancel ve manual retry.
- Channel/source change'de eski retry chain iptali.
- Offline/online signal yalnız hint; probe/engine sonucu authoritative.
- Safe diagnostics operation ID ve user-facing localized message.

### Kapsam dışı

Infinite/background retry, provider ban aşma, provider-specific workaround collection ve crash telemetry backend'i.

### Acceptance criteria

- Authentication, TLS, invalid playlist ve unsupported format automatic retry edilmez.
- Transient network/stream fail yalnız tanımlı attempt/total budget içinde retry edilir; `Retry-After` cap'e uyar.
- User cancellation ≤1 saniyede zinciri durdurur; yeni session açılmaz ve durdurulmuş yayın geri başlamaz.
- 401/403/404/429/5xx, stall, disconnect ve recovery doğru state/message üretir.
- Retry storm veya simultaneous session yoktur.
- Kullanıcı exception/type/raw provider response/URL görmez; canary scan sıfırdır.
- Clean build ve deterministic fake-clock/fault tests geçer.

### Testler / doğrulama

- Fake HTTP/player: DNS/TLS/timeout/stall/disconnect/EOF/401/403/404/429/5xx.
- Fake clock ile exact backoff/attempt/total budget.
- Cancel, channel switch, source delete ve app close race.
- Network recover/reconnect success ve exhausted result.
- Localized error mapping ve redaction.

### Ana risk

Reconnect storm'un provider ban'i, duplicate player session veya kullanıcının durdurduğu içeriğin yeniden başlamasına yol açması.

### Demo çıktısı

Kontrollü kesilen sentetik yayının bounded geri sayımla toparlanması; auth/TLS gibi kalıcı hatanın retry edilmeden doğru mesaja dönüşmesi.

## M14 — 50.000+ benchmark ve hot-path optimizasyonu

### Amaç

Özellik eklemeden, M3U→protected SQLite→virtualized UI zincirinin ölçülmüş MVP bütçelerini geçmesini sağlamak.

### Kapsam

- 100/5k/10k/20k/50k ve stress 100k sentetik corpora.
- Stage-separated download, parse, normalize, protect, DB/index, first-query ve UI ölçümü.
- Allocation, GC, working set, DB size, disk I/O, UI thread/frame/realized container.
- Hot-path trace ve yalnız evidence-backed optimization.
- Reproducible dedicated-runner benchmark report/baseline/regression gate.
- Image visible-window concurrency/cache stress.

### Kapsam dışı

Yeni feature, VOD/EPG, network download süresini import budget'a katma ve benchmark-only production hack.

### Acceptance criteria

- 50k parse p95 ≤2 s; normalize+protected persist+index p95 ≤3 s; total download hariç p95 ≤5 s.
- Import allocation ≤150 MiB, peak working-set delta ≤250 MiB ve cancellation p95 ≤250 ms.
- Cached first content ≤500 ms; query p95 ≤100 ms.
- Input p95 ≤100 ms; frame p95 ≤33,3 ms, dropped frame <%1, >200 ms UI stall sıfır, realized containers ≤300.
- Player-off steady catalog working set ≤350 MiB.
- Five defined corpora ve machine/OS/commit/schema/config report'u reproducible'dır.
- Bütçe değişikliği gerekiyorsa trace ve Product/Architecture onayıyla belgelenmiştir.
- Clean build/full regression yeşildir.

### Testler / doğrulama

- Release, instrumentation-free authoritative run; ≥20 iteration median/p90/p95/max.
- Cold/warm ayrı; power/thermal/background koşulu kaydı.
- ETW/.NET trace ile bottleneck; değişiklik sonrası aynı baseline.
- Rapid refresh/query/scroll/image cancellation.
- Same-machine regression threshold.

### Ana risk

Ortalamanın p95 stall/leak'i gizlemesi veya sentetik benchmark'a özel optimization'ın gerçek davranışı bozması.

### Demo çıktısı

50k katalogda akıcı browse/search ile stage, memory ve UI budget'larının karşılaştırıldığı reproducible benchmark report.

## M15 — MSIX ve Microsoft Store readiness

### Amaç

RC ile aynı bitlerin clean machine/private flight üzerinden kurulmasını ve teknik, privacy, içerik ve lisans gate'lerini kapatmak.

### Kapsam

- Framework-dependent MSIX; x64 ve yalnız native chain geçerse ARM64 bundle.
- Legal marka clearance sonrası public identity/reservation kararı; development identity migration testi.
- Manifest/capability minimization, assets/version ve install-dir write audit.
- Clean install, update from previous package, reset, uninstall ve data lifecycle.
- WACK, Partner Center private submission/preflight.
- Privacy/support URL, listing/onboarding authorized-source metni, rating ve reviewer notes.
- Geliştirici-owned sentetik reviewer service/account.
- Final SBOM, license notices, binary/source mapping, CVE ve codec/IP hukuk gate'i.

### Kapsam dışı

Public GA submission/marketing launch, unsupported architecture, yeni feature ve gerçek müşteri account'u.

### Acceptance criteria

- Clean VM/private flight install/launch ve previous package update geçer; expected user data korunur, secrets lifecycle testle doğrulanır.
- Uninstall/reset sonucu belgelenir; install directory'ye runtime write yoktur.
- WACK/Partner Center private preflight'ta blocking technical error yoktur.
- Capability seti minimum ve gerekçelidir.
- Reviewer sentetik source ile add→catalog→play zincirini tamamlar.
- Privacy/listing gerçek veri akışıyla uyumludur; Store policy release tarihine yakın yeniden doğrulanmıştır.
- Exact SBOM/license/notices/CVE ve uzman codec/IP review kapalıdır veya release blocker açıkça kaydedilmiştir.
- Marka/public package identity legal clearance olmadan kilitlenmez.
- Clean build/full tests geçer.

### Testler / doğrulama

- Clean install/update/reset/uninstall matrix.
- x64/conditional ARM64 native DLL inspection.
- WACK ve private Partner Center report.
- Package/app-data/log canary scan.
- Reviewer rehearsal ve synthetic service availability.
- Privacy/content/license/security checklist sign-off.

### Ana risk

Native DLL/capability/identity, IPTV content policy, privacy ya da codec/license sonucunun teknik ürün hazırken release'i durdurması.

### Demo çıktısı

Private Store flight'tan kurulmuş build'de sentetik source ekleme, 50k catalog browse ve Tier A playback; WACK/preflight/SBOM/privacy evidence pack.

## M16 — Windows MVP Release Candidate

### Amaç

Kapsamı dondurulmuş, geri alınabilir ve yayımlama kararına hazır Windows MVP adayı üretmek.

### Kapsam

- Feature freeze ve M1–M15 gate aggregation.
- Full automated regression, exploratory network/playback ve accessibility/keyboard review.
- Private-flight RC, clean install ve previous-version update.
- 24 saat/200 switch soak, resource/secret diagnostics.
- Final security/privacy/license/codec/Store/brand decision record.
- Support matrix, known issues, release notes, rollback/withdrawal ve dependency update planı.

### Kapsam dışı

VOD, Series, EPG/XMLTV, catch-up, recording, timeshift, cloud sync/backend, analytics, ads, Tizen/mobile ve yeni UI feature.

### Acceptance criteria

- M1–M15 bütün hard gate'leri yeşildir; açık P0/P1 yoktur. P2 yalnız owner, workaround ve yazılı release acceptance ile kalabilir.
- Profile/source → validation/probe → fetch/parse → persistence → browse/search → play/control → fault/reconnect → delete zinciri sentetik fixture ile geçer.
- 24 saat/200 switch'te crash/hang/deadlock/double audio yok; warm-up sonrası memory hem ≤%10 hem ≤100 MiB büyür ve monoton trend yoktur.
- Full log/app-data/package/support artifact canary scan sıfırdır.
- Private certification/reviewer rehearsal geçer.
- Supported capability/device matrix, privacy policy, license notices/SBOM ve known issues build ile eşleşir.
- Public submission yalnız Product/Legal onayıyla ayrı eylemdir.

### Testler / doğrulama

- Clean install + previous package update + reset/uninstall.
- Full unit/integration/contract/performance/UI/player/package suite.
- 24h soak ve network/lifecycle fault schedule.
- Keyboard/Narrator/UIA exploratory smoke.
- Final secret/PII/CVE/license/package architecture scan.
- Private-flight end-to-end reviewer rehearsal.

### Ana risk

Son dakika scope genişlemesi, unsupported provider vaadi veya dış hukuk/Store blocker'ını engineering “done” olarak yorumlamak.

### Demo çıktısı

Private flight RC'de source oluşturma → 50k catalog → search → live playback → controls/fullscreen → network fail/reconnect → source delete uçtan uca demo ve release evidence pack.

## M16 sonrası

Playback/Store/field verileri incelendikten sonra ayrı Product Brief/ADR/milestone ile sırasıyla VOD/Series, EPG/XMLTV, catch-up ve ilgili data/UI/performance işleri değerlendirilebilir. Bunların hiçbiri M1–M16 issue'larına önceden scaffold edilmez.
