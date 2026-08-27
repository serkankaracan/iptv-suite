# M16 Windows MVP Release Candidate blocked baseline'i

**Durum:** `M15 TECHNICAL CHAIN RENEWED / M16 HOSTED JOURNEY + FINAL-ARTIFACT CURRENT / SECURITY-ARCHITECTURE RENEWAL REQUIRED / BLOCKED — 5 M16 blocker açık, 2026-08-27`

## Checkpoint kararı

`eng/Test-WindowsReleaseCandidateReadiness.ps1`, M16 Release Candidate kararını vermek yerine bu karara girecek bounded otomatik kanıt setini fail-closed biçimde bir araya getirir. Schema v1 bilerek yalnız blocked baseline üretebilir: `result=blocked`, `m16TechnicalGateSetPassed=false` ve `candidateReady=false` değerleri değişmez. Bu şema yapısal olarak release-ready sonucu yayımlayamaz; ileride adayın gerçekten hazır sayılması yeni schema, ayrı review ve bütün açık kapıların kapanmasını gerektirir.

Default invocation, geçerli blocked evidence'ı atomik yayımladıktan sonra nonzero sonuçla durur. `-AllowBlockedCandidate` yalnız bu bilinen blocked sonucun evidence üretimi için sıfır exit code ile dönmesine izin verir; blocker kabulü, sapma onayı, release waiver'ı veya `candidateReady` değişikliği değildir. Input/schema/hash/path/commit ya da repository invariant'ı geçmezse allow switch sonucu değiştirmez ve yeni evidence yayımlanmaz.

## Bounded final-artifact tarama primitive'i

`PARTIAL VERIFIED — local automated primitive, 2026-08-26`: `IptvSuite.Testing scan-release-artifacts <root> <run-scope> <case-id>` yalnız sabit `M16ReleaseCandidate` profiliyle çalışır. Profil; depth `32`, entry `25.000`, tek dosya `4 GiB`, toplam `8 GiB`, finding `256` ve relative-path `4096` sınırlarını caller tarafından gevşetilemez biçimde uygular. Root/child reparse point, file/directory alternate data stream, invalid path encoding, erişilemeyen dosya, limit aşımı ve üç-pass ordered inventory/content SHA-256 uyuşmazlığı operational failure'dır. Temiz yüzey exit `0`, canary finding exit `2`, scanner/contract hatası exit `1`, usage hatası exit `64` üretir; dinamik CLI testi exact JSON alan setini ve raw root/path/canary yayımlanmadığını doğrular. Dokuz encoding tek stream geçişinde aranır, bulgu yolları yalnız fingerprint olarak yayımlanır ve aggregate rapor fixed canonical inventory digest'i taşır. Eski `scan-artifacts` yolu compatibility için değişmeden kalır.

Bu primitive archive açıcı değildir: compressed MSIX/ZIP/support container üyelerindeki marker ancak caller container'ı zip-slip/symlink/duplicate/case-collision ve boyut sınırlarıyla güvenli biçimde açıp hem outer blob'u hem extracted üyeleri tararsa kapsanır. Mutable live root'ta atomik snapshot iddiası kurulmaz; final producer exact process kapanışı/frozen staging, same-build commit/package bağları ve atomik evidence publish sözleşmesini ayrıca sağlamalıdır.

## Dört yüzeyli final-artifact producer'ı

`HOSTED VERIFIED / TECHNICAL FINAL-ARTIFACT ACCEPTED, 2026-08-26`: `eng/Invoke-WindowsFinalArtifactCanaryScan.ps1`, parametresiz ve sabit profilli outer controller'dır. Clean `HEAD` ile varsa `GITHUB_SHA` eşitliğini, exact SDK `10.0.302` ve yükseltilmiş Windows PowerShell 5.1 ortamını doğrular; repository'ye bağlı named mutex ile birlikte normal/WACK smoke'larıyla paylaşılan sabit package-identity mutex'ini sabit sırada tutar ve package smoke'u yalnız controller'ın ürettiği exact run token ile `-EmitM16FinalArtifactSurfaces` opt-in'inde child process olarak çalıştırır. Abandoned mutex sonrasında yalnız tek, biçimi doğrulanmış run-token ownership kaydı sabit entry/byte sınırlarıyla exact temizlenebilir; birden çok veya beklenmeyen kayıt fail-closed reddedilir. Böylece yakalanamayan bir önceki process sonlanmasının exact-owned package/certificate/raw state'i yeni build başlamadan bounded olarak toparlanır. Child askıda başlatılıp `KILL_ON_JOB_CLOSE` Job Object'e bağlanmadan yürütülmez; stdout+stderr ortak toplamı pipe seviyesinde kesin `20 MiB`, package timeout'u `45 dakika`, scanner timeout'u `10 dakika` ile sınırlıdır. Eski `last-success.json` koşu başında exact olarak kaldırılır; başarısız yeni koşu tarihsel başarı kanıtını güncel sonuç gibi bırakamaz.

