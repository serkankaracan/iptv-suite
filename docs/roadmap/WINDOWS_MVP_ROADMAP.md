# Windows MVP yol haritası

**Tarih:** 2026-08-09

**Durum:** Phase 0 implementation plan; M1–M7 completed, M8 sıradaki milestone

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
- Opaque reference ile protected record/source/endpoint binding'i M3 kanıtı değildir; M4 source-draft dilimi doğrudan await edilen create sonrası store-issued reference'ı sonuca bağlar, fakat durable metadata atomikliği/retry reconciliation kanıtlamaz. Resolved locator origin equality ve ref-swap testleri M5'te hard gate olur.

## M4 — Secure credential storage ve merkezi redaction

**Implementation status:** COMPLETED, 2026-08-20 — tarihsel v1 foundation gate hosted 153/153 x2 + packaged initialization PASS; owner-v2 snapshot 171/171 x2 PASS; exact configuration-record deletion primitive'i dahil tarihsel local gate 183/183 x2 PASS; channel deletion primitive'i dahil tarihsel local snapshot 198/198 x2 PASS; DPAPI user-boundary harness'ı ve statik contract guard'ları dahil güncel local worktree gate exact SDK `10.0.302`, Debug/Release x64 0 warning/error, architecture 16 + unit 154 + integration 34 = 204/204 x2 identical `Passed` set, fixture/sentinel/scanner/artifact-canary PASS ve summary `commitSha=null`; normal full gate gerçek-user executable'ını çalıştırmaz; commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` için run number `#15`te ilk owner-v2/same-version packaged lifecycle ve deletion primitive commit'i `3e806edd9a9482b87cccbf000ac8a81823562f6a` için run number `#16`da 3/3 iş + üç artifact + `8m24s` toplam süre workflow/UI evidence VERIFIED; signed `0.0.1.0 → 0.0.2.0` disposable test-family update lane'i commit `bfe5960d5bc911e21ba1e1e1675deb575400e531` için run number `#18`de 3/3 iş + üç artifact + `9m16s`; hardened schema-v3 disposable reset + live-state uninstall/reinstall fresh-state lane'i commit `6f30e3fa92f329c1fa26333c591edb5148261cec` için run number `#20`de 3/3 iş + üç artifact + `8m14s`; channel deletion primitive'i commit `0ef0da5793dc4fd1f4e83dbe38cd49d7a7ccb56f` için run number `#21`de 3/3 iş + üç artifact + `9m25s` toplam süreyle workflow/UI düzeyinde VERIFIED; local `Decision` commit `fc96a211171d1e4f5e5f02174da6c565ef2d59bb` üzerinde 5k/10k/20k/50k × 20 tamamlandı ve per-record DPAPI bulk channel-locator layout'ı bütçe dışı kaldığı için reddedildi; test-only protected-catalog dirty-worktree Smoke PASS; commit `786151cef6c60de5e34009ebb6643ec804490ee9` için run number `#25`te 3/3 iş + üç artifact + `8m32s` ile hosted compile/isolation/package-exclusion workflow/UI düzeyinde VERIFIED; clean commit `7cd5bf739712a5cbdcf47634275a113faab8c48e` üzerindeki protected-catalog comparative `Decision` VERIFIED ve gate CLOSED, immutable cross-file container production NO-GO; final acceptance run `#32` ve [M4 completion evidence](../quality/M4_COMPLETION_EVIDENCE.md) ile tamamlandı

### Amaç

Credential ve token-bearing locator'ları plaintext persistence/log/artifact'tan çıkarmak; ADR-003'ün exact layout kararını ölçümle kapatmak.

