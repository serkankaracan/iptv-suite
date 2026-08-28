# Güvenlik ve gizlilik baseline'ı

**Güncel M8 sonucu (2026-08-21):** same-SQLite-transaction protected catalog, durable source lifecycle/reconciliation, 50k component bütçeleri ve gerçek process-crash recovery tamamlanmış; ADR-003 `Accepted`dır. Aşağıdaki M4 zaman çizelgesindeki `Proposed`/uygulanmamış/açık M8 ifadeleri tarihsel bağlamdır ve bu kaydı geçersiz kılmaz. Production PFN/Store identity/repair M15'te açık kalır.

**Tarih:** 2026-08-09

**Durum:** M3 validation/redaction contract'ı, M4 CurrentUser protected-storage foundation ve M8 transactional layout/process-crash recovery `COMPLETED`; M15 production package lifecycle acceptance pending

**Kapsam:** Windows MVP; platformlar aynı ilkeleri kendi güvenli API'leriyle uygular

## 1. Güvenlik hedefi ve sınırlar

Uygulama yalnız kullanıcının yetkili source'u ile cihazdan doğrudan haberleşir. Geliştirici backend'i, analytics, reklam SDK'sı veya cloud account ilk MVP'de yoktur. Bu yaklaşım üçüncü tarafa aktarılan veriyi azaltır; cihazdaki credential'ı, kötü niyetli playlist/logo/stream'i, native player'ı ve Store/privacy yükümlülüğünü ortadan kaldırmaz.

Baseline hedefleri:

- secret hiçbir zaman source code, Git, plaintext DB/config, log, telemetry, UI error veya test artifact'ına girmez;
- credential-bearing trafik doğrulanmış TLS olmadan çıkmaz;
- dış input bounded ve cancellation-aware işlenir;
- local data minimum süre tutulur, kullanıcı tarafından silinebilir ve migration sırasında açığa çıkmaz;
- exact native/OSS dependency seti izlenir ve güvenlik güncellemesi alır;
- uygulama content/provider sağlamaz, erişim kontrolü veya DRM aşmaz.

M3 local kabulü; raw locator/credential döndürmeyen source validation, scheme+IDNA host+effective port ile sınırlı `SafeEndpoint`, random opaque references, registry-only stable safe errors ve provider host'unu default gizleyen pure URI/header/untrusted-text redaction contract'ını 105 testlik iki-run gate içinde doğrulamıştır. M3 kapsamı production persistence veya log sink implementation'ı içermiyordu; sonradan eklenen M4 protected-storage foundation aşağıda ayrı kanıt ve sınırlarıyla kaydedilir. Wrong-user, source deletion/reconciliation, DB/WAL/SHM taraması ve retention enforcement hâlâ `UNVERIFIED` durumdadır.

M3 opaque reference'ın temsilini doğrular, fakat protected record binding'ini kanıtlamaz. M4 foundation store-owned issuance ile kriptografik source/purpose/reference context binding'ini ekler. Owner-bound v2 dilimi ayrıca source credential/remote playlist kaydını fresh `SourceConfigurationId`ye; stream/logo kaydını `ChannelId`ye bağlar. Exact owner kind/ID hem store key, filename digest ve DPAPI entropy'de hem encrypted envelope context'inde doğrulanır; same-source/same-purpose cross-owner read/update fail-closed, yanlış-owner delete doğru kayda dokunmadan idempotent success'tir. Owner integrity context'idir, authorization principal değildir. Güncel source-draft application operation'ı validation'ı store mutation'ından önce tamamlar; protected create başarısından sonra exact store-issued reference, `SourceId` ve configuration owner'ı validated draft'a birlikte bağlar. Doğrudan await edilen çağrıda commit sonrası cancellation yeniden gözlenmez ve reference başarılı sonuçla döner. Bu davranış caller abandonment/retry, aynı `SourceId` için yinelenen create, process crash/OOM, metadata/configuration persistence, update rollback, deletion veya reconciliation atomikliği sağlamaz. M5'te resolve edilen locator yeniden HTTPS validation'dan geçirilip beklenen endpoint/source ile eşleştirilir. Owner+reference+endpoint tuple'ının birlikte değiştirilmesi, missing-reference ve origin equality geçmeden opaque ID/owner tek başına authorization veya origin kanıtı sayılmaz.

Exact configuration-record deletion primitive'i yalnız durable olarak `DeletionPending` olduğu varsayılan `ContentSource` aggregate'inden owner/purpose/reference tuple'ını türetir ve o tek credential veya remote-playlist locator kaydını idempotent siler. Caller-supplied key parçaları, source-wide enumeration ya da daha zayıf fallback kullanmaz. Bu primitive source durumunu persist etmez, yeni kullanım admission'ını kapatıp in-flight lease'leri drain etmez; channel kayıtlarını, metadata/cache'i ve kayıp referanslı orphan'ları temizlemez. Dolayısıyla source deletion/reconciliation hard-gate'i açık kalır.

`SourceChannelProtectedRecordDeletionService`, caller'ın durable `DeletionPending` olduğunu varsaydığı `ContentSource` ile authoritative persistence'tan yüklenmesi gereken `PlaylistSnapshot` ve `LiveChannel` zincirini kabul eder. Source→snapshot, snapshot→channel ve stable-key→source ilişkilerini doğrular, fakat aggregate provenance'i veya caller authorization'ı sağlamaz. Ayrı key bileşenleri kabul etmeden `ChannelId` owner üzerinden varsa exact protected-stream kaydını, sonra optional logo kaydını idempotent siler. İlk başarılı delete commit'inden sonra newly-requested cancellation ikinci exact delete denemesini engellemez; stream silinip logo silinememişse retry, store yeniden başarılı olduğunda aynı exact idempotent zincirin sonuca yakınsamasına izin verir. Configuration ve channel primitive'leri birlikte dahi status persistence, admission close, in-flight drain, bütün channel'ların enumeration'ı, metadata/cache silme, orphan reconciliation veya source-wide coordinator sağlamaz. Exact SDK `10.0.302` tarihsel channel-primitive snapshot'ı Debug/Release x64 build'leri 0 warning/error; architecture 13, unit 151 ve integration 34 olmak üzere 198/198 testi iki identical `Passed` setiyle; fixture determinism, sentinel fail/recovery, scanner CLI self-test ve artifact canary kontrolleriyle geçmiştir. Summary `commitSha=null` taşıdığı için local sonuç tek başına commit-bound değildir. Run number `#21`, commit `0ef0da5793dc4fd1f4e83dbe38cd49d7a7ccb56f` için 3/3 işi yeşil, üç artifact ve `9m25s` toplam süreyle channel primitive'in hosted execution'ını workflow/UI düzeyinde **VERIFIED** yapar. Artifact alanları, ZIP/içerik ve tam digest bağı bağımsız doğrulanmamıştır.

