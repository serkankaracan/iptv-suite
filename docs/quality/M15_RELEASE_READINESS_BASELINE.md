# M15 MSIX ve Store release-readiness teknik baseline'i

**Durum:** `IN PROGRESS / BLOCKED — technicalBaselinePassed=true; releaseReady=false, 2026-08-26`

## Checkpoint kararı

Bu kayıt, M15'in statik teknik envanter ve exact installed-package runtime audit checkpoint'lerini birlikte taşır. Statik kaynak bağlamı clean commit `0236894f8cc8c6212305f5ba9c294e335f3d031e` ve exact SDK `10.0.302`dir. `eng/Test-WindowsReleaseReadiness.ps1`, `-AllowBlockedInventory` ile teknik baseline'i doğrulayıp bilinen release blocker'larını değiştirmeden sanitized evidence yayımlar. Bu switch blocker'ları kabul etmez veya `releaseReady` sonucunu değiştirmez. Switch verilmezse aynı blocked evidence atomik yayımlandıktan sonra betik `M15ReleaseReadinessBlocked` ile fail-closed durur.

Manifestteki `IptvSuite.LocalDev.6f0d9a64` / `CN=IptvSuite Local Development` yalnız disposable development lineage'ıdır. Store reservation, public package identity, marka hakkı, production signing veya development paketinden production PFN'e migration garantisi değildir.

## İlk clean statik denetim sonucu

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
- Self-test; gerçek source audit'ine ek olarak `.artifacts` dışı/ADS evidence, extra veya yanlış-namespace capability, erken Store association, self-contained/ARM64 RID, false-condition RID, explicit MSBuild import, SDK hook property, otomatik `*.csproj.user`/default veya artifacts-redirected project-extension wildcard import, applicable `Directory.Build.targets`/`Directory.Build.rsp`, nested `Directory.Build.props`/`Directory.Solution.props`, shared multi-architecture override, eksik ya da exclusive-lock altındaki asset, production lock'a yeni LibVLC package adı ve alternatif install-root discovery yüzeylerini fail-closed mutation'larla doğruladı.
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

## Windows MVP release mimarisi kararı

M15 release seti yalnız `x64` olarak sınırlandı. Schema v2 ile kaydedilen x64 disposition güncel release-readiness evidence schema v3'te de `releaseArchitectures=[x64]`, `arm64Disposition=DeferredUntilNativeArm64ChainAccepted`, `architectureImportSurfaceAuditVersion=1` ve `sourceControlledArchitectureImportSurfacePassed=true` değerlerini taşır; production project `Platforms=x64`, `PlatformTarget=x64`, `RuntimeIdentifier=win-x64` ve `AppxBundle=Never` invariant'larını korur. `RuntimeIdentifiers`, `AppxBundlePlatforms`, `win-arm64`, çoklu platform ve bundle açılımı fail-closed reddedilir.

Kaynak-kontrollü MSBuild yüzeyi de karara bağlıdır: dört production project exact `Microsoft.NET.Sdk` kullanır; explicit `Import`/nested `Sdk`/`Target`/`UsingTask`, SDK import-hook veya artifacts-output redirect property, otomatik `*.csproj.user` ya da force-tracked project-extension wildcard import tanımlayamaz. İzin verilen iki generated project-extension adı yalnız NuGet'in `.nuget.g.props`/`.nuget.g.targets` çiftidir; başka mevcut wildcard eşleşmesi reddedilir ve tracked `obj` path denetimi case-insensitive'dir. Exact x64 property düğümleri ve taşıyan `PropertyGroup` koşulsuz/attributesız olmalıdır. Windows project ancestor zincirinde yalnız repository-root `Directory.Build.props` uygulanabilir, `Directory.Build.targets` ile otomatik CLI response dosyaları bulunamaz. Solution zincirinde yalnız root `Directory.Solution.props` uygulanabilir ve `Directory.Solution.targets` bulunamaz. Root `Directory.Build.props`, `Directory.Packages.props` ve `Directory.Solution.props` dosyaları architecture/bundle veya import-control property tanımlayamaz. Bu denetim source-controlled import yüzeyi içindir; dış command-line property, makine-geneli SDK/import veya değiştirilmiş build invocation kanıtı değildir. Release build documented locked komutla yürütülmelidir.

