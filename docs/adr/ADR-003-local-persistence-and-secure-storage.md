# ADR-003 — Yerel persistence ve güvenli saklama

**Status:** Proposed

**Date:** 2026-08-09

## Context / Problem

Kaynak credential'ları ve query/user-info içinde token taşıyabilen M3U/stream locator'ları secret'tır. Buna karşılık 50.000+ kanal metadata'sı hızlı sorgu, atomik refresh ve migration gerektirir. Plaintext secret'ın DB, config, log, crash artifact veya migration dosyasına girmemesi; kaynak silmede geri çağrılamaz hale gelmesi gerekir.

Windows Credential Locker credential için resmî bir API'dir; ancak Microsoft-account ile roam edebilir ve on binlerce hassas locator için uygun değildir. Belgelenen 20-record sınırı UWP/AppContainer desktop bağlamına özgüdür; full-trust non-AppContainer process'te diğer user locker kayıtlarına erişim/isolation davranışı ayrıca sorun oluşturur. Tek mekanizma gibi kullanılması ürünün cihaz-içi saklama vaadi ve katalog ölçeğiyle çelişir; mevcut package token'ında 20/21 probe'u yine yapılır.

## Decision drivers

- Current Windows user'a bağlı OS koruması.
- Plaintext credential ve locator içermeyen queryable katalog.
- 50.000+ item import, index ve startup performansı.
- Atomik snapshot activation; failed/cancelled refresh'in aktif veriyi bozmaması.
- MSIX update/reset/uninstall ve kullanıcı kaynak-silme yaşam döngüsü.
- Forward-only migration ve crash recovery.
- Test double ve gerçek Windows integration test sınırı.

## Considered options

1. SQLite metadata + DPAPI/Windows Data Protection ile protected blobs.
2. Credential Locker/`PasswordVault` + SQLite.
3. SQLCipher veya başka encrypted database.
4. Düz dosya/JSON + OS protection.
5. Uygulamanın kendi master-key/AES tasarımı.

## Decision

Önerilen baseline:

- Queryable, secret olmayan metadata ve snapshot state için **SQLite**.
- Source credential, token ve tam hassas URL için `ISecretStore` arkasında **DPAPI CurrentUser / Windows `LOCAL=user` data protection**.
- Database ve protected blob'lar packaged app'in device-local, cloud-backup dışı **LocalCache** alanında; küçük secretsiz tercihler ayrı local settings alanında.
- Raw credential, full remote playlist URL veya raw stream locator DB'nin açık kolonuna, index'ine ya da loguna yazılmaz.
- Xtream playback locator'ı mümkün olduğunda source secret + provider item key'den just-in-time üretilir. M3U locator'ları opaque `ProtectedLocatorReference` ile erişilen korumalı kayıtlardır.
- Credential ve remote-playlist locator kaydı source-configuration owner'ına; stream ve logo locator kaydı channel owner'ına bağlanır. Persisted owner metadata'sı authoritative olmalı ve protected-store çağrısına reference ile birlikte verilmelidir.
- Import staging snapshot'a bounded batch transaction ile yazar. Bütün referanslar ve sayımlar geçerse tek transaction aktif snapshot'ı değiştirir.
- WAL yalnız ölçülmüş concurrency yararıyla ve checkpoint/backup davranışı test edilerek açılır.
- Kaynak silme; kaynağı önce ağ/playback için devre dışı bırakır, protected secret/locator'ları siler, sonra katalog ve image cache'i temizler. Kısmi başarısızlık `DeletionPending` olur ve retry edilir.

Credential Locker primary değildir: roaming, bulk locator uyumsuzluğu ve full-trust user-locker isolation riski nedeniyle reddedilir; 20-record sınırı bu process modeli için tek başına karar gerekçesi değildir. Kendi kripto protokolü veya SQLCipher da ilk seçenek değildir. DPAPI-per-locator performansı bütçeyi geçmezse, güvenlik/hukuk review'undan geçen **DPAPI-wrapped per-source data-encryption key + standart authenticated encryption** ya da vetted encrypted database yeni karar olarak açılır; ad-hoc kripto uygulanmaz.

### M4 foundation implementation note — 2026-08-13

Application `ISecretStore` contract'ı ile Infrastructure `System.Security.Cryptography.ProtectedData 10.0.10` / `DataProtectionScope.CurrentUser` adapter'ı uygulanmıştır. İki ayrı versioned format vardır ve birbirine karıştırılmamalıdır:

- Source payload formatı v1 kalır. Xtream credential payload'ı `SRCRED01`, remote-playlist locator payload'ı `SRCLOC01` magic değerini taşır. Internal decoder exact magic/version, pozitif field length'leri, exact total/no-trailing-data, strict UTF-8 ve scalar/control/whitespace sınırlarını doğrular; yalnız numeric slice metadata'sı üretir.
- Protected-store formatı v2'dir. Binary envelope magic'i `SRCSEC02`, composition path'i `LocalCache\ProtectedStore\v2`, disk kayıt namespace'i `record-v2-*` ve temp namespace'i `temporary-v2-*` olur. Source, purpose, reference kind, opaque record ID ile owner kind/ID envelope context'e, entropy'ye ve dosya-adı digest'ine bağlanır; payload ve envelope buffer'ları bounded/zeroed tutulur.

`SourceDraftProtectionService`, local validation'dan geçen Xtream full locator+username+password veya remote-playlist full locator girdisini bounded, versioned ve length-prefixed source payload'ına kodlar. Her draft için oluşturduğu source-configuration owner'ını credential/remote-playlist protected kaydına geçirir; store-issued reference'ı yalnız başarılı create sonrasında aynı configuration'a bağlar ve geçici payload buffer'ını sıfırlar. Stream ve logo locator amaçları channel owner'ı gerektirir. Authoritative owner ile çağrıldığında owner-bound v2, aynı source/purpose içindeki bir referansın tek başına başka configuration/channel owner'ına takılmasını read/update bağlamında reddeder; yanlış-owner delete doğru owner kaydını silmez. Bu sınır owner + reference + endpoint tuple'ının birlikte değiştirilmesini, resolved locator origin equality'yi, durable authoritative owner metadata'sını veya bir authorization principal'ını kanıtlamaz.

`SourceConfigurationProtectedRecordDeletionService`, yalnız `DeletionPending` `ContentSource` aggregate'inin exact `SourceId` + configuration-owner + purpose + opaque-reference tuple'ını kullanarak tek credential veya remote-playlist locator kaydını idempotent siler. Ayrı caller-supplied key parçaları açmaz. Bu primitive durable status transition/admission gate/in-flight drain, channel record, metadata/cache veya source-wide orphan cleanup uygulamaz; tam source deletion orchestration'ı değildir.

`SourceChannelProtectedRecordDeletionService`, caller'ın durable olarak `DeletionPending`e persist edilmiş saydığı aynı `ContentSource` ile authoritative persistence'tan yüklenmesi gereken `PlaylistSnapshot` ve `LiveChannel` zincirini alır. Service bu provenance'i veya authorization'ı kurmaz; yalnız source→snapshot, snapshot→channel ve stable-key→source ilişkilerini fail-closed doğrular. Exact `SourceId`, `ChannelId` owner, stream/logo purpose ve opaque reference değerlerini aggregate'lerden türetip varsa protected stream'i, ardından optional logo kaydını idempotent siler. İlk başarılı commit'ten sonra yeni istenen cancellation ikinci exact delete denemesini kesmez; ikinci kayıtta kısmi failure oluşursa retry, store yeniden başarılı olduğunda aynı exact idempotent zincirin sonuca yakınsamasına izin verir. Configuration ve channel primitive'leri birlikte de source status persistence, admission gate, in-flight drain, bütün channel'ların enumeration'ı, metadata/cache temizliği, unknown-orphan reconciliation veya coordinator sağlamaz. Exact SDK `10.0.302` local worktree gate'i Debug/Release x64 build'leri 0 warning/error; architecture 13, unit 151 ve integration 34 olmak üzere 198/198 testi iki identical `Passed` setiyle; fixture determinism, sentinel fail/recovery, scanner CLI self-test ve artifact canary kontrolleriyle geçmiştir. Summary `commitSha=null` taşıdığı için commit-bound değildir. Implementation run `#20` commit'inden sonradır ve hosted execution'ı `UNVERIFIED`dır.

Doğrudan await edilen çağrıda store commit'inden sonra cancellation yeniden gözlenmez ve reference başarılı sonuçla döner; caller abandonment/retry, duplicate create, process crash/OOM ve metadata orphan reconciliation bu sınırın dışındadır. Public resolve consumer'ı M5 origin ve lifecycle gate'lerini bekler. Windows composition root factory'yi packaged launch'ta tam bir kez çağırır, başarılı v2 store'u uygulama ömrü boyunca tutar, initialization failure'da pencereyi oluşturmadan fail-closed olur ve fallback kullanmaz. Factory beklenen initialization hatalarını typed `StorageUnavailable` sonucuna eşler ve caller cancellation'ını korur. Adapter startup'ı yalnız exact v2 temp-namespace, regular ve en az 24 saatlik ciphertext artığını siler; fresh/future/lookalike/nested/`.dpapi` girdilerini korur, unsafe/silinemeyen exact entry'de ve 1.024 temp-aday cap'i aşıldığında cleanup mutation'ı yapmadan fail-closed davranır.