2026-08-13 M4 foundation; store-owned reference issuance, bounded input/record, owned-buffer zeroization, store-operation safe failure mapping ve central sanitizer'ı uygular. Source-draft operation'ı strict UTF-8, versioned ve length-prefixed source payload v1'i `SecretStoreLimits` sınırında üretir; Xtream için full locator+username+password'ı, remote playlist için full locator'ı yalnız owner-bound protected-store create call'ına verir ve temporary byte buffer'ı `finally` içinde sıfırlar. Internal source-payload decoder exact format ve strict UTF-8/scalar/control kurallarını yalnız numeric slice metadata'sıyla fail-closed doğrular; managed string/URI veya ikinci plaintext buffer üretmez. Protected-store envelope/path/temp namespace ise semantic-owner binding nedeniyle v2'dir; composition root başarılı `LocalCache\ProtectedStore\v2` store'unu uygulama ömrü boyunca tutar. V1 pre-release namespace otomatik okunmaz, migrate edilmez veya silinmez; released user-data migration iddiası yoktur. Public resolve consumer'ı M5 origin ve lifecycle gate'leri tamamlanmadan açılmaz. Factory initialization başarısızsa pencereyi oluşturmadan fail-closed olur, daha zayıf fallback kullanmaz ve beklenen hataları raw path/message/store taşımayan typed sonuca eşler. Hosted run `31735655363`, owner-bound v2'den önceki 153-test/v1 snapshot'ı ve tarihsel packaged directory kanıtıdır. Owner-v2 snapshot'ı 171/171; exact configuration-record deletion primitive'i dahil tarihsel local exact-SDK snapshot architecture 13, unit 137 ve integration 33 olmak üzere 183/183 testi iki deterministik koşuda geçmiştir. Channel deletion primitive'i dahil tarihsel local worktree gate'i architecture 13, unit 151 ve integration 34 olmak üzere 198/198 testi iki identical `Passed` setiyle geçmiştir. DPAPI user-boundary harness'ı ve statik contract guard'larını da içeren güncel local full gate, architecture 16 + unit 154 + integration 34 = 204/204 testi iki aynı `Passed` setiyle ve tüm fixture/sentinel/scanner/artifact-canary kontrolleriyle geçmiştir; quality summary `commitSha=null` taşır ve commit-bound değildir. Normal full gate gerçek local-user executable'ını çalıştırmaz. 2026-08-14 GitHub Actions UI kaydı, run number `#15`in commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` için 3/3 işi ve üç artifact'ı başarıyla tamamladığını gösterir; bu tarihsel kayıt ilk v2 hosted/package/lifecycle geçişini workflow/UI düzeyinde **VERIFIED** yapar. Aynı tarihli takip UI kaydında run number `#16`, exact deletion primitive'ini içeren commit `3e806edd9a9482b87cccbf000ac8a81823562f6a` için 3/3 işi yeşil, üç artifact ve `8m24s` toplam süre gösterir; bu sonuç da yalnız workflow/UI düzeyinde **VERIFIED**dir. Sonraki run number `#18` UI kaydı, signed `0.0.1.0 → 0.0.2.0` test-family update lane'ini ve package output-directory düzeltmesini içeren commit `bfe5960d5bc911e21ba1e1e1675deb575400e531` için 3/3 işi yeşil, üç artifact ve `9m16s` toplam süre gösterir; commit-bound package job'u lifecycle betiğini zorunlu çalıştırdığından update lane hosted execution'ı workflow/UI düzeyinde **VERIFIED**dır. Run number `#21` UI kaydı, channel deletion primitive'ini içeren commit `0ef0da5793dc4fd1f4e83dbe38cd49d7a7ccb56f` için 3/3 işi yeşil, üç artifact ve `9m25s` toplam süre gösterir; commit-bound hosted execution workflow/UI düzeyinde **VERIFIED**dır. GitHub database run ID'leri, job log/test sayıları, indirilen lifecycle JSON alanları ve artifact ZIP/içerik/tam digest bağları bağımsız doğrulanmamıştır. Arbitrary mid-I/O cancellation/interleaving ve cross-process sıralama kanıtı yoktur. Durable source-deletion reconciliation M8; SafeEndpoint/origin equality M5; M8 transactional production layout implementation/end-to-end 50k/crash; production PFN update/reset/repair/uninstall-reinstall/identity/Store kabulü M15 kapsamında açık kalır.

Ayrı `IptvSuite.PackageLifecycleHarness`, production package ve startup graph'ından ayrılmış disposable bir test MSIX'idir. Güncel lane aynı identity/publisher ve signer ile baseline `0.0.1.0` ve yalnız sürümü farklı update `0.0.2.0` paketlerini üretir. Baseline process'leri create ile duplicate-create rejection'ı yürütür; daha yüksek sürüm aynı package family üzerine normal `Add-AppxPackage -Path` ve exact dependency yoluyla kurulur. Microsoft'un normal package-update modeli aynı family ve artan sürüm ister; `Add-AppxPackage` imzalı MSIX ile dependency path'ini kabul eder [S89][S90]. Updated package process'i baseline v2 kaydını okur; yanlış-owner read/delete negative, doğru-owner update/read/delete ve post-delete yokluk matrisini tamamlar. Cross-activation source/owner/reference bilgisi ve payload digest'i DPAPI CurrentUser ile korunan bounded binary ticket'ta kalır; plaintext canary exact `ProtectedStore` veya lifecycle run-directory taramasında create, post-update ya da final aşamada bulunursa lane fail eder. Windows-managed mutable package hive'ları bu owned-write-surface kanıtının dışındadır. Scanner bulgu, operasyonel hata ve CLI contract sapmasını ayrı stable code'larla fail-closed tutar. Fixed phase/run ID dışında process argument'i yoktur; raw value/reference/path/PID/SID/user/host/exception evidence'a yazılmaz. Evidence schema v2 iki version/package hash/signature alanı ile same-signer, same-family, changed-full-name, update-installed, post-update read/scan ve cleanup Boolean'larını allowlist'ler. Test MSIX'i upload edilmez ve exact package/certificate/app-data/output cleanup başarısızsa success evidence oluşturulmaz. Run number `#15` tarihsel same-version lifecycle kanıtı, `#16` ise update lane'inden önceki deletion-primitive commit kanıtıdır. Run number `#18`, commit `bfe5960d5bc911e21ba1e1e1675deb575400e531` üzerindeki yeni update lane hosted execution'ını workflow/UI düzeyinde **VERIFIED** yapar; artifact indirilmediği için evidence schema v2 alan değerleri, ZIP içeriği ve tam hash bağı bağımsız doğrulanmamıştır. Bu lane production PFN, reset/repair, uninstall-reinstall retention, second-user, same-user malicious process, crash/power-loss veya source-wide reconciliation kanıtı değildir.

