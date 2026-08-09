# Windows development, quality gate ve M4 secure-storage foundation

Bu klasör Windows uygulamasını, M2 test scaffold'unu, M3 saf domain/validation çekirdeğini ve M4'ün ilk protected-storage dilimini içerir. M2 hosted kabulü ve M3 local mühendislik kabulü **PASS, 2026-08-09** durumundadır. M4 foundation `IN PROGRESS`; 2026-08-10 local gate'i 130/130 testi iki koşuda geçmiştir. Uygulama UI'sı hâlâ yalnız gerçek assembly/package sürümünü, build configuration'ını ve process architecture'ını gösteren development shell'dir; source formu, network/provider, parser, database veya playback özelliği henüz yoktur.

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

## M3 domain ve güvenli validation

- `ContentSource`, `PlaylistSnapshot`, `ChannelCategory`, `LiveChannel`, typed ID ve versioned `ChannelStableKey` contract'ları `IptvSuite.Domain` içindedir. `LiveChannel` tam bir oynatma kaynağı olarak ya typed provider item key ya da protected locator reference taşır; M3U `tvg-id` tek başına oynatılabilir sayılmaz.
- Xtream-compatible ve remote playlist girdileri yalnız HTTPS kabul eder. `SafeEndpoint` yalnız IDNA host, scheme ve effective port taşır; raw path/query/user-info/fragment taşımaz.
- Başarılı sonuç username, password veya full locator döndürmez; yalnız random opaque `SecretReference` ya da `ProtectedLocatorReference` içerir. Bunların platform-protected persistence'ı M4 kapsamıdır.
- Source adı 100, locator 4096, username 256 ve password 1024 Unicode scalar ile sınırlıdır; NFC, invalid UTF-16, control/NUL ve IDNA/IPv4/IPv6 vakaları table testlerindedir.
- URI/header/untrusted-text diagnostics policy'si raw input'u geri üretmez. `.m3u8` uzantısı catalog/HLS kararı vermez; bounded content-prefix classifier kullanılır, gerçek incremental parser M7'ye kalır.

## M4 protected-storage foundation — IN PROGRESS

- `IptvSuite.Application`, arbitrary string key taşımayan typed `ISecretStore` portunu; source/purpose/reference binding'ini; dispose sırasında owned buffer'ı sıfırlayan ve JSON/debug çıktısında `[SENSITIVE]` dışında veri vermeyen `SecretLease`i içerir.
- `IptvSuite.Infrastructure`, stable `System.Security.Cryptography.ProtectedData 10.0.10` ile yalnız `DataProtectionScope.CurrentUser` kullanan Windows adapter'ını içerir. Bounded v1 binary envelope source, purpose, reference kind ve opaque record ID'yi hem entropy'ye hem korunan içeriğe bağlar.
- Yazım aynı dizinde `CreateNew` temp + `WriteThrough` + `Flush(true)` + overwrite rename ile yapılır; yalnız transient Windows access/share/lock kodları bounded retry alır. Raw path, exception mesajı veya secret result'a taşınmaz.
- M4 fake contract testleri ve gerçek Windows DPAPI testleri CRUD/update, adapter restart, idempotent delete, pre-cancel, concurrent create, aynı süreçte iki adapter instance'ı arasında same-key update/read/delete sıralaması, ciphertext swap/corruption/oversize, zeroization ve canary-at-rest taramasını kapsar.
- Bu kanıt normal MSTest process'i ve temp root içindir. Packaged `LocalCache` two-launch/update/reset/uninstall, gerçek ikinci Windows user, source-deletion reconciliation, 5k–50k layout/performance ve ADR-003 final kararı açık hard-gate'tir.
- Managed containment/reparse kontrolleri path tabanlı TOCTOU yarışını bütünüyle kapatmaz; handle-relative Windows hardening ve silinemeyen protected temp orphan reconciliation M4 acceptance öncesi açık kalır.

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