Canary locator yalnız test harness'ındaki exact `IPTVSUITE_TEST_ONLY_CANARY_V1` handshake'iyle sentetik loopback onboarding girdisine eklenir. Normal dört-argümanlı harness yolu değişmez; harness ve marker production MSIX payload'ına girmez. Locator protected-source pipeline'ından geçtikten ve bütün app/harness süreçleri kapandıktan sonra package tarafı şu exact üç yüzeyi tarar: canlı exact-PFN owned app-data, imzalı raw MSIX ile `Expand-MsixForInspection` kullanılarak güvenli açılmış payload'ın birlikte bulunduğu exact-package staging'i ve gerçek package-smoke Boolean/count sonuçlarından üretilen bounded `ReleaseAcceptanceOnly` support artifact'ı. Staged MSIX tarama boyunca write/delete paylaşımına kapalı tutulur ve tarama sonrasında yeniden hash'lenir; support JSON'u `CreateNew` ile yazılıp tarama öncesi ve sonrasında aynı strict schema/hash ile doğrulanır. Son yüzey kullanıcıya sunulan product support-export özelliği değildir; raw locator/header/body, full dump veya otomatik upload içermez ve `SupportUrlPending` blocker'ını kapatmaz.

Outer controller child bittikten sonra bounded birleşik child stdout/stderr kaydını `full-log` yüzeyi olarak tarar. Bu yüzey GitHub Actions job logunun tamamı olduğu iddiasını taşımaz. Package child başarılı tarama sonrasında yalnız exact-package staging'ini outer cleanup'a devreder. Outer controller retained `package.msix` dosyasını write/delete paylaşımına kapalı salt-okunur handle ile tutar, SHA-256'yı kendi sürecinde tarama öncesi ve sonrasında hesaplar ve exact-package inventory'sini ayrı bounded scanner çağrısıyla yeniden üretir. Child'ın strict binding kaydı ve package intermediate'ı bu outer-owned hash/inventory beklentilerine ayrı ayrı uymadan birleştirilemez; sibling kayıtların kendi aralarında tutarlı olması yeterli değildir. `WindowsM16FinalArtifactEvidence.ps1`; bu bağımsız beklentilerle package intermediate ve full-log raporunu strict UTF-8/no-BOM, duplicate-property, NTFS named-stream, reparse, boyut/schema ve same-build SHA bağlarıyla birleştirir. Exact dört yüzey sırası `owned-app-data`, `exact-package`, `support-artifact`, `full-log`dur. Raw process I/O, birleşik log, açılmış package ve iki intermediate evidence başarıda, yakalanabilen hatada ve bir sonraki koşunun doğrulanmış abandoned-run recovery'sinde exact-safe temizlenir; yalnız sanitized `.artifacts/m16-final-artifact-scan/last-success.json` yayımlanabilir.