Tarihsel ilk v1 foundation dilimi; typed Application `ISecretStore`, zeroing `SecretLease`, merkezi sanitizer, `ProtectedData 10.0.10`/CurrentUser adapter, packaged `LocalCache` factory ve fake/gerçek contract testlerini içerir. Factory beklenen initialization hatalarını diagnostic context taşımayan typed sonuca eşler; cancellation'ı exception olarak korur. Adapter startup'ı yalnız exact current-namespace, regular ve en az 24 saatlik ciphertext temp artığını bounded biçimde temizler; `.dpapi` kayıtlarını enumerate etmez. O dilimin local exact-SDK gate'i architecture 9, unit 100 ve integration 26 olmak üzere 135/135 testi iki kez aynı setle geçirmiş; source/purpose/reference'a bağlı kriptografik context, restart edilen adapter instance'ı, CRUD/update/delete, işlem başlamadan iptal edilmiş çağrıların mutation yapmaması, exact stale-temp cleanup, birbirinden bağımsız kayıtların concurrent create'i, aynı süreçte iki adapter instance'ı için same-key update/read/delete sıralaması, ciphertext swap/corruption ve canary-at-rest davranışı doğrulanmıştır. Ayrı opt-in Release x64 spike scaffold'u, sentetik 256-byte locator workload'u ile per-record DPAPI create/restart/read/delete/cancellation ve sanitized evidence zincirini sağlar; tarihsel v1 için 1k smoke geçmiştir. Owner-v2 `Decision`, clean commit `fc96a211171d1e4f5e5f02174da6c565ef2d59bb` üzerinde 5k/10k/20k/50k ölçeklerinin her birini 20 tur tamamlamıştır. 50k create p95 `151.447 s`, create allocation p95 `668.007 MiB`; cancellation request→completion p95 `1.067 ms` ve post-cancellation commit üst sınırı `0`dır. `INFERENCE`: per-record DPAPI bulk channel-locator layout'ı tek başına 50k normalize + protected persistence + index için birleşik ≤3 s süre bütçesini ve bütün-import ≤150 MiB allocation bütçesini aştığından bu kullanım için reddedilmiştir; source-credential kararı değildir. Restart ve 256-record read yalnız dar unpackaged problardır. Arbitrary mid-I/O cancellation/interleaving ve cross-process sıralama bu kanıtın parçası değildir; sonuç packaged process/lifecycle veya gerçek wrong-user testi de değildir. Ayrıntı: [M4 secret-store Decision evidence](../quality/M4_SECRET_STORE_DECISION_EVIDENCE.md).

Reddedilen bulk yerleşim için ayrı `IptvSuite.ProtectedCatalogSpike` adayı x64, nonpackable/nonpublishable ve test-only'dir. Clean/eligible commit `7cd5bf739712a5cbdcf47634275a113faab8c48e` üzerindeki 5k/10k/20k/50k × 20 `Decision` bütün sample/hash/aggregate kontrolleri, 22 reader Boolean'ı, fault/cancellation, canary ve cleanup ile **VERIFIED**dır. 50k p95 create+activate `173,128 ms`, allocation `34,676 MiB`, boundary working-set delta `15,855 MiB`, aynı-process reopen+unwrap `13,231 ms`, 256-read `1,695 ms`, snapshot delete `2,321 ms`, disk `16,022 MiB`, staging cancellation `5,570 ms` ve post-request mutation/artifact sayıları `0`dır. Baseline'a göre create `874,766×`, allocation `19,264×` iyileşmiştir. `209.828` byte summary SHA-256 `4a219e802dcff959644bda42489f0ee5231be63df1eb37d034e2ed16ff0859de`dir. Runner threshold uygulamaz; component'in referans bütçelerin altında kalması comparative benchmark gate'ini kapatan **INFERENCE**dır, parser/normalize/index/SQLite/UI/network/package lifecycle hariç olduğundan end-to-end PASS değildir. Immutable cross-file container production için **NO-GO**dur; security-reviewed yön DPAPI-wrapped per-snapshot DEK + per-row AES-GCM + active snapshot'ı aynı SQLite transaction domain'inde tutmaktır ve M8'de `Proposed`/unimplemented kalır. M4 foundation `COMPLETED`, ADR-003 `Proposed`dur. Ayrıntı: [M4 protected-catalog Decision evidence](../quality/M4_PROTECTED_CATALOG_DECISION_EVIDENCE.md).

Run number `#25`, `test(windows): spike protected catalog layout` başlıklı commit `786151cef6c60de5e34009ebb6643ec804490ee9` için UI'da 3/3 yeşil iş, üç artifact ve `8m32s` gösterir. Normal workflow solution build'iyle adayı derlemiş; architecture isolation guard'larını ve production MSIX'teki `IptvSuite.ProtectedCatalogSpike*` denylist kontrolünü geçmiştir. Bu yalnız hosted compile/isolation/package-exclusion için workflow/UI düzeyinde **VERIFIED**dır: opt-in executable bu run'da `Smoke`/`Decision` modunda çalıştırılmamış, candidate evidence yüklenmemiş ve exact hosted test sayısı ile artifact içerik/tam digest bağları bağımsız doğrulanmamıştır. Comparative `Decision` daha sonra ayrı clean-commit local evidence ile tamamlanmıştır; run `#25` bu sonucun kanıtı değildir. Milestone acceptance pending; M4 foundation `COMPLETED`, ADR-003 `Proposed` kalır.

