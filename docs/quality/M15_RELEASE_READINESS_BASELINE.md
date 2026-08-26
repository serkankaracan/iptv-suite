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

M15 release seti yalnız `x64` olarak sınırlandı. Schema v2 ile kaydedilen x64 disposition güncel release-readiness evidence schema v4'te de `releaseArchitectures=[x64]`, `arm64Disposition=DeferredUntilNativeArm64ChainAccepted`, `architectureImportSurfaceAuditVersion=1` ve `sourceControlledArchitectureImportSurfacePassed=true` değerlerini taşır; production project `Platforms=x64`, `PlatformTarget=x64`, `RuntimeIdentifier=win-x64` ve `AppxBundle=Never` invariant'larını korur. `RuntimeIdentifiers`, `AppxBundlePlatforms`, `win-arm64`, çoklu platform ve bundle açılımı fail-closed reddedilir.

Kaynak-kontrollü MSBuild yüzeyi de karara bağlıdır: dört production project exact `Microsoft.NET.Sdk` kullanır; explicit `Import`/nested `Sdk`/`Target`/`UsingTask`, SDK import-hook veya artifacts-output redirect property, otomatik `*.csproj.user` ya da force-tracked project-extension wildcard import tanımlayamaz. İzin verilen iki generated project-extension adı yalnız NuGet'in `.nuget.g.props`/`.nuget.g.targets` çiftidir; başka mevcut wildcard eşleşmesi reddedilir ve tracked `obj` path denetimi case-insensitive'dir. Exact x64 property düğümleri ve taşıyan `PropertyGroup` koşulsuz/attributesız olmalıdır. Windows project ancestor zincirinde yalnız repository-root `Directory.Build.props` uygulanabilir, `Directory.Build.targets` ile otomatik CLI response dosyaları bulunamaz. Solution zincirinde yalnız root `Directory.Solution.props` uygulanabilir ve `Directory.Solution.targets` bulunamaz. Root `Directory.Build.props`, `Directory.Packages.props` ve `Directory.Solution.props` dosyaları architecture/bundle veya import-control property tanımlayamaz. Bu denetim source-controlled import yüzeyi içindir; dış command-line property, makine-geneli SDK/import veya değiştirilmiş build invocation kanıtı değildir. Release build documented locked komutla yürütülmelidir.

Windows PowerShell 5.1 release-readiness self-test'i, architecture `51/51` ve normal Windows kullanıcı bağlamındaki full quality gate `568/568 × 2` geçti. Restricted sandbox token'ında iki HTTPS fixture PFX importu `Access denied` ile durdu; aynı değişiklik ve exact SDK ile normal kullanıcı bağlamında integration `158/158 × 2` geçtiğinden bu ürün/test assertion failure'ı değildir.

Bu karar ARM64 desteği veya emulation acceptance'ı değildir. ARM64 ancak native build/package, gerçek ARM64 cihaz playback/lifecycle/resource ve Store zinciri ayrı acceptance ile geçerse yeniden açılır. Böyle bir kanıt olmadığı için Windows MVP'de doğrulanmamış ARM64 support sözü verilmez; `Arm64ReleaseDecisionPending` kapanır ve diğer blocker'lar etkilenmez.

## Package-bound SBOM hosted kabul checkpoint'i

Package-bound SBOM akışı, exact pin'li `Microsoft.Sbom.DotNetTool 4.1.5` ile signed application MSIX ve exact Windows App Runtime `x64` dependency MSIX'ini tek release setine bağlayan companion SPDX `2.2` üretir. Nupkg, shim ve çalıştırılan extracted tool payload'ı birebir bağlıdır; iki MSIX imzası fail-closed doğrulanır. Resmî aracın dokunulmamış çıktısı önce doğrulanır; ardından iki MSIX'in identity/hash bağları, exact production component seti ve gerekli release-set ilişkileri repository-owned sıkı doğrulamayla zenginleştirilip yeniden denetlenir.

