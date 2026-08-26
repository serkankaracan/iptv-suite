# M16 Windows MVP Release Candidate blocked baseline'i

**Durum:** `IMPLEMENTED LOCALLY / BLOCKED — clean-commit evidence pending, 2026-08-26`

## Checkpoint kararı

`eng/Test-WindowsReleaseCandidateReadiness.ps1`, M16 Release Candidate kararını vermek yerine bu karara girecek bounded otomatik kanıt setini fail-closed biçimde bir araya getirir. Schema v1 bilerek yalnız blocked baseline üretebilir: `result=blocked`, `m16TechnicalGateSetPassed=false` ve `candidateReady=false` değerleri değişmez. Bu şema yapısal olarak release-ready sonucu yayımlayamaz; ileride adayın gerçekten hazır sayılması yeni schema, ayrı review ve bütün açık kapıların kapanmasını gerektirir.

Default invocation, geçerli blocked evidence'ı atomik yayımladıktan sonra nonzero sonuçla durur. `-AllowBlockedCandidate` yalnız bu bilinen blocked sonucun evidence üretimi için sıfır exit code ile dönmesine izin verir; blocker kabulü, sapma onayı, release waiver'ı veya `candidateReady` değişikliği değildir. Input/schema/hash/path/commit ya da repository invariant'ı geçmezse allow switch sonucu değiştirmez ve yeni evidence yayımlanmaz.

## Bounded final-artifact tarama primitive'i

`PARTIAL VERIFIED — local automated primitive, 2026-08-26`: `IptvSuite.Testing scan-release-artifacts <root> <run-scope> <case-id>` yalnız sabit `M16ReleaseCandidate` profiliyle çalışır. Profil; depth `32`, entry `25.000`, tek dosya `4 GiB`, toplam `8 GiB`, finding `256` ve relative-path `4096` sınırlarını caller tarafından gevşetilemez biçimde uygular. Root/child reparse point, file/directory alternate data stream, invalid path encoding, erişilemeyen dosya, limit aşımı ve üç-pass ordered inventory/content SHA-256 uyuşmazlığı operational failure'dır. Temiz yüzey exit `0`, canary finding exit `2`, scanner/contract hatası exit `1`, usage hatası exit `64` üretir; dinamik CLI testi exact JSON alan setini ve raw root/path/canary yayımlanmadığını doğrular. Dokuz encoding tek stream geçişinde aranır, bulgu yolları yalnız fingerprint olarak yayımlanır ve aggregate rapor fixed canonical inventory digest'i taşır. Eski `scan-artifacts` yolu compatibility için değişmeden kalır.

Bu primitive archive açıcı değildir: compressed MSIX/ZIP/support container üyelerindeki marker ancak caller container'ı zip-slip/symlink/duplicate/case-collision ve boyut sınırlarıyla güvenli biçimde açıp hem outer blob'u hem extracted üyeleri tararsa kapsanır. Mutable live root'ta atomik snapshot iddiası kurulmaz; final producer exact process kapanışı/frozen staging, same-build commit/package bağları ve atomik evidence publish sözleşmesini ayrıca sağlamalıdır.

## Dört yüzeyli final-artifact producer'ı

`IMPLEMENTED LOCALLY / HOSTED ACCEPTANCE PENDING, 2026-08-26`: `eng/Invoke-WindowsFinalArtifactCanaryScan.ps1`, parametresiz ve sabit profilli outer controller'dır. Clean `HEAD` ile varsa `GITHUB_SHA` eşitliğini, exact SDK `10.0.302` ve yükseltilmiş Windows PowerShell 5.1 ortamını doğrular; repository'ye bağlı named mutex ile birlikte normal/WACK smoke'larıyla paylaşılan sabit package-identity mutex'ini sabit sırada tutar ve package smoke'u yalnız controller'ın ürettiği exact run token ile `-EmitM16FinalArtifactSurfaces` opt-in'inde child process olarak çalıştırır. Abandoned mutex sonrasında yalnız tek, biçimi doğrulanmış run-token ownership kaydı sabit entry/byte sınırlarıyla exact temizlenebilir; birden çok veya beklenmeyen kayıt fail-closed reddedilir. Böylece yakalanamayan bir önceki process sonlanmasının exact-owned package/certificate/raw state'i yeni build başlamadan bounded olarak toparlanır. Child askıda başlatılıp `KILL_ON_JOB_CLOSE` Job Object'e bağlanmadan yürütülmez; stdout+stderr ortak toplamı pipe seviyesinde kesin `20 MiB`, package timeout'u `45 dakika`, scanner timeout'u `10 dakika` ile sınırlıdır. Eski `last-success.json` koşu başında exact olarak kaldırılır; başarısız yeni koşu tarihsel başarı kanıtını güncel sonuç gibi bırakamaz.