Mevcut foundation kaydından sonraki dilimde `SourceDraftProtectionService`, draft'ı store mutation'ından önce validate eder; Xtream locator+username+password veya remote-playlist locator'ını bounded/versioned payload'a kodlar; store-issued reference ile exact `SourceId`yi yalnız başarılı create sonrasında aynı draft'a bağlar ve geçici payload buffer'ını sıfırlar. `ContentSource` kimlik/ad/configuration değerlerini bu draft'tan birlikte alır. Doğrudan await edilen çağrıda store commit'inden sonra cancellation yeniden gözlenmez ve reference başarılı sonuçla döner; caller abandonment/retry, duplicate create, process crash/OOM, DB/configuration persistence ve orphan reconciliation kanıtlanmaz. Internal source-payload codec v1 kalır: credential payload'ı `SRCRED01`, remote-playlist locator payload'ı `SRCLOC01` magic değerini taşır; decoder exact magic/version, pozitif big-endian field length'leri, exact total/no-trailing-data, strict UTF-8, control/whitespace ve Unicode-scalar sınırlarını allocation-free slice metadata'sıyla doğrular, managed string/URI ya da ikinci plaintext buffer üretmez.

Protected-store katmanı ise owner-bound v2'dir: envelope magic'i `SRCSEC02`, composition path'i `LocalCache\ProtectedStore\v2`, kayıt namespace'i `record-v2-*`, geçici dosya namespace'i `temporary-v2-*` olur. Credential ve remote-playlist locator kayıtları source-configuration owner'ına; stream ve logo locator kayıtları channel owner'ına bağlanır. Owner kind/ID kriptografik context, entropy ve dosya-adı digest girdisidir. Caller authoritative owner'ı taşıdığında bu bağ, aynı source/purpose içindeki bir referansın tek başına başka configuration/channel owner'ına takılmasını kapatır. Owner + reference + endpoint tuple'ının birlikte değiştirilmesini, resolved locator origin doğrulamasını, durable authoritative owner metadata'sını veya lifecycle yetkilendirmesini kanıtlamaz; public resolve/HTTP consumer M5 origin policy ve deletion/in-flight gate tamamlanana kadar açılmaz. Composition root factory'yi packaged launch'ta bir kez çağırır, başarısız initialization'da pencereyi oluşturmadan fail-closed olur, v2 store'u uygulama ömrü boyunca tutar ve fallback oluşturmaz. Startup cleanup exact v2 temp-namespace aday listesini 1.024 girdide sınırlar ve 1.025'inci exact adayda mutation yapmadan fail-closed olur; toplam directory-enumeration maliyeti ayrıca açıktır. Pre-release v1 kayıtları v2 store tarafından okunmaz veya otomatik silinmez; authoritative inventory bulunmadığından otomatik migration yapılmaz ve v1 verisi korunur.

Dar deletion primitive'lerinden ilki, yalnız durable `DeletionPending` olduğu varsayılan authoritative `ContentSource` içinden exact source/configuration-owner/purpose/reference tuple'ını türetip tek Xtream credential veya remote-playlist locator kaydını idempotent siler. İkinci `SourceChannelProtectedRecordDeletionService`, aynı source ile authoritative persistence'tan yüklenmesi gereken `PlaylistSnapshot` + `LiveChannel` zincirini alır; source→snapshot, snapshot→channel ve stable-key→source bağlarını doğruladıktan sonra `ChannelId` owner ile varsa protected stream'i, ardından optional logoyu exact/idempotent siler. İlk başarılı channel delete commit'inden sonra newly-requested cancellation ikinci exact delete denemesini durdurmaz; partial failure retry'ı, store yeniden başarılı olduğunda aynı zincirin sonuca yakınsamasına izin verir. Service aggregate provenance'i veya authorization'ı kanıtlamaz. Cleanup yalnız active/`Complete` snapshot'a daraltılmaz; authoritative zincirdeki retained, `Importing` veya `Rejected` snapshot kayıtları da silinebilir. İki primitive birlikte de source status'unu persist etmez, yeni operasyon admission'ını kapatmaz, in-flight kullanımı drain etmez, bütün channel'ları enumerate etmez, metadata/cache'i veya unknown orphan'ları temizlemez ve source-wide coordinator oluşturmaz. Channel primitive tarihsel local `198/198 ×2` snapshot'ında PASS'tir; run number `#21` ile commit-bound hosted execution'ı workflow/UI düzeyinde **VERIFIED**dır. Artifact içeriği bağımsız doğrulanmamıştır.

