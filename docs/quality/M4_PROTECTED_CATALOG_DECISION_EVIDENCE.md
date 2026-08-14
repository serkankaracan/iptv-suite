# M4 protected catalog Decision kanıtı

**Tarih:** 2026-08-14

**Durum:** comparative `Decision` workload `VERIFIED`; comparative gate `CLOSED`; test-only immutable container production için `NO-GO`; aynı SQLite transaction domain'i yönü M8 için `Proposed` ve uygulanmamış; M4 `IN PROGRESS`; ADR-003 `Proposed`

## 1. Sonuç

`eng/Invoke-WindowsProtectedCatalogSpike.ps1 -Mode Decision -AllowDecision` koşusu clean commit `7cd5bf739712a5cbdcf47634275a113faab8c48e` üzerinde sıfır exit code ile tamamlandı. Bu sonuç sabit 5k/10k/20k/50k × 20 workload'un, correctness/fault/cancellation matrisinin, canary kontrollerinin ve cleanup'ın sözleşmeye uygun tamamlandığını **VERIFIED** yapar. Runner performans threshold'u uygulamaz; sıfır exit code tek başına ürün bütçesi PASS'i değildir.

50k create-and-activate p95 `173,128 ms`, managed allocation p95 `36.360.200 byte` (`34,676 MiB`) ve before/after working-set boundary delta p95 `16.625.664 byte` (`15,855 MiB`) oldu. Aynı workload hash'ine bağlı reddedilmiş file-per-record DPAPI baseline'ında create p95 `151,447 s`, allocation p95 `668,007 MiB` idi. Aday sırasıyla `874,766×` hızlanma / `%99,886` süre azalması ve `19,264×` / `%94,809` allocation azalması gösterdi. Bu nedenle immutable-container'ın karşılaştırmalı crypto + sequential persistence sorusunu kapattığı kararı **INFERENCE**dır.

Bu olumlu karşılaştırma container'ı production tasarımı yapmaz. Ayrı container ile katalog metadata/active pointer arasında tek transaction yoktur; crash/power-loss, packaged lifecycle, wrong-user, reconciliation ve end-to-end parser/normalize/index kanıtı açık kalır. Container production için `NO-GO`dur. DPAPI-wrapped snapshot DEK, record-başına AES-256-GCM ciphertext, snapshot metadata ve active pointer'ın aynı SQLite transaction domain'inde tutulması tercih edilen **M8 `Proposed` yönüdür**; uygulanmış veya doğrulanmış değildir. M4 `IN PROGRESS`, ADR-003 `Proposed` kalır.

## 2. Değişmez kanıt bağı

| Alan | Değer |
|---|---|
| Repository commit | `7cd5bf739712a5cbdcf47634275a113faab8c48e` |
| Başlangıç repository durumu | clean; `isDirty=false`; `decisionEligible=true` |
| Evidence schema / mode | `1` / `Decision` |
| Candidate / scope | `immutable-protected-catalog-container-v1` / test-only one-container-per-source-snapshot |
| Summary path | `.artifacts/m4-protected-catalog-spike/evidence/decision-summary.json` |
| Summary byte length | `209.828` |
| Summary SHA-256 | `4a219e802dcff959644bda42489f0ee5231be63df1eb37d034e2ed16ff0859de` |
| Global workload SHA-256 | `eb6a4eaaecf437e80ef01feb00c6d1453e41994682a76ed08f81c1808a372f3f` |
| Specification SHA-256 | `2693eed7032d4c8ea50a062cd8b123ba782a89293e9d31b079335d3f6aa15666` |
| Synthetic license SHA-256 | `0ee38448ce47fb7c98e56984a84819138e0f7eec085b03d75607d3f5f1d0dba3` |
| Package lock SHA-256 | `fa138ce4203c6967edaeb8031722d72fd8372f1ed08bd9a52dff6342b582e730` |
| Runner assembly SHA-256 | `293897de526def889b902b2474dbffd924ab4cc80b9c5ca654791578b8dd80cc` |
| Testing assembly SHA-256 | `d3b701edae7112dc5edf4d11409024acdbd0230c2278dec6d06ca5de9663e8ba` |
| Runner deps JSON SHA-256 | `6a8972d894f1dd0604e0f02b820f34678cb9378af3b8e73037f8571118a32267` |
| SDK / runtime | `.NET SDK 10.0.302` / `.NET 10.0.10` |
| Host | Windows `10.0.26200.0`, OS/process `X64`, 24 logical processor |
| Configuration / protection | `Release`, `x64`, DPAPI `CurrentUser` |

