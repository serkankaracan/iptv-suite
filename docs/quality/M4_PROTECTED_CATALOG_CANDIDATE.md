# M4 protected catalog aday kararı

**Tarih / kaynak erişimi:** 2026-08-14

**Durum:** test-only immutable-container comparative `Decision` `VERIFIED` ve comparative gate `CLOSED`; container production için `NO-GO`; aynı SQLite transaction-domain'i yönü M8 için `Proposed` ve uygulanmamış; M4 foundation `COMPLETED`; ADR-003 `Proposed`

## 1. Sonuç

50.000 channel locator için file-per-record DPAPI düzeni önceki `Decision` koşusunda reddedilmiştir. Test-only immutable-container adayı aynı workload bağıyla karşılaştırılmış ve performans/correctness karşılaştırma gate'ini kapatmıştır. Bu kayıt yine de onun yerine production implementation seçmez.

| Seçenek | M4 kararı | Gerekçe |
|---|---|---|
| DPAPI-wrapped source/snapshot DEK + record-başına AES-256-GCM kullanan **test-only immutable container** | Comparative `Decision` `VERIFIED`, gate `CLOSED`; production `NO-GO` | 50k create-and-activate p95 `173,128 ms`, allocation p95 `34,676 MiB` ve staging cancellation p95 `5,570 ms` gözlendi; aynı workload baseline'ına göre süre `874,766×`, allocation `19,264×` iyileşti. Dış katalog metadata'sı ve active pointer ile atomik commit sağlamaz. |
| Aynı key-state ve ciphertext row'larını tek SQLite veritabanında yöneten transactional state machine | M8 için tercih edilen `Proposed` yön; uygulanmamış | Tek transaction old-or-new activation ve recovery için doğru yöndür; fakat exact schema/provider/dependency, crash matrix, migration, delete ve end-to-end 50k ölçümü seçilmemiştir. Mevcut container prototipi bu veritabanı değildir. |
| SQLCipher Commercial/Enterprise | Yalnız gelecekte conditional candidate; M4 `NO-GO` | Zetetic'in resmî .NET paket, native binary, optimizasyon ve support yolu vardır. Procurement/lisans, exact binary provenance, MSIX, migration, key handling, performance ve lifecycle kanıtı olmadan eklenmez. |
| SQLCipher Community veya public `SQLitePCLRaw.*e_sqlcipher*` | `NO-GO` | Community kullanımı lisansça mümkün olsa da bu repo için self-build/native servicing/provenance ve performans yükü açar. Zetetic, `e_sqlcipher` paketlerini üçüncü taraf, desteklenmeyen ve eski binary taşıyan paketler olarak tanımlar; resmî .NET yolu değildir [S104][S105]. |
| SQLite SEE | `NO-GO` | Ayrı ücretli native source/build/link/lisans yüzeyidir; M4 için vetted .NET package, packaged lifecycle veya comparative evidence yoktur [S106]. |

**Net karar:** encrypted database M4 production kapsamına alınmaz. SQLCipher Commercial yalnız procurement ve dependency review sonrasında yeniden açılabilecek bir adaydır. Fresh açık onayla tamamlanan immutable-container `Decision` comparative soruyu kapatmıştır; fakat container production için `NO-GO`dur. Exact hash, ölçüm ve sınırlamalar [M4 protected catalog Decision kanıtında](M4_PROTECTED_CATALOG_DECISION_EVIDENCE.md) kayıtlıdır.

## 2. Dondurulan comparative contract

Bu bölüm production formatı değil, farklı implementasyonların aynı şeyi ölçmesini sağlayan spike sözleşmesidir.