Hosted run `31735655363`, commit `6ac235f49ffd297196189e5e9441ad0c9e04f116` için architecture 12, unit 114 ve integration 27 olmak üzere 153/153 testi iki kez aynı sonuç setiyle, signed package smoke'u ve required coordinator'ı geçmiştir. Bu tarihsel artifact'taki `ProtectedStoreDirectoryInitialized=true` yalnız o commit'in `LocalCache\ProtectedStore\v1` dizinini kanıtlar; owner-bound v2 package kanıtı değildir. Decoderın round-trip/malformed matrisi dahil architecture 12, unit 120 ve integration 27 olmak üzere 159/159; owner-v2 ise 171/171 local snapshot'ında geçmiştir. Ayrı nonpublishable packaged lifecycle hostu aynı disposable identity/publisher, signer ve package family altında baseline `0.0.1.0` ile update `0.0.2.0` paketlerini üretir. Baseline process'leri create ve duplicate-create rejection'ı çalıştırır; daha yüksek sürüm normal `Add-AppxPackage` ile aynı family üzerine kurulduktan sonra updated process baseline kaydını okur, cross-owner negatiflerini ve doğru-owner update/read/delete/post-delete zincirini tamamlar. Evidence schema v2 iki package sürüm/hash/signature alanını, same-signer/family, changed-full-name, update-installed, post-update read/scan ve cleanup sonuçlarını allowlist'ler. Exact configuration-record deletion primitive'i dahil tarihsel exact SDK `10.0.302` gate'i architecture 13, unit 137 ve integration 33 olmak üzere 183/183 testi iki aynı `Passed` setiyle geçmiştir. Channel deletion primitive'i dahil tarihsel local snapshot Debug/Release x64 build'leri 0 warning/error; architecture 13, unit 151 ve integration 34 olmak üzere 198/198 testi iki identical `Passed` setiyle; fixture determinism, sentinel fail/recovery, scanner CLI self-test ve artifact canary taramasını geçmiştir; local summary `commitSha=null` taşır. 2026-08-14 GitHub Actions UI kaydı, run number `#15`in commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` için 3/3 işi ve üç artifact'ı başarıyla tamamladığını gösterir; bu tarihsel kayıt ilk signed owner-v2 quality/package/same-version lifecycle geçişini workflow/UI düzeyinde **VERIFIED** yapar. Aynı tarihli takip UI kaydında run number `#16`, exact configuration-record deletion primitive'ini içeren commit `3e806edd9a9482b87cccbf000ac8a81823562f6a` için 3/3 işi yeşil, üç artifact ve `8m24s` toplam süre gösterir; bu sonuç da yalnız workflow/UI düzeyinde **VERIFIED**dir. Sonraki run number `#18` UI kaydı, signed two-version lane'i ve package output-directory düzeltmesini içeren commit `bfe5960d5bc911e21ba1e1e1675deb575400e531` için 3/3 işi yeşil, üç artifact ve `9m16s` toplam süre gösterir; commit-bound package job'u lifecycle betiğini zorunlu çalıştırdığından hosted update execution workflow/UI düzeyinde **VERIFIED**dır. Run number `#21` UI kaydı, channel deletion primitive'ini içeren commit `0ef0da5793dc4fd1f4e83dbe38cd49d7a7ccb56f` için 3/3 işi yeşil, üç artifact ve `9m25s` toplam süre gösterir; commit-bound hosted execution workflow/UI düzeyinde **VERIFIED**dır. GitHub database run ID'leri, job log/test sayıları ve indirilen artifact JSON/ZIP/içerik/tam digest bağları bu kayıtta doğrulanmamıştır.