Takip eden schema v3 lifecycle kodu, updated test paketinin exact full name'iyle `Reset-AppxPackage` çalıştırır; package identity korunurken protected record ve control ticket'ın manual cleanup öncesinde kaldırıldığını ve reset sonrası fresh create'in yeni record identity ürettiğini denetler. Daha sonra bu fresh protected state canlıyken exact current-user package registration'ını preserve-data/`-AllUsers` seçenekleri olmadan kaldırır, exact app-data kökünün yokluğunu doğrular, aynı signed `0.0.2.0` MSIX'i yeniden kurar ve ikinci bir fresh create/delete zincirini çalıştırır. Microsoft reset'i app data'yı kalıcı silen fresh-install dönüşü, repair'i ise veriyi koruyan ayrı davranış olarak tanımlar; clean uninstall package state'i kaldırır [S91][S92]. Evidence schema v3 yalnız package/reset/uninstall/reinstall/fresh-create ve record-identity-change sonuçlarını allowlist Boolean'larıyla genişletir; path, package family/full name, record leaf, reference, source/owner veya kullanıcı kimliği yayımlamaz. Run number `#20`, commit `6f30e3fa92f329c1fa26333c591edb5148261cec` için bu lane'in hosted execution'ını workflow/UI düzeyinde **VERIFIED** yapmıştır; artifact alanları/ZIP/içerik/tam hash bağı bağımsız doğrulanmamıştır. Bu test-only current-user/fresh-state zinciri durable source-wide deletion/reconciliation gibi M8; M8 transactional production layout'ın 50k end-to-end/crash; production PFN/repair/preserve-data/identity gibi M15 kanıtlarının hiçbiri değildir; M4 foundation `COMPLETED` kalır.

2026-08-14 run number `#19`, commit `71dbb0cf23467d978674976796e2bbceca01494a` için historical broad `PackageReset`/`PackageResetFailed` stage'inde fail olmuştur. O stage native invocation ile registration/identity/manifest postcondition'larını birlikte kapsadığı ve raw HRESULT kaydedilmediği için exact failure sınıfı bilinmez; `#19` native reset veya aşağı akış uninstall/reinstall PASS kanıtı değildir. Takip implementation'ı `PackageResetInvocation`, `PackageResetRegistrationValidation` ve `PackageResetManifestValidation` stage'lerini ayırır. En fazla sekiz exception/inner-exception içindeki bilinen HRESULT yalnız allowlist'li, secretsiz failure `Code`una dönüştürülür; raw HRESULT/message/ActivityId, path, PFN veya kullanıcı verisi evidence'a eklenmez. Yalnız `0x80073D00`, `0x80073D02` ve `0x80073D05` toplam en fazla üç quiescence-kontrollü bounded deneme alır [S93]. Bu retry politikası resmi hata anlamlarından türetilen `INFERENCE`dır; run `#20` UI'ı retry'ın kullanılıp kullanılmadığını göstermez. Takip commit'i `6f30e3fa92f329c1fa26333c591edb5148261cec` için run `#20` 3/3 işi yeşil, üç artifact ve `8m14s` toplam süreyle tamamlandığından hardening ve schema-v3 zinciri workflow/UI düzeyinde **VERIFIED**dır. Artifact içeriği bağımsız doğrulanmamıştır; `#19` tarihsel failure ve `#18` update **VERIFIED** kaydı korunur.

Gerçek Windows user-boundary için production graph/MSIX dışında `IptvSuite.DpapiUserBoundaryHarness` ve elevated controller `IMPLEMENTED`dır. Framework-dependent x64 harness creator hesabında sentetik raw `CurrentUser` blob'u ile owner-bound production-adapter kaydı üretir. Farklı SID'li non-admin child process creator raw blob'unu `CryptographicException` dışında başarı saymadan reddetmeli, production adapter'da creator kaydı için `ProtectedRecordUnavailable` ve null lease almalı, kendi raw/adapter round-trip'ini geçmeli ve creator record byte'larını değiştirmemelidir; creator son aşamada digest/re-read ve deletion'ı tamamlar. Controller gerçek target security context/profile için `CreateProcessWithLogonW(LOGON_WITH_PROFILE)` kullanır; caller token'ını koruyan `LOGON_NETCREDENTIALS_ONLY` yasaktır [S107–S111]. Sanitized evidence username/SID/path/password/plaintext/opaque-reference/protected-record digest/raw exception içermez; success allowlist yalnız exact SDK, clean commit SHA, controller script SHA-256 ve staged harness assembly SHA-256 provenance metadata'sını taşır. Child invocation/custom environment password/payload/reference/SID/protected-record digest taşımaz; explicit custom environment block yoktur, OS target-profile environment username/profile değerlerini sağlar ve exact executable/DLL/workspace path command line'da zorunludur. Exact account/profile/workspace cleanup başarının parçasıdır. 2026-08-16 run `#31` (`31960438511`), commit `740d33d0d428c8f6aeded24aa358fdd8626016f1` için dört zorunlu Windows işini ve dört artifact'ı `8m23s` içinde geçmiştir. İndirilen 1.766 bayt sanitized DPAPI `last-success.json` SHA-256 `c9dbb22b577aaf08c588bbdc951e87bce6bbe82c86c8f58a2fe6cb9840b725b8` taşır; exact SDK/clean commit/provenance ve bütün boundary/cleanup Boolean'ları `true` olduğundan raw/adapter farklı-user lane'i hosted **VERIFIED**dır. Bu sonuç yalnız M4 raw DPAPI + production-adapter user-boundary kapsamıdır; production package registration/PFN/Store identity altında ikinci-user ve clean-machine acceptance M15'e aittir. M4 foundation `COMPLETED`; ADR-003 M8 production layout'u için `Proposed` kalır.