- Her import **attempt/snapshot** için `RandomNumberGenerator.Fill` ile fresh 256-bit DEK üretilir. Key bir source'a ve snapshot generation'a aittir; source ömrü boyunca tekrar kullanılan master key değildir.
- DEK, `ProtectedData` + `DataProtectionScope.CurrentUser` ile attempt başına bir kez korunur; row başına `Protect` çağrısı yapılmaz. Pre-activation reopen ve ölçülen sonraki adapter reopen ayrı ayrı `Unprotect` yapar. Optional entropy sabit domain separator + magic/version + `SourceId` + `SnapshotId` + `KeyGenerationId` + purpose bağlamından canonical üretilir. Bu binding authorization değildir [S98].
- Her locator bağımsız `AesGcm(key, 16)` çağrısıyla şifrelenir: 32-byte key, 12-byte/96-bit nonce, 16-byte/128-bit tag. `AesGcm.IsSupported` false ise fail-closed; daha zayıf veya ad-hoc fallback yoktur [S96].
- Her record nonce'u CSPRNG ile üretilir ve aynı attempt içindeki 96-bit değerleri tutan `HashSet` üzerinde collision kontrolü geçmeden kullanılmaz. Çakışmada yeniden üretilir. Sayaç seçilmez: crash-safe monotonic reservation, çoklu writer ownership ve power-loss sonrası tekrar etmeme kanıtı olmayan persisted counter daha büyük state protokolü gerektirir [S99].
- Fault/cancellation sonrası staging encryption **resume edilmez**. Container/key generation terk edilir; yeni deneme fresh DEK ve boş nonce setiyle başlar. Active generation yalnız decrypt edilir. Böylece process restart'ın görmediği nonce'ları aynı key altında tekrar üretme yolu kapatılır.
- Spike key başına en çok 50.000 encryption invocation yapar; limit formatta kayıtlı ve fail-closed'dur. Bu sınır, NIST RBG-based IV construction için key başına verdiği toplam `2^32` invocation üst sınırının çok altındadır; tek başına compliance/FIPS iddiası değildir [S99].
- Source credential ve remote-playlist credential kaydı mevcut düşük-cardinality owner-bound `ISecretStore`/DPAPI yolunda kalır. Bulk alternatif yalnız channel stream/logo locator kapsamındadır.

## 3. Format, AAD ve doğrulama

Test-only v1 container big-endian, versioned ve exact EOF isteyen canonical binary formattır. Sabit 112-byte header; magic/version, header ve wrapped-DEK uzunlukları, bounded record count, 80-byte index-entry boyutu, sentetik channel-locator purpose'u, ciphertext başlangıcı, `SourceId`, `SnapshotId`, `KeyGenerationId`, AES-GCM/DPAPI algorithm ID'leri ve key/nonce/tag boyutlarını taşır. Bunu bounded variable-length DPAPI-wrapped DEK, her kayıt için 80-byte index entry ve tek contiguous ciphertext bölgesi izler. Index entry; `ChannelId` rolündeki owner ID, opaque protected-reference ID, canonical ordinal, plaintext/ciphertext uzunluğu, exact contiguous offset, nonce ve tag taşır. Prototype owner/reference kind alanlarını ayrı encode etmez; bunlar tek sentetik channel-locator workload'unda implicit'tir. Footer veya container digest yoktur; strict bounds, contiguous offset ve exact EOF yapıyı kapatır.

AAD, değişmez canonical byte dizisi olarak şunları bağlar:

- magic/domain, format/schema version ve AES-GCM algorithm ID;
- `SourceId`, `SnapshotId`, `KeyGenerationId` ve canonical record ordinal;
- sentetik channel-locator purpose'u, `ChannelId` rolündeki owner ID ve opaque protected-reference ID;
- plaintext length ile snapshot record count.

Kanal adı, mutable display sırası/metadata veya raw URL AAD'ye girmez. Reader; header ve boyutları, enum/algorithm ID'lerini, count'u, ordinal ve contiguous offset'leri, duplicate owner/reference/nonce'u ve exact EOF'u açılışta bounded olarak doğrular. Decrypt/tag mismatch dahil container ve cryptographic kusurlar context-free `InvalidDataException` sonucuna normalize edilir; raw cryptographic exception/message/path evidence'a girmez. Activation öncesinde wrapped DEK açılır ve en çok 16 evenly-spaced record tag'i doğrulanır; bu full snapshot AEAD scan değildir. AAD, test edilen cross-source/snapshot/key-generation/purpose/owner/reference ve index-tuple değişikliklerini saptar; bütün eski container + metadata çiftinin birlikte rollback/replay edilmesini veya authoritative owner doğruluğunu kanıtlamaz [S96][S99].

