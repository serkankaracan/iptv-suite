# Kalite ve performans stratejisi

**Tarih:** 2026-08-09

**Durum:** M2 quality infrastructure ile M3–M9, M11, M13 ve M14 gate'leri `COMPLETED`; M10 `CONDITIONAL SUCCESS / ACCEPTED WITH KNOWN DEVIATION`; M12 playback UX/lifecycle `PARTIAL VERIFIED`; M15 `IN PROGRESS / BLOCKED`

M8 completion kaydı, aşağıdaki tarihsel M4 comparative paragraflarındaki “M8 Proposed/uygulanmamış/açık” ifadelerini supersede eder. Güncel acceptance bağı [M8 completion evidence](M8_COMPLETION_EVIDENCE.md) belgesindedir.

## 1. İlke

Bu ürün için genel bir “test pyramid yüzdesi” kullanılmaz. Riskler farklı kanıt ister: parser pure/deterministik test, HTTP local fixture server, storage gerçek Windows protection, player gerçek native engine ve codec corpus, mağaza ise packaged clean-machine testi gerektirir. Her test katmanı belirli bir failure mode'u kapatır.

Test verisi:

- sentetik, yeniden üretilebilir ve repository'de provenance/license kaydı olan fixture;
- yalnız sahte credential ve geliştiriciye ait endpoint;
- gerçek müşteri account'u, yakalanmış gerçek provider response'u veya yetkisiz yayın yok;
- deterministic seed, generator version ve expected-result manifesti;
- credential canary'leri açıkça test secret'ı olarak işaretli ve hiçbir production artifact'ına kopyalanmaz.

## 2. Somut test katmanları

| Katman | Ne kanıtlar | Araç/ortam sınırı | Ne kanıtlamaz |
|---|---|---|---|
| Specification/golden | Aynı M3U, terminology, normalization ve error contract'ı platformlarda aynı davranır | JSON Schema + golden input/output | OS/player davranışı |
| Domain/unit | Value object, validation, stable key, mapping, state transition | Process içi, no network/disk/UI | Integration/lifecycle |
| Component/integration | Parser stream, SQLite, migration, protected store, image cache | Temp app data + real/fake Windows API | Store/driver çeşitliliği |
| HTTP/provider contract | Endpoint, auth, redirects, timeout, response variants | Local fake HTTP(S) server + deterministic clock | Gerçek provider evreni |
| UI/accessibility | View-model, virtualization, keyboard/focus/UIA | Packaged WinUI test host | Codec doğruluğu |
| Performance/regression | 5k–50k import/query/memory/UI bütçesi | Release build, referans cihaz | Heterojen user network |
| Player adapter/compatibility | Engine state, codec/container, HW fallback, surface/lifecycle | Sentetik streams + real engine/device | Bütün dünya stream garantisi |
| Package/Store/manual | MSIX, architecture, update/uninstall, WACK, reviewer akışı | Clean VM/device + private submission | Kamuya açık mağaza kabul garantisi |
| Soak/fault | Leak, deadlock, reconnect storm, uzun yayın | 2/8/24 saat ve fault injection | Bilinmeyen her upstream bug |

Her production defect, uygun katmanda önce sentetik ve secret-free regression fixture'a dönüştürülür. Gerçek payload ancak hak/mahremiyet kontrolünden sonra tamamen sanitize ve küçültülmüş biçimde kullanılabilir.

### 2.1 M2 test harness baseline'ı

**Implementation status:** Completed / PASS, 2026-08-09; local exact-SDK iki-run 22/22 ve hosted packaged-host acceptance geçti.

M2 katmanları şu concrete sınırı kullanır:

| M2 katmanı | Proje/host | İzin verilen seam | Bu katmanın kanıtlamadığı |
|---|---|---|---|
| Unit | `IptvSuite.UnitTests` → `IptvSuite.Testing` | Resmî fake time, scripted transport, in-memory test secret store, passive fake player, generator/canary | Gerçek timer/network, DPAPI, ürün state policy'si, codec |
| Integration | `IptvSuite.IntegrationTests` → `IptvSuite.Testing` | Loopback Kestrel, OS-seçimli port, gerçek temp filesystem, timeout/cancellation/cleanup | Gerçek provider/internet, TLS policy, SQLite, packaged lifecycle |
| Architecture | `IptvSuite.ArchitectureTests` | Project/package/framework allowlist ve production→test yasağı | Runtime davranışı |
| Packaged launch | `Invoke-WindowsPackageSmoke.ps1` ile installed MSIX | Çalıştığı hostta sign, targeted production-payload inspection, install, AUMID visible launch, current packaged `LocalCache\ProtectedStore\v2` directory initialization, normal close, exact cleanup | Secret CRUD, feature UI/UIA, update/reset, v1→v2 migration, WACK/Store, provider/player/codec, ARM64, host/device matrisi |
| Packaged lifecycle | `Invoke-WindowsPackageLifecycleSmoke.ps1` + ayrı nonpublishable MSIX | Baseline `0.0.1.0` create/duplicate-create; same-family/signer `0.0.2.0` normal update; updated-process owner-v2 read/cross-owner/update/delete; schema v3'te exact reset + fresh create ve live-state current-user uninstall + aynı updated MSIX reinstall + fresh create/delete; update bölümü run number `#18`, hardened schema-v3 bölümü run number `#20` ile hosted workflow/UI düzeyinde **VERIFIED** | İndirilen artifact içeriğinin bağımsız doğrulaması, production PFN, repair, Windows-managed package hive'ları, wrong-user, crash/power-loss veya source reconciliation |
| Gerçek Windows user DPAPI boundary | `Invoke-WindowsDpapiUserBoundarySmoke.ps1` + ayrı unpackaged framework-dependent x64 harness | Creator raw `CurrentUser` + production-adapter record; farklı SID'li non-admin `LOGON_WITH_PROFILE` child'ta creator raw unprotect reddi, adapter `ProtectedRecordUnavailable` + null lease, child self round-trip, creator record immutability/re-read ve exact account/profile/workspace cleanup. Kod/static contract `IMPLEMENTED`; run `#31` ile raw/adapter boundary hosted **VERIFIED** | İndirilen sanitized evidence SHA-256 `c9dbb22b577aaf08c588bbdc951e87bce6bbe82c86c8f58a2fe6cb9840b725b8`; production package registration/PFN/Store identity ve ikinci-user packaged `LocalCache` M15; source-wide lifecycle/reconciliation ayrı M4 gate'i |
| M6 Xtream contract | `XtreamProviderClientTests` + `XtreamProviderJsonParserTests` | Protected credential owner/reference binding, exact account/live-only request order, body/HTTP auth failure, safe transport mapping, tolerant scalar/unknown/duplicate policy, 10k/50k limits ve 50k typed-key success | Gerçek provider/account, undocumented variant compatibility, durable snapshot/SQLite, UI veya playback |