Canary locator yalnız test harness'ındaki exact `IPTVSUITE_TEST_ONLY_CANARY_V1` handshake'iyle sentetik loopback onboarding girdisine eklenir. Normal dört-argümanlı harness yolu değişmez; harness ve marker production MSIX payload'ına girmez. Locator protected-source pipeline'ından geçtikten ve bütün app/harness süreçleri kapandıktan sonra package tarafı şu exact üç yüzeyi tarar: canlı exact-PFN owned app-data, imzalı raw MSIX ile `Expand-MsixForInspection` kullanılarak güvenli açılmış payload'ın birlikte bulunduğu exact-package staging'i ve gerçek package-smoke Boolean/count sonuçlarından üretilen bounded `ReleaseAcceptanceOnly` support artifact'ı. Staged MSIX tarama boyunca write/delete paylaşımına kapalı tutulur ve tarama sonrasında yeniden hash'lenir; support JSON'u `CreateNew` ile yazılıp tarama öncesi ve sonrasında aynı strict schema/hash ile doğrulanır. Son yüzey kullanıcıya sunulan product support-export özelliği değildir; raw locator/header/body, full dump veya otomatik upload içermez ve `SupportUrlPending` blocker'ını kapatmaz.

Outer controller child bittikten sonra bounded birleşik child stdout/stderr kaydını `full-log` yüzeyi olarak tarar. Bu yüzey GitHub Actions job logunun tamamı olduğu iddiasını taşımaz. Package child başarılı tarama sonrasında yalnız exact-package staging'ini outer cleanup'a devreder. Outer controller retained `package.msix` dosyasını write/delete paylaşımına kapalı salt-okunur handle ile tutar, SHA-256'yı kendi sürecinde tarama öncesi ve sonrasında hesaplar ve exact-package inventory'sini ayrı bounded scanner çağrısıyla yeniden üretir. Child'ın strict binding kaydı ve package intermediate'ı bu outer-owned hash/inventory beklentilerine ayrı ayrı uymadan birleştirilemez; sibling kayıtların kendi aralarında tutarlı olması yeterli değildir. `WindowsM16FinalArtifactEvidence.ps1`; bu bağımsız beklentilerle package intermediate ve full-log raporunu strict UTF-8/no-BOM, duplicate-property, NTFS named-stream, reparse, boyut/schema ve same-build SHA bağlarıyla birleştirir. Exact dört yüzey sırası `owned-app-data`, `exact-package`, `support-artifact`, `full-log`dur. Raw process I/O, birleşik log, açılmış package ve iki intermediate evidence başarıda, yakalanabilen hatada ve bir sonraki koşunun doğrulanmış abandoned-run recovery'sinde exact-safe temizlenir; yalnız sanitized `.artifacts/m16-final-artifact-scan/last-success.json` yayımlanabilir.

`windows-quality.yml` içindeki `run_m16_final_artifacts` yalnız manual dispatch input'udur ve `run_wack` ile aynı koşuda seçilemez. Upload allowlist'i yalnız sanitized final JSON'dur; raw yüzeyler workflow artifact'ı değildir. Local parser/self-test ve architecture guard'ları producer sözleşmesini doğrular; clean-commit hosted run henüz kaydedilmediği için `M16FinalArtifactCanaryScanPending` açık kalır. Bu producer ayrıca ayrı `M16FinalSecurityArchitectureScanPending` kapısını, feature freeze'i, 24 saat soak'ı veya fiziksel matrix'i kapatmaz.

## Bounded sentetik uçtan uca entegrasyon journey'si

`PARTIAL VERIFIED — local deterministic integration, 2026-08-26`: `M16SyntheticEndToEndJourneyTests`, tamamen sentetik ve TLS certificate-pin'li loopback veriyle production onboarding, bounded transport/probe, protected-reference import, SQLite browse/search, playback coordinator control, watchdog fault/reconnect ve source deletion/tombstone zincirini tek bounded testte yürütür. Exact HTTP istek sırası, ikinci fiziksel playback session'ına control restore sırası, stop-before-reopen, retired-source non-admission, disposed lease'ler, protected-record zeroization ve ana SQLite dosyasıyla mevcut `-journal`/`-wal`/`-shm` yan yüzeylerinde raw locator yokluğu doğrulanır. Test timeout'u sabit `60 s`dir; son hedef koşu `1/1 PASS` ve hedef build `0 warning / 0 error` vermiştir.

Bu journey in-memory secret store ve deterministik playback-engine seam'i kullanır; gerçek DPAPI, native decoder, WinUI veya packaged acceptance kanıtı değildir. Loopback fault, gerçek provider/network/device uyumluluğu ya da 24 saat soak yerine geçmez. `M16SyntheticEndToEndJourneyPending`, temiz commit üzerinde full gate ve commit-bound RC evidence zinciri tamamlanana kadar fail-closed açık kalır.

