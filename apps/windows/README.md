# Windows development, quality gate ve M4 secure-storage foundation

Bu klasör Windows uygulamasını, M2 test scaffold'unu, M3 saf domain/validation çekirdeğini ve M4'ün protected-storage dilimini içerir. M2 hosted kabulü ve M3 local mühendislik kabulü **PASS, 2026-08-09** durumundadır. M4 foundation `IN PROGRESS`; 2026-08-13 hosted run `31735655363`, source-draft protection, packaged composition ve startup temp-aday cap'i dahil tarihsel v1 store snapshot'ında 153/153 testi iki deterministik koşuda; signed packaged-smoke ve required coordinator'ı başarıyla tamamlamıştır. Internal source-payload decoder ve semantic-owner-bound store v2 snapshot'ı 171/171 testi geçmiştir. Exact configuration-record deletion primitive'i dahil güncel local exact-SDK gate architecture 13, unit 137 ve integration 33 olmak üzere 183/183 testi iki deterministik koşuda geçmiştir. 2026-08-14 GitHub Actions UI kaydında run number `#15`, commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` için 3/3 işi ve üç artifact'ı başarıyla tamamlamış; ilk v2 hosted quality/package/lifecycle geçişini workflow/UI düzeyinde **VERIFIED** yapmıştır. Aynı tarihli run number `#16` UI kaydı, exact deletion primitive'ini içeren commit `3e806edd9a9482b87cccbf000ac8a81823562f6a` için 3/3 işi yeşil, üç artifact ve `8m24s` toplam süre gösterir; bu takip sonucu da yalnız workflow/UI düzeyinde **VERIFIED**dir. Database run ID, job log/test sayısı ve indirilen artifact alan/hash doğrulaması bu kayıtların parçası değildir. Sonraki test-only lane aynı signer/package family altında signed `0.0.1.0 → 0.0.2.0` normal update ve updated-process read/update/delete zincirini kodlar; `#16` bu değişiklikten önce olduğu için yeni lane'in hosted execution'ı `UNVERIFIED`dır. Uygulama UI'sı hâlâ yalnız gerçek assembly/package sürümünü, build configuration'ını ve process architecture'ını gösteren development shell'dir; source formu, network/provider, parser, database veya playback özelliği henüz yoktur.

## Toolchain

| Bileşen | Exact sürüm / sınır |
|---|---|
| .NET SDK | `10.0.302` |
| .NET runtime/reference pack | `10.0.10` |
| Windows App SDK | `2.3.1` stable |
| Windows SDK BuildTools | `10.0.26100.8249` stable |
| ProtectedData | `System.Security.Cryptography.ProtectedData` `10.0.10` stable, Infrastructure-only |
| Fake time test paketi | `Microsoft.Extensions.TimeProvider.Testing` `10.8.0` stable, test-only |
| MSTest | `4.3.3` stable |
| OS / architecture | Windows 11 `10.0.26100+`, x64 |

`global.json`, central package versions ve her projedeki `packages.lock.json` birlikte deterministic restore sınırını oluşturur. SDK resolver exact `10.0.302` ister: `rollForward` değeri `disable`, `allowPrerelease` değeri `false` olduğu için başka feature-band/patch veya preview SDK'ya sessiz geçiş yoktur. Preview/Experimental doğrudan bağımlılık yoktur. Resmî `winapp` CLI public preview olduğundan build veya launch zincirine alınmamıştır.

## Restore, build ve test

Bütün komutları repository kökünden çalıştırın:

```powershell
dotnet --version
dotnet restore .\apps\windows\IptvSuite.Windows.sln --locked-mode -p:Platform=x64
dotnet build .\apps\windows\IptvSuite.Windows.sln -c Debug -p:Platform=x64 --no-restore
dotnet build .\apps\windows\IptvSuite.Windows.sln -c Release -p:Platform=x64 --no-restore
dotnet test .\apps\windows\tests\IptvSuite.ArchitectureTests\IptvSuite.ArchitectureTests.csproj -c Release --no-build --no-restore
```