`IptvSuite.Testing`, `IptvSuite.UnitTests` ve `IptvSuite.IntegrationTests` production graph'ına girmez. M2 fake secret store/player production port'u değildir; IntegrationTests içindeki M4 fake yalnız gerçek `ISecretStore` contract senaryosudur ve DPAPI/package kanıtı yerine geçmez.

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
7. Paketi kurar, AUMID ile görünür pencereyi açar ve normal window-close mesajıyla kapatır.
8. Exact development package'ı, package app-data'yı ve exact-thumbprint sertifikayı temizler.

Başarılı kanıt `.artifacts/msix-smoke/last-success.json` altında kalır; bütün `.artifacts` içeriği Git dışında build çıktısıdır. Shared `Microsoft.WindowsAppRuntime.2` framework paketi başka uygulamalarca kullanılabileceği için kaldırılmaz.

Bu smoke yalnız çalıştırıldığı elevated, interactive Windows host için package/sign/payload-inspection/install/launch/normal-close/uninstall zincirini kanıtlar. Payload kontrolü hedefli test-artifact/canary yasağıdır; genel credential veya malware taraması değildir. Clean VM, non-admin sideload, package update/migration, WACK/Store submission, keyboard/UIA/accessibility, gerçek provider/internet, player/codec/HW decode, ARM64 veya başka OS/GPU sonucu kanıtlamaz.

### Hosted workflow ve kanıt sınırı

`.github/workflows/windows-quality.yml`, her pull request'te, merge queue grubunda, `main` push'unda ve elle tetiklemede GitHub-hosted `windows-2025-vs2026` üzerinde çalışacak şekilde tanımlıdır. Exact SDK `10.0.302` kurulduktan sonra önce `Invoke-WindowsQualityGate.ps1`; yalnız bu job başarılıysa locked restore ve `Invoke-WindowsPackageSmoke.ps1` çalışır. Raw TRX upload edilmez; canary-taranmış minimal quality summary, fixture manifesti + license sidecar'ı ve packaged-smoke `last-success.json` artifact'ları yedi gün tutulur. `Required Windows gate`, quality fail veya smoke skip/fail durumunu tek coordinator sonucunda kırmızıya çevirir. Bu check run `31327398270` içinde başarıyla sonuç üretmiştir; ancak 2026-08-09 repository preflight'ında mevcut private-repository planı branch protection'ı etkinleştirmediği için merge enforcement yoktur. Bu policy sınırı workflow PASS iddiasına dahil değildir.

Workflow'da bilerek üst seviye `paths`/`paths-ignore` filtresi yoktur: required check filtre nedeniyle hiç başlamazsa GitHub check'i `Pending` bırakıp merge'i engelleyebilir. Bu nedenle Windows gate her PR'da sonuç raporlar; ileride maliyet optimizasyonu gerekiyorsa workflow'u skip etmek yerine her zaman sonuç üreten job tasarımı gerekir.

Gerçekten yeşil bitmiş hosted package job'u, yalnız o runner/image ve o commit için signed framework-dependent x64 MSIX üretildiğini; targeted test payload/canary kontrolünü; install, AUMID ile görünür shell launch'ı, normal close ve exact cleanup zincirini kanıtlar. Feature UI davranışı, keyboard/UIA/accessibility, update/migration, WACK veya Store kabulü, non-admin/clean-machine matrisi, gerçek provider/internet, player/codec/HW decode, ARM64 ya da bütün OS/GPU kombinasyonlarını kanıtlamaz. Run `31327398270` için indirilen iki sanitized artifact'ın commit bağı, schema, hash, capability, signature, leak gate, normal-close ve cleanup alanları doğrulanmıştır; ayrıntılar [M2 completion evidence](../../docs/quality/M2_COMPLETION_EVIDENCE.md) belgesindedir.

## Disposable development identity

Manifestteki `IptvSuite.LocalDev.6f0d9a64` / `CN=IptvSuite Local Development` yalnız yerel M1 test lineage'ıdır. Partner Center rezervasyonu, Store association veya marka hakkı iddiası değildir. Gelecekteki Store identity farklı PFN/AUMID ve app-data alanı oluşturabilir; development paketinden in-place upgrade veya veri migration garantisi yoktur.

Private key, PFX, parola, certificate thumbprint, `Package.StoreAssociation.xml` veya Store identity dosyası commit edilmez. Script wildcard package/certificate silmez.