Blocked baseline üretim çağrısı repository kökünden şöyledir:

```powershell
.\eng\Test-WindowsReleaseCandidateReadiness.ps1 -AllowBlockedCandidate
```

Sanitized sonuç `.artifacts/m16-release-candidate/rc-summary.json` altında yayımlanır.

Clean-commit baseline henüz üretilmediğinden bu kayıtta commit SHA, evidence boyutu/hash'i, hosted run veya release-package digest iddiası yoktur.

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
| `m15-readiness.json` | `Test-WindowsReleaseReadiness.ps1 -AllowBlockedInventory` ile içeride üretilen schema v6 blocked readiness sonucu | M15 `releaseReady=false` sonucunu veya blocker'larını başarıya yeniden yorumlamaz |

Inputlar strict UTF-8/schema, duplicate-property, bounded boyut/yapı, containment/reparse/ADS, clean exact repository ve pre-publish tekrar doğrulama kontrollerini geçmeden aggregation sonucu yayımlanmaz. Başarılı integrity sonucu yalnız bu sabit kanıt setinin doğrulandığını ifade eder.

## Gate alanlarının anlamı

- `aggregationIntegrityPassed=true`, inputların beklenen schema, provenance, commit/package ve bounded publication sözleşmesini geçtiğini gösterir; ürün veya Store acceptance sonucu değildir.
- `m1ToM15AutomatedGateSetPassed=true`, yalnız yukarıdaki seçilmiş otomatik predecessor evidence setinin doğrulandığını gösterir. M12 fiziksel/manual matrisi, M10 retention borcu ve M15 dış/onay-gerektiren kapılar bunun dışında kalır.
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

Blocked baseline aşağıdaki exact yedi M16 kapısını ayrıca açık tutar:

1. `M16FeatureFreezeDecisionPending`
2. `M16SyntheticEndToEndJourneyPending`
3. `M16FinalArtifactCanaryScanPending`
4. `M16FinalSecurityArchitectureScanPending`
5. `M16TwentyFourHourSoakPending`
6. `M16PhysicalDeviceAccessibilityMatrixPending`
7. `M16ReleaseOperationsPlanPending`

## Release operasyonları plan kaydı

`RECORDED LOCALLY / EXECUTION BLOCKED, 2026-08-26`: [M16 release operasyonları planı](M16_RELEASE_OPERATIONS_PLAN.md), rollback/withdrawal, dependency/CVE response, incident/known-issue triage, evidence retention, support matrix, release notes, rol sahipliği ve exact non-claim sınırlarını source-controlled karar modeli olarak kaydeder. Production identity/signing, WACK/Partner Center, privacy/support/listing URL'leri, hukuk/ülke girdileri ve dış publication/withdrawal sonuçları açıkça `PENDING` kalır; hiçbir kişi, URL, certificate, Store identity veya hukuki sonuç uydurulmaz.

Bu local kayıt release yetkisi değildir ve schema-v1 aggregator'ın hard-coded blocked sonucunu değiştirmez. `M16ReleaseOperationsPlanPending`, planın clean commit-bound RC evidence/review zincirine bağlanıp kabul edilmesine kadar açık kalır; diğer M15/M16 blocker'ları da etkilenmez.

## Non-claims ve sonraki kabul sınırı

- Bu checkpoint M15 veya M16 completion değildir; public submission ya da release kararı vermez.
- M10'daki accepted-with-known-deviation sonucu M16'nın değişmeyen `%10 / 100 MiB / non-monotonic` final resource kriterini veya 24 saat/200 switch soak'ını karşılamış sayılmaz.
- Aggregator 24 saatlik soak başlatmaz ve kısa native evidence'ı uzun soak yerine kullanmaz.
- Physical DPI/multi-monitor/sleep/audio-device/Narrator/UIA matrisi çalıştırılmış sayılmaz.
- Hosted final-artifact canary kabulü, security/architecture scan, private-flight rehearsal, production signing/WACK, Partner Center, privacy/support/listing ve hukuk kapıları henüz kapanmaz.
- Feature freeze, synthetic source→catalog→search→play/control→fault/reconnect→delete journey ve rollback/withdrawal/release-operations planı ayrıca tamamlanmalıdır.

Clean-commit evidence üretildikten sonra exact commit, evidence SHA-256/boyutu, input digest'leri, blocker sayaçları ve doğrulama sonuçları bu belgeye ayrı checkpoint olarak eklenmelidir. O zamana kadar durum `IMPLEMENTED LOCALLY / BLOCKED` kalır.
