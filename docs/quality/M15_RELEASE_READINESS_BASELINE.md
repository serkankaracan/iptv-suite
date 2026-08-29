# M15 MSIX ve Store release-readiness teknik baseline'i

**Durum:** `HISTORICAL HOSTED SBOM/CVE RENEWED; ADR-008/ADR-009 successor closure OPEN; releaseReady=false, 2026-08-29`

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

M15 release seti yalnız `x64` olarak sınırlandı. Schema v2 ile kaydedilen x64 disposition güncel release-readiness evidence schema v7'de de `releaseArchitectures=[x64]`, `arm64Disposition=DeferredUntilNativeArm64ChainAccepted`, `architectureImportSurfaceAuditVersion=1` ve `sourceControlledArchitectureImportSurfacePassed=true` değerlerini taşır; production project `Platforms=x64`, `PlatformTarget=x64`, `RuntimeIdentifier=win-x64` ve `AppxBundle=Never` invariant'larını korur. `RuntimeIdentifiers`, `AppxBundlePlatforms`, `win-arm64`, çoklu platform ve bundle açılımı fail-closed reddedilir.

Kaynak-kontrollü MSBuild yüzeyi de karara bağlıdır: dört production project exact `Microsoft.NET.Sdk` kullanır; explicit `Import`/nested `Sdk`/`Target`/`UsingTask`, SDK import-hook veya artifacts-output redirect property, otomatik `*.csproj.user` ya da force-tracked project-extension wildcard import tanımlayamaz. İzin verilen iki generated project-extension adı yalnız NuGet'in `.nuget.g.props`/`.nuget.g.targets` çiftidir; başka mevcut wildcard eşleşmesi reddedilir ve tracked `obj` path denetimi case-insensitive'dir. Exact x64 property düğümleri ve taşıyan `PropertyGroup` koşulsuz/attributesız olmalıdır. Windows project ancestor zincirinde yalnız repository-root `Directory.Build.props` uygulanabilir, `Directory.Build.targets` ile otomatik CLI response dosyaları bulunamaz. Solution zincirinde yalnız root `Directory.Solution.props` uygulanabilir ve `Directory.Solution.targets` bulunamaz. Root `Directory.Build.props`, `Directory.Packages.props` ve `Directory.Solution.props` dosyaları architecture/bundle veya import-control property tanımlayamaz. Bu denetim source-controlled import yüzeyi içindir; dış command-line property, makine-geneli SDK/import veya değiştirilmiş build invocation kanıtı değildir. Release build documented locked komutla yürütülmelidir.

Windows PowerShell 5.1 release-readiness self-test'i, architecture `51/51` ve normal Windows kullanıcı bağlamındaki full quality gate `568/568 × 2` geçti. Restricted sandbox token'ında iki HTTPS fixture PFX importu `Access denied` ile durdu; aynı değişiklik ve exact SDK ile normal kullanıcı bağlamında integration `158/158 × 2` geçtiğinden bu ürün/test assertion failure'ı değildir.

Bu karar ARM64 desteği veya emulation acceptance'ı değildir. ARM64 ancak native build/package, gerçek ARM64 cihaz playback/lifecycle/resource ve Store zinciri ayrı acceptance ile geçerse yeniden açılır. Böyle bir kanıt olmadığı için Windows MVP'de doğrulanmamış ARM64 support sözü verilmez; `Arm64ReleaseDecisionPending` kapanır ve diğer blocker'lar etkilenmez.

## Package-bound SBOM hosted kabul checkpoint'i

Package-bound SBOM akışı, exact pin'li `Microsoft.Sbom.DotNetTool 4.1.5` ile signed application MSIX ve exact Windows App Runtime `x64` dependency MSIX'ini tek release setine bağlayan companion SPDX `2.2` üretir. Nupkg, shim ve çalıştırılan extracted tool payload'ı birebir bağlıdır; iki MSIX imzası fail-closed doğrulanır. Resmî aracın dokunulmamış çıktısı önce doğrulanır; ardından iki MSIX'in identity/hash bağları, exact production component seti ve gerekli release-set ilişkileri repository-owned sıkı doğrulamayla zenginleştirilip yeniden denetlenir.

