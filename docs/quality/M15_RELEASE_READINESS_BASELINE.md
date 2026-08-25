# M15 MSIX ve Store release-readiness teknik baseline'i

**Durum:** `IN PROGRESS / BLOCKED — technicalBaselinePassed=true; releaseReady=false, 2026-08-25`

## Checkpoint kararı

Bu kayıt, M15'in statik teknik envanter ve exact installed-package runtime audit checkpoint'lerini birlikte taşır. Statik kaynak bağlamı clean commit `0236894f8cc8c6212305f5ba9c294e335f3d031e` ve exact SDK `10.0.302`dir. `eng/Test-WindowsReleaseReadiness.ps1`, `-AllowBlockedInventory` ile teknik baseline'i doğrulayıp bilinen release blocker'larını değiştirmeden sanitized evidence yayımlar. Bu switch blocker'ları kabul etmez veya `releaseReady` sonucunu değiştirmez. Switch verilmezse aynı blocked evidence atomik yayımlandıktan sonra betik `M15ReleaseReadinessBlocked` ile fail-closed durur.

Manifestteki `IptvSuite.LocalDev.6f0d9a64` / `CN=IptvSuite Local Development` yalnız disposable development lineage'ıdır. Store reservation, public package identity, marka hakkı, production signing veya development paketinden production PFN'e migration garantisi değildir.

## Clean statik denetim sonucu

| Alan | Sonuç |
|---|---|
| `result` | `blocked` |
| `technicalBaselinePassed` | `true` |
| `releaseReady` | `false` |
| Kaynak commit | `0236894f8cc8c6212305f5ba9c294e335f3d031e` |
| Evidence boyutu | `19.343` byte |
| Evidence SHA-256 | `b41bad875b940958b52383051b215630fae22b8487ba3263f2237b78c9061895` |
| Production asset envanteri | exact `8` |
| Production lockfile envanteri | exact `4`, schema v2 |
| Production package-name envanteri | exact `23` |
| Açık release blocker'ı | exact `16` |

Statik teknik baseline şu mevcut sözleşmeleri doğrular:

- framework-dependent `x64` / `win-x64`, `SelfContained=false`, `WindowsAppSDKSelfContained=false` ve `AppxBundle=Never`;
- yalnız restricted-capability namespace'indeki `runFullTrust`, `asInvoker`, `uiAccess=false`, exact Windows Desktop target family ve Store association yokluğu;
- manifest/project ile bağlı sekiz asset'in bounded boyut ve SHA-256 envanteri;
- dört production `packages.lock.json` dosyası ile exact 23 package-name allowlist'i, çözülmüş sürüm ve NuGet content-hash kaydı;
- katalog ve protected-store mutable köklerinin sırasıyla `LocalCache/Catalog/v2` ve `LocalCache/ProtectedStore/v2` olması;
- production C# kaynaklarında version `1` known install-root discovery denylist taraması ve izin verilen tek `AppContext.BaseDirectory` kullanımının `Assets/AppIcon.ico` read-only icon binding'i olması;
- evidence'ın repository-relative path, bounded `≤1 MiB`, atomic replace/move ve kullanıcı/host/makine/SID/PFN/package-full-name/certificate/absolute-path sızıntısı olmayan şeması.

## Test bağı