Build içindeki analyzers/code-style denetimi lint gate'idir; ayrı bir linter dependency'si yoktur. SDK sürümü tam `10.0.302` değilse komutlara devam etmeyin.

Yeni package sürümü bilinçli değiştirildiğinde önce normal restore ile lock dosyaları güncellenir, diff incelenir ve ardından yukarıdaki `--locked-mode` akışı tekrar çalıştırılır.

## Visual Studio'da çalıştırma

1. `apps/windows/IptvSuite.Windows.sln` dosyasını açın ve solution platformunu `x64`, configuration'ı `Debug` seçin. **Build > Configuration Manager** içindeki `IptvSuite.Windows` satırında `Build` ve `Deploy` işaretli olmalıdır; solution bu eşlemeleri Debug/Release için source-control altında taşır.
2. `IptvSuite.Windows` projesini **Başlangıç Projesi Olarak Ayarla** seçeneğiyle işaretleyin.
3. Debug target listesinden **IptvSuite.Windows (Package)** profilini seçip `F5` kullanın.

Source-controlled `Properties/launchSettings.json`, Visual Studio'ya packaged single-project MSIX deploy/launch yolu için `MsixPackage` komutunu verir. Profil görünmezse solution'ı kapatıp yeniden açın veya projeyi unload/reload edin. `IptvSuite.Windows (Package)` yerine unpackaged/Project profili eklemeyin; uygulamanın identity ve `LocalCache` davranışı packaged modele bağlıdır.

## M3 domain ve güvenli validation

- `ContentSource`, `PlaylistSnapshot`, `ChannelCategory`, `LiveChannel`, typed ID ve versioned `ChannelStableKey` contract'ları `IptvSuite.Domain` içindedir. `LiveChannel` tam bir oynatma kaynağı olarak ya typed provider item key ya da protected locator reference taşır; M3U `tvg-id` tek başına oynatılabilir sayılmaz.
- Xtream-compatible ve remote playlist girdileri yalnız HTTPS kabul eder. `SafeEndpoint` yalnız IDNA host, scheme ve effective port taşır; raw path/query/user-info/fragment taşımaz.
- Başarılı sonuç username, password veya full locator döndürmez; yalnız random opaque `SecretReference` ya da `ProtectedLocatorReference` içerir. Bunların platform-protected persistence'ı M4 kapsamıdır.
- Source adı 100, locator 4096, username 256 ve password 1024 Unicode scalar ile sınırlıdır; NFC, invalid UTF-16, control/NUL ve IDNA/IPv4/IPv6 vakaları table testlerindedir.
- URI/header/untrusted-text diagnostics policy'si raw input'u geri üretmez. `.m3u8` uzantısı catalog/HLS kararı vermez; bounded content-prefix classifier kullanılır, gerçek incremental parser M7'ye kalır.

## M4 protected-storage foundation — IN PROGRESS