Pre-release v1 protected-store kayıtları bilerek korunur, fakat v2 store tarafından okunmaz veya silinmez. Released kullanıcı verisi ve güvenilir owner inventory'si olmadığından otomatik migration yapılmaz; bu, genel bir migration stratejisi kanıtı değildir. Hosted run `31735655363`, commit `6ac235f49ffd297196189e5e9441ad0c9e04f116` için architecture 12, unit 114 ve integration 27 olmak üzere 153/153 testi iki deterministik koşuda; signed package initialization smoke'unu ve coordinator'ı geçmiştir. Artifact'taki `ProtectedStoreDirectoryInitialized=true` yalnız o commit'in `LocalCache\ProtectedStore\v1` dizinini kanıtlar, owner-bound v2'yi kanıtlamaz. Decoder snapshot'ı 159/159 ve owner-v2 snapshot'ı 171/171 testi geçmiştir. Exact configuration-record deletion primitive'i dahil tarihsel local snapshot exact SDK ile architecture 13, unit 137 ve integration 33 olmak üzere 183/183 testi iki aynı setle geçmiştir. Channel deletion primitive'i dahil güncel local worktree snapshot'ı exact SDK ile Debug/Release x64 build'leri 0 warning/error; architecture 13, unit 151 ve integration 34 olmak üzere 198/198 testi iki identical `Passed` setiyle; fixture determinism, sentinel fail/recovery, scanner CLI self-test ve artifact-canary kontrollerini geçmiştir; summary `commitSha=null` taşır. 2026-08-14 GitHub Actions UI kaydı, run number `#15`in commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` için 3/3 işi ve üç artifact'ı başarıyla tamamladığını gösterir; bu tarihsel kayıt ilk owner-v2 quality/package/lifecycle geçişini workflow/UI düzeyinde **VERIFIED** yapar. Aynı tarihli takip UI kaydında run number `#16`, exact deletion primitive'ini içeren commit `3e806edd9a9482b87cccbf000ac8a81823562f6a` için 3/3 işi yeşil, üç artifact ve `8m24s` toplam süre gösterir; bu sonuç da yalnız workflow/UI düzeyinde **VERIFIED**dir. GitHub database run ID'leri, job log/test sayıları ve indirilen artifact JSON/ZIP/hash bağları doğrulanmamıştır. Arbitrary mid-I/O cancellation/interleaving ve cross-process sıralama kanıtlanmış değildir.

M4'ün ayrı nonpublishable package-lifecycle hostu, production application graph'ına test mode eklemeden aynı disposable identity/publisher, signer, package family ve CurrentUser altında iki signed test paketi üretir: baseline `0.0.1.0` ve update `0.0.2.0`. Baseline process'leri owner-bound v2 kaydı ile DPAPI-protected control ticket'ı oluşturur ve duplicate create'i reddeder. Ardından daha yüksek sürüm, force/downgrade yolu kullanılmadan normal `Add-AppxPackage -Path` ve exact dependency path'i ile aynı package family üzerine kurulur; Microsoft'un normal app-package update modeli aynı family ile artan sürüm ister [S89][S90]. Updated package process'i baseline kaydını okur; yanlış-owner read/delete negatiflerini, doğru-owner update/read/delete'i ve post-delete unavailable sonucunu doğrular. Exact `ProtectedStore` ile lifecycle run-directory canary scan'leri create, post-update ve final aşamalarında; exact package/app-data/certificate/output cleanup'ı success evidence öncesinde yürütülür. Package root'taki Windows-managed mutable registry hive'ları owned-write-surface taramasına dahil değildir. Test MSIX'leri artifact değildir; sanitized evidence schema v2 yalnız iki package sürüm/hash/signature alanını, same-signer/family, changed-full-name, update-installed, post-update read/scan ve lifecycle/cleanup Boolean'larını allowlist'ler. Bu lane production PFN update'i, reset/repair, uninstall-reinstall retention, second-user veya crash-recovery testi değildir. Kod/workflow tanımı PASS sayılmaz: run number `#15` tarihsel same-version lifecycle geçişini, run number `#16` ise yeni lane'den önceki exact deletion primitive commit'ini yalnız workflow/UI düzeyinde **VERIFIED** yapar. Run number `#18`, signed two-version lane'i ve package output-directory düzeltmesini içeren commit `bfe5960d5bc911e21ba1e1e1675deb575400e531` için hosted execution'ı workflow/UI düzeyinde **VERIFIED** yapar; evidence schema v2 artifact'ı indirilmediği için alan değerleri, ZIP içeriği ve tam hash bağı bağımsız doğrulanmamıştır.