M6 local exact-SDK full gate architecture 18, unit 157 ve integration 68 olmak üzere `243/243 ×2` sonucu vermiştir. [GitHub Actions run `32404441692`](https://github.com/serkankaracan/iptv-suite/actions/runs/32404441692), commit `d5b1161e9ff88552cbebe52524b556e7aaf976bc` için dört zorunlu Windows işinin 4/4'ünü `9m05s` içinde tamamlayarak M6'nın commit-bound workflow execution'ını **VERIFIED** yapmıştır. Scripted transport gerçek provider/account uyumluluğu değildir; indirilmeyen artifact içerik ve tam digest bağları bağımsız doğrulanmamıştır.

User-boundary controller clean worktree/exact SDK/elevated 64-bit Windows PowerShell önkoşullarını, exact verified `dotnet` + staged harness DLL launch'ını, child process SID/start-time/profile doğrulamasını ve numeric-SID ACL/group sınırını fail-closed uygular. `CreateProcessWithLogonW` yalnız `LOGON_WITH_PROFILE` ile çağrılır; caller token'ını koruyan `LOGON_NETCREDENTIALS_ONLY` sabiti ayrıca yasaklanır [S107–S111]. Success schema exact sabit metadata, `DotNetSdk`, 40-lowercase-hex `CommitSha`, 64-lowercase-hex `ControllerScriptSha256`/`HarnessAssemblySha256` ve Boolean allowlist'idir; account/SID/password/path/payload/reference/protected-record digest yayımlamaz. Son sekiz cleanup alanı ancak process/profile/group/account/run+tool workspace temizliği ve clean repository yeniden doğrulamasından sonra eklenir. Failure evidence yalnız `Stage`/`Code` taşır; success evidence canary scan'den geçmeden yayımlanmaz. Workflow lane'i quality job'una bağımlıdır ve required coordinator bu job'u da zorunlu `success` sayar. Kod/static workflow sözleşmesi çalıştırma kanıtı değildir; hosted run gelene kadar **UNVERIFIED** kalır.

Test fake'leri production contract değildir. Özellikle fake secret store M4 DPAPI kanıtı, fake player M11 adapter/codec kanıtı ve scripted transport M5 HTTP güvenlik politikası yerine kullanılamaz. Local HTTP server yalnız `IPAddress.Loopback` ve port `0` ile çalışır; gerçek provider host'u fixture testine sokulmaz.

M2 fixture specification'ı generator `1.0.0`, algorithm version `1`, seed `20260809`, record count, provenance flag'leri ve output SHA-256'sını manifestte taşır. `LicenseRef-IPTVSuite-Synthetic-Test-Only` durumu `UNVERIFIED` ve internal-only'dir; public redistribution izni değildir. Deterministik fixture manifestine timestamp, absolute path veya makine bilgisi girmez; bunlar ayrı run evidence'ına aittir.

Canary açıkça test-only marker'dır. Scanner marker'ın UTF-8, UTF-16LE/BE, URI-escaped ve alignment-safe Base64 varyantlarını scope'tan bağımsız ve chunk boundary boyunca arar; marker taşıyan artifact path'ini fingerprint ile redakte eder. Library corpus'una ek olarak CLI, kontamine artifact'ta exact exit `2`, cleanup sonrası temiz artifact'ta `0` vermelidir. Bu kontrol genel secret discovery/redaction doğruluğu veya production artifact güvenliği iddiası değildir. GitHub job logları artifact scan kapsamına girmez ve canary sonucu `UNVERIFIED` kalır. Hang timeout'ta full/minidump üretilmez; test host `dump-type none` ile sonlandırılır.

Local M2 gate'i `eng/Invoke-WindowsQualityGate.ps1` komutudur. Exact SDK `10.0.302` (`rollForward: disable`, prerelease kapalı), locked restore, Debug/Release x64 build, aynı full suite'in ayrı TRX dizinlerinde iki ardışık geçişi, exact sentinel TRX'inde armed `Failed`/disarmed `Passed`, scanner CLI exit `2`/`0`, fixture byte/SHA-256 eşitliği ve quality-artifact canary taramasını tek akışta doğrular. TRX byte eşitliği beklenmez; sıralanmış `testName|Passed` seti karşılaştırılır. Sanitized local özet `.artifacts/quality-gates/evidence/quality-summary.json` altında üretilir.

M3 sonrası aynı gate high-core host restore/build process fan-out'unu sınırlamak için MSBuild node sayısını bire sabitler; method-level paralel unit/integration isolation davranışını değiştirmez. M3 summary'si hosted koşuda doğrulanmış 40-hex `GITHUB_SHA`, local koşuda `null` commit alanı taşır; dirty local değişikliği yanlış bir commit'e bağlamaz. 2026-08-09 local koşusunda architecture 9, unit 91 ve integration 5 olmak üzere 105/105 test iki kez aynı `Passed` setini üretmiş; sentinel, fixture ve canary kontrolleri de geçmiştir. Bu local sonuç hosted/package M3 kanıtı değildir ve M2'nin tarihsel 22-test artifact'ını değiştirmez.

İlk 2026-08-13 M4 foundation koşusu architecture 9, unit 100 ve integration 26 olmak üzere 135/135 testi iki kez aynı `Passed` setiyle üretmiştir. Hosted run `31735655363`, commit `6ac235f49ffd297196189e5e9441ad0c9e04f116` üzerinde source-draft'ta validation-before-mutation, bounded deterministic encoding, exact purpose/store-issued reference bağlama, temporary buffer zeroing, safe store-failure mapping ve post-commit cancellation davranışını; startup'ta 1.024 adayı kabul edip 1.025 adayda hiçbir cleanup mutation'ı yapmadan fail-closed olma sınırını; composition root'ta tek factory call, retained store, fallback yasağı ve neutral `LocalCache` path'ini de kapsayarak architecture 12, unit 114 ve integration 27 olmak üzere 153/153 testi iki kez aynı `Passed` setiyle üretmiştir. Summary `milestone=M4-foundation`, exact commit SHA, sentinel, fixture ve artifact-file canary PASS alanlarını taşır. Signed package artifact'ı `ProtectedStoreDirectoryInitialized=true`, `PayloadLeakGate=true`, `NormalClose=true` ve `PackageRemoved=true` sonuçlarını aynı commit'e bağlar. Bu koşu owner binding'den önceki protected-store v1 (`LocalCache\ProtectedStore\v1`) kanıtıdır; owner-bound v2 için quality veya package kanıtı değildir. Gerçek DPAPI testleri normal Windows test host'u + guarded temp root kapsamındadır; Windows composition factory'nin exception mapping'i doğrudan fault-injection seam'iyle test edilmemiştir. Bu tarihsel koşunun açık bıraktığı per-record 50k yerleşimi daha sonra reddedilmiş ve test-only protected-catalog comparative gate'i kapanmıştır; real-user boundary M4'te tamamlanmış, production same-SQLite-transaction/end-to-end ve durable source lifecycle kanıtı M8 ve production PFN lifecycle/repair M15 kapsamında hâlâ açıktır.

Run `31735655363` attempt 1 içindeki `windows-quality-evidence` artifact ID `9195093623`, ZIP SHA-256 `69b02b78b6580043f4d3cd3ad775544cb0eab987c86f8d497a21ca70a679f6a2`; `windows-msix-smoke-evidence` artifact ID `9195234209`, ZIP SHA-256 `e3ba59e70b4ce4547f5a06b1f5d101b772bb2789d06299fc2fc6cebd0c5267c9` değerindedir. GitHub retention nedeniyle iki artifact'ın expiry tarihi `2026-08-20`dir; bu belge commit/run/hash ve güvenli alanları kalıcı kayıt olarak tutar, artifact dosyasının veya development imzasının kendisi değildir.

Source-draft kanıtı yalnız doğrudan await edilen çağrıda store-issued reference'ın başarılı sonuçla döndüğünü ve commit sonrası cancellation'ın yeniden gözlenmediğini kapsar. Caller abandonment/retry, duplicate create, process crash/OOM, metadata persistence ve orphan reconciliation kapsam dışıdır. Internal source-payload codec v1'dir: credential için `SRCRED01`, remote-playlist locator için `SRCLOC01` kullanır. Encode→decode exact UTF-8 slice round-trip; wrong magic/kind/version; her truncation noktası; non-positive/overflow/inconsistent length; trailing byte; invalid UTF-8/control/whitespace ve exact Unicode-scalar sınırları test edilir. Decoder string/URI üretmez ve public resolver değildir. Decoder dilimi dahil tarihsel local exact-SDK snapshot architecture 12, unit 120 ve integration 27 olmak üzere 159/159 testi iki temiz koşuda; owner-v2 snapshot'ı 171/171 testi iki koşuda geçmiştir. Exact configuration-record deletion primitive'i dahil tarihsel local gate architecture 13, unit 137 ve integration 33 olmak üzere 183/183 testi iki aynı `Passed` setiyle geçmiştir. Channel deletion primitive'i dahil tarihsel local snapshot exact SDK `10.0.302` ile Debug/Release x64 build'leri 0 warning/error; architecture 13, unit 151 ve integration 34 olmak üzere 198/198 testi iki identical `Passed` setiyle; sentinel fail/recovery, fixture determinism, scanner CLI self-test ve artifact-file canary taramasını geçmiştir. Local summary `commitSha=null` taşır; bu sonuç commit-bound hosted/package kanıtı değildir.

Protected-store katmanının güncel implementation'ı v2'dir: binary envelope magic'i `SRCSEC02`; path `LocalCache\ProtectedStore\v2`; kayıt ve temp namespace'leri sırasıyla `record-v2-*` ve `temporary-v2-*` olur. Credential ve remote-playlist locator kayıtları source-configuration owner'ına, stream ve logo locator kayıtları channel owner'ına bağlanır; owner kind/ID envelope context, entropy ve dosya-adı digest girdisidir. Authoritative owner sağlandığında cross-owner reference-only substitution read/update'ta reddedilir; yanlış-owner delete doğru owner'ın kaydını kaldırmaz. Aynı channel owner/reference altında stream↔logo purpose-only swap read/update'ta reddedilir ve yanlış-purpose delete doğru kaydı korur. Bu kanıt owner + reference + endpoint tuple'ının birlikte değiştirilmesini, resolved locator origin'i, durable authoritative owner metadata'sını veya lifecycle/deletion/reconciliation'ı kapatmaz. Pre-release v1 kayıtları bilerek korunur fakat v2 store tarafından okunmaz; authoritative inventory olmadığı için otomatik migration ya da silme yapılmaz. Owner-bound v2 full local gate PASS'tir. Commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` için run number `#15`, ilk v2 hosted quality/package/lifecycle geçişini workflow/UI düzeyinde **VERIFIED** yapmıştır. Exact configuration-record deletion primitive'ini içeren commit `3e806edd9a9482b87cccbf000ac8a81823562f6a` için run number `#16` da 3/3 yeşil işi, üç artifact'ı ve `8m24s` toplam süreyi yalnız workflow/UI düzeyinde **VERIFIED** yapar. Signed two-version update lane'ini ve package output-directory düzeltmesini içeren commit `bfe5960d5bc911e21ba1e1e1675deb575400e531` için run number `#18`, 3/3 yeşil işi, üç artifact'ı ve `9m16s` toplam süreyi gösterir; commit-bound package job'u lifecycle betiğini zorunlu çalıştırdığı için update execution workflow/UI düzeyinde **VERIFIED**dır. Database run ID, job log/test sayısı ve indirilen evidence schema v2 artifact alan/ZIP/içerik/tam hash doğrulaması yapılmamıştır.

Configuration deletion testleri, yalnız `DeletionPending` aggregate'ten türetilen exact source/configuration-owner/purpose/reference tuple'ıyla tek configuration kaydının silindiğini; aynı `SourceId` altındaki sibling configuration kaydının korunduğunu; ikinci delete'in idempotent olduğunu; pre-cancel'in mutation yapmadığını ve commit anındaki cancellation'ın success'i bozmadığını doğrular.

Yeni `SourceChannelProtectedRecordDeletionService` contract'ı caller'ın durable `DeletionPending` olduğunu varsaydığı `ContentSource` ile authoritative persistence'tan yüklenmesi gereken `PlaylistSnapshot` + `LiveChannel` zincirini kabul eder; service provenance veya authorization kurmaz. Eklenen unit/integration senaryoları source→snapshot, snapshot→channel ve stable-key→source bağlarından biri bozuksa mutation olmamasını; `ChannelId` owner ile stream→optional-logo exact sırasını; idempotent no-op/retry davranışını; ilk başarılı commit'ten sonra newly-requested cancellation'ın ikinci exact delete'i durdurmamasını; partial failure retry'ının store recovery sonrasında yakınsamaya izin vermesini ve target channel silinirken sibling channel ile configuration kayıtlarının korunmasını doğrular. Bu primitive source status'unu persist etmez, operation admission'ı kapatmaz, in-flight kullanımı drain etmez, bütün channel'ları enumerate etmez, metadata/cache/orphan reconciliation yapmaz ve configuration primitive'iyle birlikte source-wide coordinator değildir. Bu senaryolar tarihsel local `198/198 ×2` channel-primitive snapshot'ında PASS'tir. Ayrıca 2026-08-14 run number `#21`, commit `0ef0da5793dc4fd1f4e83dbe38cd49d7a7ccb56f` için 3/3 işi yeşil, üç artifact ve `9m25s` toplam süreyle channel primitive'in commit-bound hosted execution'ını workflow/UI düzeyinde **VERIFIED** yapar. Artifact alanları, ZIP/içerik ve tam digest bağı bağımsız doğrulanmamıştır.

2026-08-14 run number `#22`, threat-model kararı ve production `asInvoker` / `uiAccess=false` manifestini içeren commit `43bf88c591bdc216d3a288ca578f90e4fad927c6` için 3/3 işi yeşil, üç artifact ve `8m23s` toplam süre gösterir. Exact quality gate ile signed package smoke/lifecycle adımları commit-bound olduğundan declarative least-privilege manifest sınırının ve packaged execution'ın hosted sonucu workflow/UI düzeyinde **VERIFIED**dır. Bu, R26 kararının doğruluğunu, runtime token'ın non-elevated olduğunu, atomik path hardening'i veya same-user sandbox'ı kanıtlamaz; artifact JSON/ZIP içeriği, tam digest, job log/test sayısı ve GitHub database run ID'si bağımsız doğrulanmamıştır.

2026-08-14 run number `#23`, yalnız documentation değişikliği olan commit `fc96a211171d1e4f5e5f02174da6c565ef2d59bb` için GitHub Actions UI'da 3/3 yeşil iş, üç artifact ve `8m22s` toplam süre gösterir. Bu kayıt yalnız o docs-only commit'in workflow/UI sonucudur; M4 Decision workload'u veya yeni threat-model doğrulaması değildir. Artifact içerikleri, tam digest bağları, job log/test sayısı ve GitHub database run ID'si bağımsız doğrulanmamıştır.

Run number `#24` UI kaydı da yalnız documentation değişikliği olan commit `207455a54d2d7ac9b6b5c1ce8eb5e29bbee0c383` için 3/3 yeşil iş, üç artifact ve `9m02s` toplam süre gösterir. Protected-catalog aday executable'ı normal hosted workflow tarafından çağrılmadığından bu kayıt aday Smoke veya `Decision` kanıtı değildir.

Run number `#25` UI kaydı, `test(windows): spike protected catalog layout` başlıklı commit `786151cef6c60de5e34009ebb6643ec804490ee9` için 3/3 işi yeşil, üç artifact ve `8m32s` toplam süre gösterir. Commit-bound normal workflow adayı solution'ın Debug/Release x64 build'lerinde derlemiş; architecture guard'ları production graph/normal-workflow ayrımını denetlemiş ve package smoke çıkarılmış production MSIX'te `IptvSuite.ProtectedCatalogSpike*` denylist'ini uygulamıştır. Bu compile/isolation/package-exclusion zinciri workflow/UI düzeyinde **VERIFIED**dır. Normal workflow opt-in wrapper'ı çağırmadığından bu run'da `Smoke` veya `Decision` çalıştırılmamış, candidate evidence yüklenmemiştir; exact hosted test sayısı ile artifact JSON/ZIP/içerik/tam digest bağları bağımsız doğrulanmamıştır. Comparative `Decision` daha sonra ayrı clean-commit local evidence ile tamamlanmıştır; run `#25` bu sonucun kanıtı değildir. Production seçimi açık, M4 foundation `COMPLETED`, ADR-003 `Proposed` kalır.

`IptvSuite.ProtectedCatalogSpike` normal quality/hosted akışta executable olarak çağrılmayan, nonpackable/nonpublishable x64 test-only adaydır. Bununla birlikte fresh explicit onayla clean/eligible commit `7cd5bf739712a5cbdcf47634275a113faab8c48e` üzerinde ayrı 5k/10k/20k/50k × 20 `Decision` tamamlanmıştır. Dört ölçekte 20'şer raw sample, workload hashleri/aggregate'ler, 22 reader Boolean'ı, fault/cancellation matrisi, canary ve cleanup **VERIFIED**dır. 50k p95: create+activate `173,128 ms`, allocation `36360200` byte (`34,676 MiB`), boundary working-set delta `16625664` byte (`15,855 MiB`), aynı-process reopen+unwrap `13,231 ms`, 256-read `1,695 ms`, snapshot delete `2,321 ms`, disk `16800374` byte (`16,022 MiB`) ve staging cancellation `5,570 ms`; post-request encryption/activation/post-completion mutation/temp artifact `0`dır. Baseline'a göre create `874,766×` hızlı (`%99,886` azalma), allocation `19,264×` düşüktür (`%94,809` azalma). `209.828` byte summary SHA-256 `4a219e802dcff959644bda42489f0ee5231be63df1eb37d034e2ed16ff0859de`dir ve exact input/workload hash zinciri [Decision evidence](M4_PROTECTED_CATALOG_DECISION_EVIDENCE.md) belgesindedir. Runner threshold uygulamaz. Component'in 3 s/150 MiB/250 MiB/250 ms referansları altında kalması comparative benchmark gate'ini kapatan **INFERENCE**dır; normalize/index/SQLite/parser/UI/network/package lifecycle kapsam dışı olduğundan end-to-end PASS değildir. Immutable cross-file container production için **NO-GO**dur; reviewed yön DPAPI-wrapped per-snapshot DEK + per-row AES-GCM + active snapshot'ı aynı SQLite transaction domain'inde tutmaktır ve M8'de `Proposed`/unimplemented kalır. M4 foundation `COMPLETED`, ADR-003 `Proposed`dur.

Yeni packaged lifecycle lane production shell'e test switch'i eklemez. Ayrı test identity'si ve yalnız `runFullTrust` capability'si olan host, aynı publisher/signer ve package family altında baseline `0.0.1.0` ile update `0.0.2.0` MSIX'lerini ayrı rebuild/output köklerinde üretir. Microsoft'a göre normal package update aynı family içinde daha yüksek hedef sürüm ister; `Add-AppxPackage -Path` imzalı paketi kullanıcıya ekler/günceller ve bağımlılıkları `-DependencyPath` ile alır [S89][S90]. Lane bu normal ileri-update yolunu kullanır; force-any-version veya dependency-only `-Update` switch'i kullanmaz. Baseline process'leri create ve duplicate-create rejection'ı çalıştırır; update kurulduktan sonra updated process önce baseline protected kaydını okur, cross-owner negatifleri ve doğru-owner update/delete/post-delete matrisini tamamlar. DPAPI-protected bounded control ticket `Creating → Created → Consuming` state'ini activation ve package version sınırı boyunca taşır; argümanda yalnız fixed phase ve secretsiz run ID bulunur. Exact source/configuration owner/reference yalnız ticket içinde kalır. Canary gate create, post-update ve final aşamalarında exact `ProtectedStore` ile lifecycle run-directory ağaçlarını tarar; Windows-managed mutable package hive'ları kapsam dışıdır. Scanner exit `2` bulgu, exit `1` operasyonel hata ve diğer exit'ler contract sapması olarak ayrı stable code'larla fail-closed kalır. Lifecycle MSIX'leri ve app data upload edilmez; cleanup tamamlanmadan success evidence yayımlanmaz. Sanitized evidence schema v2; iki package file/version/SHA-256/signature alanını, same-signer/family, changed-full-name, update-installed, post-update read/scan ve lifecycle/cleanup Boolean'larını allowlist'ler. Kod ve workflow tanımı tek başına PASS değildir: `#15` yalnız tarihsel same-version akışı doğrular, `#16` ise bu update lane'inden önceki deletion commit'ine aittir. Run number `#18`, commit `bfe5960d5bc911e21ba1e1e1675deb575400e531` üzerindeki güncel signed update lane'inin hosted execution'ını workflow/UI düzeyinde **VERIFIED** yapar. Lifecycle artifact'ı indirilmediği için schema v2 alan değerleri, ZIP içeriği ve tam hash bağı bağımsız doğrulanmamıştır; local elevated execution kaydı yoktur.

Schema v3, bu doğrulanmış update zincirinin arkasına iki fresh-state kontrolü ekler. Updated package canlı record/ticket oluşturduktan sonra exact package full name ile reset edilir; version/family/full-name değişmeden exact owned state'in manual cleanup öncesinde yok olduğu ve sonraki create'in yeni record identity ürettiği doğrulanır. Bu state yeniden canlıyken exact current-user package preserve-data ve `-AllUsers` kullanılmadan kaldırılır; registration ile exact app-data yokluğu doğrulandıktan sonra aynı signed `0.0.2.0` package/dependency yeniden kurulur, boş başlangıç state'i ve ikinci farklı record identity ile fresh create/delete tamamlanır. Reset'in app data'yı kalıcı silen fresh-install dönüşü, repair'in veriyi koruyan ayrı işlem olması ve clean uninstall'ın package state'ini kaldırması resmi platform davranışlarıdır [S91][S92]. Evidence schema v3 path, PFN/full-name, record leaf, source/owner/reference veya kullanıcı kimliği eklemeden reset/uninstall/reinstall/fresh-create sonuçlarını Boolean allowlist ile taşır. Architecture testleri adım sırasını, reset/uninstall incelemesinden önce manual app-data cleanup yapılmamasını, live-state uninstall önkoşulunu, preserve/all-users yasaklarını ve evidence denylist'ini statik olarak kilitler. Run number `#20`, commit `6f30e3fa92f329c1fa26333c591edb5148261cec` için bu kodun hosted execution'ını workflow/UI düzeyinde **VERIFIED** yapmıştır; artifact alanları/ZIP/içerik/tam hash bağı bağımsız doğrulanmamıştır. Wrong-user M4'te tamamlanmıştır; durable source-wide deletion/reconciliation M8; production PFN/repair/identity M15 kapsamında açık kalır. Comparative persistence kararı kapanmıştır; M8 aynı-SQLite-transaction production implementation ve crash/power-loss/end-to-end kanıtı henüz yoktur.

2026-08-14 run number `#19`, commit `71dbb0cf23467d978674976796e2bbceca01494a` üzerindeki schema-v3 lane'i broad `PackageReset`/`PackageResetFailed` stage'ine kadar çalıştırmış, packaged job'u burada; required coordinator'ı da bunun sonucu olarak kırmıştır. Historical stage hem native cmdlet invocation'ını hem registration/identity/manifest postcondition'larını kapsadığı ve raw HRESULT kaydedilmediği için exact failure point bilinmez; `#19` native reset veya ardından gelen uninstall/reinstall PASS kanıtı değildir. Takip implementation'ı `PackageResetInvocation`, `PackageResetRegistrationValidation` ve `PackageResetManifestValidation` stage'lerini ayrı stable code'larla ayırır. Exception zincirindeki bilinen HRESULT yalnız allowlist'li, secretsiz failure `Code`una çevrilir; raw HRESULT/message/ActivityId veya yeni diagnostic evidence alanı yayımlanmaz. S93'teki hata anlamlarına dayanan mühendislik retry politikası (`INFERENCE`), yalnız `0x80073D00`, `0x80073D02` ve `0x80073D05` sınıflarına 500 ms ve 1500 ms beklemeli toplam en fazla üç deneme verir; her deneme öncesi bounded exact-process quiescence, invocation sonrasında bounded registration readiness aranır [S93]. Run `#20` UI kaydı retry'ın tetiklenip tetiklenmediğini göstermez. Ancak takip commit'i `6f30e3fa92f329c1fa26333c591edb5148261cec` için run `#20` 3/3 işi yeşil, üç artifact ve `8m14s` toplam süreyle tamamlanmıştır; zorunlu lifecycle adımı nedeniyle tanılama hardening'i ve schema-v3 reset/live-state uninstall/reinstall fresh-state zinciri workflow/UI düzeyinde **VERIFIED**dır. Artifact içeriği bağımsız doğrulanmamıştır; run `#19` tarihsel failure, run `#18` update **VERIFIED** kaydı ve M4 foundation `COMPLETED` durumu korunur.

M4 per-record DPAPI ölçümü `eng/Invoke-WindowsSecretStoreSpike.ps1` ile quality/CI dışındaki ayrı Release x64 executable'da çalışır. `Smoke` yalnız 1.000 kayıt/tek tur ve harness doğrulamasıdır. `Decision`; clean worktree + explicit switch ile ölçüm dışı warmup sonrasında sabit 5k/10k/20k/50k × 20 tur, aggregate create/restart/bounded-read/delete ölçümleri ve 20 cancellation-boundary örneği üretir. Evidence; başlangıç/son commit-clean doğrulaması, exact SDK/runtime/OS/architecture, runner/spec/license/workload SHA-256, duration/throughput/allocation/GC/boundary working-set, request→completion cancellation latency, committed alt/üst sınırları, cleanup ve pre-publish canary-scan durumunu taşır; raw sample, locator/reference/path, user veya hostname taşımaz. Tek-process lock eşzamanlı run'ların ortak work/evidence yüzeyini silmesini veya ezmesini engeller. Bu unpackaged, cache-controlsüz DPAPI-only sonuç normalize/parser/index/packaged lifecycle maliyetini kanıtlamaz ve tek başına ADR kabulü değildir.

2026-08-14 Decision koşusu clean commit `fc96a211171d1e4f5e5f02174da6c565ef2d59bb` üzerinde exact SDK `10.0.302` ile sıfır exit code verdi; summary SHA-256 `8cd4c6d86b813fd07794217a71a824e7368694363f89a16be36cb8a311d67460`, workload SHA-256 `eb6a4eaaecf437e80ef01feb00c6d1453e41994682a76ed08f81c1808a372f3f` oldu. Bu **VERIFIED** sonuç workload correctness, canary ve cleanup PASS'idir; runner threshold uygulamadığı için performans PASS'i değildir. Create p95 5k/10k/20k/50k için sırasıyla `16,041 s`, `30,509 s`, `60,777 s`, `151,447 s`; 50k create p95 allocation `668,007 MiB` oldu. 50k delete p95 `14,821 s`, 256-record read p95 `78,853 ms`, store-adapter restart p95 `0,416 ms`; cancellation p95 `1,067 ms`, post-cancel commit üst sınırı `0` ve temporary artifact sayısı `0` olarak ölçüldü. Tek başına 50k create'in p95 `≤ 3,0 s` normalize + persistence + index, p95 `≤ 5,0 s` end-to-end ve `≤ 150 MiB` bütün import allocation bütçelerini aşması nedeniyle mevcut per-record blob/dosya yerleşiminin 50k channel locator persistence için reddedilmesi **INFERENCE**dır. Ayrıntılı hash bağı, yorum sınırı ve ölçüm kısıtları [M4 secret-store Decision kanıtında](M4_SECRET_STORE_DECISION_EVIDENCE.md) kayıtlıdır. Takip protected-catalog comparative `Decision`ı tamamlanmış ve comparative gate kapanmıştır; M4 foundation `COMPLETED`, ADR-003 `Proposed` kalır. M8 production SQLite transaction implementation ve end-to-end referans-cihaz ölçümü gerekir.

Spike summary'sinin diskte bulunması PASS değildir; başarısız yeni koşu önceki temiz summary'yi koruyabilir. Yalnız wrapper'ın sıfır exit code'u ve summary içindeki commit/hash bağları birlikte değerlendirilir. Runner assembly SHA-256 yalnız çalıştırılan spike DLL'ini bağlar; bütün dependency seti veya reproducible build kanıtı olarak yorumlanmaz.

Hosted `windows-quality.yml` bütün pull request, `merge_group`, `main` push ve manual dispatch olaylarında `windows-2025-vs2026` üzerinde exact SDK ile quality gate'i çalıştırır; package job'u quality başarısına bağlıdır. `always()` coordinator'ı quality fail veya package skip/fail sonucunu tek `Required Windows gate` check'inde kırmızıya çevirir. Workflow düzeyinde path filter bilerek yoktur, çünkü filtre nedeniyle skip edilen required check `Pending` kalıp merge'i engelleyebilir [S81]. Raw TRX ve lifecycle test MSIX'i upload edilmez; scanner'dan geçmiş minimal summary, manifest + license sidecar, package launch success ve package lifecycle success evidence yedi gün tutulur. Yeşil hosted package job'u yalnız o commit/runner için production signed x64 MSIX'in hedefli test-payload/canary inspection, install, `EnableLUA=1` önkoşulu altında resmi AUMID `LOCAL_SERVER` activation API'sinden dönen exact PID ile visible launch, normal close ve exact cleanup zincirini; ayrıca o committeki ayrı test identity lifecycle implementasyonunu kanıtlar [S86][S87]. Lifecycle host, desktop'ta daima boş olan XAML launch argümanı yerine packaged activation payload'unu `AppInstance` üzerinden erken alır [S88]. Run `31735655363` içindeki sanitized artifact exact package-family altındaki tarihsel `LocalCache\ProtectedStore\v1` dizininin oluştuğunu/reparse point olmadığını `ProtectedStoreDirectoryInitialized=true` ile doğrulamıştır; bu dizin kanıtı owner-v2, secret CRUD veya lifecycle kanıtı değildir. 2026-08-14 UI kaydında run number `#15`, commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` için üç işi yeşil ve artifact sayısını üç gösterir; ilk v2 quality/package/same-version lifecycle geçişi bu workflow/UI düzeyinde **VERIFIED**dir. Aynı tarihli run number `#16` UI kaydı, exact deletion primitive'ini içeren commit `3e806edd9a9482b87cccbf000ac8a81823562f6a` için 3/3 işi yeşil, artifact sayısını üç ve toplam süreyi `8m24s` gösterir; bu takip sonucu da yalnız workflow/UI-level **VERIFIED**dir. Run number `#18` UI kaydı, signed two-version update lane'ini ve package output-directory düzeltmesini içeren commit `bfe5960d5bc911e21ba1e1e1675deb575400e531` için 3/3 işi yeşil, artifact sayısını üç ve toplam süreyi `9m16s` gösterir; commit-bound package job'u lifecycle betiğini zorunlu çalıştırdığından update execution workflow/UI düzeyinde **VERIFIED**dır. Bu UI kayıtları için GitHub database run ID'leri, job log/test sayıları ve indirilen artifact field/ZIP/içerik/tam hash bağları doğrulanmamıştır. Gözlemlenebilen erken exit safe numeric exit-code ile, activation ile process gözlemi arasındaki çok kısa çıkış ise sabit bir hata sınıfıyla ayrılır; activation tanısı uygulama path/title/event-log metnini sorgulamaz. Bu smoke/lane; feature UI/UIA/accessibility, production PFN update, reset/uninstall-reinstall, WACK/Store, non-admin veya clean-machine matrisi, second-user, gerçek provider/internet, player/codec/HW decode, ARM64 ve genel credential yokluğu kanıtı değildir.

[Run `31327398270`](https://github.com/serkankaracan/iptv-suite/actions/runs/31327398270), commit `79cf619c6683fa9c4213846455e376fb1b0cb11c` için quality, packaged-smoke ve `Required Windows gate` işlerini başarıyla tamamladı; indirilen iki allowlist'li artifact `14/14` alan/hash assertion'ını geçti. Kalıcı değerler [M2 completion evidence](M2_COMPLETION_EVIDENCE.md) belgesindedir. Coordinator'ın yeşil olması merge policy enforcement kanıtı değildir: 2026-08-09 preflight'ında mevcut private-repository planı branch protection endpoint'inde `403` verdiği için check henüz branch policy tarafından required değildir.

2026-08-21 güncellemesi: özel `windows-2025-vs2026` label'ında üç ardışık iş sıfır step ve `runner_id=0` ile tahsis edilemedi. Workflow bu nedenle, 2026-06 sonrasında aynı Windows Server 2025 + VS2026 image ailesine yönlenen GA `windows-2025` label'ına taşındı [S114]. Exact .NET SDK setup ve runtime verification, dört required job, fail-closed coordinator ve bütün security/package gate'leri değişmeden kalır.

2026-08-24 native-runner düzeltmesi: Standart GitHub-hosted x64 Windows label'ları Windows Server'dır; mevcut Windows Client label'ı ARM64 olduğundan x64 Client-only Media Foundation kabulü standart hosted runner'da temsil edilemez [S114]. Run `32752547781`de package job'u production MSIX içindeki sentetik HLS `play → pause → resume → stop` zincirini geçerken, sonradan tek başına çalışan M10 spike job'u aynı committe `InstallationType=Server` üzerinde transport/source-open tamamlandıktan sonra `MediaOpenTimeout` vermiştir. Bu nedenle native M10 smoke kaldırılmadan yalnız explicit manual dispatch + `self-hosted/Windows/X64/iptv-windows-client` label'larına bağlanır ve registry/architecture preflight yanlış runner'ı fail-closed reddeder. Normal push/PR required gate'i quality, production package playback ve DPAPI işlerini zorunlu tutar; native job'un yalnız talep edilmediğinde `skipped`, talep edildiğinde ise `success` olmasını exact kontrol eder. Startup/resource/security eşikleri ve script değiştirilmemiştir.

2026-08-24 M11 completion: Clean head `5a07cae8138642d669f272eaee1340e87edcb47b` için [run `32754184474`](https://github.com/serkankaracan/iptv-suite/actions/runs/32754184474), quality `354/354 × 2`, signed production-package `play → pause → resume → stop`, protected-store lifecycle, DPAPI real-user boundary ve Required Windows gate işlerini geçti. İndirilen commit-bound package evidence `PlaybackUiAcceptanceVerified=true`, request/completed `1/1`, `1.002.980` completed body byte, normal close ve exact package removal kaydetti. Native Client-only iş normal push'ta policy gereği `skipped` kaldı; bu M10 testini veya eşiklerini değiştirmez. Exact artifact ID/digest ve kapsam sınırı [M11 completion evidence](M11_COMPLETION_EVIDENCE.md) belgesindedir.

## 3. Feature test planı

### 3.1 Validation ve endpoint construction

Table-driven testler:

- blank/whitespace, Unicode/IDNA host, IPv4/IPv6, default/non-default port;
- `http/https` dışı scheme, user-info, fragment, query credential;
- aşırı uzun name/URL/field, control/NUL, mixed normalization;
- trailing slash ve provider endpoint join; path traversal veya double-encoding;
- `.m3u8` uzantısının tek başına catalog/HLS ayrımı yapmaması;
- redirect target scheme/origin policy;
- error result'in raw input/credential taşımaması.

URL normalization stream'i bozacak biçimde query ordering/value değiştirmemeli; display-safe endpoint ayrı, raw protected locator ayrı olmalıdır.

### 3.2 M3U incremental parser

M7 implementation `COMPLETED, 2026-08-20`: production loader response body'yi string'e çevirmeden streaming parser'a verir ve accepted entry'leri mandatory internal sink'e tek tek aktarır. Deterministik malformed-byte corpus, exact 50k/limit+1, HLS routing, final-origin resolution, consumption-time byte cap ve mid-stream cancellation testleri bu sınırı kapsar. Local exact-SDK gate architecture 19 + unit 157 + integration 84 = **260/260 ×2** PASS'tir; ayrıntı [M7 completion evidence](M7_COMPLETION_EVIDENCE.md) belgesindedir.

Golden corpus en az:

- empty file, yalnız `#EXTM3U`, BOM/no BOM, CRLF/LF/CR;
- UTF-8/Unicode NFC-NFD, kontrollü legacy fallback ve invalid byte;
- quoted/unquoted/mixed-case attributes; escaped quote; extra whitespace;
- `tvg-id`, `tvg-name`, `tvg-logo`, `group-title`, channel-number hints;
- unknown directive/attribute;
- orphan `#EXTINF`, orphan locator, missing name/URL, comments/blank lines;
- relative URL, query token, user-info, invalid/unsafe scheme;
- duplicate exact entry, same name/different locator, conflicting provider ID;
- HLS master/media manifest'in channel catalog yerine single-stream source olarak sınıflandırılması;
- oversized line/attribute/value/item count; truncated input;
- cancellation her pipeline stage'inde;
- 5k/10k/20k/50k generator outputs.

Property/fuzz invariants:

- parser crash/hang etmez ve budget üstünde allocation yapmaz;
- accepted entry sayısı + rejected entry sayısı input assembly sonucuyla tutarlıdır;
- secret locator output/log string'e çıkmaz;
- aynı input + version aynı normalized result/stable key üretir;
- cancellation active snapshot'ı değiştirmez.

### 3.3 Xtream-compatible API

Local fake server fixture'ları:

- successful auth/account info, live categories ve live streams;
- 200 içinde “auth failed”, 401/403, 404, 429 + Retry-After, 5xx;
- empty/missing/null field; bool/int/string dönüşümleri; unknown extra fields;
- duplicate/missing category ve stream IDs; unusual stream extension;
- malformed/truncated JSON, excessive depth/array/string, gzip;
- slow headers/body, mid-body disconnect, timeout/cancellation;
- same-origin/cross-origin redirect, HTTPS downgrade ve cookie attempt;
- large Live-only catalog; VOD/Series çağrılmadığının request assertion'ı.

Endpoint builder ayrı pure tests alır. Provider DTO'su domain/persistence'e sızamaz. Contract corpus sürümlenir; gerçek provider adı taşımaz.

### 3.4 Normalization ve persistence

- text normalization/control removal/length; localized “Uncategorized” display key;
- stable-key priority, collision discriminator ve version migration;
- exact duplicate collapse, conflicting ID retain+warning;
- category/channel foreign keys ve indexes;
- fresh DB, N-1→N migration, failure rollback, corruption/recovery;
- staging + atomic activation, concurrent read/refresh, app termination fault injection;
- content hash/parser/schema version cache invalidation;
- source deletion, protected-store failure, `DeletionPending` ve startup orphan reconciliation;
- DB/WAL/SHM/log içinde canary secret binary scan.

SQL tests gerçek SQLite ile çalışır; yalnız mocked repository “DB doğru” kanıtı değildir.

### 3.5 Secure storage ve redaction

Ortak secret-store contract:

- create/read/update/delete/idempotency;
- missing/corrupt/wrong-user/wrong-context;
- restart, packaged update, reset, uninstall/reinstall;
- deletion crash ve orphan recovery;
- 5k–50k locator throughput/cancellation.

Redaction corpus:

- URI user-info;
- case/encoding varyantlı password, username, token, auth, key, signature query;
- nested/percent-encoded URL;
- Authorization/Proxy-Authorization/Cookie/Set-Cookie/custom API key;
- provider body/exception içinde echo edilen secret;
- JSON, structured scope, interpolated string ve native player log;
- Unicode homoglyph testleri tanılama için; allowlist güvenlik kontrolü yerine kullanılmaz.

Canary secret; app data, DB/WAL, logs, screenshots metadata, crash/support artifact ve MSIX content üzerinde aranır. Beklenen plaintext yalnız test process'inin kontrollü memory/input boundary'sidir.

### 3.6 HTTP, timeout, cancellation ve retry

Fake server/network fault:

- DNS/connect/TLS/header/body timeout ayrımı;
- user cancellation'ın bütün read/parse/write/retry zincirini kesmesi;
- redirect loop/max hop/origin değişimi/downgrade;
- 408/429/selected 5xx bounded retry; `Retry-After` cap;
- auth/TLS/parse error'da retry olmaması;
- disconnect/stall/partial body/decompression bomb/oversize;
- cookies default disabled ve cross-source isolation;
- simultaneous source refresh ve app shutdown.

Deterministic fake clock backoff testini hızlı yapar; en az bir packaged end-to-end test gerçek timer/cancellation davranışını doğrular.

### 3.7 Image pipeline

- scheme/address/redirect/DNS rebinding policy;
- credential/cookie/referer gönderilmemesi;
- MIME/magic mismatch, oversized bytes, excessive dimensions/pixels ve malformed image;
- concurrency cap, priority, visible-item cancellation ve rapid scroll;
- LRU eviction, cache version/corruption, source delete;
- placeholder ve decode error'ın UI virtualization'ı bozmaması.

### 3.8 Player adapter

Aynı contract suite fake ve gerçek adapter'a uygulanır:

- legal state transitions, tek active session;
- open/play/pause/stop/volume/mute/track/aspect;
- open→cancel, rapid open A→B, stop/dispose callback race;
- stale session event'inin yeni session'ı değiştirmemesi;
- engine error'ın stable `DomainError`'a mapping'i;
- native event thread'inden UI dispatcher sınırı;
- repeated dispose ve app/window lifecycle;
- raw URI/header/native log redaction.

Unit fake player codec desteğini kanıtlamaz; compatibility corpus gerçek engine/device ister.

## 4. Büyük playlist sentetik corpus'u

### 4.1 Boyutlar

| Corpus | Channel | Category | Logo oranı | Amaç |
|---|---:|---:|---:|---|
| Small | 100 | 10 | %50 | Developer smoke |
| Medium | 5.000 | 100 | %70 | Normal regression |
| Large | 10.000 | 200 | %80 | Scale trend |
| Very large | 20.000 | 300 | %90 | Memory/DB pressure |
| MVP gate | 50.000 | 500 | %100 reference, fetch edilmez | Acceptance |
| Stress | 100.000 | 1.000 | %100 | Limit davranışı; release gate değil |

Her boyutta duplicate, missing group, malformed entry, Unicode, long text, token-bearing locator, invalid logo ve stable-key collision varyantı bulunur. Generator seed ve version report'a kaydedilir. Logo URL sayısı, 50.000 image'ın aynı anda indirileceği anlamına gelmez.

### 4.2 Ölçüm aşamaları

Zamanlar ayrı ölçülür:

1. DNS/connect/TTFB/download bytes.
2. Encoding + line tokenization.
3. Directive parse + validation.
4. Normalize + stable key.
5. Secret protection.
6. SQLite staging/index + activation.
7. Cached DB open/first query.
8. UI realization/search/filter/scroll.
9. Image fetch/decode/cache.

Network total'i parse/import budget'ına eklenmez. Her stage wall-clock, CPU, allocation, peak working set, bytes read/written, DB size ve cancellation latency raporlar.

## 5. Provisional performans bütçeleri

Referans: Release x64, telemetry/profiler instrumentation'ın ölçümü bozmadığı mod, güncel servisli Windows 11, 4 logical core, 8 GiB RAM, SSD ve entegre GPU sınıfı clean cihaz. Commit, OS build, .NET/WASDK, DB schema, player, GPU/driver ve power mode rapora yazılır.

| Alan | Bütçe / kabul |
|---|---|
| 50k local bytes parse | p95 ≤ 2,0 s |
| 50k normalize + protected persistence + index | p95 ≤ 3,0 s |
| 50k end-to-end parse+normalize+persist | p95 ≤ 5,0 s; download hariç |
| Import managed allocation | ≤ 150 MiB |
| Import peak working-set delta | ≤ 250 MiB; player kapalı |
| Parser/import cancellation observed | p95 ≤ 250 ms |
| Cold shell interactive | p95 ≤ 2,0 s |
| Cached DB'den first visible content | p95 ≤ 500 ms |
| Indexed search/filter/category query | p95 ≤ 100 ms |
| Input-to-visible response | p95 ≤ 100 ms |
| Scripted scroll frame | p95 ≤ 33,3 ms; dropped frame < %1 |
| UI thread stall | >200 ms event sıfır; test trace ile |
| Realized channel containers | ≤ 300, viewport'a bağlı assertion |
| Player kapalı steady catalog working set | ≤ 350 MiB |
| Image network concurrency | 4 |
| Image memory/disk budget | Memory-only 32 MiB / 128-entry LRU; MVP durable disk cache `0` byte; gelecekte açılırsa hard üst sınır 200 MiB |
| Tier A playback start, controlled LAN | p95 ≤ 3,0 s |
| Channel switch, controlled LAN | p95 ≤ 3,0 s |
| User retry/reconnect cancel | ≤ 1,0 s; yeni session açılmaz |
| M10 soak | 8 saat, 100 switch, crash/deadlock sıfır |
| M16 RC soak | 24 saat, 200 switch, crash/hang sıfır |
| Soak memory | 30 dk warm-up sonrası monoton trend yok; net büyüme hem ≤%10 hem ≤100 MiB |

Bunlar garanti değil, tasarım bütçesidir. İlk ölçüm bütçeyi geçerse sayı sessizce gevşetilmez: trace ile bottleneck, cihaz temsiliyeti ve kullanıcı etkisi raporlanır; optimize edilir veya Product/Architecture yazılı değişiklik yapar.

### 5.1 Benchmark yöntemi

- Clean build + Release artifact; debug/JIT warm-up etkisi ayrı.
- Cold ölçümde process ve OS-cache koşulu raporlanır; warm ölçüm ayrıca.
- En az 20 iteration; median, p90, p95, max ve coefficient of variation.
- Outlier silinmez; açıklanır. Background process/power/thermal koşulu kaydedilir.
- Baseline artifact saklanır; CI smoke threshold daha gevşek olabilir, M14 dedicated runner authoritative'dir.
- Regression gate: aynı declared profile/baseline'a göre p95'te >%10 **veya** mutlak kullanıcı bütçesi ihlali fail/review gerektirir; profile etiketi fiziksel makine kimliğini bağımsız doğrulamaz.
- ETW/.NET counters/profiler trace yalnız ihlali açıklamak için; benchmark sonucu instrumentation'sız tekrar doğrulanır.
- Signed package smoke'un opt-in `-EmitM14TraceMarkers` modu, exact activation PID'li fixed WPR begin/end marker'larını yalnız input/search + iki scroll/proxy geçişi çevresinde üretir; player-off idle working-set örneklemesini marker aralığına katmaz. Normal CI bu switch'i kullanmaz. Marker üretimi trace PASS'i değildir: dışarıdan CPU + XAML activity/responsiveness kaydı alınır, WPA/ADK `10.1.26100.1+` ile exact PID ve marker aralığında frame/UI delay süreleri incelenir; authoritative bütçe koşusu instrumentation kapalı tekrar edilir [S133][S134].
- Image görünür-pencere stresi; recycle edilen satırın per-row linked cancellation'ını, noncooperative dönüşün cache'e girmemesini, peak concurrency `4` ve `32 MiB / 128-entry` memory LRU sınırını deterministik doğrular. Durable disk image cache MVP'de kapalıdır; `200 MiB` yalnız gelecekte review ile açılabilecek hard üst sınırdır.

`M14 COMPLETED — 2026-08-25`: Commit `912c25a661c08d2a693627f493fa1e2318061c7f` için instrumentation-free 50k reference benchmark parser `65,1227 ms`, conservative import upper bound `1.350,5073 ms`, allocation maximum `146,3085 MiB`, WS delta `3,0039 MiB`, cancellation `15,6047 ms` ve query/reopen `9,4470–26,5633 ms` p95 değerleriyle bütün mutlak bütçeleri geçti. Same-declared-profile baseline/candidate sekiz p95 regression kontrolünü geçti; aynı commit bağı repeatability kanıtıdır. Run `32858287103` normal trace-off package'ta input p95 `3,305 ms`, DWM proxy p95/max `15,625/31,250 ms`, drop proxy `%0`, realized `8` ve player-off WS maximum `164.458.496 byte` ile geçti. Ayrı PID-bound XAML marker penceresinde Frame p95 `2,1269 ms`, bütün XAML event'lerinde maximum `9,6794 ms` ve `>200 ms` event `0` idi. Runner profile fiziksel host kimliğini doğrulamaz; exact sub-stage split, OS-cache-flushed cold koşul ve packaged logo-network/decode acceptance iddia edilmez. [Exact kanıt](M14_COMPLETION_EVIDENCE.md).

## 6. Playback compatibility matrix

### 6.1 Tier'ler

| Tier | Protocol/container | Codec/track | Beklenti |
|---|---|---|---|
| A — MVP hard gate | HLS + MPEG-TS; direct HTTPS continuous MPEG-TS | H.264 High + AAC-LC/HE-AAC, stereo; basic aspect | Referans matrix'te %100 |
| B — Hedef capability | HLS fMP4/CMAF; HLS/TS | HEVC Main/Main10, AC-3/E-AC-3, multi-audio, subtitle/CC | Sonuç capability olarak yayımlanır; legal + device gate |
| C — Robustness | Discontinuity, timestamp reset, variant switch, corrupt/missing segment, EOF | Mixed tracks/metadata | Graceful error/recovery; crash/hang yok |
| Out | DRM, recording, timeshift, unauthorized streams | — | Test edilmez/vaat edilmez |

Bir format “pass” olsa bile exact fixture sonucu; player/build, OS, CPU/GPU, driver, hardware decode state ve package architecture ile kaydedilir.

### 6.2 Cihaz/architecture matrisi

- Clean Windows 11 reference; optional codec package yok.
- Intel iGPU, AMD GPU/APU ve NVIDIA GPU temsilcisi.
- x64 mandatory.
- ARM64 yalnız native LibVLC/plugin zinciri varsa gerçek ARM64 cihaz; x64 emulation ARM64 pass değildir.
- Supported minimum OS ve current OS build.
- Multi-monitor, 100/150/200% DPI, SDR; HDR yalnız gelecek scope.
- Speaker/headphone ve playback sırasında audio-device değişimi.

### 6.3 Network/failure matrisi

- fast/slow start, latency/jitter, bandwidth step-down;
- DNS/TLS/connect/first-byte/read timeout;
- 401/403/404/429/5xx;
- mid-stream disconnect, stall, EOF ve recovery;
- auth header/query, same-origin redirect ve rejected cross-origin secret forwarding;
- user channel switch/cancel sırasında in-flight retry;
- reconnect total budget ve attempt cap.

## 7. Soak ve lifecycle planı

### M10 spike

`VERIFIED — dependency boundary, 2026-08-22`: Önceki central `Microsoft.WindowsAppSDK 2.3.1` graph'ı `Microsoft.WindowsAppSDK.WinUI 2.3.0` çözüyor ve exact WinUI paketi Engineering Preview/live-environment/dağıtım kısıtlı lisans taşıyordu. Microsoft #6654 bu sürümü etkilenen set içinde gösterip yayımlamadan önce `Windows App SDK 2.4.0 → WinUI 2.3.6` düzeltmesine geçilmesini ister [S121][S122]. Yeni root ve WinUI NuGet'lerinin exact SHA-256, embedded license SHA-256 ve Microsoft/NuGet imzaları local audit'te doğrulanmıştır. Paket/runtime binary seti değiştiğinden aşağıdaki eski playback, lifecycle, fault ve developer-soak sonuçları successor graph için yalnız tarihsel regression kanıtıdır.

Güncel graph fail-closed exact package inventory ve kısa packaged/native kontrolleri geçti. Ürün sahibinin 2026-08-23 waiver'ı ADR-007'yi `Accepted with known deviation`, M10'u koşullu kapalı ve M11'i açık yapar. Teknik inventory repository kök `LICENSE`/`NOTICE`, uygulama asset provenance'i, corpus sahipliği/CC0 dağıtım hakkı veya codec/patent hukuk gate'ini kapatmaz; WACK, gerçek cihaz/HW-decode, kalan surface/lifecycle ve release matrix'i M15/M16'da, relative-memory sapması ise R15 altında M16 profiler/final-soak işinde açık kalır.

- 2 saat developer smoke, sonra 8 saat unattended.
- Her 5 dakikada state, safe error count, process private/working set, GC heap, thread, handle ve player stats snapshot.
- 100 scripted channel switch; fullscreen/minimize/restore/display change araları.
- Network interruption/recovery en az her saat.
- Crash, hang, deadlock, double audio, orphan session ve monotonic memory trend fail.

`Invoke-WindowsNativePlaybackSmoke.ps1 -SwitchCount 100 -SoakMinutes 480 -NetworkInterruptionCount 7`, disposable packaged native fallback için bu uzun koşuyu otomatikleştirir: süre ilk switch ile başlar, 100 HLS/direct switch sonrasında HLS loop sürer, her 5 dakikada private-bytes/handle örneği alınır, 60–420. dakikalarda yedi tek-response interruption/recovery uygulanır ve 30 dakika warm-up sonrası net memory büyümesi hem `%10` hem `100 MiB` sınırına bağlanır. Post-warm tüm örneklerin strict artışı ayrıca monoton trend olarak fail olur. Commit `077b3e9d5404055687f4b3e74db41e111a77cc8d` üzerindeki gerçek Windows Client schema-v2 koşusu 100 switch + 120 dakika developer soak için 25 sample, `%2,894`/`6.885.376` byte post-warm büyüme ve monoton trend `false` ile **VERIFIED**dır; [kalıcı kayıt](M10_NATIVE_TIER_A_DEVELOPER_SOAK_EVIDENCE.md) ortamı, hash'leri ve sınırları içerir. Bu tarihsel `2.3.1` graph sonucu sekiz saat acceptance değildir; successor graph'ın ilk sekiz saat sonucu aşağıdadır.

`UNVERIFIED NEGATIVE — kullanıcı tarafından sağlanan ilk sekiz saat successor-graph transcript'i, 2026-08-22`: Transcript, bildirilen fakat artık ref tarafından tutulmayan `c9d5cd67607f97be9c1d49b68587f91c7515846c` için `100/100` switch ve `7/7` interruption/recovery ardından `ResourceBudgetExceeded` gösterir; kalıcı run/commit/hash bağı yoktur. `INFERENCE`: `14369 completed + 49 I/O abort + 7 injected-failure attempt = 14425 mapped request` sayaçları aritmetik olarak eşleşir, fakat bu delivered `503` veya terminal kategori kanıtı değildir. Eski failure logu `ResourceSampleCount`, warm-up, byte, yüzde ve monotonic alanlarını göstermeden transient envelope cleanup'ına geçtiği için exact resource-budget predicate'i — yetersiz sample dahil — `UNVERIFIED`dır. `droppedAfterCapacity=14393`, kod incelemesine göre `14425 - 32` bounded trace omission sayısıdır; transport drop değildir. Diagnostic takip düzeltmesi completed-result invariant'larını doğrular, tamamlanmış probe metriklerini korur ve safe aggregate resource değerlerini loglar; eşikleri değiştirmez. [Negatif kayıt](M10_NATIVE_TIER_A_ACCEPTANCE_SOAK_ATTEMPT.md) acceptance gate'ini kırmızı, R15'i aktif ve tekrar koşusunu zorunlu tutar.

`CONDITIONAL SUCCESS — ürün waiver'ı, 2026-08-23`: Temiz `1f158883b692ed87c7dedcb9348c0d0821b2267e` envelope-v8 sekiz saat koşusu startup, 100 switch, 101 detach, 7/7 recovery, sıfır retry, mutlak memory, non-monotonic trend ve handle-no-growth kontrollerini geçti. Warm-up `162.238.464` byte, net büyüme `18.161.664 byte` (`~17,32 MiB`), relative `%11,1944255`, handle delta `-21` idi. Yalnız `%10` predicate'i `1.937.818 byte` (`~1,85 MiB`) aşıldı. Teknik gate/threshold değiştirilmeden ürün sahibi M11 geçişini kabul etti; retention profiling ve final soak R15 altında M16'ya taşındı. [Exact kayıt](M10_NATIVE_TIER_A_ACCEPTANCE_SOAK_ATTEMPT.md).

Schema-3 kısa packaged surface koşusu iki resize, minimize/restore ve fullscreen/overlapped olmak üzere exact altı geçişi switch döngüsüne bağlar. Her geçişten sonraki open/advance mevcut timeout'larla geçmeden kanıt yazılmaz. 2026-08-21 Windows Client koşusu 100/100 switch ve altı geçişi startup p95 `265,492 ms` ile geçirdi; [surface kaydı](M10_NATIVE_TIER_A_SURFACE_EVIDENCE.md) yalnız bu otomatik alt kümeyi **PARTIAL VERIFIED** yapar.

Schema-4 controller, açıkça istenen sayıda mapped loopback medya isteğine tek-response sentetik `503` enjekte eder; scheduled/injected/recovered sayaçlarının tam eşitliği olmadan evidence yayımlamaz. 2026-08-21 kısa Windows Client koşusu bir interruption, fault sonrasında gözlenen bir mapped response completion'ı ve 100/100 switch'i doğruladı; tarihsel sayaç concurrent response'un fault'tan önce başlayıp başlamadığını ayırmıyordu. [Network kaydı](M10_NATIVE_TIER_A_NETWORK_RECOVERY_EVIDENCE.md) yalnız bu fault türünü **PARTIAL VERIFIED** yapar. Schema-7 request ordinal'i bu yarış sınırını kapatır; sekiz saat koşusunda saatlik schedule ayrıca doğrulanmalıdır.

Schema-7 source-lifecycle checkpoint'i, `MediaPlayer.Source = null` sonrasında `PlaybackSession.PlaybackState` sorgulamanın `COM E_FAIL (0x80004005)` ürettiğini doğruladı; bu nedenle geçersiz post-detach session sorgusu, `CanPause` guard → exact `Source is null` doğrulaması → attempt-owned `MediaSource.Dispose()` invariant'ıyla değiştirildi. 2026-08-21 gerçek Windows Client koşusu 100/100 switch ve 100/100 source detachment; detach p95 `7,986 ms`, maksimum `9,493 ms`; startup p95 `261,169 ms`, maksimum `2.576,309 ms`; altı surface transition; sıfır playback retry; tek network interruption/recovery ve request ordinal `54→56`; exact package/certificate cleanup sonuçlarını **PARTIAL VERIFIED** yaptı. Bu [session-lifecycle kaydı](M10_NATIVE_TIER_A_SESSION_LIFECYCLE_EVIDENCE.md) OS-level session quiescence veya ghost-audio yokluğunu kanıtlamaz; 8 saat, WACK, device/HW-decode, multi-monitor/display-change, audio-device ve sleep/resume kabulleri açıktır.

Schema-9 cleanup contract'ı shared `Microsoft.WindowsAppRuntime.2` ownership'ini fail-closed sınırlar: pre-run exact package-full-name baseline'ındaki hiçbir kayıt kaybolamaz; güncel successor graph'ta post-run ekler exact Microsoft publisher/family, framework, `>=2.4.0.0` version ve izinli X64/X86 architecture ister, X86 ek aynı version'da exact X64 sibling olmadan reddedilir. Doğrulanmış shared ekler korunur ve `Remove-AppxPackage` çağrısı almaz; exact disposable test MSIX/app data/process/certificate/output cleanup'ı aynen kalır [S116]. Evidence `RuntimePackageBaselinePreserved`, `RuntimePackageGraphDisposition=ExactRestored|SharedAdditionsPreserved` ve bounded `RuntimePackageSharedAdditionCount` alanlarıyla bu iki sonucu ayırır. Eski `2.3.1` graph'ında commit `b4839bf` için run #150 (`32546738817`) bu sözleşmenin o sürümünü commit-bound hosted PASS yaptı; sonuç yeni graph için tarihseldir.

`VERIFIED`: Run `32537541013` iki hosted attempt ile playback ve cleanup failure domain'lerini ayırdı. İlk attempt tam beş HLS yanıtı/`940229` byte sonrası ilk açılış timeout'u verdi; bounded rerun 100 switch ve schema-9 cleanup'ı tamamladı fakat startup maximum `5056,3179 ms` ile 5 saniye hard maximum'u geçti (`p95=2157,0052 ms`). Bounded `MediaPlayerElement.Loaded` bariyerli `55945b9` checkpoint'i de run `32540016736` içinde readiness'i geçtiği halde tam beş HLS response/`940229` byte ve sıfır transport failure sonrası ilk `MediaOpenTimeout` verdi; dolayısıyla `Loaded` eksikliği tek başına neden değildir. Explicit `MediaPlayer.RealTimePlayback=true` checkpoint'inin run'ı `32540817711`, 100 switch probe invariant'larını tamamladı ve p95 `2162,9392 ms` ölçtü; maximum `5102,2433 ms` ile hard tavanı `102,2433 ms` aştı, HLS/direct p95 `2165,6151/2162,2629 ms` idi. Bu sonuç real-time konfigürasyonunu hosted root-cause düzeltmesi yapmaz. Commit `12fac08` run'ı `32542190663`, tam beş HLS response/`940229` byte ve sıfır transport failure ardından ilk `MediaOpenTimeout` verdi; v2 completed-maximum alanları neutral kaldığından failure fazı sınıflanamadı. Probe envelope v3, reset/dispose öncesi active stage/switch/fixture/attempt/surface ile source-create/source-assign/`Play`/active-stage sürelerini exact doğrulayıp failure loguna ekler; kalıcı schema-9 evidence 71 alan kalır. Ölçüm başlangıcı, `≤3000 ms` p95/`≤5000 ms` maximum, HLS-first ve yalnız explicit `MediaFailed` için bounded retry kuralları değişmez. [Attribution checkpoint'indeki](M10_NATIVE_TIER_A_STARTUP_ATTRIBUTION.md) schema-3 hosted doğrulaması aşağıdaki #148 sonucu ile tamamlanmıştır.

Envelope-v3 commit `c299410` için [run `32543488245`](https://github.com/serkankaracan/iptv-suite/actions/runs/32543488245), 100 switch'i retry olmadan tamamladı; p95 `2150,7287 ms`, ilk-HLS maximum `5049,0157 ms`, pre-wait `165,1331 ms`, `MediaOpened` wait `4883,8826 ms` ve direct maximum `2302,9441 ms` idi. `VERIFIED`: dominant süre `MediaOpenWait` stage'indedir; injected interruption, transport failure, retry veya surface transition gözlenmemiştir. Stage normal loopback teslimi ile media pipeline'ı ayırmadığından kök neden henüz belirlenmiş değildir. Hard maximum ihlali nedeniyle koşu PASS değildir. `INFERENCE`: ordinal-1 UI lifecycle contention hipotezi için `Loaded` sonrasında tek global `CompositionTarget.Rendered` frame'i ayrı 5 saniyelik fail-closed surface precondition olarak beklenir. Bu measured-source/media warm-up veya sample exclusion değildir; per-switch source-to-open timer, `≤3000/≤5000 ms`, HLS-first ve timeout retry yasağı aynen kalır [S117, S119]. Successor hosted sonuç beklenir.

`VERIFIED`: Rendered-frame hipotezli commit `1411472` için [run `32544835406`](https://github.com/serkankaracan/iptv-suite/actions/runs/32544835406), quality/package/DPAPI işlerini geçti; native ilk HLS/attempt-1'de `MediaOpenTimeout` verdi. `5/5` response ve `940229` byte tamamlanmasına rağmen toplam `5154,3957 ms` içinde aktif `MediaOpenWait` `5003,0138 ms` idi. Hipotez reddedildi ve global handler kaldırıldı. Envelope v4 salt attribution'ı, Source atamasından önce bağlanan ve reset/dispose öncesinde ayrılan attempt-owned `MediaSource.OpenOperationCompleted` ile source-open completion'ı sonraki `MediaOpened` bekleyişinden ayırır [S120]. Aynı `CreateFromUri` yolu, tek mutlak beş saniyelik deadline, source-creation öncesi timer, HLS-first/retry ve `≤3000/≤5000 ms` bütçeleri korunur; event playback readiness kabulü değildir.

`VERIFIED`: Attempt/deadline/cleanup bağını kapatan commit `b4839bfe5c89fed3fb851c68b911181be48c9f6d` için [run #150 (`32546738817`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32546738817), bütün zorunlu işleri geçti. Schema-9 evidence 100 switch, startup p95 `2178,003 ms`, maximum `4977,728 ms`, HLS/direct p95 `2180,145/2178,003 ms`, bir interruption/recovery, sıfır retry, 100/100 exact source detachment ve exact cleanup kaydetti. Maximum ilk HLS/attempt-1'de source-open'ı hatasız `2467,5278 ms`, source-open sonrası `MediaOpened` bekleyişini `2510,1997 ms` ölçtü. Bu schema-10 cancellation/recovery önkoşulunu kapatır; sekiz saatlik soak veya M10 completion sonucu değildir.

`VERIFIED`: Commit `29bb89ce5a2c411f000c5678f4a4eb482e6a9a61` için [run #151 (`32549714175`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32549714175), schema-10/envelope-v5 kısa lane'i `100` switch, `0` soak, bağımsız bir network interruption/recovery ve bir application-owned managed cancellation/recovery ile geçti. Startup p95/maximum `2149.623/4974.486 ms`, retry `0`, detach `102`; dört cancellation/recovery count'u `1`, observation `1000 ms` ve üç Boolean `true` oldu. Exact detach/dispose sonrasındaki fresh recovery source-specific `OpenOperationCompleted` ve exact current-source position advance'a bağlıdır. [Contract/evidence kaydı](M10_NATIVE_TIER_A_CANCELLATION_RECOVERY_EVIDENCE.md) artifact/JSON digest'lerini ve exact bütçeleri içerir. Bu OS audio/session quiescence, native API cancellation, M13 reconnect veya M10 completion kanıtı değildir.

`VERIFIED — successor graph timeout reproduction`: Commit `0503ed7` run `32555764285` ve tek failed-job rerun'u fail-closed signed package inventory adımını geçti; native ilk HLS iki attempt'te de hatasız source-open ve eksiksiz `5/5`/`940229` byte transfer sonrasında `MediaOpenTimeout` verdi. Toplam/source-open/post-source-open süreleri `5103,4564/2454,8078/2648,6486 ms` ve `5139,9608/2567,7833/2572,1775 ms` idi. Envelope v6 salt diagnostic'i, timeout catch'inde ek bekleme yapmadan `MediaOpened` completion timestamp'ini mevcut wait deadline ve hard `5000 ms` startup bütçesine bağlar. Bu instrumentation failure'ı PASS yapmaz; hosted attribution sonrası yalnız kanıtın desteklediği en küçük düzeltme değerlendirilecektir.

`VERIFIED — successor envelope-v6 classification`: Commit `78ff98e` için run `32557484626`, quality/package/DPAPI/real signed inventory adımlarını geçti ve native probe 100 switch'i tamamladı. P95 `2160,3704 ms`; ilk HLS maximum `5024,5709 ms`, pre-wait `86,1858 ms`, `MediaOpened` wait `4938,3851 ms`, source-open `2454,8193 ms`, direct maximum `2286,6558 ms` ve retry `0` idi. Bu timeout değil, source-creation öncesinden ölçülen hard total maksimumun `24,5709 ms` ihlalidir; v6 timeout-only alanları doğru biçimde neutral kaldı. Sonraki fixture checkpoint'i mevcut TS byte'larını koruyup her segmentte first-packet keyframe ile SPS/PPS/IDR'ı generator-time doğruladı ve playlist/runtime contract'ını exact `#EXT-X-INDEPENDENT-SEGMENTS` ile bağladı [S126–S128]. Bu standards-correct metadata değişiminin etkisi #156'ya kadar `UNVERIFIED` kaldı; `≤3000/≤5000 ms`, HLS-first, retry, TLS/loopback ve cleanup sözleşmeleri değişmedi.

`VERIFIED — independent-segments negative A/B`: Commit `e517fa8` için run #156 (`32559852397`), quality/package/DPAPI/inventory adımlarını ve 100 switch'i geçti. P95 `2156,3241 ms`; ilk HLS maximum `5105,5754 ms`, pre-wait `122,5842 ms`, `MediaOpened` wait `4982,9912 ms`, source-open `2518,1525 ms`, source-open sonrası `MediaOpened` `2587,4229 ms`, direct maximum `2295,4649 ms` ve retry `0` idi. Event kendi wait'i içinde geldi; sonuç total hard-budget failure ve metadata hardening'inin tek başına yeterli startup düzeltmesi olmadığının negatif A/B kanıtıdır.

`VERIFIED — zero-timeline negative A/B`: HLS remux'a, direct üretimde zaten bulunan `-muxdelay 0 -muxpreload 0` eklenerek aynı sentetik presentation'ın video PTS/DTS başlangıcı `1,480/1,400 → 0,080/0,000` saniye hizalandı [S129]. Generator dört segmentte iki saniyelik cadence'i, ilk audio timestamp parity'sini, sonlu timestamp'leri ve çıkarılmış H.264/AAC elementary-stream byte/hash eşliğini fail-closed doğrular. Dört TS toplamı `940000 → 978164` byte büyüdüğünden fixture kısaltılmamıştır. Commit `a5897a5` için run #157 (`32563209337`), quality/package/DPAPI/inventory adımlarını ve 100 switch'i tamamladı; p95 `2147,1658 ms`, ilk HLS maximum `5074,3298 ms`, pre-wait/media-open wait `109,1497/4965,1801 ms`, source-open/post-source-open `2484,1057/2590,2241 ms`, HLS/direct p95 `2157,1753/2144,6475 ms`, direct maximum `2254,3729 ms` ve retry `0` idi. Hard toplam tavan `74,3298 ms` aşıldı; normalized timeline tek başına yeterli startup düzeltmesi değildir. Tek maximum örneği önceki run'a göre performans değişimi kanıtlamaz. `≤3000/≤5000 ms`, HLS-first, retry, TLS/loopback, security ve cleanup sözleşmeleri değişmez.

`IMPLEMENTED LOCALLY; HOSTED ATTRIBUTION PENDING`: Davranış-korumalı envelope-v8, first-HLS QPC transport window'una ek olarak en çok `128` process resource sample'ını ordinal/elapsed/private-bytes/handle/phase/switch alanlarıyla ve en çok `7` injection/recovery olayını aynı `Stopwatch.GetTimestamp()` tabanında bağlar. Controller aggregate resource değerlerini seriden yeniden türetir; her sample için recovery phase/ordinal ve post-warm minimum/maximum/peak/final özetini bounded loglar. Raw QPC kalıcı schema-10 success artifact'ına girmez. Server/player davranışı, 30 dakika warm-up, `%10/100 MiB/monotonic` threshold'ları, timer, HLS-first, retry ve cleanup aynıdır; sonuç gelmeden retention root cause'u iddia edilmez.

### M12 playback UX ve lifecycle

`PARTIAL VERIFIED, 2026-08-25`: Commit `8d537010f64193f7bc9baf05bc3707fb31d5362f` için run `32764149667` signed production package'ta 25 rapid switch ile fullscreen/focus/resize/minimize/state/active-close zincirini geçti. Run `32785306860`/commit `33afcef741f518b439c9934b0f4035fd92662586` ayrı packaged kısa-run guard'ını `PlaybackResourceBudgetVerified=true` ile geçti. Run `32792088083` production source-delete success yolunu geçti. Commit `45124f72cc62b21942790c39bd032242706af339` için run `32796910469`, source-delete cancel/dialog-close no-mutation, deterministic `DeletionPending`, restart admission block, exact katalog/configuration/tombstone/sibling korunumu, fault release, manual retry, active-playback drain ve final katalog/DPAPI cleanup sonuçlarının tamamını geçti; aynı run'ın short-run resource budget sonucu da `true`dur (`-2.805.760 / +6.062.080 byte / -9 / -6`, p95 `2228,312 ms`). Guard sınırları private `≤+8 MiB`, working set `≤+16 MiB`, handle `≤+64`, thread `≤0`, p95 `≤3000 ms`dir; leak-yokluğu veya M10/M16 uzun-soak kabulü değildir. Fiziksel DPI/mixed-DPI multi-monitor, gerçek sleep/wake, audio endpoint ve Narrator `NOT RUN` kalır. [Exact sınır](M12_COMPLETION_EVIDENCE.md).

### M16 RC

- Packaged private-flight build ile 24 saat.
- 200 deterministic switch; Tier A rotation.
- Sleep/resume, network loss/recovery ve audio device change.
- Full logs canary scan; WER/support artifact policy.
- Aynı build clean install ve previous package upgrade senaryosunda çalışır.

`IMPLEMENTED LOCALLY / BLOCKED; clean-commit evidence pending, 2026-08-26`: Schema-v1 fail-closed RC aggregator, fixed sekiz predecessor evidence girdisini bounded biçimde doğrular; exact M15 blocker'larını değiştirmeden taşır ve yedi M16 blocker'ı ekler. Bu schema yalnız `result=blocked`, `m16TechnicalGateSetPassed=false` ve `candidateReady=false` yayımlayabilir. `-AllowBlockedCandidate` yalnız blocked evidence exit davranışını değiştirir; acceptance veya waiver değildir. Sabit M16 release-artifact scanner primitive'i depth/entry/file/total/finding/path bütçelerini, reparse reddini ve iki-pass ordered inventory/SHA-256 stabilitesini local testlerde doğrular; full log/app-data/package/support aynı-build evidence üretilmediği için final canary blocker'ını kapatmaz. Aggregator 24 saat/200 switch soak'ı çalıştırmaz ve kısa native sonucu onun yerine kullanmaz. [Exact sözleşme ve non-claim'ler](M16_RELEASE_CANDIDATE_BASELINE.md).

Hang watchdog process'i otomatik öldürmeden önce safe stack/metric snapshot alabilir; full memory dump secret riski nedeniyle default değildir. Crash recovery eski stream'i kullanıcı niyeti olmadan otomatik başlatmaz.

## 8. Quality gates by milestone

| Milestone | Minimum green evidence |
|---|---|
| M1 | PASS: clean temp workspace + empty-cache locked restore, Debug/Release x64 build, 6/6 boundaries/toolchain/manifest, negative gate, signed install/launch/normal-close/uninstall |
| M2 | PASS, 2026-08-09: local exact-SDK iki-run 22/22, fixture/canary CLI/sentinel exact-TRX; hosted quality + signed packaged-smoke + coordinator success ve iki doğrulanmış sanitized artifact |
| M3 | PASS, 2026-08-09: local exact-SDK iki-run 105/105; domain invariants, terminology, safe endpoint/configuration, opaque references, bounded playlist-kind decision, redirect policy ve redaction corpus |
| M4 | COMPLETED, 2026-08-20: tarihsel protected-store v1 hosted foundation/source-draft/package initialization 153/153 iki-run PASS; internal decoder 159/159 ve owner-v2 171/171 local snapshot'ları PASS; exact configuration-record deletion primitive'i dahil tarihsel local gate 183/183 x2 PASS; channel deletion primitive'i dahil tarihsel local snapshot 198/198 x2 PASS; DPAPI user-boundary harness'ı ve statik contract guard'ları dahil güncel local worktree gate 204/204 x2 PASS, summary `commitSha=null`; normal full gate gerçek-user executable'ını çalıştırmadı; commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` run number `#15`te ilk owner-v2/same-version lifecycle, deletion primitive commit'i `3e806edd9a9482b87cccbf000ac8a81823562f6a` ise run number `#16`da 3/3 iş ve üç artifact ile hosted workflow/UI evidence VERIFIED; `#16` toplam süre `8m24s`; signed `0.0.1.0 → 0.0.2.0` test-family update lane'i commit `bfe5960d5bc911e21ba1e1e1675deb575400e531` için run number `#18`de 3/3 iş, üç artifact ve `9m16s` ile; hardened schema-v3 disposable reset + live-state uninstall/reinstall fresh-state lane'i commit `6f30e3fa92f329c1fa26333c591edb5148261cec` için run number `#20`de 3/3 iş, üç artifact ve `8m14s` ile; channel deletion primitive'i commit `0ef0da5793dc4fd1f4e83dbe38cd49d7a7ccb56f` için run number `#21`de 3/3 iş, üç artifact ve `9m25s` ile workflow/UI düzeyinde VERIFIED; artifact içerikleri ve tam digest bağları bağımsız doğrulanmadı; per-record 5k–50k ×20 Decision VERIFIED fakat 50k create p95 `151,447 s` ve allocation `668,007 MiB` nedeniyle bulk locator için REJECTED (`INFERENCE`); protected-catalog 5k–50k ×20 comparative `Decision` clean commit `7cd5bf739712a5cbdcf47634275a113faab8c48e` ve summary SHA-256 `4a219e802dcff959644bda42489f0ee5231be63df1eb37d034e2ed16ff0859de` ile VERIFIED, comparative gate CLOSED, immutable cross-file container production `NO-GO`; durable authoritative owner metadata/source-wide deletion-reconciliation M8, tuple/origin policy M5, production SQLite transaction/end-to-end/crash kanıtı M8, production PFN/repair/identity/Store kabulü M15 kapsamında açık |
| M5 | HTTP fault/redirect/TLS/timeout/cancel suite |
| M6 | Xtream Live contract variants |
| M7 | COMPLETED, 2026-08-20: streaming loader + mandatory sink handoff, M3U/HLS golden/adversarial/50k/cancel suite; local 260/260 ×2 ve commit `fc4e2e360e1b0283369058e6bef349e667f3a764` hosted run `32409222785` 4/4 PASS |
| M8 | `COMPLETED`: same-SQLite-transaction persistence, clean 5k–50k ×20 Decision ve gerçek ayrı-process kill/recovery tamamlandı; 50k duration p95 `2,739 s`, allocation p95 `116,330 MiB`, working-set delta p95 `6,164 MiB`, cancellation p95 `12,390 ms`; migration/fault/crash, canary, source lifecycle ve query/cache acceptance PASS. [Kanıt](M8_COMPLETION_EVIDENCE.md) |
| M9 | `COMPLETED, 2026-08-21`: bounded query/UI/logo local `292/292 × 2` ve clean query Decision PASS; run `32443355378` commit-bound packaged 50k UIA/Tab/virtualization/input/DWM acceptance PASS. Realized item `8`, input p95 `16,284 ms`, compositor p95 `31,25 ms`, max `171,875 ms`, late/drop proxy `%0`, interval `404`. [Kanıt](M9_COMPLETION_EVIDENCE.md) |
| M10 | `CONDITIONAL SUCCESS / ACCEPTED WITH KNOWN DEVIATION, 2026-08-23`: LibVLC candidate GPL-zero hard gate'te reddedildi; Windows-native Tier A fallback temiz `1f15888` sekiz saat koşusunda bütün temel playback/lifecycle/startup/recovery kontrollerini geçti. Tek sapma relative memory `%11,1944255 > %10` (`~1,85 MiB` aşım); absolute `~17,32 MiB ≤100 MiB`, monotonic `false`, handle delta `-21`. Gate değişmedi; R15/M16 hardening borcu açık, M11 başladı. [Acceptance](M10_NATIVE_TIER_A_ACCEPTANCE_SOAK_ATTEMPT.md), [candidate](M10_PLAYBACK_CANDIDATE_DECISION.md), [surface](M10_NATIVE_TIER_A_SURFACE_EVIDENCE.md), [network](M10_NATIVE_TIER_A_NETWORK_RECOVERY_EVIDENCE.md), [session lifecycle](M10_NATIVE_TIER_A_SESSION_LIFECYCLE_EVIDENCE.md) |
| M11 | `COMPLETED, 2026-08-24`: engine-neutral lifecycle/control, protected Remote-M3U ve Xtream JIT resolution, Windows-native adapter ve packaged WinUI playback tamamlandı. Local architecture `35/35`, unit `171/171`, integration `116/116`; run `32754184474` quality `354/354 ×2`, signed package playback/lifecycle, DPAPI ve required gate PASS. [Kanıt](M11_COMPLETION_EVIDENCE.md) |
| M12 | `PARTIAL VERIFIED, 2026-08-25`: run `32764149667` playback UX zinciri PASS; run `32785306860` signed short-run resource guard'ını PASS etti; run `32792088083` production source-delete success yolunu PASS etti; run `32796910469` pending failure/restart admission block/manual retry/final cleanup zincirini PASS etti. Fiziksel DPI/multi-monitor/sleep/audio/Narrator `NOT RUN`. [Kanıt](M12_COMPLETION_EVIDENCE.md) |
| M13 | `COMPLETED, 2026-08-25`: retry/reconnect deterministic fault suite ve signed production-package clean-EOF recovery/cancel acceptance PASS; cancel `22,087 ms ≤ 1000 ms`, no-later-open `31.009 ms`, request `60 → 60`; run `32839777516`, quality `553 × 2`. [Kanıt](M13_COMPLETION_EVIDENCE.md) |
| M14 | `COMPLETED, 2026-08-25`: clean `912c25a` üzerinde 50k component absolute budgets ve 8/8 same-profile regression PASS; run `32858287103` quality `566 × 2` + signed package input/frame/realized/steady-WS PASS; PID-bound XAML marker penceresinde Frame p95 `2,1269 ms`, `>200 ms` event `0`. [Kanıt](M14_COMPLETION_EVIDENCE.md) |
| M15 | `IN PROGRESS / BLOCKED, 2026-08-26`: clean `0236894f8cc8c6212305f5ba9c294e335f3d031e` schema-v1 static audit'te `technicalBaselinePassed=true`, `releaseReady=false`; exact 8 asset, 4 schema-v2 lockfile, 23 package adı ve tarihsel 16 blocker. Run #223 attempt 2 (`32881949188`) exact install-root runtime audit'ini sıfır mutation ve pre/post equivalent `true` ile geçti. Schema-v2 x64-only disposition `Arm64ReleaseDecisionPending` blocker'ını kapattı. İlk hosted SBOM/CVE kabulleri tarihsel olarak run #226 ve #4'tür. Pre-asset SBOM run `#1` (`32918708589`) ile CVE run `#10` (`32919649968`) schema-v4'te 13 blocker bırakan tarihsel accepted zincirdir. Deterministic sekiz-asset snapshot'ı SBOM run `#2` (`32923642659`) ve yeni SBOM ledger'ına zincirlenen CVE run `#13` (`32924589536`) ile official+strict kabul edildi. Development WACK preflight contract'ı SBOM run `#3` (`32928618825`) ve buna zincirli CVE run `#18` (`32929529931`) ile yeniden official+strict kabul edildi; güncel schema-v6 sonucu `technicalBaselinePassed=true`, `releaseReady=false` ve exact 12 blocker'dır. WACK/private Store, production identity/lifecycle, license/NOTICE/privacy/codec-IP hukuk/signing ve diğer kapılar açık; M15 tamamlanmış değildir. [Baseline](M15_RELEASE_READINESS_BASELINE.md) |
| M16 | `IMPLEMENTED LOCALLY / BLOCKED`: schema-v1 fail-closed RC aggregation contract; `candidateReady=false`. Bounded final-artifact scanner ve deterministik sentetik onboarding→catalog→search→play/control→fault/reconnect→delete integration journey primitive'leri local doğrulandı. Exact M15 blocker'ları korunur; clean commit-bound aggregation, feature freeze, 24h soak, physical device/accessibility, final canary/security scan ve release-operations kapıları açıktır. [Baseline](M16_RELEASE_CANDIDATE_BASELINE.md) |

`M15 CVE producer — HOSTED VERIFIED / TECHNICAL REVIEW ACCEPTED, 2026-08-26`: Ayrı `windows-cve-review.yml` lane'i exact SDK `10.0.302`, fresh bounded NuGet cache/home, locked ve `4/0/4` audited restore ile yalnız production Windows leaf'ini denetler. Filtresiz JSON v1 envanteri exact `2` direct + `21` transitive package'ı dört production lockfile'daki `23` package/content-hash graph'ına bağlar; filtreli JSON v1 sonucu exact official audit source'unda `knownVulnerabilityCount=0` olmadan evidence üretmez. Run #4 (`32912296486`), job `98008739618` ve artifact `9586961516` bu contract'ı clean commit `2053f4099819b3bb19bb9dd3370d60f0161098f1` üzerinde geçti. Acceptance ledger SHA-256 `8c60360ae0dac240ef801688a04472266dabd16bfb1069a841b365b61c89a197`; exact 16-file contract SHA-256 `6b09978b5ee3ffc4d14e09458724a3d18fd1d23c5ec9ab3134dd25bfc7e91ff3` ve graph SHA-256 `760562b81e0097913e1daf4ec88c67596337dd6636ed6d88c8f645424dc50b6e` değişmediği ve run-completion +7 gün freshness penceresi geçerli olduğu sürece yalnız `CveReviewPending` kapanır. Sonuç schema v4, `releaseReady=false` ve 13 blocker'dır. “0 bulgu” yalnız sorgu anındaki bilinen vulnerability verisi içindir; genel “CVE-free”, perpetual freshness veya hukuk kabulü değildir. Resmî dayanaklar (erişim `2026-08-26`): [.NET CLI](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list), [JSON v1 spec](https://github.com/NuGet/Home/wiki/%5BSpec%5D-Machine-readable-output-for-dotnet-list-package), [NuGet Audit](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages).

`M15 pre-asset SBOM/CVE acceptance renewal — HOSTED VERIFIED / HISTORICAL, 2026-08-26`: Bağımsız SBOM producer run `#1` (`32918708589`), clean `e7564aa9382fb12b10869866811e75847d1f6f16` üzerinde DWM refresh-gap normalizasyonu ve exact maximum korumasını içeren package smoke'u geçti; acceptance ledger/contract SHA-256 değerleri `9eaf890f027f3485e89851701cfbfe4676afb44be4a918f525290c58385d9a21` / `e5324fafa743cd003af480ab2c521ae2181a306fd67704c221d905025a7fccae`dir. Buna zincirlenen CVE producer run `#10` (`32919649968`), commit `15826e5ecad4a4b5737aa7bf39d28c62b0e71de7`, job `98030516890` ve artifact `9589408603` ile aynı exact graph'ı yeniden `0/0/0` bulguyla geçti. Bu tarihsel CVE ledger SHA-256 `01180e5c90816fda2f1712a2ecd5ed634399caee76d68b7ffeb47c178e2aa208`, freshness sonu `2026-09-02T01:38:42Z`dir. Threshold, blocker ve non-claim kapsamı değişmedi.

`M15 deterministic asset provenance — TECHNICAL CHAIN ACCEPTED / 12 BLOCKERS, 2026-08-26`: Exact sekiz production-package PNG/ICO asset'i source-controlled ve dış girdisiz deterministic tariften yeniden üretildi; strict ledger path, dimension/ICO-frame, length, hash ve canonical asset-set bağını doğrular. SBOM run `#2` (`32923642659`) yeni package snapshot'ını official+strict `PASS` doğruladı; acceptance ledger raw SHA-256 `5ad87061b0586ad5d84f8f38d244bb3e9131358e27905bffd1f8b2648259bb47`dir. Buna zincirlenen fresh CVE run `#13` (`32924589536`), commit `58e0b615d1c531a9c94583772d4d80a5740eb3e3`, job `98044789701` ve artifact `9591048689` ile exact `4/0/4` audit, `2 + 21 = 23` package ve `0/0/0` known-vulnerability sonucunu official+strict geçti. Current schema-v6 `technicalBaselinePassed=true`, `releaseReady=false` ve exact 12 blocker taşır; provenance/SBOM/CVE blocker'ları kapalıdır. Asset'ler yalnız nötr development placeholder'dır; sıfır bulgu genel “CVE-free”, marka/logo onayı, copyright ownership, redistribution, root `LICENSE`/`NOTICE`, Store/legal veya codec-IP kabulü değildir.

`M15 development identity WACK preflight — EXECUTED / TEST FAILED, 2026-08-26`: Manual `run_wack=true` yolu yalnız disposable development identity üzerinde exact `appcert reset` → bounded installed-package test sırasını çalıştırır. Ham WACK XML/stdout/stderr kalıcı veya upload artifact'ı değildir; strict parser yalnız PASS/non-partial sonucu kabul eder ve sanitized success summary `ClosedBlocker=None`, `ReleaseReady=false` taşır. Değişen package/workflow contract'ı SBOM run `#3` (`32928618825`) ve SBOM ledger'ına zincirli CVE run `#18` (`32929529931`) ile yeniden kabul edilmiştir. Hosted run `32932186491` attempt 1 WACK öncesi tekil katalog input-response bütçesinde fail oldu; bounded attempt 2 exact WACK çağrısına ulaşıp stable `WindowsWack:TestFailed` ile kırmızı kaldı. Takip Actions yolu yalnız allowlist'li stable failure code'u ayrı sanitized artifact'a dönüştürür; ham rapor korunmaz, job başarısızlığı ve `WackPending` değişmez. Local PS5.1 self-test ve architecture `54/54 PASS` sonrasında run `32934454845` locked/DPAPI işlerini geçirip WACK job'unu beklendiği gibi kırmızı tuttu; preparation/upload adımları geçti ve artifact `9594709230` yalnız schema-v1 `FailureCode=TestFailed` özetini taşıdı. Failure-evidence yolu hosted **VERIFIED**dır. Bu preflight final RC-bound WACK/Partner Center kabulü değildir.

## 9. Manual ve dış doğrulama

Otomasyon şunların yerini tutmaz:

- codec patent ve OSS redistribution hukuk incelemesi;
- Microsoft/Samsung Store preflight ve public acceptance;
- Intel/AMD/NVIDIA/ARM64 gerçek cihaz ve driver;
- Samsung gerçek TV/remote/model yılı;
- Narrator/UIA/keyboard exploratory accessibility;
- real-world provider uyumluluğu. Gerçek hesap ancak ayrı yazılı yetki, veri minimizasyonu ve sanitize süreçle; MVP fixture repository'sine alınmadan test edilir.
- repository kök `LICENSE`/`NOTICE`, app asset provenance'i, sentetik corpus sahipliği/CC0 dağıtım hakkı ve exact codec/patent değerlendirmesi. Package inventory/SBOM veya upstream imza doğrulaması bunların yerine geçmez.

## 10. Açık kalemler

- Per-record DPAPI bulk-locator layout'ı bütçeyi karşılamadı. Test-only immutable-container comparative gate'i kapandı ve container cross-file atomicity nedeniyle production `NO-GO` kaldı. M8 same-SQLite-transaction implementation/crash acceptance'ı tamamlandı; M14 final 50k production-path benchmark'ı bütün mutlak bütçeleri geçti. M15 production identity/lifecycle ve M16 final hardening ayrı kalır.
- Tier B hangi kombinasyonların marketing support matrix'ine gireceği M10 + hukuk sonucudur.
- Engineering minimum M1'de Windows 11 build 10.0.26100/x64 olarak pinlendi; product support minimumu, ARM64 ve reference hardware M15'te kapanacak.
- Player A/V sync için sayısal ölçüm düzeneği M10'da seçilecek; yalnız “gözle iyi” kabul değildir.
- Signed package smoke, M9 UIA/focus/50k/virtualization kabulüne M14'te bounded input, DWM proxy, WM_NULL responsiveness proxy ve 30 saniyelik player-off steady working-set kontrolünü ekler. Run `32858287103` instrumentation-off hosted sonucu bu zinciri final M14 commit'inde geçmiştir. Ayrı opt-in trace exact PID/marker penceresinde XAML frame/layout event'lerini bağlar; DWM/WM_NULL proxy'lerini app-specific full-session ETW sonucu, Narrator/genel accessibility veya cihaz matrisi yapmaz.

## Kaynaklar

[S14–S16, S21–S32, S33–S41, S76–S84](../research/SOURCES.md)