Takip eden schema v3 lane, updated disposable package üzerinde canlı protected record/ticket oluşturup exact package full name ile reset uygular; package identity korunurken owned state'in manual cleanup öncesinde silindiğini ve fresh create'in farklı record identity ürettiğini doğrular. Ardından fresh protected state canlıyken exact current-user package preserve-data/`-AllUsers` yolu kullanılmadan kaldırılır, registration ve exact app-data yokluğu denetlenir, aynı signed `0.0.2.0` MSIX yeniden kurulur ve boş state'ten ikinci farklı record identity ile create/delete tamamlanır. Reset'in app data'yı kalıcı silen fresh-install dönüşü, repair'in veriyi koruyan ayrı işlem olması ve clean uninstall'ın package state'ini kaldırması platform kaynaklarıyla ayrılır [S91][S92]. Sanitized evidence schema v3, schema v2 alanlarına package identity-preservation, reset-owned-state removal, live-state uninstall/app-data removal, reinstall ve iki fresh-create/record-identity-change Boolean'larını ekler; raw path/PFN/full-name/record/reference/source/owner/user yayımlamaz. Run number `#20`, commit `6f30e3fa92f329c1fa26333c591edb5148261cec` için kod ve statik architecture sözleşmesinin hosted execution'ını workflow/UI düzeyinde **VERIFIED** yapmıştır; artifact alanları/ZIP/içerik/tam hash bağı bağımsız doğrulanmamıştır.

2026-08-14 run number `#19`, commit `71dbb0cf23467d978674976796e2bbceca01494a` için broad `PackageReset`/`PackageResetFailed` stage'inde fail olmuştur; quality job'u yeşil, packaged job ile ona bağlı required coordinator kırmızıdır. Bu historical stage cmdlet invocation'ını ve registration/identity/manifest postcondition'larını birlikte kapsadığı, HRESULT da yayımlanmadığı için exact failure bilinmez; `#19` native reset veya uninstall/reinstall PASS kanıtı değildir. Takip implementation'ı reset invocation, registration ve manifest validation'ı ayrı stable stage/code'lara böler; bilinen HRESULT'ı raw diagnostic yerine allowlist'li failure `Code`una sınıflandırır. Yalnız `0x80073D00`, `0x80073D02` ve `0x80073D05` bounded process-quiescence ile toplam en fazla üç deneme alır; diğer sınıflar retry edilmez [S93]. Bu retry politikası resmi hata anlamlarından türetilen `INFERENCE`dır ve run `#20` UI'ı retry'ın tetiklenip tetiklenmediğini göstermez. Takip commit'i `6f30e3fa92f329c1fa26333c591edb5148261cec` için run `#20` 3/3 işi yeşil, üç artifact ve `8m14s` toplam süreyle tamamlanmıştır; zorunlu package-lifecycle adımı nedeniyle hardening ile schema-v3 reset/live-state uninstall/reinstall zinciri workflow/UI düzeyinde **VERIFIED**dır. Artifact içeriği bağımsız doğrulanmamıştır; `#19` tarihsel failure, `#18` update execution **VERIFIED**, M4 foundation `COMPLETED` olarak kalır.

Gerçek farklı-user boundary lane'inin repository implementation'ı tamamlanmıştır: production graph/MSIX dışındaki framework-dependent x64 harness, creator hesabında raw `CurrentUser` blob'u ve production-adapter kaydı hazırlar; `CreateProcessWithLogonW(LOGON_WITH_PROFILE)` ile farklı SID'li non-admin child process raw unprotect reddini, adapter `ProtectedRecordUnavailable` + null lease sonucunu, kendi raw/adapter round-trip'ini ve creator kaydının değişmezliğini doğrular. `LOGON_NETCREDENTIALS_ONLY` caller token'ını koruduğu için yasaktır [S107–S111]. Sanitized evidence username/SID/path/password/payload/reference/protected-record digest içermez; success allowlist exact SDK, clean commit SHA, controller script SHA-256 ve staged harness assembly SHA-256 provenance metadata'sını taşır. Child invocation/custom environment password/payload/reference/SID/protected-record digest taşımaz; explicit custom environment block yoktur, OS target-profile environment username/profile değerlerini sağlar ve exact executable/DLL/workspace path command line'da zorunludur. Exact account/profile/workspace cleanup zorunludur. Kod/static CI sözleşmesi `IMPLEMENTED`dır. 2026-08-16 run `#31` (`31960438511`), commit `740d33d0d428c8f6aeded24aa358fdd8626016f1` için dört zorunlu Windows işini ve dört artifact'ı `8m23s` içinde geçmiştir. İndirilen 1.766 bayt sanitized DPAPI `last-success.json` SHA-256 `c9dbb22b577aaf08c588bbdc951e87bce6bbe82c86c8f58a2fe6cb9840b725b8` taşır; exact SDK/clean commit/provenance ve bütün boundary/cleanup Boolean'ları `true` olduğundan raw/adapter farklı-user lane'i hosted **VERIFIED**dır. Bu M4 raw/adapter sınırıdır; production package'ın ikinci-user registration/PFN/Store identity ve clean-machine davranışı M15'tedir.

