# M15 MSIX ve Store release-readiness teknik baseline'i

**Durum:** `IN PROGRESS / BLOCKED — technicalBaselinePassed=true; releaseReady=false, 2026-08-25`

## Checkpoint kararı

Bu kayıt, M15'in ilk statik teknik envanter checkpoint'idir. Kaynak bağlamı clean commit `0236894f8cc8c6212305f5ba9c294e335f3d031e` ve exact SDK `10.0.302`dir. `eng/Test-WindowsReleaseReadiness.ps1`, `-AllowBlockedInventory` ile teknik baseline'i doğrulayıp bilinen release blocker'larını değiştirmeden sanitized evidence yayımlar. Bu switch blocker'ları kabul etmez veya `releaseReady` sonucunu değiştirmez. Switch verilmezse aynı blocked evidence atomik yayımlandıktan sonra betik `M15ReleaseReadinessBlocked` ile fail-closed durur.

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

- Known-pattern source taramasının geçmesi, runtime'da veya clean VM'de install directory'ye hiç yazılmadığının kanıtı değildir. Runtime write audit ve clean install/update/reset/uninstall matrisi ayrıca geçmelidir.
- Exact 23-package inventory teknik dependency drift guard'ıdır; SBOM, root `LICENSE`/`NOTICE`, asset provenance, CVE sonucu, redistribution kabulü veya codec/IP hukuk görüşü değildir.
- Development identity ile mevcut disposable lifecycle kanıtları production identity/PFN, signing lineage, previous-package migration, repair veya private-flight sonucu değildir.
- WACK, Partner Center private submission, privacy/support URL, Store listing/rating/reviewer notes ve geliştirici-owned reviewer service henüz kabul edilmemiştir.
- ARM64 bu checkpoint'te açılmamıştır; Windows release target'ı x64 kalır.

Bu nedenle M15 `IN PROGRESS / BLOCKED` durumundadır. Bu belge teknik baseline checkpoint'ini kaydeder; M15 completion veya Store release kararı değildir.