Mevcut managed containment + `ReparsePoint` kontrolleri check-to-use yarışını tamamen kapatmaz. 2026-08-14 threat-model kararı, production uygulamanın yükseltilmeden çalıştığı ve aynı Windows kullanıcısı altında kötü amaçlı kod yürütülmesinin zaten hesap/oturum compromise'ı sayıldığı sınırda, yarışın ek integrity/availability etkisini M4 için residual risk olarak **ACCEPTED** tutar. Production executable manifesti `asInvoker` + `uiAccess=false` değerlerini açıkça ister; package capability allowlist'i `allowElevation` içermez ve architecture gate ikisini de kilitler [S13][S95]. Bu, atomik path veya same-user sandbox kanıtı değildir. Store kökü paylaşılan/ağ konumuna taşınırsa, uygulama yükseltilirse ya da same-user adversary kapsama alınırsa karar yeniden açılır. Tam kapatma; pinned directory handle üzerinden root-relative child open/create, handle attribute doğrulaması, rename, delete ve startup enumeration yapan ayrı Windows I/O adapter'ı gerektirir; path alan `File.OpenHandle` veya yalnız son bileşende `FILE_FLAG_OPEN_REPARSE_POINT` kullanan küçük bir patch parent-swap yarışını kapatmaz [S94]. Exact stale-temp cleanup non-adversarial filesystem yarışlarında fail-closed/best-effort sınırındadır; active/recent temp korunur ve silinemeyen exact stale temp safe initialization failure üretir. Cleanup, exact lowercase current-namespace aday listesini 1.024 girdide sınırlar; 1.025'inci exact adayda hiçbir adayı silmeden fail-closed olur. Bu cap mutation öncesi aday belleğini sınırlar; dizin aramasının toplam süresini veya aynı-user candidate-fill DoS'unu çözmez. Metadata referansı kaybolmuş `.dpapi` source record'larının source-wide startup reconciliation'ı henüz uygulanmamıştır. Production composition factory expected initialization hatalarını safe typed sonuca eşler; test/adapter constructor'ı doğrudan çağrıldığında exception contract'ını korur. Process-local gate yalnız çakışan store operasyonlarını sıralar; caller'a daha önce teslim edilmiş plaintext lease geriye dönük revoke edilmez. Source deletion coordinator yeni resolve'ları durdurup in-flight operasyonların/lease kullanımının kapanmasını beklemelidir. Bunlar plaintext sızıntı kanıtı değildir; orphan/DoS/lifecycle yüzeyleri M4 `Completed` öncesi açık kalır.

Run number `#22` UI kaydı, bu threat-model kararı ve production `asInvoker` / `uiAccess=false` manifestini içeren commit `43bf88c591bdc216d3a288ca578f90e4fad927c6` için 3/3 işi yeşil, üç artifact ve `8m23s` toplam süre gösterir. Commit-bound exact quality, signed production-package launch ve disposable lifecycle işleri declarative least-privilege manifest sınırının hosted execution'ını workflow/UI düzeyinde **VERIFIED** yapar; R26 kararının doğruluğunu veya runtime token'ın non-elevated olduğunu kanıtlamaz, path/reparse yarışını atomik kapatma ya da same-user isolation iddiası oluşturmaz. Artifact alanları, ZIP/içerik/tam digest, job log/test sayısı ve GitHub database run ID'si bağımsız doğrulanmamıştır.

## 2. Veri sınıflandırması

| Sınıf | Örnek | Saklama | Log/diagnostics |
|---|---|---|---|
| Secret | Password, token, Authorization, Cookie, URI user-info, credential-bearing full M3U/stream URL | Yalnız platform-protected store/blob; mümkün olan en kısa plaintext lifetime | Hiçbir koşulda yazılmaz |
| Sensitive metadata | Provider origin, channel list, categories, favorites, recently played, logo host | Device-local DB/cache; minimizasyon ve delete lifecycle | Default loglanmaz; source ID ve count kullanılır |
| Public/config | App version, schema/parser version, feature flag, player build | Secretsiz local config/package | Yapılandırılmış biçimde yazılabilir |
| Untrusted content | Channel/category name, provider error/body, playlist directive, image bytes | Length/type-bound; normalize veya cache | Raw değer yok; gerekirse hash/count/warning code |
| Operational | Random operation ID, duration, item count, stable domain error code | Bounded local log | İzinli, secret-free |

`SecretReference` ve `ProtectedLocatorReference` opaque değerlerdir; `ToString` veya serialization secret veremez. Channel/provider DTO'su credential taşımamalıdır.

## 3. Threat assumptions

| Threat | Varsayım/etki | Kontrol |
|---|---|---|
| Malicious playlist/API payload | Oversized line/body, parser ambiguity, unsafe URI, injection, memory exhaustion | Byte/line/item/depth limits, incremental parser, allowlisted schemes, parameterized SQL, cancellation |
| Malicious logo/image | Decompression bomb, malformed codec, local-network probing, cache abuse | MIME+magic validation, byte/pixel/dimension cap, bounded decode/concurrency, address policy, LRU |
| Malicious/broken stream | Native parser exploit, hang, endless buffer/retry, secret echo | Patched engine, bounded open/retry, adapter isolation boundary, sanitized logs, corpus/fuzz/soak |
| Credential leak | Query/user-info/header, exception, crash dump, clipboard, screenshot | Opaque refs, central sanitizer, no full memory dump support artifact, scan tests |
| Network attacker | HTTP sniffing, invalid certificate, downgrade, redirect theft | HTTPS credential gate, OS trust validation, no bypass, manual redirect, no cross-origin auth |
| Local DB theft | Channel preferences visible; protected secrets targeted | DPAPI CurrentUser blobs, app-data ACL, minimization, deletion; acknowledge same-user threat |
| Same-user malicious process | Can read app files/process memory under desktop threat model | DPAPI reduces at-rest theft but is not process sandbox; OS account security and short secret lifetime |
| Supply-chain compromise | Native DLL/plugin or NuGet update | Exact lock, official source, hashes/signatures where supplied, SBOM, license/CVE review |
| Store/reviewer data exposure | Real account shared for certification | Only developer-owned synthetic service and fake credentials |
| Unauthorized product use | User connects to unlicensed service | No provider discovery/list, clear BYO-authorized terms, no recording/DRM bypass; Store/legal review |