Takip eden schema v3 kodu, updated disposable package'ta canlı protected record/ticket seed'inden sonra exact package full name ile reset uygular; version/family/full-name korunurken owned state'in manual cleanup öncesinde yokluğunu ve reset sonrası fresh create'in farklı record identity üretmesini ister. Yeniden canlı state oluşturulduktan sonra exact current-user uninstall preserve-data/`-AllUsers` yolu olmadan yapılır; registration ve exact package app-data yokluğu doğrulanır. Aynı signed `0.0.2.0` MSIX/dependency reinstall'i aynı package kimliği ve boş başlangıç state'iyle ikinci farklı record identity üzerinden fresh create/delete'i tamamlar. Microsoft reset'i app data'yı kalıcı silip fresh-install state'ine döndüren, repair'i veriyi koruyan ayrı davranış olarak tanımlar; clean uninstall package state'ini kaldırır [S91][S92]. Sanitized schema v3 yalnız reset/uninstall/reinstall/fresh-create ve identity-change sonuçlarını Boolean olarak ekler; raw path, PFN/full-name, record leaf, reference, source/owner veya kullanıcı bilgisi taşımaz. Run number `#20`, commit `6f30e3fa92f329c1fa26333c591edb5148261cec` için bu genişletmenin hosted execution'ını workflow/UI düzeyinde **VERIFIED** yapmıştır; indirilen schema-v3 artifact alanları, ZIP içeriği ve tam hash bağı bağımsız doğrulanmamıştır.

2026-08-14 run number `#19`, commit `71dbb0cf23467d978674976796e2bbceca01494a` için broad `PackageReset`/`PackageResetFailed` stage'inde fail olmuştur. Historical stage `Reset-AppxPackage` invocation'ı ile registration/identity/manifest postcondition'larını ayırmadığı ve HRESULT'ı allowlist evidence'a taşımadığı için exact failure bilinmez; `#19` native reset sonucu `UNVERIFIED` kalır. Takip kodu invocation, registration ve manifest validation'ı ayrı stable stage/code'lara böler; raw HRESULT/message/ActivityId yayımlamadan bilinen deployment HRESULT'ını allowlist'li failure `Code`una çevirir. Yalnız `0x80073D00`, `0x80073D02` ve `0x80073D05` quiescence kontrollü toplam en fazla üç bounded deneme alır [S93]. Bu retry seçimi resmi hata anlamlarından türetilen bir mühendislik politikasıdır (`INFERENCE`); `#20` UI kaydı retry'ın devreye girip girmediğini göstermez. Takip commit'i `6f30e3fa92f329c1fa26333c591edb5148261cec` için run number `#20` 3/3 işi yeşil, üç artifact ve `8m14s` toplam süreyle tamamlamıştır. Commit-bound package işindeki lifecycle betiği zorunlu olduğundan hardening ve schema-v3 reset/live-state uninstall/reinstall fresh-state zinciri workflow/UI düzeyinde **VERIFIED**dır; artifact içeriği bağımsız doğrulanmamıştır. `#19` tarihsel failure, `#18` update sonucu **VERIFIED** kalır.

Bu ara sonuç ADR'yi `Accepted` yapmaz. Source-draft operation'ı yalnız doğrudan await edilen çağrıda store create sonrası reference'ı başarılı sonuçla döndürür. Tarihsel same-version packaged harness, signed `0.0.1.0 → 0.0.2.0` disposable test-family update lane'i ve schema-v3 disposable reset/live-state uninstall-reinstall fresh-state lane'inin hosted geçişleri workflow/UI düzeyinde tamamlanmıştır; indirilen schema v2/v3 artifact içerikleri bağımsız doğrulanmamıştır. Production PFN lifecycle, repair, wrong-user ve 5k–50k karşılaştırmalı ölçüme ek olarak durable authoritative owner metadata'sı, owner + reference + endpoint tuple swap ve M5 resolved-origin politikası, source-wide delete/startup protected-record orphan reconciliation, deletion/in-flight coordination ve check-to-use yarışını kapatan handle-relative path/reparse kararı henüz yoktur.