Owned DEK, plaintext locator, encode/decode ve temporary AAD buffer'ları en dar ömürle tutulur ve `finally` içinde `CryptographicOperations.ZeroMemory` ile sıfırlanır. `AesGcm.Dispose` çağrılır. Managed owned buffer temizliği doğrulanabilir; runtime/OS iç key schedule'ının kesin zeroization'ı iddia edilmez [S97].

## 4. Crash ve activation sınırı

### Test-only container

Spike aynı dizinde fresh staging file oluşturur; header, index ve contiguous ciphertext bölgelerini yazar, diske flush eder, strict structural reopen + DPAPI unwrap + en çok 16 evenly-spaced AEAD tag probe uygular ve ancak sonra fixed active adına `File.Move(..., overwrite: true)` ile taşır. Controlled fault/cancel ve gözlenen managed exception yollarında staging active sayılmaz, resume edilmez ve exact cleanup edilir. Bu in-process hook modeli process kill, OS crash veya power-loss atomikliği kanıtlamaz. Crypto + sequential persistence maliyetini karşılaştırmak içindir.

Atomic rename tek başına production snapshot activation değildir. Katalog SQLite metadata'sı, active snapshot pointer'ı, wrapped key state'i ve container ayrı dosyalarda kalırsa process/power failure bunları farklı generation'larda bırakabilir. SQLite'ın tek transaction atomikliği ayrı container'a uzanmaz; hot journal/WAL dosyalarını manuel ayırmak da güvenli değildir [S102]. Bu **cross-file metadata atomicity** açığı production `NO-GO` blocker'ıdır.

### Gelecekte tercih edilen SQLite state machine

Production adayı, wrapped key generation, encrypted rows, snapshot metadata ve active pointer'ı aynı SQLite DB/transaction domain'inde tutmalıdır:

1. `staging/encrypt-only` generation fresh key ile oluşturulur.
2. Ciphertext row'ları bounded batch transaction'larla yazılır; okuyucu yalnız mevcut `active/decrypt-only` generation'ı görür.
3. Exact count/hash ve foreign-key doğrulamasından sonra tek transaction yeni snapshot/key'i `active/decrypt-only` yapar, source active pointer'ını değiştirir ve eski generation'ı `retired` yapar.
4. Crash old veya new active state bırakmalıdır; mixed snapshot, key'siz active generation veya staging serve/resume yasaktır.
5. Startup reconciliation incomplete staging'i abandon eder; active key ile yeni encryption yapmaz.

İlk spike bu veritabanını uygulamaz veya doğrulamaz. Rollback-journal `DELETE` + `synchronous=EXTRA` başlangıç adayıdır; WAL ancak concurrency/checkpoint/backup/crash ve plaintext-canary ölçümünden sonra değerlendirilir [S102].

## 5. Rotation ve deletion

Her başarılı refresh fresh snapshot generation/key olduğundan rotation refresh ile doğal olarak gerçekleşir. Key compromise/corruption tek record yerine o source snapshot'ının bütün locator'larını etkiler; kısa generation ve key başına 50k cap blast radius'i sınırlar [S100]. Shared DEK korunurken tek channel silmek cryptographic erase değildir.

Source delete hedefi:

1. durable `DeletionPending`, yeni resolve/network/playback admission'ını kapatma;
2. in-flight decrypt ve plaintext lease'lerini drain edip owned buffer'ları sıfırlama;
3. active pointer'ı ayırma ve bütün wrapped DEK generation'larını erişilemez kılma;
4. ciphertext/metadata/cache'i bounded ve idempotent cleanup ile silme;
5. startup reconciliation ile yarım silmeyi tamamlama.