Windows PowerShell 5.1 release-readiness self-test'i, architecture `51/51` ve normal Windows kullanıcı bağlamındaki full quality gate `568/568 × 2` geçti. Restricted sandbox token'ında iki HTTPS fixture PFX importu `Access denied` ile durdu; aynı değişiklik ve exact SDK ile normal kullanıcı bağlamında integration `158/158 × 2` geçtiğinden bu ürün/test assertion failure'ı değildir.

Bu karar ARM64 desteği veya emulation acceptance'ı değildir. ARM64 ancak native build/package, gerçek ARM64 cihaz playback/lifecycle/resource ve Store zinciri ayrı acceptance ile geçerse yeniden açılır. Böyle bir kanıt olmadığı için Windows MVP'de doğrulanmamış ARM64 support sözü verilmez; `Arm64ReleaseDecisionPending` kapanır ve diğer blocker'lar etkilenmez.

## Package-bound SBOM hosted kabul checkpoint'i

Package-bound SBOM akışı, exact pin'li `Microsoft.Sbom.DotNetTool 4.1.5` ile signed application MSIX ve exact Windows App Runtime `x64` dependency MSIX'ini tek release setine bağlayan companion SPDX `2.2` üretir. Nupkg, shim ve çalıştırılan extracted tool payload'ı birebir bağlıdır; iki MSIX imzası fail-closed doğrulanır. Resmî aracın dokunulmamış çıktısı önce doğrulanır; ardından iki MSIX'in identity/hash bağları, exact production component seti ve gerekli release-set ilişkileri repository-owned sıkı doğrulamayla zenginleştirilip yeniden denetlenir.