Kalıcı kayıt summary dosyasını repository'ye eklemez; commit ve hash zinciri daha sonra elde edilen exact local evidence dosyasını doğrulamak içindir. Runner, Testing ve deps hashleri çalıştırılan managed yüzeyi bağlar; reproducible build, native runtime'ın bütün transitif byte'ları veya bağımsız imzalı benchmark artifact'ı olduklarını kanıtlamaz.

Payload 256 byte, sentetik ve deterministiktir: generator `IptvSuite.ProtectedCatalogSpike.DeterministicPayloadGenerator`, baseline generator `IptvSuite.SecretStoreSpike.DeterministicPayloadGenerator`, algorithm version `1`, seed `20260813`. Evidence; üçüncü taraf içerik, kişisel veri, gerçek credential veya yetkisiz medya içermediğini kaydeder. `LicenseRef-IPTVSuite-Synthetic-Test-Only` durumu `UNVERIFIED`dır ve public redistribution hakkı sayılmaz.

Candidate summary baseline'ı ayrıca commit `fc96a211171d1e4f5e5f02174da6c565ef2d59bb`, spec `0447355215f8c744340a39640e55bc798916638b48e5386b213e7d3f06c7a568`, runner `3df0676151a906f815bd0881994ffd3f7f347f2f7121a494409f85afcdeca119`, summary `8cd4c6d86b813fd07794217a71a824e7368694363f89a16be36cb8a311d67460` ve evidence-record commit `207455a54d2d7ac9b6b5c1ce8eb5e29bbee0c383` ile bağlar.

Her scale'in workload hash'i baseline Decision ile aynıdır:

| Kayıt sayısı | Workload SHA-256 |
|---:|---|
| 5.000 | `80f110a11351dd95b3489f0a8973cc826f096334da0b4363e1d4b24e98082fe1` |
| 10.000 | `c4084013d6205597e412d47ec65329b8d671b9e0edb551a6d29c54cf34cd1512` |
| 20.000 | `94bb81ddc7d2afe6fc4b2935dd9d2dec5f1bf8e80b5444cf90e8e860b9512c86` |
| 50.000 | `88b5fad60d89e2fb6c16e9dac1a3372abb0779cdd216424833555b8f906ab232` |

## 3. VERIFIED gözlemler

Warmup ölçüm dışı tamamlandı. Her scale 20 iteration çalıştı. Create-and-activate; fresh DEK üretimi, tek DPAPI `Protect`, record-başına AES-256-GCM, nonce duplicate rejection, sequential write/flush, structural reopen + DPAPI `Unprotect` + en çok 16 tag probe ve file activation'ın birleşik sınırıdır.

| Kayıt | p50 | p90 | p95 | max | CV | Allocation p95 | Boundary WS delta p95 | Disk p95 |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 5.000 | `18,174 ms` | `21,095 ms` | `21,829 ms` | `27,751 ms` | `0,135` | `3.788.240 B` | `1.208.320 B` | `1.680.374 B` |
| 10.000 | `30,772 ms` | `33,730 ms` | `33,930 ms` | `34,798 ms` | `0,061` | `7.306.480 B` | `1.044.480 B` | `3.360.374 B` |
| 20.000 | `61,951 ms` | `70,275 ms` | `71,232 ms` | `78,037 ms` | `0,091` | `14.609.952 B` | `4.464.640 B` | `6.720.374 B` |
| 50.000 | `154,118 ms` | `168,110 ms` | `173,128 ms` | `178,638 ms` | `0,065` | `36.360.200 B` | `16.625.664 B` | `16.800.374 B` |