Bir wrapped key'i null etmek uygulama düzeyinde crypto-unavailability sağlar; SQLite journal/free page, filesystem, backup veya storage snapshot içindeki bütün kopyaların sanitization'ını kanıtlamaz. Bu nedenle “NIST secure erase” denmez [S101].

## 6. Performans modeli

`.NET AesGcm` one-shot API'dir; tek 50k-record plaintext blob yerine her bounded locator ayrı AEAD message olur. Sequential container yazımı batch I/O sağlar. Sabit wire overhead record başına 12-byte nonce + 16-byte tag, yani 50.000 kayıtta yaklaşık 1,4 MB'dır; header/index/length alanları ve allocator maliyeti ayrıca ölçülür.

Comparative workload önceki evidence ile aynı generator/seed/payload ve workload hashlerini; Decision modunda 5k/10k/20k/50k × 20, Release x64, exact SDK ve sentetik 256-byte corpus'u kullanır. Mevcut runner şu ölçümleri üretir:

- create-and-activate: fresh DEK, bir DPAPI `Protect`, AES-GCM/nonce-set, sequential write, flush, structural reopen + bir `Unprotect` + en çok 16 tag probe ve file activation'ın birleşik süresi;
- aynı process içinde fresh store adapter oluşturup reader açma + bir DPAPI `Unprotect`; bu cold process restart değildir;
- en çok 256 evenly-spaced deterministic read probe;
- iki idempotent çağrıyla bir snapshot delete; operations/sample `1`, ayrıca records-covered alanı;
- raw samples ile p50/p90/p95/max/CV, allocation/GC, yalnız before/after boundary working set ve signed boundary delta, container byte boyutu ve crypto operation count'ları;
- controlled pre/post-activation fault/cancellation matrisi ile 50k nominal staging attempt'ini tam 10 encrypted record sonrasında kesen request→completion probe'u. Bu progress **encrypted staging record** sayısıdır; committed record değildir ve arbitrary asynchronous mid-DPAPI/I/O cancellation kanıtı değildir.

Runner variable/worst-case locator corpus, full snapshot scan, cold process restart, source-delete key-inaccessible latency veya background cleanup ölçmez. Bunlar production adayı için ilerideki ayrı gate'lerdir.

Referans bütçeler: normalize + protected persistence + index p95 `≤3,0 s`; end-to-end p95 `≤5,0 s`; whole-import allocation `≤150 MiB`; working-set delta `≤250 MiB`; cancellation `≤250 ms`. Container spike bu bütçeyi geçse bile parser/normalize/index/UI veya packaged `LocalCache` end-to-end PASS'i kanıtlamaz.

Clean commit `7cd5bf739712a5cbdcf47634275a113faab8c48e` üzerinde tamamlanan 5k/10k/20k/50k × 20 `Decision` **VERIFIED**dır. 50k create-and-activate p95 `173,128 ms`, allocation p95 `34,676 MiB`, boundary working-set delta p95 `15,855 MiB`, adapter reopen+unwrap p95 `13,231 ms`, 256-read p95 `1,695 ms`, delete p95 `2,321 ms` ve staging cancellation p95 `5,570 ms` oldu. Post-request encryption/activation/mutation/temp üst sınırları `0`; 22 reader-validation Boolean'ı, canary ve cleanup geçti. Aynı workload baseline'ına göre create `874,766×`, allocation `19,264×` iyileşti. Runner threshold uygulamadığından bu, component değerleri üzerinden comparative gate'i kapatan **INFERENCE**dır; birleşik veya end-to-end ürün PASS'i değildir.

## 7. Negative/fault matrisi ve sınırı

Local Smoke/Decision correctness matrisi; wrong source/snapshot/key-generation/purpose/owner/reference binding, magic/version/header length/count/record length/offset/overlap, duplicate owner/reference/nonce, authenticated index tuple, cross-container wrapped-DEK swap, ciphertext/tag ve trailing-byte bozulmalarını context-free fail-closed sonuçla sınar. Writer nonce-collision branch'i kontrollü collision injector ile retry edilerek, delete iki kez çağrılarak doğrulanır. Pre-cancel, exact pre-activation cancel/fault, post-activation committed fault/cancel ve 50k nominal staging/after-10-encrypted-record cancellation yollarında önceki active state, activation sonucu, post-completion mutation ve temporary artifact invariant'ları kontrol edilir. Canary taraması owned work/evidence yüzeylerinde sentetik plaintext sızıntısını arar.