M9'un logo dilimi active snapshot'taki exact encrypted `SourceId`/`ChannelId`/purpose/reference tuple'ını çözer; strict UTF-8 ve HTTPS validation'dan sonra endpoint'in durable source scheme/host/port üçlüsüyle birebir aynı olmasını transport çağrısından önce zorunlu kılar. Cross-origin logo bu nedenle transport'a ulaşmadan reddedilir. Response `512 KiB` ile, içerik PNG/JPEG/WebP magic signature'larıyla ve fetch eşzamanlılığı dört ile sınırlıdır. M14 cache'i process-memory-only `32 MiB / 128-entry` LRU olarak sınırlar; recycle/source-delete/page-dispose queued/in-flight yükü iptal eder ve cancellation'a uymayan provider'ın stale dönüşünü cache'e almaz. Durable disk image cache kapalıdır (`0` byte). Bu bounded lifecycle deterministik testlerle `VERIFIED`dır; packaged 50k seed logo taşımadığından package-level image fetch/decode/network kabulü değildir. MIME header, decoded pixel/dimension, codec-level decompression veya genel DNS/address policy kanıtlanmaz; bu kontroller gelene kadar cross-origin logo fetch kapalı kalır.

Not protected against: fully compromised Windows user/admin/kernel, screen capture by the OS/user, or a provider itself mishandling data. These limits are stated honestly; “encrypted” is not marketed as complete endpoint protection.

## 4. Credential ve locator storage

### 4.1 Windows kararı

[ADR-003](../adr/ADR-003-local-persistence-and-secure-storage.md) Proposed baseline'ı:

- metadata/snapshot/index: SQLite;
- düşük-cardinality source credential ve remote-playlist secret: mevcut owner-bound DPAPI `CurrentUser` / Windows `LOCAL=user` protected blob; bulk channel locator production yerleşimi henüz seçilmedi;
- packaged app'in device-local ve cloud-backup dışı LocalCache alanı;
- app DB'de yalnız opaque reference;
- Xtream stream URL'si mümkünse provider key + source secret'tan just-in-time oluşturulur;
- M3U stream locator'ı protected reference ile çözülür.

M7 loader remote-playlist locator'ını yalnız authoritative configuration owner/purpose/reference tuple'ıyla protected store'dan okur; lease operation sonunda dispose edilir. Streaming transport ve parser response'u tek string'e dönüştürmez. Relative locator yalnız final authorized HTTPS URI'ye resolve edilir; HTTP, user-info ve fragment skip edilir. Raw locator public result, diagnostic veya log yüzeyine verilmez; production loader accepted entry'yi zorunlu internal sink'e tek tek aktarır. Sink'in raw locator'ı protected opaque reference'a dönüştüren durable implementation'ı M8 transaction sınırına aittir.

Credential Locker primary değildir: roaming/bulk-locator uyumsuzluğuna ek olarak full-trust non-AppContainer process'te user locker isolation'ı beklenmemelidir. Belgelenen 20-record sınırı UWP/AppContainer desktop bağlamına özgüdür; mevcut full-trust package için otomatik gerekçe sayılmaz ve 20/21 probe'u açık kalır. `LocalMachine` kapsamı kullanılmaz. Kendi master-key/crypto formatı tasarlanmaz.

DPAPI-per-locator 50k bütçeyi aşarsa yeni security decision gerekir. 2026-08-14 clean-commit `Decision` koşusunda bu trigger gerçekleşmiştir: commit `fc96a211171d1e4f5e5f02174da6c565ef2d59bb` için 50k create p95 `151.447 s`, 50k normalize + protected persistence + index için birleşik `3 s` bütçesinin `50.48` katı; create allocation p95 `668.007 MiB`, bütün-import `150 MiB` bütçesinin `4.45` katıdır. Cancellation p95 `1.067 ms`, post-cancellation commit üst sınırı `0` olmuş; canary taramaları ve cleanup geçmiştir. Ölçümün desteklediği `INFERENCE`, file-per-record DPAPI düzenini **bulk channel locator** için reddetmektir; düşük-cardinality source credential kayıtları reddedilmez. Ayrıntılı evidence için [M4 Secret Store Decision Evidence](../quality/M4_SECRET_STORE_DECISION_EVIDENCE.md) belgesine bakın.

Takip araştırması encrypted database'i M4 production için `NO-GO` tutar. Resmî .NET/native package yolu nedeniyle SQLCipher Commercial/Enterprise yalnız procurement, lisans, exact binary provenance/SBOM, MSIX, migration, lifecycle ve performans review'undan sonra açılabilecek conditional future candidate'tır. Community'nin self-build/native servicing yükü, Zetetic'in public `e_sqlcipher` paketlerini desteklenmeyen/eski binary olarak sınıflaması ve SEE'nin ayrı ücretli native integration yolu nedeniyle SQLCipher Community/`e_sqlcipher`/SEE M4'te `NO-GO`dur [S103–S106].

Yalnız production graph'ından ayrı immutable-container comparative spike'ı için `GO` verilmiştir; production için `NO-GO`dur. Spike contract'ı her source attempt/snapshot'ta fresh 256-bit DEK'i bir kez DPAPI `CurrentUser` ile wrap eder; her locator'ı `AesGcm(key, 16)`, 96-bit CSPRNG nonce ve 128-bit tag ile bağımsız korur. Aynı attempt'te nonce `HashSet` collision rejection zorunludur. Persistent counter kullanılmaz; fault/cancel/crash staging generation'ı resume edilmez, yeni attempt fresh key ile başlar ve active generation yalnız decrypt edilir [S96–S100].

Test-only v1 canonical AAD; magic/format/algorithm, `SourceId`, `SnapshotId`, `KeyGenerationId`, canonical record ordinal, tek sentetik channel-locator purpose'u, `ChannelId` rolündeki owner ID, opaque protected-reference ID, plaintext length ve record count'u bağlar. Owner/reference kind bu tek workload'da implicit'tir; production schema bunları explicit versioned alanlarla çözmeden kabul edilemez. Mutable ad/URL metadata AAD'ye girmez. Owned DEK/plaintext/AAD buffer'ları en dar ömürle tutulur ve `CryptographicOperations.ZeroMemory` ile sıfırlanır; `AesGcm.Dispose` internal/native key schedule'ın wipe kanıtı sayılmaz. AAD whole-snapshot rollback/replay veya authoritative owner doğruluğunu tek başına çözmez [S96][S97][S99]. Exact format ve spike sınırı [M4 protected catalog aday kararında](../quality/M4_PROTECTED_CATALOG_CANDIDATE.md) kayıtlıdır.