50k scale için diğer p95 gözlemleri:

| Ölçüm | p95 | Sınır |
|---|---:|---|
| Fresh adapter reopen + DPAPI unwrap | `13,231 ms` | Aynı process; cold process restart değil |
| 256 evenly-spaced read probe | `1,695 ms` | Full 50k scan/query değil |
| Snapshot delete | `2,321 ms` | Sample başına bir snapshot operasyonu; iki idempotence çağrısı; 50k record covered |
| Staging cancellation request→completion | `5,570 ms` | 50k nominal attempt, 10 encrypted staging record sonrasında |
| Controlled boundary cancellation request→completion | `0,220 ms` | In-process exact hook |

50k'nın 20 iteration'ında 20 DPAPI wrap, 40 toplam unwrap, 320 pre-activation tag probe ve 1.000.000 nonce gözlendi; doğal nonce collision retry sayısı `0` oldu. Kontrollü injector collision branch'ini bir retry ile ayrıca geçirdi. Her iteration'da protected-store canary scan'i geçti; summary publish öncesi evidence scan'i ve final cleanup geçti.

Fault/cancellation matrisi 20 örnekte pre-cancel no-mutation, pre-activation cancel/fault prior-active koruması ve post-activation committed sınıflandırmasını geçti. Staging cancellation'da request sonrasında encrypted-record üst sınırı `0`, activation `0`, completion-sonrası mutation `0` ve temporary artifact `0` oldu. Progress birimi committed record değil, encrypted staging record'dur.

Reader validation'ın 22 Boolean'ı geçti: source/snapshot/key-generation/purpose/owner/reference binding; magic/version/header/count/length/offset/overlap; duplicate owner/reference/nonce; authenticated index tuple; cross-container wrapped-DEK swap; tag/ciphertext ve trailing-byte corruption; injected nonce collision retry ve idempotent delete.

## 4. Baseline karşılaştırması ve karar sınırı

| 50k ölçümü | File-per-record DPAPI baseline | Immutable-container adayı | Karşılaştırma |
|---|---:|---:|---:|
| Create p95 | `151.446,536 ms` | `173,128 ms` | `874,766×` hızlanma; `%99,886` azalma |
| Managed allocation p95 | `700.456.336 B` | `36.360.200 B` | `19,264×` az; `%94,809` azalma |

**VERIFIED:** İki Decision aynı deterministik generator sözleşmesi/algorithm version/seed, 256-byte payload, scale/iteration matrisi ve global/scale workload hashlerini kullanır. Candidate 50k component değerleri önceden ilan edilen p95 `≤3.000 ms` normalize + protected persistence + index, `≤150 MiB` whole-import allocation, `≤250 MiB` working-set delta ve `≤250 ms` cancellation referanslarının altındadır. Bu yalnız ölçülen component değerlerinin aritmetik karşılaştırmasıdır.

**INFERENCE:** Baseline'ın açık performans blocker'ını aynı sentetik workload'da kaldırdığı ve correctness/fault/cancellation sözleşmesini geçtiği için protected-catalog **comparative gate kapanmıştır**. Bu sonuç test-only container'ın production kabulü değildir. Ayrı metadata/active-pointer transaction'ı olmayan container production için `NO-GO`; tercih edilen devam yönü key-state, ciphertext row, snapshot metadata ve active pointer'ı aynı SQLite transaction domain'ine alan M8 state machine'idir.

Runner threshold uygulamadığı için “bütçenin altında” sınıflandırması evidence runner'ın verdiği PASS değil, ilan edilmiş referanslarla yapılan mühendislik değerlendirmesidir. Ölçülen component parser, normalize veya SQLite index içermediğinden p95 `≤3 s` birleşik persist/index ya da p95 `≤5 s` end-to-end import PASS'i ilan edilemez.