Bu matris process kill/power loss, wrong Windows user, whole-container replay, complete metadata+container rollback, production package lifecycle, full snapshot tag scan, source delete/reconciliation veya DB/journal/WAL/SHM recovery kanıtı değildir. Bunlar tek bir pre-code gate değildir: wrong-user M4'te tamamlanmıştır; durable source lifecycle/reconciliation M8; transactional recovery/replay ve end-to-end katalog M8; production PFN lifecycle/repair/identity M15 takipleridir.

## 8. Milestone takip kapıları

M8 production persistence kodu başlamadan ilk üç tasarım kararı kapanmalı; kalan maddeler ilgili milestone kabul kanıtlarıdır:

1. Canonical format/AAD/key-state/source-delete ve replay threat-model security review'u.
2. Production persistence'ın tek SQLite transaction domain'i ve exact provider/dependency/lisans kararı.
3. Cross-file activation blocker'ının kaldırılması; container + ayrı DB production çözümü olarak kabul edilmez.
4. M8 crash/fault, migration, rollback/replay ve DB/journal/WAL/SHM recovery kanıtı.
5. Tamamlanan comparative `Decision` sonrasında gerçek parser/normalize/index end-to-end referans-cihaz koşusu.
6. SQLCipher Commercial yeniden açılırsa procurement, license grant, resmî package digest/signature, SBOM/notice, native servicing, MSIX ve migration review'u.
7. Ciphertext length leakage/padding product kararı; bu spike padding seçmez.

M4 gerçek ikinci kullanıcı ile source admission/drain, source-wide deletion ve startup reconciliation'ı; M15 production PFN lifecycle/repair/identity ile Store kabulünü ayrıca kapatır.

Repository'de ayrı, nonpackable/nonpublishable `IptvSuite.ProtectedCatalogSpike` proje/runner implementation'ı bulunur. Normal quality/hosted workflow onu çalıştırmaz; production graph ve production MSIX denylist ile ayrı tutulur. 2026-08-14 local worktree Smoke'u exact SDK `10.0.302` ile locked restore ve Release x64 build'i 0 warning/error tamamlamış; 1.000-record/tek-tur workload, fault/cancellation, 22 reader-validation Boolean'ı, nonce-collision retry, canary ve cleanup kontrollerini geçmiştir. Evidence `20.469` byte ve SHA-256 `9d52172e61416fa1c73206ec745bd72a5aa18f15844153b08026c4597494cb99`; spec `2693eed7032d4c8ea50a062cd8b123ba782a89293e9d31b079335d3f6aa15666`, lock `fa138ce4203c6967edaeb8031722d72fd8372f1ed08bd9a52dff6342b582e730` ve runner `a7697286871d17a96abe4c238975c57bd5bc1878c70735ead632d963c7989cf3` hashlerini bağlar. Worktree dirty ve `decisionEligible=false` olduğundan bu yalnız correctness/harness Smoke kanıtıdır; commit-bound veya performance kararı değildir. Targeted architecture guard'ları 14/14 geçmiştir. Aynı worktree'de ayrıca exact SDK ile Debug+Release x64 full quality gate; architecture 14, unit 151 ve integration 34 olmak üzere 199/199 testi iki aynı `Passed` setiyle, fixture determinism, sentinel fail/recovery, scanner CLI self-test ve artifact-canary kontrolleriyle geçmiştir. `19.158` byte quality summary SHA-256 `d9f52bb16965afe60eb46ff9ee50edbce2191ef05af46e734ed8c3be6b75640a` ve `commitSha=null` taşır; bu nedenle sonuç commit-bound değildir.