Clean commit `7cd5bf739712a5cbdcf47634275a113faab8c48e` üzerindeki karar-uygun 5k/10k/20k/50k × 20 protected-catalog `Decision` summary'si `209.828` byte ve SHA-256 `4a219e802dcff959644bda42489f0ee5231be63df1eb37d034e2ed16ff0859de`dir. **VERIFIED ölçümler:** 50k create+activate p95 `173.128 ms`, allocation p95 `34.676 MiB`, working-set boundary delta p95 `15.855 MiB`, fresh-adapter reopen+unwrap p95 `13.231 ms`, 256 read probe p95 `1.695 ms`, snapshot delete p95 `2.321 ms`, controlled cancellation p95 `0.220 ms` ve 10 encrypted staging record sonrasındaki cancellation p95 `5.570 ms`; post-cancellation activation/mutation ile temporary artifact sayıları `0`, canary ve cleanup kontrolleri geçmiştir. Bu dar crypto + sequential persistence ölçümlerinin ilgili `3 s`, `150 MiB`, `250 MiB` ve `250 ms` referans bütçelerinin sırasıyla `%5,77`, `%23,12`, `%6,34` ve `%2,23`ü olması **INFERENCE**dır; parser/normalize/index, packaged `LocalCache`, crash/power-loss ya da production end-to-end PASS değildir. Comparative benchmark kapanmıştır. Immutable cross-file container production için `NO-GO`dur. M8 için tercih edilen yön, DPAPI `CurrentUser` ile wrap edilmiş fresh per-snapshot DEK ile per-row AES-256-GCM ciphertext, snapshot metadata ve active pointer'ı aynı SQLite transaction domain'inde yöneten state machine'dir; yalnız `Proposed`dur ve uygulanmamıştır. M4 foundation `COMPLETED`; ADR-003 ve M8 production implementation `Proposed`/kabul edilmemiş kalır.

### 4.2 Secret lifetime

1. Form input'u view state'te yalnız işlem süresince kalır; diagnostics ve navigation state'e serialize edilmez.
2. Başarılı explicit “save” işleminde protected store'a gider.
3. HTTP/player adapter secret'ı operation başlangıcında çözer, yalnız hedef request/session'a verir.
4. Raw locator tekrar domain/UI'ya dönmez.
5. Managed string'in güvenli wipe garantisi olmadığı kabul edilir; kopya sayısı ve lifetime azaltılır.
6. Native player'a verilen URL/header'ın engine loglarına düşmediği M10'da canary scan ile kanıtlanır.
7. Clipboard, drag/drop, toast, recent documents veya OS search index'e secret yazılmaz.

## 5. URL, header ve log redaction

### 5.1 Tek merkezî politika

Log çağıran her component kendi regex'ini yazmaz. Typed fields veri sınıfı taşır ve tek sanitizer çıkıştan önce uygular. Formatter'a raw secret verilmemesi ilk savunmadır; sanitizer ikinci savunmadır.

URI politikası:

- raw URI string'i log template'ine verilmez;
- parsing başarılıysa scheme ve gerekirse safe host/origin ayrı alan olur; path default redacted, fragment silinir;
- user-info bütünü kaldırılır;
- bütün query **değerleri** default `[REDACTED]` olur; key adları bile `password/token/key/auth/signature` gibi ise yalnız `secret-param` sayacı tutulur;
- parse başarısızsa input'un parçası yazılmaz, `invalid-uri` + length + random operation ID yazılır;
- URL-encoded, nested URL ve provider response içinde tekrar edilmiş credential ayrıca test edilir;
- source display name/channel name gibi untrusted text operational logda yer almaz.

Her zaman redacted header'lar:

- `Authorization`, `Proxy-Authorization`;
- `Cookie`, `Set-Cookie`;
- API key/token iması taşıyan custom header;
- provider tarafından geri yansıtılan request header/body.

Response body, playlist satırı, player command line, environment ve full exception `Data` dump edilmez. Exception type + mapped error code + safe stack, development build'de yalnız lokal ve bounded tutulur. Kullanıcı UI'ı exception/type/stack göstermez.

### 5.2 Log retention

- Production'da default ayrıntılı network/player debug kapalıdır.
- Secret-free local operational log için provisional rolling retention en fazla 7 gün ve 20 MiB'dir; M15 privacy review değiştirebilir.
- Support export explicit kullanıcı eylemidir; export öncesi tekrar sanitization ve canary scan uygulanır.
- Full process memory dump veya raw libVLC log paketi otomatik support artifact'i değildir.
- Analytics/remote telemetry yoktur. Eklenirse bu baseline, KVKK/store declarations ve threat model yeniden açılır.

## 6. Network güvenliği

### 6.1 HTTP client policy

- Long-lived/typed `HttpClient` ve bounded connection lifetime.
- OS trust store ve hostname validation her zaman açık; custom “accept all” callback yasak.
- Connect, request, total retry, response bytes, decompressed bytes ve item limitleri ayrıdır.
- `ResponseHeadersRead` ile streaming; cancellation bütün read/parse/write pipeline'ına akar.
- Automatic redirect kapalı veya uygulama tarafından manual doğrulanır; maximum hop ve loop detection vardır.
- Origin değişiminde Authorization/Cookie/user-info/query credential forward edilmez.
- HTTPS→HTTP downgrade reddedilir.
- Cookie default kapalı; kanıtlanmış provider ihtiyacında source-scoped container ve delete lifecycle gerekir.
- Production transport OS/environment proxy'sini kullanmaz (`UseProxy=false`); connect callback her request'te bağlanan IDNA-canonical exact host + effective port authority ile gerçek socket authority'sini eşleştirmeden DNS/connect yapmaz; geçersiz IDN fail-closed reddedilir.
- Retry yalnız safe/idempotent GET ve transient sınıflarda; exponential backoff + jitter + `Retry-After` cap. Auth, TLS, validation ve parse error retry edilmez.
- DNS sonucu connect öncesi address policy'ye, redirect sonrası tekrar policy'ye tabi olur; mixed public/private cevap ile special-use adresler (en az `192.88.99.0/24`, `2001::/23`, `2002::/16`, `3fff::/20`) fail-closed reddedilir.

### 6.2 HTTP-only provider kararı

Credential, token veya kişisel playlist locator'ı taşıyan HTTP **reddedilir**; kullanıcı uyarısı trafiği şifrelemez. Anonymous media için HTTP compatibility ticari gereksinim haline gelirse:

- ayrı ADR/threat/Store review,
- açık per-source opt-in ve kalıcı warning,
- hiçbir sensitive header, cookie, user-info veya query değeri olmaması,
- HTTPS'ten sessiz downgrade olmaması,
- UI'da güvenli/güvensiz durumun açık ayrımı

olmadan açılmaz.

### 6.3 Image ve local-network policy