Opt-in `IptvSuite.SecretStoreSpike` scaffold'u Release x64 üzerinde sentetik 256-byte locator workload'u için per-record DPAPI create, adapter restart, bounded read, delete ve cancellation-boundary ölçümlerini aggregate-only/canary-scanned evidence'a dönüştürür. Tarihsel v1 için 1.000 kayıtlık tek-tur smoke harness zincirini doğrulamıştır; dirty worktree sonucu decision-eligible değildir ve performans kararı sayılmaz. Sabit 5k/10k/20k/50k × 20 `Decision` koşusu clean commit üzerinde owner-bound v2 ile ayrıca çalıştırılmadan v2 file-per-record layout kabul edilmez. Spike unpackaged temp-root kullanır; packaged `LocalCache`, parser/normalize/index ve lifecycle maliyetlerini kapsamaz.

## Consequences and trade-offs

- Katalog normal SQL ile hızlı aranır; secret material ayrı güvenlik sınırında kalır.
- Aynı Windows kullanıcı bağlamındaki kötü amaçlı süreçlere karşı DPAPI tek başına sandbox değildir; OS account güvenliği ve app-data ACL'leri threat modelin parçasıdır.
- Çok sayıda DPAPI çağrısı import süresini artırabilir.
- LocalCache cloud backup/roaming yapmaz; cross-device sync yoktur.
- App reset/uninstall veri kaybıdır ve ürün metninde açık olmalıdır.
- Encrypted DB kadar geniş at-rest gizlilik sağlamaz; kanal adları/izleme tercihleri kişisel metadata kabul edilip minimizasyon ve silme uygulanır.

## Risks

- 50.000 protected locator'da süre, allocation veya DB büyüklüğü bütçesinin aşılması.
- Migration sırasında geçici plaintext veya orphan ciphertext.
- Bilinen tekil referanslar dışındaki source-wide record/temp orphan'ların mevcut contract üzerinden reconcile edilememesi.
- Durable metadata'nın yanlış owner taşıması veya owner + reference + endpoint tuple'ının birlikte değiştirilmesi; kriptografik owner binding tek başına bu yetki/origin problemini çözmez.
- Otomatik migrate edilmeyen pre-release v1 kayıtlarının orphan disk verisi olarak kalması.
- Yeni/öngörülmeyen store initialization hata tipinin safe result allowlist'i dışında raw OS exception üretmesi.
- Managed path/reparse kontrolü ile gerçek file open arasındaki check-to-use yarışı.
- Aynı-user process'in temp-aday cap'ini doldurarak protected-store initialization'ını fail-closed DoS'a dönüştürmesi.
- App identity/publisher değişiminde korumalı verinin açılamaması.
- Uninstall/reinstall/reset yaşam döngüsünün varsayılandan farklı olması.
- Secret silinip metadata ya da cache'in kalması veya tersi.
- DB corruption/WAL checkpoint sonrası aktif snapshot kaybı.

## Validation plan

M4/M8'de:

- create/read/update/delete, process restart ve yanlış user/context negatif testleri;
- source-draft validation-before-mutation, bounded encoding, post-create reference attach/cancellation ve safe failure testleri;
- canary secret ile DB, WAL, SHM, app data, log ve crash artifact binary scan;
- source-configuration ve channel owner matrisi, cross-owner read/update reddi ve yanlış-owner delete'in doğru kaydı koruması;
- signed packaged launch'ta exact package-family `LocalCache\ProtectedStore\v2` directory initialization evidence'i;
- v1 namespace'in v2 tarafından okunmaması/silinmemesi ve açık migration kararı;
- app reset, clean uninstall/reinstall, production signed package update ve package identity değişimi;
- 5k/10k/20k/50k protected locator benchmark'ı; allocation, working set ve cancellation;
- crash/fault injection ile staging, activation, deletion ve migration recovery;
- 20/21 hesap senaryosuyla Credential Locker kararının tekrar kontrolü;
- source silme ve startup orphan reconciliation contract testleri.

M4 bitmeden exact encryption layout `Accepted` yapılmaz. Performans alternatifi seçilirse threat model, key rotation, nonce uniqueness, authenticated encryption ve recovery için ayrı security review gerekir.

## Revisit triggers

- DPAPI throughput'u 50.000-item bütçesini geçemezse.
- Cloud/cross-device sync, backup/export veya multi-user paylaşım ürüne girerse.
- Compliance tüm katalog metadata'sının şifrelenmesini isterse.
- Windows App SDK/.NET data-protection API veya MSIX lifecycle davranışı değişirse.
- First-party account/backend ya da telemetry eklenirse.
- Credential Locker roaming davranışı ürün tercihi haline gelir ve account sayısı açıkça 20'nin altında sınırlandırılırsa.

## References

[S33–S38, S63–S65, S89–S90](../research/SOURCES.md)