- `IptvSuite.Application`, arbitrary string key taşımayan typed `ISecretStore` portunu; source/purpose/reference binding'ini; dispose sırasında owned buffer'ı sıfırlayan ve JSON/debug çıktısında `[SENSITIVE]` dışında veri vermeyen `SecretLease`i içerir.
- `SourceDraftProtectionService`, Xtream-compatible veya remote-playlist draft'ını store mutation'ından önce validate eder. Bounded, versioned ve length-prefixed strict UTF-8 payload'ı typed create operation'ına verir; store başarısından sonra exact store-issued opaque reference'ı validated draft'a bağlar ve geçici byte buffer'ı `finally` içinde sıfırlar. Doğrudan await edilen çağrıda başarılı store commit'inden sonra cancellation yeniden gözlenmez ve reference başarılı sonuçla döner. Caller abandonment/retry, aynı `SourceId` için yinelenen create, process crash/OOM, gelecekteki DB/configuration persistence veya deletion/reconciliation bu sınırın dışındadır.
- `SourceConfigurationProtectedRecordDeletionService`, yalnız `DeletionPending` durumundaki authoritative `ContentSource` aggregate'inden `SourceId`, `SourceConfigurationId` owner'ı, purpose ve opaque reference'ı türetip Xtream credential ya da remote-playlist locator kaydını idempotent siler. Ayrı key/owner/reference parametresi kabul etmez; pre-cancel store'a ulaşmaz ve başarılı delete commit'inden sonra cancellation'ı yeniden gözlemez. Channel kayıtları, metadata/snapshot/cache, source status persistence, yeni operation admission/drain ve bilinmeyen orphan kayıtları bu dar primitive'in kapsamı değildir.
- `IptvSuite.Infrastructure`, stable `System.Security.Cryptography.ProtectedData 10.0.10` ile yalnız `DataProtectionScope.CurrentUser` kullanan Windows adapter'ını içerir. Bounded v2 binary envelope source, purpose, semantic owner kind/ID, reference kind ve opaque record ID'yi entropy, dosya adı ve korunan içeriğe birlikte bağlar. Source credential/remote playlist için owner `SourceConfigurationId`, channel stream/logo için `ChannelId`dir.
- Yazım aynı dizinde `CreateNew` temp + `WriteThrough` + `Flush(true)` + overwrite rename ile yapılır; yalnız transient Windows access/share/lock kodları bounded retry alır. Raw path, exception mesajı veya secret result'a taşınmaz.
- Windows composition root, packaged launch sırasında factory'yi tam bir kez çağırır; başarılı store'u uygulama ömrü boyunca tutar, initialization başarısızsa pencereyi oluşturmadan fail-closed olur ve fallback store oluşturmaz. Factory, codename'i persistence path'ine koymadan `ApplicationData.LocalCachePath\ProtectedStore\v2` kullanır, beklenen filesystem/path/WinRT initialization hatalarını store veya diagnostic context taşımayan typed `StorageUnavailable` sonucuna eşler ve caller cancellation'ı `OperationCanceledException` olarak korur.
- Adapter başlangıcı yalnız protected-store kökündeki tam lowercase `temporary-v2-{32 hex}.tmp` biçimli, regular ve en az 24 saatlik crash artığını idempotent temizler. Fresh/future, lookalike, nested, v1 namespace ve `.dpapi` kayıtlarına dokunmaz; non-regular/reparse veya silinemeyen exact v2 stale entry'de fail-closed davranır. Cleanup, exact current-namespace aday listesini 1.024 girdide sınırlar; 1.025'inci exact aday görülürse hiçbir adayı silmeden `IOException` ile fail-closed olur. Dizin aramasının toplam maliyeti bu aday cap'iyle sınırlandırılmış sayılmaz ve 50k Decision gate'inde ayrıca ölçülecektir.
- M4 fake contract testleri ve gerçek Windows DPAPI testleri CRUD/update, adapter restart, idempotent delete, pre-cancel, concurrent create, aynı süreçte iki adapter instance'ı arasında same-key update/read/delete sıralaması, ciphertext swap/corruption/oversize, same-source semantic owner/ref-swap reddi, bounded startup temp cleanup ve 1.024/1.025 temp-aday sınırı, zeroization ve canary-at-rest taramasını kapsar. Ayrı Application unit testleri source-draft pre-validation/no-mutation, deterministic bounded encode, exact purpose/owner/reference binding, safe failure mapping ve doğrudan await edilen post-commit cancellation sınırını kapsar. Internal source-payload v1 decoder exact round-trip, magic/kind/version, truncation, length overflow, trailing-data, strict UTF-8/control/whitespace ve Unicode-scalar sınırlarını fail-closed test eder; string/URI üretmez. Public resolve, M5 origin policy, lifecycle gate ve UI wiring henüz yoktur.
- Normal MSTest process'i/temp-root kanıtına ek olarak ayrı `IptvSuite.PackageLifecycleHarness`, aynı disposable signer/package family ve kullanıcı bağlamında baseline `0.0.1.0` create/duplicate-create rejection; normal `Add-AppxPackage` ile `0.0.2.0` update; updated-process read/wrong-owner/update/delete/post-delete zincirini kodlar. Bu harness production payload'a girmez ve yeni update lane'inin hosted execution'ı henüz `UNVERIFIED`dır. Production PFN update, reset/uninstall-reinstall, gerçek ikinci Windows user, source-wide deletion/reconciliation, 5k–50k layout/performance ve ADR-003 final kararı açık hard-gate'tir.
- Managed containment/reparse kontrolleri path tabanlı TOCTOU yarışını bütünüyle kapatmaz; cleanup non-adversarial filesystem yarışlarında fail-closed/best-effort sınırındadır. Handle-relative Windows hardening ile `.dpapi` source-wide deletion/record reconciliation M4 acceptance öncesi açık kalır.