Logo fetch player request'inden ayrıdır:

- yalnız `http/https`; `file/data/javascript/ftp/smb` reddedilir;
- credential, Cookie, Authorization ve Referer verilmez;
- logo/image isteğinde loopback, link-local, multicast, unspecified ve private address her zaman `PublicOnly` policy ile reddedilir;
- kullanıcı onboarding ekranında private/local kaynak erişimini açıkça onayladıysa yalnız yetkilendirilmiş Remote M3U veya kalıcı Xtream source probe/import/catalog isteğinin exact HTTPS host + effective port origin'i private/local policy ile değerlendirilebilir; bu opt-in logo/image isteğine veya cross-origin redirect'e taşınmaz;
- Bu pre-release modelde desteklenen legacy upgrade yoktur: kalıcı source kaydının varlığı, kaydın clean-install onboarding'deki aynı exact-origin private/local onay yolundan üretildiğinin kanıtıdır. Xtream production composition'a bağlanmadan önce de aynı açık consent UI/contract yolu zorunlu kılınmalı ve packaged acceptance ile doğrulanmalıdır; aksi durumda private/local opt-in verilemez.
- DNS rebinding/redirect'te final IP tekrar kontrol edilir;
- exact Content-Type ile bounded image header signature/dimension/pixel metadata doğrulanır ve byte sınırı uygulanır; bu kontrol tam bitstream geçerliliğini veya decode-time bütçesini kanıtlamaz, OS image decoder'ın codec karmaşıklığı/failure maliyeti residual risk olarak kalır;
- concurrency 4 ve memory-only 32 MiB / 128-entry LRU uygulanır; görünürlükten çıkan satırın işi iptal edilir ve iptal edilmiş noncooperative sonuç cache'e alınmaz;
- MVP'de durable image disk cache kapalıdır (`0` byte). İleride ayrıca threat/lifecycle review ile açılırsa `200 MiB` sessiz hedef değil hard üst sınırdır;
- source silme cache namespace'ini temizler.

## 7. Local data lifecycle

| Olay | Secret | SQLite/snapshot | Image/download cache | Log |
|---|---|---|---|---|
| Draft/validation fail | Persist edilmez | Draft ancak secretsizse | Yok | Safe error code |
| Successful source save | Protected create/update | Source metadata | Yok | Source ID |
| Successful refresh | Fresh generation; active key yalnız decrypt | Tek SQLite transaction'da key-state + snapshot + active pointer hedefi; current container production değildir | Lazy | Counts/duration |
| Failed/cancelled refresh | Staging generation abandon; aynı DEK/nonce state resume edilmez | Staging rollback/reconcile; old active kalır | Partial temp silinir | Safe stage/error |
| Source delete | Önce admission kapat/drain, sonra wrapped key'leri erişilemez kıl ve bounded cleanup | Source/snapshot/favorites policy delete | Namespace delete | Deletion result |
| “Clear all local data” | Bütün protected refs delete | DB close/delete/recreate | Tümü | Son güvenli kayıt ardından log delete |
| Upgrade/migration | Plaintext temp yok | Forward migration + backup/recovery policy | Version invalidation | Schema version only |
| Reset/uninstall | Disposable test PFN için reset + live-state current-user uninstall/reinstall fresh-state lane'i schema v3'te run number `#20` ile hosted workflow/UI düzeyinde **VERIFIED**; artifact içeriği, production PFN ve repair M15 dahil açık | Reset/uninstall sırasında exact package app-data yokluğu test edilir; production metadata policy açık | Test-only owned state yokluğu test edilir; production cache policy açık | Test harness PFN/full-name/path/user/record/reference/secret yayımlamaz; production log policy açık |

Bu tablo hedef lifecycle policy'sidir; mevcut M4 foundation exact stale ciphertext temp cleanup'ına ek olarak exact configuration kaydı ve authoritative aggregate zincirinin işaret ettiği tek channel'ın stream/optional-logo kayıtları için dar idempotent delete primitive'lerini uygular. Source-wide enumeration/deletion, durable `DeletionPending` orchestration, admission/drain veya startup protected-record orphan reconciliation uygulamaz. Tamamlandığında silme kısmi başarısızsa source `DeletionPending` ve network/playback-disabled olur; startup reconciliation DB refs ile protected records arasındaki orphan'ları idempotent temizler. Secret silmeden katalog kaydını yok ederek ulaşılamaz secret bırakılmaz. Shared snapshot DEK korunurken tek channel delete cryptographic erase değildir. Wrapped key'i DB'de null etmek de journal/free page, backup veya storage snapshot'taki bütün kopyaları kapsadığı kanıtlanmadan “NIST secure erase” sayılmaz [S101].

Snapshot retention provisional olarak active + bir önceki complete snapshot'tır; eski snapshot protected locator'ları pruning işleminde silinir. Favorites ve recently-played için retention/privacy product kararı gerekir; recently-played MVP'de zorunlu değildir.

## 8. Parser, database ve player güvenliği

### Parser/API

- Response body, JSON depth/string/array, M3U line/attribute/item ve total byte cap.
- UTF-8/BOM; legacy fallback yalnız explicit warning ve corpus kanıtıyla.
- Unicode NFC, control removal, length cap; display string SQL/log/markup olarak yorumlanmaz.
- Unknown directive ignored + bounded count; unbounded diagnostics yok.
- Parameterized SQL; provider string'i identifier/order clause olmaz.
- HLS media manifest channel catalog olarak import edilmez.

### SQLite

- App-owned schema version ve forward migration.
- Foreign keys ve integrity checks; wrapped key-state, ciphertext row, snapshot metadata ve active pointer aynı transaction domain'inde old-or-new activation.
- Hedef state machine `staging/encrypt-only → active/decrypt-only → retired/destroyed`; key'siz active, mixed snapshot, staging serve ve crash sonrası staging encryption resume fail-closed reddedilir.
- Current no-database immutable-container prototipi yalnız comparative spike'tır; ayrı metadata DB ile cross-file activation atomik olmadığı için production'a giremez.
- Rollback-journal `DELETE` + `synchronous=EXTRA` başlangıç adayı; WAL yalnız checkpoint/backup/crash/concurrency ve owned-write-surface canary kanıtıyla değerlendirilir [S102].
- DB/WAL/SHM canary secret scan.
- Install directory'ye yazım yok.
- Backup/export MVP dışıdır; debug DB artifact'ı gerçek kullanıcı verisi içeremez.

### Native player

