# M16 Windows MVP Release Candidate blocked baseline'i

**Durum:** `IMPLEMENTED LOCALLY / BLOCKED — clean-commit evidence pending, 2026-08-26`

## Checkpoint kararı

`eng/Test-WindowsReleaseCandidateReadiness.ps1`, M16 Release Candidate kararını vermek yerine bu karara girecek bounded otomatik kanıt setini fail-closed biçimde bir araya getirir. Schema v1 bilerek yalnız blocked baseline üretebilir: `result=blocked`, `m16TechnicalGateSetPassed=false` ve `candidateReady=false` değerleri değişmez. Bu şema yapısal olarak release-ready sonucu yayımlayamaz; ileride adayın gerçekten hazır sayılması yeni schema, ayrı review ve bütün açık kapıların kapanmasını gerektirir.

Default invocation, geçerli blocked evidence'ı atomik yayımladıktan sonra nonzero sonuçla durur. `-AllowBlockedCandidate` yalnız bu bilinen blocked sonucun evidence üretimi için sıfır exit code ile dönmesine izin verir; blocker kabulü, sapma onayı, release waiver'ı veya `candidateReady` değişikliği değildir. Input/schema/hash/path/commit ya da repository invariant'ı geçmezse allow switch sonucu değiştirmez ve yeni evidence yayımlanmaz.

## Bounded final-artifact tarama primitive'i

`PARTIAL VERIFIED — local automated primitive, 2026-08-26`: `IptvSuite.Testing scan-release-artifacts <root> <run-scope> <case-id>` yalnız sabit `M16ReleaseCandidate` profiliyle çalışır. Profil; depth `32`, entry `25.000`, tek dosya `4 GiB`, toplam `8 GiB`, finding `256` ve relative-path `4096` sınırlarını caller tarafından gevşetilemez biçimde uygular. Root/child reparse point, erişilemeyen dosya, limit aşımı ve iki-pass ordered inventory/SHA-256 uyuşmazlığı operational failure'dır; temiz yüzey exit `0`, canary finding exit `2`, scanner/contract hatası exit `1` üretir. Dokuz encoding tek stream geçişinde aranır ve bulgu yolları yalnız fingerprint olarak yayımlanır. Eski `scan-artifacts` yolu compatibility için değişmeden kalır.

Bu primitive tek başına `M16FinalArtifactCanaryScanPending` blocker'ını kapatmaz. Aynı final commit/build için full log, owned app-data, exact package ve support-export yüzeylerinin sabit inventory ile taranması ve commit-bound sanitized evidence üretilmesi hâlâ gereklidir.

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

## Non-claims ve sonraki kabul sınırı

- Bu checkpoint M15 veya M16 completion değildir; public submission ya da release kararı vermez.
- M10'daki accepted-with-known-deviation sonucu M16'nın değişmeyen `%10 / 100 MiB / non-monotonic` final resource kriterini veya 24 saat/200 switch soak'ını karşılamış sayılmaz.
- Aggregator 24 saatlik soak başlatmaz ve kısa native evidence'ı uzun soak yerine kullanmaz.
- Physical DPI/multi-monitor/sleep/audio-device/Narrator/UIA matrisi çalıştırılmış sayılmaz.
- Final artifact canary, security/architecture scan, private-flight rehearsal, production signing/WACK, Partner Center, privacy/support/listing ve hukuk kapıları kapanmaz.
- Feature freeze, synthetic source→catalog→search→play/control→fault/reconnect→delete journey ve rollback/withdrawal/release-operations planı ayrıca tamamlanmalıdır.

Clean-commit evidence üretildikten sonra exact commit, evidence SHA-256/boyutu, input digest'leri, blocker sayaçları ve doğrulama sonuçları bu belgeye ayrı checkpoint olarak eklenmelidir. O zamana kadar durum `IMPLEMENTED LOCALLY / BLOCKED` kalır.