M4 acceptance tamamlanmıştır. Durable authoritative owner inventory, transactional `DeletionPending`, admission/drain, source-wide deletion ve startup orphan reconciliation M8'e; owner + reference + endpoint tuple swap ile resolved-origin policy M5'e; production PFN lifecycle/repair/identity, ikinci-user package registration ve Store kabulü M15'e devredilmiştir. Protected-catalog comparative benchmark'ı kapanmıştır; aynı reddedilmiş per-record yerleşimin yeniden koşulması yeni gate değildir. Path/reparse check-to-use yarışı yalnız yükseltilmemiş user-local store ve same-user compromise sınırında residual risk olarak **ACCEPTED**dır; scope değişikliği kararı yeniden açar [S94]. Ayrıntılı acceptance ve artifact bağı [M4 completion evidence](../quality/M4_COMPLETION_EVIDENCE.md) belgesindedir.

Run number `#22` UI kaydı, bu threat-model kararı ile production `asInvoker` / `uiAccess=false` manifestini içeren commit `43bf88c591bdc216d3a288ca578f90e4fad927c6` için 3/3 işi yeşil, üç artifact ve `8m23s` toplam süre gösterir. Commit-bound quality, signed package launch ve lifecycle işleri declarative least-privilege manifest sınırının hosted execution'ını workflow/UI düzeyinde **VERIFIED** yapar. Bu sonuç R26 kararının doğruluğunu, runtime token'ın non-elevated olduğunu, atomik path güvenliğini veya same-user sandbox'ı kanıtlamaz; artifact JSON/ZIP içeriği, tam digest, job log/test sayısı ve GitHub database run ID'si bağımsız doğrulanmamıştır. M4 foundation `COMPLETED` ve aşağıdaki açık hard-gate'ler değişmez.

### Kapsam

- `ISecretStore` contract ve Windows DPAPI CurrentUser/LOCAL=user packaged adapter.
- Production graph/MSIX dışında framework-dependent x64 harness ile gerçek farklı-SID raw DPAPI ve production-adapter boundary lane'i; run `#31` ile hosted acceptance tamamlanmıştır.
- Pre-validation → bounded protected payload create → source-configuration owner'a bağlı store-issued opaque reference bağlama source-draft application operation'ı.
- Credential/remote-playlist için source-configuration; stream/logo için channel owner'ını v2 envelope, entropy ve dosya namespace'ine bağlama.
- Source secret create/read/update/delete, opaque reference ve restart; `DeletionPending` aggregate zincirinden exact configuration-owned kayıt ile tek channel'ın stream/optional-logo kayıtlarını silen dar primitive'ler.
- Bulk channel locator için test-only immutable protected-catalog adayının güvenlik/layout spike'ı ve reddedilen DPAPI-per-record baseline ile aynı 5k/10k/20k/50k × 20 protokolündeki tamamlanmış comparative `Decision`; production SQLite transaction tasarımı/implementasyonu M8 kapsamındadır.
- Central structured sanitizer; URI/header/exception/native-text adapters.
- `DeletionPending` aggregate zincirinden authoritative tuple türeten configuration ve channel exact-delete primitive'leri; durable source-wide orchestration/inventory M8'e devredilir.
- Disposable test identity üzerinde packaged update/reset/uninstall/reinstall lifecycle harness'ı.

### Kapsam dışı

Provider login, production catalog DB, cloud sync/export, own cryptographic scheme ve Credential Locker'a bulk JSON/locator yazma.

### Acceptance criteria

- Password/token/full sensitive URL app-data, test DB, config, log, crash/support artifact'ta plaintext bulunmaz.
- Restart sonrası authorized context secret'ı çözer; wrong user/context çözemez ve exact deleted record unavailable olur.
- Dar delete primitive'leri idempotenttir, yalnız `DeletionPending` aggregate kabul eder ve partial store failure'ını fallback olmadan typed sonuçla döndürür; durable status/orchestration M8 acceptance'ıdır.
- 50k protected locator layout'ı 5 saniyelik total import bütçesinde ayrılan ≤3 saniyelik persistence hedefi için ölçülmüş karara sahiptir.
- ADR-003 Accepted yapılmış ya da ölçümlü alternatif için yeni Proposed decision vardır; belirsizlik gizlenmez.
- Clean build/test ve canary scan geçer.