### M4 opt-in secret-store spike

`IptvSuite.SecretStoreSpike`, production payload'a girmeyen ve normal quality/CI tarafından çalıştırılmayan Release x64 ölçüm executable'ıdır. Tamamen sentetik 256-byte locator-benzeri değerleri gerçek CurrentUser DPAPI adapter'ına yazar; ölçüm dışı kısa warmup ardından create, restart, bounded read probe, delete ve cancellation-boundary ölçümlerini aggregate-only evidence olarak `.artifacts/m4-secret-store-spike/evidence` altına kaydeder. Raw payload/reference/path, kullanıcı veya makine adı evidence'a yazılmaz; warmup, measured ve cancellation protected root'ları silinmeden önce, evidence ise izole staging dizininde taranıp yalnız temizse atomik olarak yayımlanır. Cancellation kaydı request→completion latency'yi, iptal anındaki committed alt sınırını, iptal sonrası commit üst sınırını ve completion sonrası yeni mutation olmadığını ayrı alanlarda taşır.

Hafif harness kontrolü:

```powershell
.\eng\Invoke-WindowsSecretStoreSpike.ps1 -Mode Smoke
```

`Smoke`, 1.000 kayıt ve tek turdur; performans kabulü veya ADR kararı değildir. `Decision`, sabit 5k/10k/20k/50k matrisini her ölçek için 20 tur ve 20 cancellation örneğiyle çalıştırır. Yoğun ve uzun disk yazımı nedeniyle otomatik başlatılmaz; clean worktree üzerinde bilinçli olarak şu komut gerekir:

```powershell
.\eng\Invoke-WindowsSecretStoreSpike.ps1 -Mode Decision -AllowDecision
```

Evidence dosyasının varlığı tek başına başarılı run anlamına gelmez: başarısız yeni çağrı önceki temiz summary'yi koruyabilir. Yalnız betiğin sıfır exit code'u ile bitmesi ve summary içindeki commit, runner assembly, spec ve workload hash'lerinin değerlendirilen koşuyla eşleşmesi PASS sayılır. Runner assembly hash'i dependency setinin veya reproducible-build eşitliğinin kanıtı değildir.

Bu unpackaged test-host sonucu bile packaged `LocalCache`, gerçek ikinci kullanıcı, source lifecycle, parser/normalize/index maliyeti veya 5 saniyelik uçtan uca import bütçesi değildir. `Decision` sonucu yalnız ADR-003 girdisidir; layout seçimi ayrıca güvenlik/lifecycle incelemesi ister.

## M2 iki-run quality gate

Repository kökünde tek quality komutunu çalıştırın:

```powershell
.\eng\Invoke-WindowsQualityGate.ps1
```

`Invoke-WindowsQualityGate.ps1`; `global.json` içindeki exact SDK/`rollForward: disable` kuralını doğrular, locked restore yapar, Debug ve Release x64 build'lerini tek MSBuild node ile çalıştırır, architecture/unit/integration projelerini Release'te ayrı TRX dosyalarıyla iki kez koşar ve iki koşunun sıralanmış `testName|Passed` setlerini karşılaştırır. Tek-node sınırı high-core Windows host'larında restore/build process fan-out'unu sınırlar; method-level paralel test izolasyonu korunur. Her test host'unda iki dakikalık hang timeout vardır; secret taşıyabilecek dump üretilmez. TRX dosyaları timestamp ve execution order nedeniyle byte-identical olmak zorunda değildir.