Gerçek araçla iki sentetik MSIX kullanan local uçtan uca prova `PASS`, architecture suite `52/52 PASS` ve full quality gate `569/569 × 2 PASS` sonucundadır. Ardından clean commit `12b1e95e8c3df04c42482daa52bdabd81abe1701` için [run `#226` (`32897767622`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32897767622) ve package job `97966018579`, signed application MSIX + exact x64 Windows App Runtime release setinin üretim, resmî doğrulama, sıkı doğrulama ve sanitized artifact upload zincirini `PASS` tamamladı. İndirilen artifact ile iç üyelerin hash'leri doğrulandı; source-controlled schema-v1 acceptance ledger'ı bu exact hosted sonucu güncel release-readiness evidence schema v4'e bağladı. Producer summary'sindeki tarihsel `HostedAcceptancePending` / `SbomPending=true` candidate durumu değiştirilmedi; ayrı ledger yalnız bu run için `SbomPending` blocker'ını kapatır.

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

Snapshot metin girdilerini strict UTF-8/LF, PNG/ICO asset'lerini ham byte olarak canonicalize eder; bounded exact file reads, lazy bounded traversal ve yayımdan önce ikinci snapshot doğrulaması kullanır. Dosya ekleme, silme, içerik değişimi, daha yakın central-package importu, reparse veya sınır aşımı eski hosted kabulü fail-closed geçersiz kılar. Ledger duplicate JSON property'lerini reddeder ve workflow tamamlanma zamanını yalnız `runCompletedAtUtc` olarak adlandırır. Bu teknik package-bound companion SPDX kabulü tek başına root `LICENSE`/`NOTICE`, redistribution kararı, asset provenance, known-vulnerability sonucu, codec/IP hukuk incelemesi, production signing veya Store kabulü değildir. Ayrı known-vulnerability kabulü aşağıdadır; kalan kapılar 13 blocker içinde açık kalır.

## Known-vulnerability producer checkpoint'i

`windows-cve-review.yml`, mevcut accepted package/SBOM workflow ve ledger'ından ayrı bir producer lane'dir. Exact SDK `10.0.302` ile yalnız `apps/windows/src/IptvSuite.Windows/IptvSuite.Windows.csproj` leaf'ini, dedicated config'teki exact package source ve `https://data.nuget.org/v3/index.json` audit source'u üzerinden denetler. Restore; yeni ve repository `.artifacts` altında bounded `NUGET_HTTP_CACHE_PATH`, `NUGET_PACKAGES` ve `DOTNET_CLI_HOME`, `--no-http-cache`, locked evaluation, `RestoreUseStaticGraphEvaluation=false`, `NuGetAudit=true`, `NuGetAuditMode=all`, `NuGetAuditLevel=low` ve fatal `NU1900`–`NU1905` contract'ıyla yürür. Authoritative MSBuild sonucu exact `RestoreProjectCount=4`, `RestoreSkippedCount=0`, `RestoreProjectsAuditedCount=4` olmalıdır.

Producer iki ayrı JSON v1 çıktısını fail-closed okur. Filtresiz `--include-transitive` envanteri exact bir project/framework, `2` top-level + `21` transitive = `23` package ID/sürümünü dört strict UTF-8/schema-v2 production lockfile'ındaki content-hash bağlı aggregate graph ile birebir eşleştirir. Ardından `--vulnerable --include-transitive` çıktısı exact source/project/parameter/empty-package şeklinde olmalı ve `knownVulnerabilityCount=0` üretmelidir. `NuGetAuditSuppress`, warning suppression, audit/cache/fallback/import gölgelemesi ve beklenmeyen ancestor build/config yüzeyi reddedilir; 16 kritik girdi restore öncesi, audit öncesi ve evidence öncesi aynı snapshot'ta kalmalı ve worktree sonunda yeniden temiz olmalıdır. Duplicate JSON property, schema drift'i, yanlış source/project/framework, stderr, nonzero exit, timeout, invalid UTF-8, canlı output/cache bound aşımı veya herhangi bilinen vulnerability sonucu reddedilir. Artifact yalnız repository-relative project adı, sayaç ve hash taşır; raw CLI çıktısını, absolute path'i ya da advisory URL'sini taşımaz. Offline Windows PowerShell 5.1 adversarial self-test'i `PASS`tir.

Clean commit `2053f4099819b3bb19bb9dd3370d60f0161098f1` için [run `#4` (`32912296486`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32912296486) ve job `98008739618`, bu contract'ı `PASS` tamamladı. Artifact ID `9586961516`, digest'i `4d0c0a2a928038721053a61a0931b6e1fcfdf57053383fa0db0c0b9bccbb9210`; tek `last-success.json` üyesi `2.403` byte ve SHA-256 `f62f147842bf2e8d3951fbaca103a6d4b2d485fa269fd972d7ae3360f754c553`dır. Evidence exact `4/0/4` audited restore, dört project/lockfile, `2 + 21 = 23` package, sıfır suppression/override ve direct/transitive/toplam `0/0/0` bilinen vulnerability kaydeder.

SHA-256 `8c60360ae0dac240ef801688a04472266dabd16bfb1069a841b365b61c89a197` source-controlled acceptance ledger'ı önceki SBOM ledger'ına zincirlenir; exact 16 contract girdisini (`6b09978b5ee3ffc4d14e09458724a3d18fd1d23c5ec9ab3134dd25bfc7e91ff3`) ve production graph'ını (`760562b81e0097913e1daf4ec88c67596337dd6636ed6d88c8f645424dc50b6e`) yeniden hesaplatır. Validation yayımdan önce ikinci kez çalışır. Freshness süresi ledger-controlled değildir: validator run tamamlanmasından başlayan en fazla yedi günü uygular; bu exact run için `2026-09-01T23:48:39Z` sonrasında yalnız `CveReviewPending` yeniden açılır. Hash/schema/provenance, suppression, contract veya graph drift'i teknik invariant olarak fail-closed reddedilir.

Bu hosted kabul fresh olduğu sürece yalnız `CveReviewPending` blocker'ını kapatır ve schema-v4 evidence'ta 13 blocker bırakır. Producer evidence'ındaki tarihsel `producerCheckpointOnly=true` ve `cveReviewPending=true` alanları değiştirilmez; ayrı ledger kabul kararını taşır. `0` bulgu yalnız tarama anında sorgulanan exact graph için bilinen vulnerability bulunmadığı anlamına gelir; genel “CVE-free”, lisans/NOTICE, redistribution veya codec/IP hukuk kabulü değildir. Final release öncesinde en fazla 24 saatlik yeni audit ayrıca gereklidir; required-check/perpetual freshness kararı bu checkpoint'in iddiası değildir.

## Pre-asset SBOM/CVE kabul yenilemesi — tarihsel

İlk `#226` SBOM ve `#4` CVE kabulleri yukarıda tarihsel olarak korunur. DWM sampler'ın poller'ın kaçırdığı çoklu refresh aralıklarını tek yavaş frame saymaması ve strict maximum'u yalnız exact single-refresh aralıklarından üretmesi sağlandı; p95 `33,3 ms`, drop `%1` ve maximum `200 ms` kabul eşikleri değiştirilmedi. Ardından clean `e7564aa9382fb12b10869866811e75847d1f6f16` üzerinde bağımsız [Windows package SBOM producer run `#1` (`32918708589`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32918708589) tamamen `success` tamamlandı. Job `98027737145`, artifact `9589197924`, artifact SHA-256 `13c330fc6203838f4a6108281f5daa76cd31f6f85d2e2d0a942bb147fc7921a2`dır. Bu pre-asset SBOM acceptance ledger SHA-256 `9eaf890f027f3485e89851701cfbfe4676afb44be4a918f525290c58385d9a21`; dedicated workflow dahil exact yedi-file contract SHA-256 `e5324fafa743cd003af480ab2c521ae2181a306fd67704c221d905025a7fccae`dir. Production input seti `10 / 293481fe2194c6f1fde3f667cf45872f4790e0b5955e17ac88c2d16a885b81df`, package-producing snapshot `111 / 465b2a74eba4f6c45871d57e4e042772a5a30024ff7e45ac7b9563571f101d9d` olarak değişmedi.

Bu exact SBOM ledger'ına zincirlenen clean `15826e5ecad4a4b5737aa7bf39d28c62b0e71de7` için [known-vulnerability producer run `#10` (`32919649968`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32919649968), job `98030516890` ile `success` tamamlandı. Artifact ID `9589408603`, digest'i `45e628c6dcfee4ddd9cc1b45c93074883c1f99e78f0d9580decc848f1f69d4ad`; `last-success.json` SHA-256 `8670c3e3442cd7342a0bb6e51f44fc4b31a1cf29f7da27ad523e87e6f7a8d316`dır. Exact `4/0/4` audited restore, `2 + 21 = 23` package graph ve direct/transitive/toplam `0/0/0` bilinen vulnerability sonucu değişmedi. Bu pre-asset CVE acceptance ledger SHA-256 `01180e5c90816fda2f1712a2ecd5ed634399caee76d68b7ffeb47c178e2aa208`; freshness sonu `2026-09-02T01:38:42Z`dir. O checkpoint'te bu renewal yalnız `SbomPending` ve freshness süresince `CveReviewPending` blocker'larını kapatmış; `releaseReady=false`, 13 blocker ve bütün hukuk/Store/non-claim sınırlarını aynen bırakmıştır.

Resmî dayanaklar (erişim: `2026-08-26`): [.NET 10 `dotnet package list`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list), [NuGet JSON v1 makine-okunur çıktı spec'i](https://github.com/NuGet/Home/wiki/%5BSpec%5D-Machine-readable-output-for-dotnet-list-package) ve [NuGet Audit / `VulnerabilityInfo` davranışı](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages).

## Deterministic package asset provenance ve güncel SBOM/CVE kabulü

Manifestin exact sekiz PNG/ICO package asset'i artık `eng/New-WindowsProductionAssets.ps1` içindeki source-controlled `WindowsProductionAssets-Rgba8Filter0FixedHuffmanLz77-PngFrameIco-v1` tarifiyle üretilir. Tarif source asset dependency, üçüncü taraf asset girdisi, font, metin veya trademark girdisi kullanmaz; iki ayrı output root'unda byte-identical üretim ile existing-root verification destekler. Strict `eng/windows-production-asset-provenance.json` ledger'ı exact sekiz path'i, PNG dimension'larını, ICO frame setini, byte length/SHA-256 değerlerini, generator SHA-256 `4ac099e8da587b5df61817ab92071235e4e91408d891f5cafa3037599d7f603b` ve canonical asset-set SHA-256 `6338f26af851a45eb4c7da593430ef1eab5a34afa6013365c2621fbfa0957777` değerini fail-closed bağlar.

Bu bağ yalnız exact package asset origin'ini kanıtlar. Görseller `developmentPlaceholderOnly=true` olan nötr teknik placeholder'lardır; `productionBrandApproved`, `copyrightOwnershipDetermined`, `redistributionDecisionComplete` ve `legalReviewComplete` değerlerinin tamamı `false` kalır. Dolayısıyla bu checkpoint public marka/logo onayı, copyright ownership, redistribution hakkı, root `LICENSE`/`NOTICE`, Store/legal ya da codec-IP kapanışı değildir.

Asset byte'ları önceki 111-dosyalık package-producing snapshot'ını değiştirdi. Yeni clean commit `b78c6c0512a91c9ab1fd86e18f703856a45a4b53` için [Windows package SBOM producer run `#2` (`32923642659`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32923642659), job `98042026161` ile `2026-08-26T02:48:32Z` zamanında `success` tamamlandı. Artifact ID `9590841342`, digest'i `66bb75c354f9c08c0002f0f3d0a390ecfe0326284928e2da91dc9836ffe41a33`; application MSIX SHA-256 `b23fba0be6bc56e450f0e9a45d5822dc3b400160f29c89c77a9df2231b136a4c`, SPDX SBOM SHA-256 `eeefe6c741b41fc630a089d41445498e919cc1f49ed4f5f3350c87439a789e16` ve exact 111-file package-producing snapshot SHA-256 `6b068e8ccb651159dc2bad754f370ba428c6c3087c01c6bc7e419e6095cfadcc`dır. Acceptance ledger raw SHA-256 `5ad87061b0586ad5d84f8f38d244bb3e9131358e27905bffd1f8b2648259bb47`dir. Official ve strict validation birlikte `PASS`tir; deterministic asset provenance bağıyla `AssetProvenancePending`, bu asset-bound package acceptance ile `SbomPending` teknik zincirde kapanır.

Yukarıdaki run `#1`/run `#10` ve ledger'ları pre-asset tarihsel accepted checkpoint olarak korunur. Yeni SBOM acceptance ledger raw SHA-256 `5ad87061b0586ad5d84f8f38d244bb3e9131358e27905bffd1f8b2648259bb47` değerine zincirlenen clean `58e0b615d1c531a9c94583772d4d80a5740eb3e3` için [known-vulnerability producer run `#13` (`32924589536`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32924589536), job `98044789701` ile `2026-08-26T02:58:07Z` zamanında `success` tamamlandı. Artifact ID `9591048689`, boyutu `1.120` byte, digest'i `29e3c4136c05c42849f7141530ed0ce073c279d4ad617c007c2733ccfe5e1ecb`; `last-success.json` SHA-256 `d8ef63b6a66c4cd8fa415c8f8d9f857f90125dfbd759c89dde5616cae4ee64f0`dır. Acceptance ledger raw SHA-256 `60aa4cf4a7d346b18e1301ec8e58151afed5c59ceea4e17cb308e21864c101cd`dir. Evidence `2026-08-26T02:57:54.2270308Z` zamanında gözlemlenen exact değişmemiş contract/graph için `4/0/4` audit, `2 + 21 = 23` package ve direct/transitive/toplam `0/0/0` known-vulnerability sonucu taşır; official ve strict validation `PASS`tir. Freshness sonu `2026-09-02T02:58:07Z`dir.

Güncel schema-v5 readiness sonucu `technicalBaselinePassed=true`, `releaseReady=false` ve exact 12 remaining blocker'dır. `AssetProvenancePending`, `SbomPending` ve `CveReviewPending` teknik olarak kapanmıştır. Bu teknik zincir M15 completion, perpetual freshness veya genel “CVE-free” sonucu değildir.

## Tarihsel pre-asset schema-v4 exact açık blocker seti

Aşağıdaki 13 kod, asset byte değişiminden önceki son hosted-kabul schema-v4 evidence'ında açıktır ve ordinal sıralı tutulmuştur:

1. `AssetProvenancePending`
2. `CodecIpLegalReviewPending`
3. `LicenseFilePending`
4. `NoticeFilePending`
5. `PartnerCenterPrivateFlightPending`
6. `PrivacyPolicyPending`
7. `ProductionIdentityMigrationPending`
8. `ProductionLifecycleMatrixPending`
9. `ReleaseSigningPending`
10. `ReviewerServiceAndRehearsalPending`
11. `StoreListingPending`
12. `SupportUrlPending`
13. `WackPending`

## Güncel schema-v5 exact açık blocker seti

Aşağıdaki 12 kod current evidence'ta açıktır ve ordinal sıralıdır:

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

## Non-claims ve sonraki kabul sınırı

- Known-pattern source taraması ile exact installed-package runtime audit'i geçti. Runtime sonucu yalnız exact hosted package gözlem penceresindeki deterministic pre/post eşitliğini ve watcher'ın mutation görmediğini kanıtlar; clean VM'de install/update/reset/uninstall matrisi ve bütün olası write yolları ayrıca geçmelidir.
- Exact 23-package inventory teknik dependency drift guard'ıdır ve tek başına SBOM değildir. Tarihsel hosted package-bound companion SPDX ile ayrı, fresh known-vulnerability incelemesi root `LICENSE`/`NOTICE`, genel “CVE-free” iddiası, redistribution kabulü veya codec/IP hukuk görüşü değildir; yeni deterministic asset-origin bağı da bu hukuk kapılarını kapatmaz.
- Development identity ile mevcut disposable lifecycle kanıtları production identity/PFN, signing lineage, previous-package migration, repair veya private-flight sonucu değildir.
- WACK, Partner Center private submission, privacy/support URL, Store listing/rating/reviewer notes ve geliştirici-owned reviewer service henüz kabul edilmemiştir.
- ARM64 Windows MVP release setinde yoktur; deferred disposition gelecekte native ARM64 acceptance yapılmadan destek iddiasına dönüştürülemez.
- Source-controlled MSBuild audit'i dış command-line/global-machine import yüzeyini doğrulamaz; release build invocation ve clean runner ayrıca bağlanmalıdır.

Bu nedenle M15 `IN PROGRESS / BLOCKED` durumundadır. Bu belge teknik baseline checkpoint'ini kaydeder; M15 completion veya Store release kararı değildir.