## 5. Sınırlamalar ve açık kanıtlar

- Koşu dedicated/reference benchmark cihazında değil developer workstation'da yapıldı; background load, power ve thermal koşulları kontrollü değildi.
- Scale'ler sabit küçükten büyüğe sırayla çalıştı; cold/warm OS cache etkisi ayrıştırılmadı.
- Harness unpackaged Release x64 test executable'ıdır. Parser, normalization, SQLite/index, UI, network ve production `LocalCache`/MSIX lifecycle maliyetleri kapsam dışıdır.
- Reopen metriği aynı process'te fresh adapter + reader construction'dır; process restart veya packaged cold start değildir. Read metriği yalnız 256 evenly-spaced record'dur.
- Working-set değeri yalnız ölçüm sınırındaki before/after sample ve signed delta'dır; operasyon içi gerçek peak working set değildir.
- Sabit 256-byte corpus variable/worst-case locator dağılımını, padding/length-leakage kararını veya tam ürün veri modelini temsil etmez.
- Pre-activation en çok 16 tag probe yapar; full snapshot authentication scan değildir. Whole-container + metadata rollback/replay ve authoritative owner doğruluğu kapanmaz.
- Controlled hooks process kill, OS crash veya power-loss atomikliği değildir. Cross-process writer/read sıralaması ve filesystem/disk durability ayrıca kanıtlanmadı.
- Gerçek ikinci Windows kullanıcısı/wrong-user, production PFN update/reset/repair/uninstall-reinstall, source-wide deletion/reconciliation, migration ve backup/journal/free-page sanitization açık kalır.
- AES-GCM/DPAPI kullanımı kendi başına security review, FIPS/compliance veya managed runtime/OS iç key schedule zeroization kanıtı değildir.

## 6. Hosted workflow/UI kaydı

2026-08-14 kullanıcı tarafından sağlanan GitHub Actions UI kaydı, run number `#27`nin docs-only commit `0c71d811b7f804e73daae04a684c1c4062a4a55a` için üç işin üçünü yeşil, artifact sayısını üç ve toplam süreyi `8m41s` gösterdiğini **VERIFIED** yapar. Bu yalnız Decision kaydını taşıyan commit'in normal hosted workflow sonucudur. Workflow `Invoke-WindowsProtectedCatalogSpike.ps1` çağırmadığından local opt-in `Decision` workload'unu, metriklerini veya summary hash'ini yeniden doğrulamamıştır. GitHub database run ID'si, job log/test sayısı ile artifact ID/içerik/JSON/ZIP/tam digest bağları bağımsız doğrulanmamıştır. Bu kayıt M4 veya ADR-003 durumunu değiştirmez.

## 7. Takip koşulu

Comparative spike tekrar edilmek zorunda olan açık gate değildir. M4'te gerçek ikinci Windows user ile source admission/drain, source-wide deletion ve startup reconciliation kanıtı kapanmalıdır. M8 production tasarımı; exact SQLite schema/provider/dependency/lisans kararı, aynı transaction'da old-or-new activation, bounded batch, crash/recovery, key rotation/delete ve migration contract'larını ayrı review ile kapatmalı; aynı 50k corpus üzerinde parser + normalize + protected persist + index end-to-end referans-cihaz ölçümünü geçmelidir. Production PFN lifecycle/repair/identity ve Store kabulü M15'te kalır.

Bu sonuç M4'ü tamamlamaz ve ADR-003'ü `Accepted` yapmaz. Tasarım bağlamı [M4 protected catalog aday kararında](M4_PROTECTED_CATALOG_CANDIDATE.md), reddedilen baseline'ın exact kanıtı [M4 secret-store Decision kaydında](M4_SECRET_STORE_DECISION_EVIDENCE.md) bulunur.