Betik fixture'ı iki ayrı dizinde üretip `records.json` ile `fixture-manifest.json` SHA-256 değerlerinin eşitliğini denetler. Sonra `Invoke-QualityGateSelfTest.ps1` aracılığıyla `IPTV_SUITE_ARM_QUALITY_GATE_SENTINEL=1` iken hedef testin gerçekten başarısız, değişken kaldırılınca yeniden başarılı olduğunu kanıtlar; process'in önceki environment değerini `finally` içinde geri yükler. Quality artifact'larını summary yazılmadan önce ve sonra canary marker'ı için tarar.

Her çağrı yalnız exact `.artifacts/quality-gates` alt ağacını temizleyip yeniden üretir. TRX'ler `.artifacts/quality-gates/test-results/run-{1,2}`, fixture'lar `.artifacts/quality-gates/fixtures/run-{1,2}`, sanitized özet ise `.artifacts/quality-gates/evidence/quality-summary.json` altında kalır. Bu dosyaların varlığı tek başına PASS değildir; komutun sıfır exit code ile tamamlanması gerekir.

### Test katmanı sınırları

| Katman | M2'de kanıtladığı | Kanıtlamadığı |
|---|---|---|
| Unit | Fake time advance, scripted transport, in-memory fake secret store, passive fake player, fixture/canary helper | Gerçek timer/network, DPAPI, codec veya ürün state policy'si |
| Integration | Loopback Kestrel, OS-seçimli port, timeout/isolation; M4 fake contract ve normal Windows host'ta gerçek CurrentUser DPAPI | Gerçek provider/internet/TLS, packaged LocalCache lifecycle, second-user, database veya Store lifecycle |
| Architecture | Production/test project ve package/framework reference allowlist'i | Runtime davranışı |
| Packaged-host smoke | Signed development MSIX install, AUMID launch, görünür boş shell, normal close ve exact cleanup | Feature UI, UIA/accessibility, provider/player/codec, update veya Store kabulü |
| Packaged lifecycle | Ayrı test MSIX'inde baseline `0.0.1.0` create/duplicate-create, same-family/signer `0.0.2.0` normal update, updated-process CurrentUser DPAPI v2 read/wrong-owner/update/delete/post-delete, owned-surface canary scan ve exact cleanup | Yeni update lane'i için hosted execution, production PFN, reset/uninstall-reinstall, second-user veya source-wide reconciliation |

`IptvSuite.Testing`, `IptvSuite.UnitTests`, `IptvSuite.IntegrationTests`, opt-in `IptvSuite.SecretStoreSpike` ve packaged `IptvSuite.PackageLifecycleHarness` production graph'ına girmez. M2 fake secret store/player production port'u değildir; IntegrationTests içindeki M4 fake yalnız gerçek `ISecretStore` contract senaryosudur ve DPAPI/package kanıtı yerine geçmez.

## Sentetik fixture ve canary

`apps/windows/testdata/m2/fixture-spec.json`; generator `1.0.0`, algorithm version `1`, seed `20260809` ve 16 tamamen sentetik record tanımlar. Gerçek provider response'u, kullanıcı verisi, credential, playlist veya medya içermez. Aynı girdiden `records.json` ve `fixture-manifest.json` üretmek için:

```powershell
$fixtureRoot = Join-Path ".artifacts\fixtures" ([Guid]::NewGuid().ToString("N"))
$fixtureOutput = Join-Path $fixtureRoot "run-1"
$fixtureLicenseRoot = Join-Path $fixtureRoot "LICENSES"
New-Item -ItemType Directory -Path $fixtureLicenseRoot -Force | Out-Null
Copy-Item .\apps\windows\testdata\LICENSES\LicenseRef-IPTVSuite-Synthetic-Test-Only.txt $fixtureLicenseRoot
dotnet run --project .\apps\windows\tests\IptvSuite.Testing\IptvSuite.Testing.csproj `
    -c Release --no-build -- generate-fixtures `
    .\apps\windows\testdata\m2\fixture-spec.json $fixtureOutput
```