- Windows PowerShell 5.1 parser ve deterministik release-readiness self-test: `PASS`.
- Self-test; gerçek source audit'ine ek olarak `.artifacts` dışı/ADS evidence, extra veya yanlış-namespace capability, erken Store association, self-contained/ARM64 RID, eksik ya da exclusive-lock altındaki asset, production lock'a yeni LibVLC package adı ve alternatif install-root discovery yüzeylerini fail-closed mutation'larla doğruladı.
- Architecture suite: `50/50 PASS`.
- Full quality gate: dış sandbox, normal Windows kullanıcı bağlamında `567/567 × 2 PASS`; iki koşunun test seti, fixture determinism, sentinel, scanner ve canary kontrolleri geçti.
- [Run #219 (`32871431008`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32871431008), nested Windows PowerShell 5.1 denetiminde iki denemede de generic `ProjectContractsFailed` verdi; package/DPAPI işleri doğru biçimde skip edildi. `INFERENCE`: Stage içindeki tek çıplak dış cmdlet olan `Get-FileHash`, local/CI host farkının en güçlü adayıdır; log exact inner exception taşımadığı için bu kesin kök neden sayılmaz. Takip commit'i bu çağrıyı owned .NET SHA-256 akışıyla değiştirdi, yalnız sharing/lock violation için toplam `150 ms` bounded retry ekledi ve ham exception yerine stable kodu kilitledi.
- [Run #220 (`32872859688`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32872859688), önceki M15 contract failure'ını geçtikten sonra tek architecture failure olarak M14 regression self-test child process'inin `30 saniye` içinde tamamlanmamasını kaydetti; stdout/stderr boştu ve regression assertion ya da nonzero exit-code hatası kanıtı yoktu. `INFERENCE`: hosted Windows/PowerShell 5.1 scheduling veya temp-I/O gecikmesi en güçlü açıklamadır. Takip commit'i yalnız bounded harness süresini komşu PowerShell self-testleriyle aynı `120 saniye` değerine çıkardı; assertion, exit-code ve fail-closed process-tree kill değişmedi.
- [Run #221 (`32873776770`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32873776770), clean `0236894f8cc8c6212305f5ba9c294e335f3d031e` üzerinde locked quality, signed package/install/launch/lifecycle, DPAPI real-user boundary ve required Windows gate işlerinin tamamını geçti; opt-in native playback işi beklendiği gibi skip edildi.

## Exact install-root runtime audit sonucu

`eng/WindowsPackageInstallRootAudit.ps1`, kurulan exact product package kaydının canonical `InstallLocation` kökünü exact package-full-name ile bağlar. İlk activation'dan önce deterministic, bounded ve content-hash'li envanter ile mutation watcher'ı başlar; product ve lifecycle harness süreçleri kapandıktan sonra watcher dispatch kuyruğu drain edilir ve son envanter alınır. Named ADS, reparse traversal, byte-identical root replacement, watcher overflow, mutation event'i veya pre/post farkı fail-closed sonuçtur. Ham install root/path ve iç audit handle'ı evidence'a girmez.

[Run #223 attempt 2 (`32881949188`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32881949188), clean commit `5e60ca6bd60d72e35438a08b18f2d5fe4267f3e9` için locked quality, signed package/install/launch/lifecycle, DPAPI real-user boundary ve required gate zincirini geçirdi. Attempt 1'de yeni audit kodu değil, daha önce de gözlenen generic packaged playback channel-bound timeout'u oluştu; assertion veya budget değiştirilmeden yalnız failed job bir kez yeniden çalıştırıldı.

| Runtime audit alanı | Hosted sonuç |
|---|---|
| Scope | `ExactRegisteredProductPackageInstallLocation` |
| Başlangıç / son entry | `74 / 74` |
| Başlangıç / son file | `70 / 70` |
| Başlangıç / son byte | `85.486.210 / 85.486.210` |
| Başlangıç / son manifest SHA-256 | `97a87159042e8e845b5c495d0257710766e7517da48a85dd01a195dbd5d3ca21` / aynı |
| Mutation event | `0` |
| Watcher overflow | `false` |
| Pre/post inventory equivalent | `true` |
| Audit passed | `true` |
| Sanitized artifact boyutu | `16.757` byte |
| Sanitized artifact SHA-256 | `79ea7afa4db32d4a68c196aa322a8a891fa8f0190dd1b4cad54a4f4e26106441` |

Windows PowerShell 5.1 adversarial self-test ve architecture suite `51/51` geçti. Hosted artifact taraması install path/root/handle sızıntısı bulmadı. Bu checkpoint, o exact package ve runner'daki gözlem penceresi için install-root'un değişmediğini kanıtlar; clean-machine, başka OS/package manager davranışları veya bütün olası filesystem write'ları hakkında evrensel iddia değildir.

## Exact açık blocker seti

Aşağıdaki 16 kodun tamamı açıktır ve evidence'ta ordinal sıralı tutulur:

1. `Arm64ReleaseDecisionPending`
2. `AssetProvenancePending`
3. `CodecIpLegalReviewPending`
4. `CveReviewPending`
5. `LicenseFilePending`
6. `NoticeFilePending`
7. `PartnerCenterPrivateFlightPending`
8. `PrivacyPolicyPending`
9. `ProductionIdentityMigrationPending`
10. `ProductionLifecycleMatrixPending`
11. `ReleaseSigningPending`
12. `ReviewerServiceAndRehearsalPending`
13. `SbomPending`
14. `StoreListingPending`
15. `SupportUrlPending`
16. `WackPending`

## Non-claims ve sonraki kabul sınırı

- Known-pattern source taraması ile exact installed-package runtime audit'i geçti. Runtime sonucu yalnız exact hosted package gözlem penceresindeki deterministic pre/post eşitliğini ve watcher'ın mutation görmediğini kanıtlar; clean VM'de install/update/reset/uninstall matrisi ve bütün olası write yolları ayrıca geçmelidir.
- Exact 23-package inventory teknik dependency drift guard'ıdır; SBOM, root `LICENSE`/`NOTICE`, asset provenance, CVE sonucu, redistribution kabulü veya codec/IP hukuk görüşü değildir.
- Development identity ile mevcut disposable lifecycle kanıtları production identity/PFN, signing lineage, previous-package migration, repair veya private-flight sonucu değildir.
- WACK, Partner Center private submission, privacy/support URL, Store listing/rating/reviewer notes ve geliştirici-owned reviewer service henüz kabul edilmemiştir.
- ARM64 bu checkpoint'te açılmamıştır; Windows release target'ı x64 kalır.

Bu nedenle M15 `IN PROGRESS / BLOCKED` durumundadır. Bu belge teknik baseline checkpoint'ini kaydeder; M15 completion veya Store release kararı değildir.