Gerçek araçla iki sentetik MSIX kullanan local uçtan uca prova `PASS`, architecture suite `52/52 PASS` ve full quality gate `569/569 × 2 PASS` sonucundadır. Ardından clean commit `12b1e95e8c3df04c42482daa52bdabd81abe1701` için [run `#226` (`32897767622`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32897767622) ve package job `97966018579`, signed application MSIX + exact x64 Windows App Runtime release setinin üretim, resmî doğrulama, sıkı doğrulama ve sanitized artifact upload zincirini `PASS` tamamladı. İndirilen artifact ile iç üyelerin hash'leri doğrulandı; source-controlled schema-v1 acceptance ledger'ı bu exact hosted sonucu release-readiness evidence schema v3'e bağladı. Producer summary'sindeki tarihsel `HostedAcceptancePending` / `SbomPending=true` candidate durumu değiştirilmedi; ayrı ledger yalnız bu run için `SbomPending` blocker'ını kapatır.

| Kabul alanı | Exact değer |
|---|---|
| Run / kaynak | `#226` / `32897767622`; commit `12b1e95e8c3df04c42482daa52bdabd81abe1701` |
| Package job | `97966018579` — `Packaged install and launch smoke`, `success` |
| Artifact | ID `9582332831`; `windows-msix-smoke-evidence`; SHA-256 `342fad95524b3624de842889428d4e2921ef3a481d3e8dd0b13ace27d932f106` |
| İç SBOM | SPDX `2.2`; SHA-256 `97d7e4aebedffbaae95a2d4e36f01bf1efff79dfe34cf001e7c787d637bffd39` |
| Summary / package evidence | SHA-256 `d1bc7587ad3b5cbca42c78baad4c49a44f54199826f404b6b0849cf18435c5cd` / `63644f96edb507be86980fb983fa69feef116652ea9f03d29da5f600414c3b04` |
| Araç / validation | `Microsoft.Sbom.DotNetTool 4.1.5`; official `true`; strict `true` |
| Exact sayaçlar | file/component/package/relationship = `2/24/27/43` |
| Acceptance ledger | SHA-256 `853f1c702b9acc5e500d232688a22322aaeb6c3ff3f497a2fff269abc83fb904`; kapatılan tek blocker `SbomPending` |
| Package-producing snapshot | Beş kök build girdisi + solution + `apps/windows/src` altındaki bütün production girdileri, exact `111` dosya; SHA-256 `465b2a74eba4f6c45871d57e4e042772a5a30024ff7e45ac7b9563571f101d9d` |
| Contract source seti | Workflow, tool config ve package/SBOM/install-root scriptlerinden exact `7` dosya; SHA-256 `2b9cfe5d859ed070c47e2e74591b5567a5a8bc3a2006d2a5d775428f8a54c9ce` |

Snapshot metin girdilerini strict UTF-8/LF, PNG/ICO asset'lerini ham byte olarak canonicalize eder; bounded exact file reads, lazy bounded traversal ve yayımdan önce ikinci snapshot doğrulaması kullanır. Dosya ekleme, silme, içerik değişimi, daha yakın central-package importu, reparse veya sınır aşımı eski hosted kabulü fail-closed geçersiz kılar. Ledger duplicate JSON property'lerini reddeder ve workflow tamamlanma zamanını yalnız `runCompletedAtUtc` olarak adlandırır. Bu teknik package-bound companion SPDX kabulü root `LICENSE`/`NOTICE`, redistribution kararı, asset provenance, CVE sonucu, codec/IP hukuk incelemesi, production signing veya Store kabulü değildir. Bu kapılar aşağıdaki 14 blocker içinde açık kalır.

## Exact açık blocker seti

Aşağıdaki 14 kodun tamamı açıktır ve evidence'ta ordinal sıralı tutulur:

1. `AssetProvenancePending`
2. `CodecIpLegalReviewPending`
3. `CveReviewPending`
4. `LicenseFilePending`
5. `NoticeFilePending`
6. `PartnerCenterPrivateFlightPending`
7. `PrivacyPolicyPending`
8. `ProductionIdentityMigrationPending`
9. `ProductionLifecycleMatrixPending`
10. `ReleaseSigningPending`
11. `ReviewerServiceAndRehearsalPending`
12. `StoreListingPending`
13. `SupportUrlPending`
14. `WackPending`

## Non-claims ve sonraki kabul sınırı

- Known-pattern source taraması ile exact installed-package runtime audit'i geçti. Runtime sonucu yalnız exact hosted package gözlem penceresindeki deterministic pre/post eşitliğini ve watcher'ın mutation görmediğini kanıtlar; clean VM'de install/update/reset/uninstall matrisi ve bütün olası write yolları ayrıca geçmelidir.
- Exact 23-package inventory teknik dependency drift guard'ıdır ve tek başına SBOM değildir. Hosted kabul edilmiş package-bound companion SPDX de root `LICENSE`/`NOTICE`, asset provenance, CVE sonucu, redistribution kabulü veya codec/IP hukuk görüşü değildir.
- Development identity ile mevcut disposable lifecycle kanıtları production identity/PFN, signing lineage, previous-package migration, repair veya private-flight sonucu değildir.
- WACK, Partner Center private submission, privacy/support URL, Store listing/rating/reviewer notes ve geliştirici-owned reviewer service henüz kabul edilmemiştir.
- ARM64 Windows MVP release setinde yoktur; deferred disposition gelecekte native ARM64 acceptance yapılmadan destek iddiasına dönüştürülemez.
- Source-controlled MSBuild audit'i dış command-line/global-machine import yüzeyini doğrulamaz; release build invocation ve clean runner ayrıca bağlanmalıdır.

Bu nedenle M15 `IN PROGRESS / BLOCKED` durumundadır. Bu belge teknik baseline checkpoint'ini kaydeder; M15 completion veya Store release kararı değildir.