Manifest generator/version/algorithm/seed, file length/SHA-256, provenance flag'leri ve `LicenseRef-IPTVSuite-Synthetic-Test-Only` kaydını taşır. Lisans durumu bilerek `UNVERIFIED` ve internal-only'dir; public redistribution hakkı veya onaylanmış açık kaynak lisansı değildir. Generated output build artifact'ıdır ve commit edilmez.

Canary, `IPTVSUITE_TEST_ONLY_CANARY_V1` tabanlı açık bir test marker'ıdır; gerçek secret değildir. Scanner bilinen marker'ın UTF-8, UTF-16LE, UTF-16BE, URI-escaped ve alignment-safe Base64 biçimlerini scope'tan bağımsız arar; canary taşıyan path'i redakte eder, reparse point'te fail-closed davranır ve pozitif/negatif corpus ile CLI exit `2`/`0` zincirinde test edilir. Bu helper genel credential keşfi, redaction doğruluğu, production log güvenliği veya memory-dump temizliği kanıtı değildir. GitHub job logları artifact scanner kapsamı dışındadır ve canary açısından `UNVERIFIED` kalır; log'a canary yazmama kuralı ayrıca korunur.

## M4 packaged process-lifecycle smoke

`IptvSuite.PackageLifecycleHarness`; ayrı disposable identity kullanan, publish edilemeyen ve uygulama listesinde görünmeyen test-only MSIX hostudur. Production `IptvSuite.Windows` başlangıç yoluna test argument'i, environment switch'i veya hidden mode eklemez. Yönetici PowerShell'de:

```powershell
.\eng\Invoke-WindowsPackageLifecycleSmoke.ps1 -Configuration Release
```

Betik aynı disposable identity/publisher ve geçici signer ile iki exact x64 MSIX üretir: baseline `0.0.1.0` ve yalnız package version'ı farklı update `0.0.2.0`. İlk baseline AUMID activation sentetik canary byte'larını gerçek CurrentUser DPAPI v2 kaydına ve DPAPI-protected control ticket'a yazar; ikinci baseline process aynı ticket varken duplicate create'i fail-closed reddeder. Daha yüksek sürüm normal `Add-AppxPackage -Path ... -DependencyPath ...` yoluyla, force/downgrade veya dependency-only `-Update` switch'i olmadan aynı package family üzerine kurulur. Updated package'ın üçüncü process'i ticket'tan yalnız typed source/owner/reference bağlamını alıp baseline kaydını yeniden okur; yanlış owner read reddini ve yanlış-owner delete'in doğru kaydı koruduğunu, ardından doğru-owner update/read/delete ve post-delete unavailable sonucunu doğrular. Launch'lar yalnız fixed phase ile secretsiz 32-hex run ID alır. Canary gate create sonrasında, package update sonrasında ve finalde yalnız harness'ın yazdığı exact `ProtectedStore` ile run-directory ağaçlarını tarar; Windows-managed mutable package hive'ları bu kanıta dahil değildir. Scanner finding, operasyonel tarama hatası ve CLI contract sapmasını ayrı stable code'larla fail-closed raporlar. Evidence schema v2 iki package file/version/SHA-256/signature alanını; `SameSigner`, `SamePackageFamily`, `PackageFullNameChanged`, `UpdateInstalled`, `ProtectedRecordReadAfterPackageUpdate` ve post-update scan sonuçlarını allowlist'ler. Test package/output/certificate ve package-family app data exact cleanup sonrasında yalnız `.artifacts/package-lifecycle/last-success.json` sanitized evidence olarak kalır; test MSIX upload edilmez.

Microsoft'un update kısıtları aynı package family ve normal akışta daha yüksek hedef sürüm gerektirir; `Add-AppxPackage` imzalı paketi kullanıcıya `-Path` ile ekler/günceller ve gereken dependency paketlerini `-DependencyPath` ile alır [S89][S90]. Güncel lane bu exact `0.0.1.0 → 0.0.2.0` test-family zincirini kodlar, fakat başarılı bir local/elevated veya hosted çalıştırma kaydı henüz yoktur; execution sonucu `UNVERIFIED`dır. Gelecek yeşil koşu da yalnız disposable test PFN, aynı Windows hesabı/makine ve bu iki sürüm için kanıt olacaktır; production package family, repair/reset, uninstall/reinstall retention, ikinci gerçek Windows user, power-loss/crash recovery, cross-process simultaneous writer veya source-wide orphan reconciliation kanıtı olmayacaktır. Local oturum elevated olmadığı sürece install/lifecycle çalıştırılmaz; hosted Windows job sonucu commit-bound acceptance kaydıdır.