`windows-quality.yml` içindeki `run_m16_final_artifacts` yalnız manual dispatch input'udur ve `run_wack` ile aynı koşuda seçilemez. Upload allowlist'i yalnız sanitized final JSON'dur; raw yüzeyler workflow artifact'ı değildir. [Hosted run `33060587316`](https://github.com/serkankaracan/iptv-suite/actions/runs/33060587316), clean commit `be52ab67687cc44a9ca820ec1907c1b92bf1d24a` üzerinde bütün required job zincirini ve dört yüzeyli final taramayı geçti; producer job `98480943428`, Required Windows gate `98483121630` ile başarıyla tamamlandı. Artifact `9642123749` digest'i `b40f8742681546c74f1c9d4b6d345ecc699addd2b1bca0830f647b380076f32f`; allowlist'teki tek `last-success.json` üyesinin SHA-256 değeri `fe27278d17391e2946642758c185f4f389e59d81f35e74482452ccdf1867fb11`dir. Exact `owned-app-data`, `exact-package`, `support-artifact` ve bounded `full-log` yüzeylerinin dördü de `clean`, toplam finding sayısı `0`, `SameBuildBindingPassed=true`, `RepositoryStable=true` ve `RawSurfacesUploaded=false` sonucunu verdi. Aynı-run package artifact'ı `9642122977`, package/SBOM bağını exact commit ve `0ceb0e95967c1ede0db1e034d958f0f7a4e7e9da00f65d66010b95f58da86333` package SHA-256 değeriyle doğrular. Bu commit-bound current kabul yalnız `M16FinalArtifactCanaryScanPending` kapısını kapatır; feature freeze, 24 saat soak, fiziksel matrix, Store/WACK, hukuk veya release kararı üzerinde sonuç üretmez.

Source-controlled `eng/windows-m16-final-artifact-acceptance.json` kabul ledger'ının raw SHA-256 değeri `d0da8a15ff410886c7f9450a8a0ec4c1fe0e463a951b665c2797d178da4db91a`dır. Producer contract bağı exact `39` source'u; text girdilerini strict UTF-8/no-BOM ve LF-normalized byte'larla, binary `.ts` fixture girdisini raw byte'larla `18b20bf208943c6ac9cc1ac4075f3df3f7668765bdf3833b03de664134bae6ae` SHA-256 değerine bağlar. Current package-producing snapshot exact `115` dosya ve `5568fb8fc87f614392762501cb2a4b3be1a13487bb8cfab037ccaec579756810` SHA-256 bağıdır. Ledger raw byte/schema/artifact tutarsızlığı hard-invalid kalır ve RC evidence üretmez. Intact ledger current producer source veya package snapshot'ından geri kalırsa aggregator yine `result=stale-reopen`, `current=false` yayımlar ve `M16FinalArtifactCanaryScanPending` blocker'ını yeniden açarak blocked evidence üretmeye devam eder.

## Bounded sentetik uçtan uca entegrasyon journey'si

`HOSTED VERIFIED / CURRENT ACCEPTED, 2026-08-27`: `M16SyntheticEndToEndJourneyTests`, tamamen sentetik ve TLS certificate-pin'li loopback veriyle production onboarding, bounded transport/probe, protected-reference import, SQLite browse/search, playback coordinator control, watchdog fault/reconnect ve source deletion/tombstone zincirini tek bounded testte yürütür. Exact HTTP istek sırası, ikinci fiziksel playback session'ına control restore sırası, stop-before-reopen, retired-source non-admission, disposed lease'ler, protected-record zeroization ve ana SQLite dosyasıyla mevcut `-journal`/`-wal`/`-shm` yan yüzeylerinde raw locator yokluğu doğrulanır. Test timeout'u sabit `60 s`dir.

[Hosted run `33069492771`, attempt `1`](https://github.com/serkankaracan/iptv-suite/actions/runs/33069492771) clean `ca63f5959fd0becf59411d6aa979ee350faed90f` üzerinde exact journey sonucunu iki deterministik run'da geçti. Quality job `98507784764`, Required Windows gate `98513232942` ve workflow sonucu `success` oldu. `windows-quality-evidence` artifact `9645528070` digest'i `8da92578e4f226a37255c8d618f240db3439df1b260b18403b83addf356bf658`; `evidence/quality-summary.json` üyesi `47.236` byte ve SHA-256 `27013951798ba1b77b646d0a77ab39b1bd4c045a67cf0ecc039e0ec37db9b520`dir. Summary exact `631` test/run, `cleanRunCount=2` ve tek `AuthorizedRemotePlaylistCompletesExactSyntheticReleaseCandidateJourney|Passed` sonucu taşır.

Bu current kabul `eng/windows-m16-synthetic-journey-acceptance.json` ledger'ında raw SHA-256 `50d867fd845e96bb4ad9207fc356bce891e36801d250e8f2d5e1f04e968a8480` ile kayıtlıdır. Exact `132` source'un producer/dependency closure SHA-256 değeri `08ef66d9ce752f91721cfcf9a3b848cfb69eb45fd454dfd674342e29a4a961ca`dır. Ledger current kaldığı sürece yalnız `M16SyntheticEndToEndJourneyPending` kapısını kapatır; closure drift'i `stale-reopen` ile kapıyı yeniden açar. Raw ledger/schema/artifact tutarsızlığı ise stale değil hard-invalid'dir.

Bu üç source-controlled acceptance ledger'ının validator tarafından okunması, kaydedilen GitHub run/job/artifact occurrence'ının veya GitHub provenance'ının bağımsız attestation'ı değildir. Validator yalnız bounded ledger byte/schema/hash bağlarını, kaydedilmiş artifact alanlarını ve current repository source closure'ını doğrular; canlı GitHub API'sine başvurmaz. Run bağlantıları ve artifact incelemesi ayrı insan/hosted kabul kaydıdır. Yeni bir producer sonucu ancak bu dış doğrulama yapılıp exact metadata source-controlled ledger'a açıkça kabul edildiğinde current teknik kapıyı kapatabilir.

Bu journey in-memory secret store ve deterministik playback-engine seam'i kullanır; gerçek DPAPI, native decoder, WinUI veya packaged acceptance kanıtı değildir. Loopback fault, gerçek provider/network/device uyumluluğu ya da 24 saat soak yerine geçmez.

## Hosted final security/architecture kabulü

`HOSTED VERIFIED / STALE-REOPEN AFTER CONTRACT ASSERTION UPDATE, 2026-08-27`: [Run `33069492771`, attempt `1`](https://github.com/serkankaracan/iptv-suite/actions/runs/33069492771), clean `ca63f5959fd0becf59411d6aa979ee350faed90f` ve exact SDK `10.0.302` ile iki deterministik full quality runında `631/631` sonucu geçti. Quality job `98507784764` (`2026-08-27T12:08:01Z`) ve Required Windows gate `98513232942` başarıyla tamamlandı. Canonical full-result set SHA-256 değeri `66dab64fa75e52da441dd863490f8d0c5c32f54c5963a12b860ff8af19663ff2`dir. Bunun içindeki exact `77` ArchitectureTests sonucu birer kez ve `Passed` olarak bulundu; ordinal canonical altküme SHA-256 değeri `9d2e961e127593313f48365a9c7f700a6bf1e745c832c8947a94c90a0c4da778`dir. Quality sentinel fail/recovery, scanner CLI contaminated/clean self-test'i ve artifact-files-only canary scan da geçti. `windows-quality-evidence` artifact `9645528070`, `14.131` byte ve SHA-256 `8da92578e4f226a37255c8d618f240db3439df1b260b18403b83addf356bf658`dır; içindeki `47.236` byte `evidence/quality-summary.json` üyesinin SHA-256 değeri `27013951798ba1b77b646d0a77ab39b1bd4c045a67cf0ecc039e0ec37db9b520`dir.

Source-controlled `eng/windows-m16-security-architecture-acceptance.json` ledger'ının raw SHA-256 değeri `87e6dbc14c674e07a7c244a0414c6ad347fe63db5ea54fdf018fdf5a612fa5ef`dir. Run #302 exact `329` source / `7.162.233` canonical byte closure'ı `ed77c3f055f4d8119913a8d44664b0d3ac35e2e86df8a85aae2c724375823e92` ile bağladı. Validator contract testindeki iki byte-length assertion'ının aynı değere yenilenmesi güvenlik closure'ını bilinçli olarak `580bbf89b427828db09485310f0d2284e2b4f24fda947d6e2cc9d721c78b2265` değerine taşıdığı için ledger intact fakat current değildir; aggregator `stale-reopen` üretir ve `M16FinalSecurityArchitectureScanPending` kapısını açık tutar. Acceptance/readiness ledger ve validator döngüleri exact exclusions ile closure dışında kalır; yeni clean hosted quality run'ı bu son closure'a bağlanmadan current kabul iddiası kurulmaz. Missing/tamper/schema/artifact bağı hard-invalid'dir.

Quality artifact'ı TRX üyelerini yayımlamadığı için bu kayıt bağımsız TRX provenance attestation'ı değildir. Penetration/SAST, CVE/license/legal/privacy, Store identity/signing/WACK, fiziksel cihaz/accessibility, current final-artifact scan veya 24 saat soak kabulü üretmez.

Blocked baseline üretim çağrısı repository kökünden şöyledir:

```powershell
.\eng\Test-WindowsReleaseCandidateReadiness.ps1 -AllowBlockedCandidate
```

Sanitized sonuç `.artifacts/m16-release-candidate/rc-summary.json` altında yayımlanır.

Tam RC aggregation baseline'ı gerçek current predecessor input setiyle henüz yeniden üretilmemiştir. Hosted journey ve final-artifact kayıtları kendi exact bağlarında current accepted evidence'dır; security/architecture kaydı contract assertion drift'i nedeniyle `stale-reopen` durumundadır. Yeni hosted quality checkpoint'i kabul edilene kadar beş M16 blocker açık kalır. Bu kayıtlar kalan predecessor veya M16 kapılarının tamamlandığı iddiası değildir.

## Sabit ve bounded input seti

Input staging kökü sabit `.artifacts/m16-release-candidate/inputs` dizinidir; per-input path override yoktur. Aggregator aşağıdaki ilk yedi staged dosyayı okur ve sekizinci logical input olan M15 readiness sonucunu aynı sabit kökte içeride üretir:

| Input | Beklenen dar sözleşme | İddia sınırı |
|---|---|---|
| `quality-summary.json` | Schema v1; iki clean deterministik run ve bütün test sonuçları `Passed` | Seçilmiş otomatik suite; milestone/manual kabul toplamı değildir |
| `package-smoke-success.json` | Existing package-smoke success sözleşmesi; exact commit ve package SHA-256 bağı | Development package smoke; private-flight RC veya Store install kanıtı değildir |
| `package-lifecycle-success.json` | Schema v3 disposable lifecycle success | Production PFN/identity/update/repair lifecycle kabulü değildir |
| `dpapi-user-boundary-success.json` | Schema v1, M4 real-user DPAPI boundary success | Production package'ın second-user/Store identity kabulü değildir |
| `native-tier-a-success.json` | Schema v10; exact `100` switch, `0` soak minute ve `1` interruption kısa profili | 24 saat soak, gerçek cihaz/HW decode veya retention kabulü değildir |
| `catalog-benchmark-summary.json` | Schema v1 M14 reference-eligible performance sonucu | Aynı build'de full RC journey veya gerçek-provider ölçümü değildir |
| `catalog-regression-summary.json` | Schema v1 M14 regression sonucu ve `allPassed=true` | Cross-device/perpetual regression garantisi değildir |
| `m15-readiness.json` | `Test-WindowsReleaseReadiness.ps1 -AllowBlockedInventory` ile içeride üretilen schema v7 blocked readiness sonucu | M15 `releaseReady=false` sonucunu veya blocker'larını başarıya yeniden yorumlamaz |

Inputlar strict UTF-8/schema, duplicate-property, bounded boyut/yapı, containment/reparse/ADS, clean exact repository ve pre-publish tekrar doğrulama kontrollerini geçmeden aggregation sonucu yayımlanmaz. Başarılı integrity sonucu yalnız bu sabit kanıt setinin doğrulandığını ifade eder.

## Gate alanlarının anlamı

- `aggregationIntegrityPassed=true`, inputların beklenen schema, provenance, commit/package ve bounded publication sözleşmesini geçtiğini gösterir; ürün veya Store acceptance sonucu değildir.
- Son historical RC evidence'ındaki `m1ToM15AutomatedGateSetPassed=false`, o evaluation anındaki stale SBOM predecessor sonucunu kaydeder. Current source-controlled M15 zinciri hosted SBOM run `33057515129` ve CVE run `33057496306` ile `technicalBaselinePassed=true` durumuna yenilenmiştir. CVE final-release freshness sınırı `2026-08-28T09:14:09Z`, yedi günlük teknik freshness sınırı `2026-09-03T09:14:09Z`dir; aggregation alanı ancak current RC input seti yeniden üretildiğinde değişebilir.
- `m16TechnicalGateSetPassed=false`, M16'nın kendi hard gate setinin tamamlanmadığını açıkça kaydeder.
- `candidateReady=false`, schema v1'in değişmez ve fail-closed sonucudur.

## Değişmeden taşınan M15 blocker'ları

M16 aggregator aşağıdaki exact 12 M15 blocker'ı kapatmaz, yeniden adlandırmaz veya daha düşük öneme çevirmez:

1. `CodecIpLegalReviewPending`
2. `LicenseFilePending`
3. `NoticeFilePending`
4. `PartnerCenterPrivateFlightPending`
5. `PrivacyPolicyPending`
6. `ProductionIdentityMigrationPending`
7. `ProductionLifecycleMatrixPending`
8. `ReleaseSigningPending`
9. `ReviewerServiceAndRehearsalPending`
10. `StoreListingPending`
11. `SupportUrlPending`
12. `WackPending`

M15'in final-release CVE freshness predicate'i evaluation anında geçmezse `CveReviewPending` de M15 kaynağından yeniden açılır; M16 allow modu bunu kabul edilmiş sayamaz.

## M16 blocker'ları

Current journey ve final-artifact ledger'ları kendi automated kapılarını kapatır; stale security/architecture kapısı yeniden açıldığı için blocked baseline aşağıdaki exact beş M16 kapısını açık tutar:

1. `M16FeatureFreezeDecisionPending`
2. `M16FinalSecurityArchitectureScanPending`
3. `M16PhysicalDeviceAccessibilityMatrixPending`
4. `M16ReleaseOperationsPlanPending`
5. `M16TwentyFourHourSoakPending`

## Release operasyonları plan kaydı

`RECORDED LOCALLY / EXECUTION BLOCKED, 2026-08-26`: [M16 release operasyonları planı](M16_RELEASE_OPERATIONS_PLAN.md), rollback/withdrawal, dependency/CVE response, incident/known-issue triage, evidence retention, support matrix, release notes, rol sahipliği ve exact non-claim sınırlarını source-controlled karar modeli olarak kaydeder. Production identity/signing, WACK/Partner Center, privacy/support/listing URL'leri, hukuk/ülke girdileri ve dış publication/withdrawal sonuçları açıkça `PENDING` kalır; hiçbir kişi, URL, certificate, Store identity veya hukuki sonuç uydurulmaz.

Bu local kayıt release yetkisi değildir ve schema-v1 aggregator'ın hard-coded blocked sonucunu değiştirmez. `M16ReleaseOperationsPlanPending`, planın clean commit-bound RC evidence/review zincirine bağlanıp kabul edilmesine kadar açık kalır; diğer M15/M16 blocker'ları da etkilenmez.

## Non-claims ve sonraki kabul sınırı

- Bu checkpoint M15 veya M16 completion değildir; public submission ya da release kararı vermez.
- M10'daki accepted-with-known-deviation sonucu M16'nın değişmeyen `%10 / 100 MiB / non-monotonic` final resource kriterini veya 24 saat/200 switch soak'ını karşılamış sayılmaz.
- Aggregator 24 saatlik soak başlatmaz ve kısa native evidence'ı uzun soak yerine kullanmaz.
- Physical DPI/multi-monitor/sleep/audio-device/Narrator/UIA matrisi çalıştırılmış sayılmaz.
- Hosted ledger kabulleri yalnız bağlı oldukları exact source closure'larda kendi kapılarını kapatır; source drift'te kapı yeniden açılır. Private-flight rehearsal, production signing/WACK, Partner Center, privacy/support/listing ve hukuk kapıları kapanmaz.
- Feature freeze ile rollback/withdrawal/release-operations kabulü ayrıca tamamlanmalıdır.

Tam commit-bound RC aggregation evidence üretildikten sonra input digest'leri, kalan blocker sayaçları ve doğrulama sonuçları bu belgeye ayrı checkpoint olarak eklenmelidir. O zamana kadar `m16TechnicalGateSetPassed=false`, `candidateReady=false` ve durum `BLOCKED` kalır.