Sonraki clean-commit `Decision`, exact SDK `10.0.302` ile commit `7cd5bf739712a5cbdcf47634275a113faab8c48e` üzerinde `decisionEligible=true` tamamlanmıştır. `209.828` byte summary SHA-256 `4a219e802dcff959644bda42489f0ee5231be63df1eb37d034e2ed16ff0859de`, global workload SHA-256 `eb6a4eaaecf437e80ef01feb00c6d1453e41994682a76ed08f81c1808a372f3f` değerindedir. Bu local evidence comparative performans/correctness gate'ini kapatır; hosted run veya production acceptance değildir. Ayrıntı: [M4 protected catalog Decision kanıtı](M4_PROTECTED_CATALOG_DECISION_EVIDENCE.md).

## 9. Hosted UI kaydının sınırı

Run number `#24` kullanıcı tarafından sağlanan GitHub Actions UI kaydında docs commit'i için 3/3 işi yeşil, üç artifact ve `9m02s` toplam süre gösterir. Bu yalnız hosted workflow/UI düzeyinde docs-commit geçişidir; immutable-container implementation'ı, AES-GCM/nonce correctness'i, cryptographic evidence, benchmark `Decision` veya production seçimi değildir.

Run number `#25` kullanıcı tarafından sağlanan GitHub Actions UI kaydında `test(windows): spike protected catalog layout` başlıklı commit `786151cef6c60de5e34009ebb6643ec804490ee9` için 3/3 işi yeşil, üç artifact ve `8m32s` toplam süre gösterir. Commit-bound normal workflow protected-catalog projesini solution'ın Debug/Release x64 build'lerinde derler; architecture guard'ları production graph ayrımını ve `Invoke-WindowsProtectedCatalogSpike.ps1` çağrısının normal quality/hosted akışta bulunmamasını denetler, package smoke ise çıkarılmış production MSIX payload'ında `IptvSuite.ProtectedCatalogSpike*` adlarını denylist ile reddeder. Bu nedenle hosted compile, isolation ve package-exclusion sonucu workflow/UI düzeyinde **VERIFIED**dır. Normal üç artifact candidate evidence değildir; workflow aday executable'ının `Smoke` veya `Decision` modunu çalıştırmamıştır. Job log/test sayıları ile indirilen JSON/ZIP/içerik/tam digest bağları bağımsız doğrulanmadığından run, AES-GCM/nonce/fault correctness'i, comparative performansı veya production seçimini kanıtlamaz. Comparative `Decision` yalnız yukarıdaki ayrı local exact-hash evidence ile `VERIFIED`dır; M4 foundation `COMPLETED` ve ADR-003 `Proposed` kalır.

## 10. Kanıt sınıflandırması

- **VERIFIED:** Resmî .NET API boyutları/semantiği, DPAPI scope'u, NIST final guidance, SQLite transaction/recovery modeli ve Zetetic'in resmî package/lisans ayrımları [S96–S106]; exact-hash bağlı dirty-worktree local Smoke correctness/harness sonucu; clean-commit comparative `Decision` raw sample/aggregate, 22 reader-validation Boolean'ı, fault/cancellation, canary ve cleanup sonucu; run `#25` için yalnız commit-bound hosted compile, architecture isolation ve production-package exclusion zinciri.
- **INFERENCE:** Immutable-container adayının baseline performans blocker'ını kaldırarak comparative gate'i kapatması; container'ın cross-file atomicity nedeniyle production `NO-GO` kalması; fresh key + in-attempt collision rejection + no-resume modelinin persisted counter'dan daha küçük crash state'i oluşturması; aynı SQLite transaction-domain'inin M8 için tercih edilmesi; Community/SEE entegrasyon yükünün bu milestone'a uygun olmaması.
- **UNVERIFIED:** Run `#25` job log/test sayıları ve indirilen artifact JSON/ZIP/içerik/tam digest bağları; end-to-end parser/normalize/index performansı, packaged lifecycle/wrong-user, crash/power-loss matrisi, production schema/provider, source deletion, migration ve SQLCipher Commercial procurement.

## Kaynaklar

[S35–S38, S85, S96–S106](../research/SOURCES.md)