### Testler / doğrulama

- Fake-store shared contract + real Windows DPAPI integration; packaged `LocalCache` initialization smoke.
- Gerçek non-admin local-user token/profile altında raw `CurrentUser` negatif ve production-adapter `ProtectedRecordUnavailable`/null-lease testi; exact account/profile/workspace cleanup.
- Add/read/update/delete, restart, corrupt blob, wrong context.
- App update, reset, uninstall/reinstall, provisional identity change; production PFN altında ikinci-user package-registration matrisi M15'te.
- DB/file/log/artifact binary canary scan.
- M8 production SQLite transaction yerleşimi için 5k–50k end-to-end duration/allocation/working-set/cancellation, crash/recovery ve startup orphan reconciliation.

### Ana risk

Per-record DPAPI-per-locator'ın doğrulanmış bütçe aşımı ve test-only comparative başarısından sonra M8 production SQLite transaction yerleşiminin end-to-end bütçeyi karşılamaması veya M15 app identity/lifecycle'ın protected data'yı orphan etmesi.

### Demo çıktısı

Canary secret'ın kaydedilip restart sonrası okunması, her output'ta maskelenmesi, silinmesi ve artifact scan'in sıfır hit vermesi; per-record baseline reddi ile reviewed alternatif karşılaştırmasını taşıyan 50k karar raporu.

## M5 — HTTP/networking ve connection probe

**Implementation status:** COMPLETED, 2026-08-20 — Application `IHttpTransport` contract'ı, `ConnectionProbeService` ve Infrastructure `BoundedHttpTransport` uygulanmıştır. HTTPS expected-origin binding, long-lived client, disabled automatic redirects/cookies, bounded manual redirects, downgrade ve cross-origin credential reddi, connect/total timeout, caller cancellation, decompression sonrası response byte cap'i, typed status/TLS/network sonuçları, en fazla üç safe transient attempt, capped `Retry-After`, zeroing response/request buffer'ları ve URI/header taşımayan safe operation observation sözleşmesi fail-closed çalışır. Local full gate architecture 17 + unit 157 + integration 54 = 228/228 testi iki deterministic koşuda geçmiştir; fixture/sentinel/scanner/artifact-canary kontrolleri de PASS'tir. Provider endpoint semantiği M6'ya devredilmiştir.

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

**Implementation status:** COMPLETED, 2026-08-20 — local exact-SDK acceptance ve commit-bound hosted workflow PASS

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

### Uygulama kanıtı