## Signed packaged-host MSIX smoke

Önce restore ve Release build'i tamamlayın. Ardından repository kökünde **yönetici olarak açılmış PowerShell** içinde:

```powershell
.\eng\Invoke-WindowsPackageSmoke.ps1 -Configuration Release
```

Script şu kontrollü akışı uygular:

1. Source manifestte disposable identity ve yalnız `runFullTrust` capability'sini doğrular.
2. Private key'i export edilemeyen, yedi günlük bir local code-signing certificate oluşturur.
3. Yalnız public certificate'i geçici olarak `LocalMachine\TrustedPeople` deposuna alır.
4. Framework-dependent, signed x64 MSIX ve x64 Windows App Runtime dependency layout'unu üretir.
5. İmzayı, built/installed manifesti, x64 mimarisini ve runtime `2.3.1` dependency'sini doğrular.
6. MSIX'i sınırlı bir temp dizine açıp test/fixture dizinlerini, test assembly/artifact adlarını, fixture manifestini ve bilinen canary marker biçimlerini production payload'ında yasaklar.
7. Paketi kurar, AUMID ile görünür pencereyi açar; exact package family altındaki güncel `LocalCache\ProtectedStore\v2` dizininin oluştuğunu ve reparse point olmadığını doğrular; uygulamayı normal window-close mesajıyla kapatır.
8. Exact development package'ı, package app-data'yı ve exact-thumbprint sertifikayı temizler.

Başarılı kanıt `.artifacts/msix-smoke/last-success.json` altında kalır; schema `ProtectedStoreDirectoryInitialized=true` alanını da taşır. Bütün `.artifacts` içeriği Git dışında build çıktısıdır. Shared `Microsoft.WindowsAppRuntime.2` framework paketi başka uygulamalarca kullanılabileceği için kaldırılmaz. Run `31735655363` içindeki sanitized artifact bu alanı commit `6ac235f49ffd297196189e5e9441ad0c9e04f116` ve o committeki tarihsel v1 namespace için doğrulamıştır. Run number `#15`in yeşil package işi, commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` üzerinde güncel owner-bound v2 directory kontrolünü workflow/UI düzeyinde doğrulamıştır; indirilen JSON alanı veya artifact hash'i ayrıca incelenmemiştir. Eski yeşil M2 run'ı packaged store initialization kanıtı sayılmaz.

Bu smoke yalnız çalıştırıldığı elevated, interactive Windows host için package/sign/payload-inspection/install/launch/normal-close/uninstall zincirini kanıtlar. Payload kontrolü hedefli test-artifact/canary yasağıdır; genel credential veya malware taraması değildir. Clean VM, non-admin sideload, package update/migration, WACK/Store submission, keyboard/UIA/accessibility, gerçek provider/internet, player/codec/HW decode, ARM64 veya başka OS/GPU sonucu kanıtlamaz.

### Hosted workflow ve kanıt sınırı

`.github/workflows/windows-quality.yml`, her pull request'te, merge queue grubunda, `main` push'unda ve elle tetiklemede GitHub-hosted `windows-2025-vs2026` üzerinde çalışacak şekilde tanımlıdır. Exact SDK `10.0.302` kurulduktan sonra önce `Invoke-WindowsQualityGate.ps1`; yalnız bu job başarılıysa locked restore, `Invoke-WindowsPackageSmoke.ps1` ve ayrı test-only `Invoke-WindowsPackageLifecycleSmoke.ps1` çalışır. Raw TRX veya test MSIX upload edilmez; canary-taranmış minimal quality summary, fixture manifesti + license sidecar'ı, packaged-smoke ve packaged-lifecycle `last-success.json` artifact'ları yedi gün tutulur. `Required Windows gate`, quality fail veya package adımlarından birinin skip/fail durumunu tek coordinator sonucunda kırmızıya çevirir. 2026-08-14 UI kaydında run number `#15`, commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` için `Locked build and test gate`, `Packaged install and launch smoke` ve `Required Windows gate` işlerinin üçünü de yeşil ve artifact sayısını üç gösterir. Bu workflow/UI kanıtı ilk owner-v2 package ve same-version lifecycle lane geçişini **VERIFIED** yapar. Takip eden run number `#16` UI kaydı, exact deletion primitive'ini içeren commit `3e806edd9a9482b87cccbf000ac8a81823562f6a` için aynı 3/3 işi yeşil, artifact sayısını üç ve toplam süreyi `8m24s` gösterir; bu yalnız workflow/UI-level **VERIFIED** sonuçtur. Signed two-version update lane'i `#16`dan sonra eklendiği için bu iki koşu evidence schema v2'yi veya update akışını doğrulamaz; yeni lane'in hosted execution'ı `UNVERIFIED`dır. Her iki tarihsel kayıt için de GitHub database run ID'si, job log/test sayısı, indirilen JSON field seti ve artifact ZIP/hash bağı doğrulanmış değildir. 2026-08-09 repository preflight'ında mevcut private-repository planı branch protection'ı etkinleştirmediği için merge enforcement yoktur. Bu policy sınırı workflow PASS iddiasına dahil değildir.