Gerçek araçla iki sentetik MSIX kullanan local uçtan uca prova `PASS`, architecture suite `52/52 PASS` ve full quality gate `569/569 × 2 PASS` sonucundadır. Ardından clean commit `12b1e95e8c3df04c42482daa52bdabd81abe1701` için [run `#226` (`32897767622`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32897767622) ve package job `97966018579`, signed application MSIX + exact x64 Windows App Runtime release setinin üretim, resmî doğrulama, sıkı doğrulama ve sanitized artifact upload zincirini `PASS` tamamladı. İndirilen artifact ile iç üyelerin hash'leri doğrulandı; source-controlled schema-v1 acceptance ledger'ı bu exact hosted sonucu o checkpoint'teki release-readiness evidence schema v4'e bağladı. Producer summary'sindeki tarihsel `HostedAcceptancePending` / `SbomPending=true` candidate durumu değiştirilmedi; ayrı ledger yalnız bu run için `SbomPending` blocker'ını kapatır.

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

SHA-256 `8c60360ae0dac240ef801688a04472266dabd16bfb1069a841b365b61c89a197` source-controlled acceptance ledger'ı önceki SBOM ledger'ına zincirlenir; exact 16 contract girdisini (`6b09978b5ee3ffc4d14e09458724a3d18fd1d23c5ec9ab3134dd25bfc7e91ff3`) ve production graph'ını (`760562b81e0097913e1daf4ec88c67596337dd6636ed6d88c8f645424dc50b6e`) yeniden hesaplatır. Validation yayımdan önce ikinci kez çalışır. Teknik freshness süresi ledger-controlled değildir: validator run tamamlanmasından başlayan en fazla yedi günü uygular; bu exact run için `2026-09-01T23:48:39Z` sonrasında `freshAtEvaluation=false` olur. Hash/schema/provenance, suppression, contract veya graph drift'i teknik invariant olarak fail-closed reddedilir.

Hosted teknik kabulün `freshAtEvaluation` alanı run completion'dan başlayan yedi günlük pencereyi korur. Schema-v6 evidence ayrıca `finalReleaseMaximumAgeHours=24` ve `finalReleaseFreshAtEvaluation` alanlarını taşır. Final-release blocker kapanışı teknik freshness'ten daha dardır: `CveReviewPending` yalnız ikinci alan `true` ise kapanır; exact 24 saat sınırı dahildir, `+1 saniye` blocker'ı yeniden açar. Böylece 24 saatten eski fakat yedi gün içinde kalan kayıt teknik kabul olarak fresh kalırken final release için yeterli sayılmaz. Producer evidence'ındaki tarihsel `producerCheckpointOnly=true` ve `cveReviewPending=true` alanları değiştirilmez; ayrı ledger kabul kararını taşır. `0` bulgu yalnız tarama anında sorgulanan exact graph için bilinen vulnerability bulunmadığı anlamına gelir; genel “CVE-free”, lisans/NOTICE, redistribution veya codec/IP hukuk kabulü değildir. Required-check/perpetual freshness kararı bu checkpoint'in iddiası değildir.

## Pre-asset SBOM/CVE kabul yenilemesi — tarihsel

İlk `#226` SBOM ve `#4` CVE kabulleri yukarıda tarihsel olarak korunur. DWM sampler'ın poller'ın kaçırdığı çoklu refresh aralıklarını tek yavaş frame saymaması ve strict maximum'u yalnız exact single-refresh aralıklarından üretmesi sağlandı; p95 `33,3 ms`, drop `%1` ve maximum `200 ms` kabul eşikleri değiştirilmedi. Ardından clean `e7564aa9382fb12b10869866811e75847d1f6f16` üzerinde bağımsız [Windows package SBOM producer run `#1` (`32918708589`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32918708589) tamamen `success` tamamlandı. Job `98027737145`, artifact `9589197924`, artifact SHA-256 `13c330fc6203838f4a6108281f5daa76cd31f6f85d2e2d0a942bb147fc7921a2`dır. Bu pre-asset SBOM acceptance ledger SHA-256 `9eaf890f027f3485e89851701cfbfe4676afb44be4a918f525290c58385d9a21`; dedicated workflow dahil exact yedi-file contract SHA-256 `e5324fafa743cd003af480ab2c521ae2181a306fd67704c221d905025a7fccae`dir. Production input seti `10 / 293481fe2194c6f1fde3f667cf45872f4790e0b5955e17ac88c2d16a885b81df`, package-producing snapshot `111 / 465b2a74eba4f6c45871d57e4e042772a5a30024ff7e45ac7b9563571f101d9d` olarak değişmedi.

Bu exact SBOM ledger'ına zincirlenen clean `15826e5ecad4a4b5737aa7bf39d28c62b0e71de7` için [known-vulnerability producer run `#10` (`32919649968`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32919649968), job `98030516890` ile `success` tamamlandı. Artifact ID `9589408603`, digest'i `45e628c6dcfee4ddd9cc1b45c93074883c1f99e78f0d9580decc848f1f69d4ad`; `last-success.json` SHA-256 `8670c3e3442cd7342a0bb6e51f44fc4b31a1cf29f7da27ad523e87e6f7a8d316`dır. Exact `4/0/4` audited restore, `2 + 21 = 23` package graph ve direct/transitive/toplam `0/0/0` bilinen vulnerability sonucu değişmedi. Bu pre-asset CVE acceptance ledger SHA-256 `01180e5c90816fda2f1712a2ecd5ed634399caee76d68b7ffeb47c178e2aa208`; freshness sonu `2026-09-02T01:38:42Z`dir. O checkpoint'te bu renewal yalnız `SbomPending` ve freshness süresince `CveReviewPending` blocker'larını kapatmış; `releaseReady=false`, 13 blocker ve bütün hukuk/Store/non-claim sınırlarını aynen bırakmıştır.

Resmî dayanaklar (erişim: `2026-08-26`): [.NET 10 `dotnet package list`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list), [NuGet JSON v1 makine-okunur çıktı spec'i](https://github.com/NuGet/Home/wiki/%5BSpec%5D-Machine-readable-output-for-dotnet-list-package) ve [NuGet Audit / `VulnerabilityInfo` davranışı](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages).

## Deterministic package asset provenance ve güncel SBOM/CVE kabulü

Manifestin exact sekiz PNG/ICO package asset'i artık `eng/New-WindowsProductionAssets.ps1` içindeki source-controlled `WindowsProductionAssets-Rgba8Filter0FixedHuffmanLz77-PngFrameIco-v1` tarifiyle üretilir. Tarif source asset dependency, üçüncü taraf asset girdisi, font, metin veya trademark girdisi kullanmaz; iki ayrı output root'unda byte-identical üretim ile existing-root verification destekler. Strict `eng/windows-production-asset-provenance.json` ledger'ı exact sekiz path'i, PNG dimension'larını, ICO frame setini, byte length/SHA-256 değerlerini, generator SHA-256 `4ac099e8da587b5df61817ab92071235e4e91408d891f5cafa3037599d7f603b` ve canonical asset-set SHA-256 `6338f26af851a45eb4c7da593430ef1eab5a34afa6013365c2621fbfa0957777` değerini fail-closed bağlar.

Bu bağ yalnız exact package asset origin'ini kanıtlar. Görseller `developmentPlaceholderOnly=true` olan nötr teknik placeholder'lardır; `productionBrandApproved`, `copyrightOwnershipDetermined`, `redistributionDecisionComplete` ve `legalReviewComplete` değerlerinin tamamı `false` kalır. Dolayısıyla bu checkpoint public marka/logo onayı, copyright ownership, redistribution hakkı, root `LICENSE`/`NOTICE`, Store/legal ya da codec-IP kapanışı değildir.

Asset byte'ları önceki 111-dosyalık package-producing snapshot'ını değiştirdi. Yeni clean commit `b78c6c0512a91c9ab1fd86e18f703856a45a4b53` için [Windows package SBOM producer run `#2` (`32923642659`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32923642659), job `98042026161` ile `2026-08-26T02:48:32Z` zamanında `success` tamamlandı. Artifact ID `9590841342`, digest'i `66bb75c354f9c08c0002f0f3d0a390ecfe0326284928e2da91dc9836ffe41a33`; application MSIX SHA-256 `b23fba0be6bc56e450f0e9a45d5822dc3b400160f29c89c77a9df2231b136a4c`, SPDX SBOM SHA-256 `eeefe6c741b41fc630a089d41445498e919cc1f49ed4f5f3350c87439a789e16` ve exact 111-file package-producing snapshot SHA-256 `6b068e8ccb651159dc2bad754f370ba428c6c3087c01c6bc7e419e6095cfadcc`dır. Acceptance ledger raw SHA-256 `5ad87061b0586ad5d84f8f38d244bb3e9131358e27905bffd1f8b2648259bb47`dir. Official ve strict validation birlikte `PASS`tir; deterministic asset provenance bağıyla `AssetProvenancePending`, bu asset-bound package acceptance ile `SbomPending` teknik zincirde kapanır.

Yukarıdaki run `#1`/run `#10` ve ledger'ları pre-asset tarihsel accepted checkpoint olarak korunur. Yeni SBOM acceptance ledger raw SHA-256 `5ad87061b0586ad5d84f8f38d244bb3e9131358e27905bffd1f8b2648259bb47` değerine zincirlenen clean `58e0b615d1c531a9c94583772d4d80a5740eb3e3` için [known-vulnerability producer run `#13` (`32924589536`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32924589536), job `98044789701` ile `2026-08-26T02:58:07Z` zamanında `success` tamamlandı. Artifact ID `9591048689`, boyutu `1.120` byte, digest'i `29e3c4136c05c42849f7141530ed0ce073c279d4ad617c007c2733ccfe5e1ecb`; `last-success.json` SHA-256 `d8ef63b6a66c4cd8fa415c8f8d9f857f90125dfbd759c89dde5616cae4ee64f0`dır. Acceptance ledger raw SHA-256 `60aa4cf4a7d346b18e1301ec8e58151afed5c59ceea4e17cb308e21864c101cd`dir. Evidence `2026-08-26T02:57:54.2270308Z` zamanında gözlemlenen exact değişmemiş contract/graph için `4/0/4` audit, `2 + 21 = 23` package ve direct/transitive/toplam `0/0/0` known-vulnerability sonucu taşır; official ve strict validation `PASS`tir. Teknik freshness sonu `2026-09-02T02:58:07Z`dir.

Commit `c686424ea43be3a01b5fb364b2115cc84319b242` üzerindeki schema-v5 predecessor, aynı accepted asset/SBOM/CVE zinciriyle evaluation anında exact 12 blocker üretmiştir. O tarihsel şema yedi günlük `freshAtEvaluation` alanını taşıyor, fakat 24 saatlik final-release predicate'ini evidence içinde ayrı göstermiyordu; schema-v6 bu sözleşme boşluğunu kapatır. Schema-v5 sonucu tarihsel teknik checkpoint'tir ve geriye dönük final-release kabulü sayılmaz.

Güncel package-bound kabul için clean `f6e8fdbb56644bff62ab261cb4725d1088e5124a` üzerinde [Windows package SBOM producer run `#15` (`33143245610`)](https://github.com/serkankaracan/iptv-suite/actions/runs/33143245610), job `98758566366` ile `2026-08-28T05:03:57Z` zamanında `success` tamamlandı. Artifact `9674902720`, `7.761` byte ve digest `0e4db853546ee21de46bf4144623c3504f56f8855c561aa7ede0ec631aa423f1`; application MSIX SHA-256 `7c60614597bfe499ca8c39625996e9f6e6621638ffa8277ee3d293ee144c788d`, package evidence `last-success.json` SHA-256 `9ea1aef00fbf9cdbec4298fa79a4d78c301e6f639f7fe3aa58fee85b94236aa6`, SBOM summary SHA-256 `32da901aba2a6438ff96b865e51cc00369f6a08458c958ca56ef6ad354bc2de5` ve SPDX SHA-256 `507bbeac2430de4aeb404c23eaf24c15f8f85ba3f3b264521c410b7e28411fce`dır. Acceptance ledger raw SHA-256 `a1a6fbb24e2ece1884995264eb375982242f4b86d5bd743b7ee572b93fc50f8d`; exact `115` dosyalık current package-producing snapshot `5568fb8fc87f614392762501cb2a4b3be1a13487bb8cfab037ccaec579756810` ve exact yedi-file contract `bd081182d1088af346126f4154b94ce92fadf0d438a2f919d67b12b0893357f8` ile fail-closed bağlıdır. Official ve strict SBOM validation birlikte `PASS`tir.

Exact accepted SBOM ledger raw SHA-256 `a1a6fbb24e2ece1884995264eb375982242f4b86d5bd743b7ee572b93fc50f8d` değerine zincirlenen, fakat farklı clean commit `1c7ae00298414fb7707ad6097e151fc39aa38c03` üzerinde çalışan [known-vulnerability producer run `#88` (`33143833495`)](https://github.com/serkankaracan/iptv-suite/actions/runs/33143833495), job `98760427129` ile `2026-08-28T05:09:29Z` zamanında `success` tamamlandı. Artifact `9675011337`, `1.121` byte ve digest `1b1954bce5c7c80dcdc895e57e4359df6acdedf0de2534470ca175131c2e9792`dır; exact `4/0/4` audited restore, `2 + 21 = 23` package graph ve direct/transitive/toplam `0/0/0` bilinen vulnerability sonucu official+strict validation'dan geçti. CVE acceptance ledger raw SHA-256 `290f1e57eecd8c9b30093644af87fdd83d4fe338b0151691b9de62ef8310e126`; yedi günlük teknik freshness sonu `2026-09-04T05:09:29Z`dir. Ayrı final-release freshness sınırı inclusive `2026-08-29T05:09:29Z`dir: bu anda schema-v7 local doğrulama `packageSbomAcceptance.result=accepted-current`, `technicalBaselinePassed=true`, `releaseReady=false` ve exact 12 external blocker üretir; bir saniye sonrasında `CveReviewPending` yeniden açılır ve blocker sayısı 13 olur. Ledger/schema/contract tahrifi, daha yakın package override ve iki geçiş arasındaki TOCTOU değişimi hard-invalid kalır; bu teknik yenileme M15 completion, hukuk/Store/signing ya da genel “CVE-free” iddiası değildir.

## Development identity WACK preflight — EXECUTED / TEST FAILED

`eng/WindowsWack.ps1`, yalnız disposable development identity için `DevelopmentIdentityWackPreflightOnly` kapsamlı opt-in bir teknik preflight sağlar. Elevated ve interactive Windows hostta exact sistem `appcert.exe` dosyasını doğrular; resmî `reset` → `test -packagefullname ... -reportoutputpath ...` sırasını toplam en fazla 60 dakikada çalıştırır. Stdout/stderr ayrı ayrı 4 MiB, XML report 16 MiB ile sınırlıdır. Parser DTD/external resolution'ı kapatır; exact `REPORT`, `OVERALL_RESULT=PASS`, `PARTIAL_RUN=FALSE` ve varsa `LATEST_VERSION=TRUE` ister. Ham XML ile stdout/stderr her sonuçta silinir; yalnız path, package-full-name, publisher veya ham test metni taşımayan allowlist'li `wack-development-preflight-summary.json` kalabilir.

Normal push/PR akışı WACK çalıştırmaz. Yalnız elle tetiklenen `windows-quality.yml` akışında `run_wack=true`, mevcut signed package-smoke içine bu preflight'ı bağlar. Summary açıkça `ClosedBlocker=None` ve `ReleaseReady=false` taşır. Bu development signer/identity sonucu production identity, release signer, exact RC, clean-machine/private-flight veya Partner Center kabulü değildir; `WackPending` aynen açık kalır ve final candidate üzerinde yeniden WACK gerekir.

WACK-bound package-smoke contract'ı clean `becd2cb408982eb34b1bf62de67cb628c9408055` üzerinde [SBOM producer run `#3` (`32928618825`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32928618825) ile official+strict `PASS` doğrulandı. SBOM acceptance ledger raw SHA-256 `69bfd62dc8145ba280c1aa45c92dde15173440d4378cb568df68beef3f814c80`, exact yedi-file contract SHA-256 `4896d447e53b075f48c0bbad7fe9336a64c2d8789b12c000e7b3c8a21c65e5fe`dir. Buna exact ledger hash'iyle zincirlenen clean `ef876d103223165bf546fb60fddef102e74c2c08` için [CVE producer run `#18` (`32929529931`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32929529931), `4/0/4` audited restore, 23-package graph ve direct/transitive/toplam `0/0/0` known-vulnerability sonucunu official+strict geçti. CVE acceptance ledger raw SHA-256 `a7f5e50f37337442d770b8d9a026dc5a9cd843d833c03af13b0689a0b69099e5`, teknik freshness sonu `2026-09-02T04:17:16Z`dir.

Clean `edf1513ec1c9c4ec2e82e1086b49c82843dec6b3` için opt-in [hosted WACK run `32932186491`](https://github.com/serkankaracan/iptv-suite/actions/runs/32932186491) attempt 1, WACK'e ulaşmadan katalog input-response bütçesinde tekil olarak fail oldu; aynı commit'in normal push package smoke'u geçtiği için eşik değiştirilmedi. Tek bounded failed-job rerun olan attempt 2'de build, package-bound SBOM ve kısa resource kontrolü geçti; sistem `appcert.exe test` çağrısı sıfırdan farklı çıkışla stable `WindowsWack:TestFailed` üretti. Job ve required gate doğru biçimde kırmızı kaldı. Ham XML/stdout/stderr her durumda silindi; önceki workflow başarı koşulu nedeniyle failure özeti upload edilmedi. Takip Actions değişikliği yalnız exact dört alanlı `last-failure.json` ve `WindowsWack:<alphanumeric-code>` desenini kabul edip yol/zaman/run kimliği veya ham hata metni taşımayan ayrı failure summary üretir; gate'i gevşetmez ve `continue-on-error` kullanmaz. Bu kanıt yolu PS5.1 WACK self-test'i ve architecture `54/54 PASS` ile local doğrulandı.

Clean `b9d809347637439b8b850c3d0d2faa78fc7e5446` için [hosted doğrulama run `32934454845`](https://github.com/serkankaracan/iptv-suite/actions/runs/32934454845), locked gate'i `8m53s` ve DPAPI boundary'yi `2m12s` içinde geçti; package job exact `WindowsWack:TestFailed` nedeniyle beklendiği gibi kırmızı kaldı. Sanitize hazırlama ve upload adımlarının ikisi de geçti. Artifact `9594709230`, archive SHA-256 `b077b0d8e0d427495e3a59341cc063f565020b75fa437205d646caec64910cc1` ve tek 179-byte JSON member SHA-256 `c683df744906612b02d3c2770175b717601ff7ea34c50a0473fc09550944e198` taşır. Exact yedi alan schema v1, `Scope=DevelopmentIdentityWackPreflightOnly`, `ClosedBlocker=None`, `ReleaseReady=false`, `Status=FAIL`, `FailureCode=TestFailed`, `Configuration=Release` değerlerinden oluşur. Ham WACK içeriği artifact'a girmemiştir. Failure-evidence yolu hosted **VERIFIED**dır; WACK teknik testi geçmediği ve final RC-bound kabul yapılmadığı için `WackPending` açık kalır.

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

## Son accepted schema-v7 tarihsel açık blocker seti

Aşağıdaki 12 kod, final-release CVE freshness geçerliyken current evidence'ta açıktır ve ordinal sıralıdır:

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

## ADR-008/ADR-009 sonrası successor SBOM/CVE kapanışı — OPEN

M17–M19 production kaynakları ile package-smoke sözleşmesi, yukarıdaki run `#15`
SBOM kabulünün exact source/package closure'ını değiştirmiştir. Ledger'ın byte'ları ve
tarihsel hosted provenance'i geçerli kalır; fakat yeni aday için `currentAtEvaluation=false`,
`technicalBaselinePassed=false` ve `SbomPending` beklenen fail-closed sonuçtur. Bu durum
ledger tahrifi değildir: doğrulayıcı, raw ledger hash/schema/pin uyuşmazlığını hâlâ
`PackageSbomAcceptanceInvalid` ile hard-fail eder; source, production-input veya package
snapshot drift'ini ise açık stale blocker olarak raporlar. İki geçişli snapshot, contract
source ve repository-stability kontrolleri yayım öncesinde değişimi reddetmeye devam eder.

Successor teknik closure sırası şöyledir:

1. M17–M19 düzeltmeleri tamamlanır; aday clean commit olarak `main` dalına push edilir.
2. Exact aday SHA üzerinde `Windows package SBOM producer` workflow'u elle tetiklenir.
3. Başarılı run/job kimliği ile `windows-msix-smoke-evidence` artifact digest'i ve üç exact
   üyenin (`last-success.json`, `package-sbom-summary.json`, `package-sbom.spdx.json`)
   byte length/SHA-256 değerleri indirilmiş artifact üzerinden doğrulanır.
4. Yalnız bu doğrulanmış hosted metadata/evidence ile
   `eng/windows-package-sbom-acceptance.json`, validator pin'leri, offline self-test pin'leri
   ve bu checkpoint kaydı birlikte yenilenir; eski run numarası yeni kaynak için tekrar
   kullanılamaz.
5. SBOM acceptance commit'i push edildikten sonra `Windows known-vulnerability producer`
   exact yeni SBOM-ledger hash'i üzerinde yeniden çalıştırılır. Başarılı artifact
   doğrulanarak CVE acceptance ledger/validator pin'leri yenilenir. Final-release için
   24 saatlik freshness sınırı ayrıca geçmelidir.
6. Windows PowerShell 5.1 adversarial self-test, Release x64 build ve architecture suite
   yeniden çalıştırılır. Bu teknik zincir geçse bile aşağıdaki Store, signing, hukuk ve
   operator blocker'ları ayrıca kapanmadan M15 `COMPLETED` olmaz.

Yerelde elevated development package smoke ve offline SBOM self-test yapılabilir; bunlar
hosted Actions run/job/artifact provenance'i üretmediği için successor ledger'ı tek başına
kapatamaz. Signing private key/parolası veya gerçekte üretilmemiş Actions metadata'sı source'a
yazılamaz.

## Non-claims ve sonraki kabul sınırı

- Known-pattern source taraması ile exact installed-package runtime audit'i geçti. Runtime sonucu yalnız exact hosted package gözlem penceresindeki deterministic pre/post eşitliğini ve watcher'ın mutation görmediğini kanıtlar; clean VM'de install/update/reset/uninstall matrisi ve bütün olası write yolları ayrıca geçmelidir.
- Exact 23-package inventory teknik dependency drift guard'ıdır ve tek başına SBOM değildir. Tarihsel hosted package-bound companion SPDX ile ayrı, fresh known-vulnerability incelemesi root `LICENSE`/`NOTICE`, genel “CVE-free” iddiası, redistribution kabulü veya codec/IP hukuk görüşü değildir; yeni deterministic asset-origin bağı da bu hukuk kapılarını kapatmaz.
- Development identity ile mevcut disposable lifecycle kanıtları production identity/PFN, signing lineage, previous-package migration, repair veya private-flight sonucu değildir.
- Development-identity WACK preflight sözleşmesi implement edilmiştir; final RC-bound WACK, Partner Center private submission, privacy/support URL, Store listing/rating/reviewer notes ve geliştirici-owned reviewer service henüz kabul edilmemiştir.
- ARM64 Windows MVP release setinde yoktur; deferred disposition gelecekte native ARM64 acceptance yapılmadan destek iddiasına dönüştürülemez.
- Source-controlled MSBuild audit'i dış command-line/global-machine import yüzeyini doğrulamaz; release build invocation ve clean runner ayrıca bağlanmalıdır.

Bu nedenle M15 `IN PROGRESS / BLOCKED` durumundadır. Bu belge teknik baseline checkpoint'ini kaydeder; M15 completion veya Store release kararı değildir.