- `IXtreamProviderClient` / `XtreamProviderClient`, authoritative source-configuration owner ve opaque credential reference ile `ISecretStore` lease'ini okur; lease bütün operation boyunca sahiplenilir ve başarı/hata/iptalde dispose edilerek owned plaintext buffer sıfırlanır.
- Endpoint builder protected locator'ın exact HTTPS origin bağını yeniden doğrular, mevcut base path altındaki `player_api.php` için yalnız account, `get_live_categories` ve `get_live_streams` query'lerini üretir. Credential içeren URI/string'ler operation-local managed temsil olarak kalır; result, observation, persistence veya diagnostic yüzeyine verilmez.
- Account parser string/number/Boolean `auth` varyantını kabul eder; 200-body auth false ve HTTP 401/403 `AuthenticationRejected` olur. 429/5xx/timeout/oversize safe typed domain hatasına eşlenir; category/stream response budget'ları sırasıyla 10k/50k item ve transport byte cap'i ile sınırlıdır.
- Tolerant parser unknown alanları ve `direct_source` değerini tutmaz; first-wins duplicate identifier ile skipped/duplicate sayaçlarını yayımlar. Stream sonucu full URL yerine bounded `ProviderItemKey` taşır. VOD/Series/EPG production endpoint/symbol'ü yoktur.
- Sentetik scripted provider suite success/empty/partial/malformed, body auth fail, 401/403/429/5xx/timeout/oversize, scalar/null/unknown/duplicate, exact 50k success ve limit+1 rejection senaryolarını kapsar. Local exact SDK full gate Debug/Release x64 0 warning/error; architecture 18 + unit 157 + integration 68 = **243/243 ×2**, fixture/sentinel/scanner/artifact-canary PASS'tir. `23.204` byte local summary SHA-256 `6cc5c020cd15bcc2232205d41dae0ea48234e849ad8f66c0191450a2ee98e60b` ve `commitSha=null` taşır; commit-bound hosted kanıt değildir.
- [GitHub Actions run `32404441692`](https://github.com/serkankaracan/iptv-suite/actions/runs/32404441692), commit `d5b1161e9ff88552cbebe52524b556e7aaf976bc` için dört zorunlu Windows işinin 4/4'ünü `9m05s` içinde başarıyla tamamlamıştır. Bu commit-bound workflow sonucu M6 implementation ve regression zincirini workflow/UI düzeyinde **VERIFIED** yapar; gerçek provider/account uyumluluğu veya indirilmemiş artifact içerik/digest doğrulaması değildir.
- Ayrıntılı sınır ve doğrulama [M6 completion evidence](../quality/M6_COMPLETION_EVIDENCE.md) belgesindedir.

## M7 — Remote M3U/M3U8 download ve incremental parser

**Implementation status:** `COMPLETED, 2026-08-20`. Streaming response lease, strict UTF-8/BOM incremental parser, bounded metadata/locator policy, final-origin relative resolution, typed HLS routing, duplicate warning, 50k/cancellation ölçümü ve mandatory internal entry-sink handoff tamamlandı. Durable sink, protected row persistence ve active snapshot transaction'ı M8 kapsamındadır. Kanıt: [M7 completion evidence](../quality/M7_COMPLETION_EVIDENCE.md).

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

**Implementation status:** `COMPLETED, 2026-08-21`. Versioned SQLite schema, same-transaction DPAPI-wrapped snapshot key + AES-GCM locator rows, streaming parser sink, atomik activation, query/cache + HTTP validator binding, sanitized sync history, favorite reconciliation, prune, delete, startup reconciliation ve atomik migration rollback uygulanmıştır. Clean 5k–50k ×20 Decision ölçümünde 50k import p95 `2,739 s`, allocation p95 `116,330 MiB`, working-set delta p95 `6,164 MiB` ve cancellation p95 `12,390 ms`dir. Gerçek ayrı-process kill testi açık replacement transaction'ını öldürür; startup recovery önceki complete snapshot'ı tek active result olarak bırakır ve hot rollback journal/WAL/SHM kalmaz. Kanıt: [M8 completion evidence](../quality/M8_COMPLETION_EVIDENCE.md).

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

**Implementation status:** IN PROGRESS — bounded `ICatalogBrowser` source/category/search/page port'u, real SQLite query adapter'ı, 200 kayıtlık page sınırı, 250 ms debounce ve generation-bound stale-result suppression uygulanmıştır. WinUI composition root device-local `Catalog\v2\catalog.db` query adapter'ını kurar; source/category/search/paging, virtualized `ItemsStackPanel`, loading/empty/failure durumu ve UIA adları olan player'sız katalog ekranını derler. Active snapshot'taki exact encrypted logo tuple'ını çözen provider yalnız durable source ile aynı HTTPS origin'e gider; 512 KiB response/signature sınırı, dört eşzamanlı fetch, 128-entry FIFO memory cache, page cancellation ve stale-row suppression uygular. Signed package smoke, gerçek WinUI automation ağacında source/category/search/list name+role sözleşmesini ve source→category→search Tab focus sırasını fail-closed doğrulayacak biçimde genişletilmiştir; local shell elevated olmadığı için bu yeni packaged yürütme henüz **UNVERIFIED**dır. Exact-SDK local gate architecture 24 + unit 162 + integration 106 = `292/292 × 2`, Debug/Release 0 warning/error, fixture/sentinel/canary PASS'tir. Clean commit `d3ebbdcf24bb842bbab8f39c3abbed47a085db65` 50k ×20 query Decision'ında first-page/category/search p95 değerlerini sırasıyla `7,704`/`8,352`/`22,490 ms`, adapter reopen+first-page p95 değerini `7,443 ms` ölçerek query `≤100 ms` ve cached component `≤500 ms` bütçelerini karşılamıştır. [Query Decision evidence](../quality/M9_CATALOG_QUERY_DECISION_EVIDENCE.md). Frame/realized-container ölçümü ile data-bearing packaged UIA acceptance'ı henüz kanıtlanmadı; M9 tamamlanmamıştır.

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