Workflow'da bilerek üst seviye `paths`/`paths-ignore` filtresi yoktur: required check filtre nedeniyle hiç başlamazsa GitHub check'i `Pending` bırakıp merge'i engelleyebilir. Bu nedenle Windows gate her PR'da sonuç raporlar; ileride maliyet optimizasyonu gerekiyorsa workflow'u skip etmek yerine her zaman sonuç üreten job tasarımı gerekir.

Gerçekten yeşil bitmiş hosted package job'u, yalnız o runner/image ve o commit için signed framework-dependent x64 MSIX üretildiğini; targeted test payload/canary kontrolünü; install, `EnableLUA=1` önkoşulu altında AUMID'in resmi `LOCAL_SERVER` activation API'sinden dönen exact process ID ile görünür launch'ı, normal close ve exact cleanup zincirini kanıtlar. `ProtectedStoreDirectoryInitialized=true` yalnız exact package-family `LocalCache` path'inin launch sırasında oluştuğunu ve o anda reparse point olmadığını kanıtlar; secret create/read, update/reset/uninstall retention veya deletion/reconciliation kanıtı değildir. Gözlemlenebilen erken process exit'i yalnız exit-code sınıfıyla raporlanır; activation ile process gözlemi arasındaki çok kısa çıkış ayrı bir sabit hata sınıfıdır. Activation tanısı uygulama path'ini, pencere başlığını veya event-log içeriğini sorgulamaz. Feature UI davranışı, keyboard/UIA/accessibility, update/migration, WACK veya Store kabulü, non-admin/clean-machine matrisi, gerçek provider/internet, player/codec/HW decode, ARM64 ya da bütün OS/GPU kombinasyonlarını kanıtlamaz. Run `31327398270` için indirilen iki sanitized artifact'ın commit bağı, schema, hash, capability, signature, leak gate, normal-close ve cleanup alanları doğrulanmıştır; bu tarihsel artifact yeni protected-store alanını içermez. Ayrıntılar [M2 completion evidence](../../docs/quality/M2_COMPLETION_EVIDENCE.md) belgesindedir.

## Disposable development identity

Manifestteki `IptvSuite.LocalDev.6f0d9a64` / `CN=IptvSuite Local Development` yalnız yerel M1 test lineage'ıdır. Partner Center rezervasyonu, Store association veya marka hakkı iddiası değildir. Gelecekteki Store identity farklı PFN/AUMID ve app-data alanı oluşturabilir; development paketinden in-place upgrade veya veri migration garantisi yoktur.

Private key, PFX, parola, certificate thumbprint, `Package.StoreAssociation.xml` veya Store identity dosyası commit edilmez. Script wildcard package/certificate silmez.