- Exact non-GPL package/plugin allowlist ve SBOM.
- Unused protocol/access plugin'leri mümkünse build/package review ile çıkarılır; fonksiyon kaybı corpus'ta ölçülür.
- Engine option/URL logları sanitized; raw log shipping yok.
- Open/stop/retry bounded; close/dispose session ownership.
- Güvenlik update SLA: critical exploitable media-parser CVE release öncesi blocker; yayın sonrası triage ve gerekirse Store withdrawal/forced update planı.
- Codec/parser fuzz corpus ve malformed stream testleri hakları temiz fixture kullanır.

## 9. Privacy ve mağaza hazırlığı

Yayın öncesi uygulama içinde ve Store listing'de erişilebilir privacy policy bulunur. En az:

- uygulamanın içerik/abonelik/provider sağlamadığı;
- credential ve katalog metadata'sının device-local saklandığı;
- trafiğin geliştirici backend'inden geçmediği ve user-selected provider'a doğrudan gittiği;
- amaç, retention, cache, delete/reset/uninstall davranışı;
- analytics/reklam/third-party crash SDK bulunmadığı;
- support/export süreci ve iletişim;
- kullanıcı hakları ve ilgili ülke mekanizmaları

açıklanır.

Microsoft certification notes için yalnız geliştiriciye ait sentetik M3U/Xtream fixture ve çalışan fake account sağlanır. Store policy'nin “server'dan gelen content” kapsamı bulunduğu kabul edilir; BYO model onay garantisi değildir.

Samsung tarafında Partner Seller/Content Manager/Türkiye dağıtımı ayrı P0 blocker'dır. Tizen `WidgetData` ve privacy/test account davranışı gerçek cihaz/store preflight ister.

KVKK'da saklama ve koruma veri işleme kapsamına girebilir. Aydınlatma ile açık rıza ayrı tutulur; her işlem için gereksiz rıza istenmez. Cihaz-içi modelde yayıncının veri sorumlusu rolü, VERBİS ve provider'a yurt dışı aktarımın kime atfedileceği **UNVERIFIED / hukuk görüşü gerekir**. “Backend yoksa KVKK yoktur” varsayımı yapılmaz.

## 10. Supply chain ve release gates

- Yalnız official source/repository/feed; exact version + lock.
- Dependency update PR'ında release notes, license, transitive/native binary ve CVE review.
- SBOM; binary-to-source mapping; license/notices; GPL plugin taraması.
- Codec patent değerlendirmesi OSS lisansından ayrı hukuk gate'i.
- Secrets/signing certificates repository'ye girmez; CI platform-scoped secret environment kullanır.
- M1 local signing key'i export edilemez ve `CurrentUser\My` içinde kısa ömürlüdür; yalnız public certificate install süresince `LocalMachine\TrustedPeople` deposuna alınır. Paket ve her iki certificate kaydı exact identity/thumbprint guard ile temizlenir.
- M1 dev package identity disposable'dır. Store identity'ye in-place update, PFN/AUMID veya app-data migration garantisi verilmez; `Package.StoreAssociation.xml` M1'de fail'dir.
- Source ve installed effective manifest birlikte doğrulanır; M1 capability allowlist'i yalnız `runFullTrust`'tır.
- Preview/Experimental dependency release'e girmez.
- WACK, clean install/update/uninstall, package content/architecture ve app-data scan.
- Store/privacy policy release günü tekrar doğrulanır.

## 11. Doğrulama kapıları

| Gate | Kanıt | Milestone |
|---|---|---|
| Redaction | Canary secret table tests + log/artifact binary scan | M3–M5, her release |
| Protected storage | Packaged create/read/update/delete + reset/uninstall/reinstall/repair; per-record bulk locator düzeni reddedildi; test-only immutable-container comparative `Decision` tamamlandı; production SQLite state machine uygulanmadı ve end-to-end 50k/crash kanıtı açık | M4/M8/M15 |
| Transport | TLS fail, downgrade, cross-origin redirect, timeout, cancellation, oversize | M5 |
| Hostile input | Parser/API/image malformed and budget corpus | M6–M8 |
| Native player | Codec/network/malformed corpus, diagnostics scan, SBOM/CVE | M10 |
| Data deletion | Source delete, clear-all, orphan reconciliation, migration fault injection | M4/M8/M15 |
| Store/privacy | Policy checklist, privacy text, synthetic reviewer access, WACK/private preflight | M15 |
| Release | Secret scan, license/patent review, no critical open security issue | M16 |

## 12. UNVERIFIED ve reopen tetikleri

- Per-record DPAPI bulk channel-locator throughput'u ölçülüp 50k bütçesinde reddedildi; düşük-cardinality source credential kullanımı korunur. Test-only immutable-container comparative `Decision` clean commit ve exact evidence hash ile tamamlandı; comparative gate kapandı.
- Production SQLite transaction layout'u, gerçek parser/normalize/index end-to-end 50k ve crash/power-loss kanıtı M8'de hâlâ `UNVERIFIED`dır.
- Production PFN lifecycle/repair/identity/Store kabulü ve ikinci-user packaged identity M15 için `UNVERIFIED`dır.
- Test-only spike'ın controlled fresh-key/no-resume, nonce-collision ve AAD tamper/swap matrisi geçmiştir; production implementation için process-kill/power-loss, authoritative metadata + ciphertext rollback/replay, key rotation/deletion ve DB journal/WAL/SHM recovery matrisi `UNVERIFIED`dır.
- Source-wide deletion, `DeletionPending` ve startup record/temp orphan reconciliation.
- Owner+reference+endpoint tuple'ının birlikte değiştirilmesi, resolved-origin equality ve authoritative persisted owner doğrulaması.
- Store initialization için raw path/message taşımayan typed failure sonucu.
- Same-user path mutation residual-risk kararı **ACCEPTED**; yalnız yükseltilmemiş, kullanıcıya özel local store sınırında geçerli. Paylaşılan/ağ store, elevation veya same-user adversary scope değişikliği kararı yeniden açar [S94].
- Credential Locker/AppData/WidgetData uninstall/reset/update sonucu.
- Microsoft/Samsung Store'un genel BYO IPTV acceptance sonucu.
- Codec patent sonucu ve exact license obligations.
- KVKK role/transfer sonucu.
- Backend, telemetry, crash SDK, ads, sync, export, DRM, recording/download veya anonymous HTTP scope'a girerse bu belge ve ilgili ADR'ler yeniden açılır.

## Kaynaklar

[S10–S13, S21, S27, S30, S33–S41, S47, S51–S52, S60–S65, S96–S106](../research/SOURCES.md)
