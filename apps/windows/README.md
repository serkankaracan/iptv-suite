# Windows development ve M2 test bootstrap

Bu klasör M1 kapsamındaki tek platform uygulamasını ve M2 test scaffold'unu içerir. M2 **implementation in progress** durumundadır; local iki-run quality gate 2026-08-09'da geçmiştir, başarılı hosted packaged-smoke kanıtı henüz kaydedilmemiştir. Uygulama hâlâ yalnız gerçek assembly/package sürümünü, build configuration'ını ve process architecture'ını gösteren boş development shell'dir; test fake'leri ürün özelliği veya production adapter değildir.

## Toolchain

| Bileşen | Exact sürüm / sınır |
|---|---|
| .NET SDK | `10.0.302` |
| .NET runtime/reference pack | `10.0.10` |
| Windows App SDK | `2.3.1` stable |
| Windows SDK BuildTools | `10.0.26100.8249` stable |
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

## M2 iki-run quality gate

Repository kökünde tek quality komutunu çalıştırın:

```powershell
.\eng\Invoke-WindowsQualityGate.ps1
```

`Invoke-WindowsQualityGate.ps1`; `global.json` içindeki exact SDK/`rollForward: disable` kuralını doğrular, locked restore yapar, Debug ve Release x64 build'lerini çalıştırır, architecture/unit/integration projelerini Release'te ayrı TRX dosyalarıyla iki kez koşar ve iki koşunun sıralanmış `testName|Passed` setlerini karşılaştırır. Her test host'unda iki dakikalık hang timeout vardır; secret taşıyabilecek dump üretilmez. TRX dosyaları timestamp ve execution order nedeniyle byte-identical olmak zorunda değildir.

Betik fixture'ı iki ayrı dizinde üretip `records.json` ile `fixture-manifest.json` SHA-256 değerlerinin eşitliğini denetler. Sonra `Invoke-QualityGateSelfTest.ps1` aracılığıyla `IPTV_SUITE_ARM_QUALITY_GATE_SENTINEL=1` iken hedef testin gerçekten başarısız, değişken kaldırılınca yeniden başarılı olduğunu kanıtlar; process'in önceki environment değerini `finally` içinde geri yükler. Quality artifact'larını summary yazılmadan önce ve sonra canary marker'ı için tarar.

Her çağrı yalnız exact `.artifacts/quality-gates` alt ağacını temizleyip yeniden üretir. TRX'ler `.artifacts/quality-gates/test-results/run-{1,2}`, fixture'lar `.artifacts/quality-gates/fixtures/run-{1,2}`, sanitized özet ise `.artifacts/quality-gates/evidence/quality-summary.json` altında kalır. Bu dosyaların varlığı tek başına PASS değildir; komutun sıfır exit code ile tamamlanması gerekir.

### Test katmanı sınırları

| Katman | M2'de kanıtladığı | Kanıtlamadığı |
|---|---|---|
| Unit | Fake time advance, scripted transport, in-memory fake secret store, passive fake player, fixture/canary helper | Gerçek timer/network, DPAPI, codec veya ürün state policy'si |
| Integration | Loopback Kestrel, OS-seçimli port, HTTP byte response, timeout, paralel temp/port isolation ve cleanup | Gerçek provider, internet, TLS policy, database veya Store lifecycle |
| Architecture | Production/test project ve package/framework reference allowlist'i | Runtime davranışı |
| Packaged-host smoke | Signed development MSIX install, AUMID launch, görünür boş shell, normal close ve exact cleanup | Feature UI, UIA/accessibility, provider/player/codec, update veya Store kabulü |

`IptvSuite.Testing`, `IptvSuite.UnitTests` ve `IptvSuite.IntegrationTests` production graph'ına girmez. Fake secret store/player, M4/M11'de tanımlanacak production contract'ları erkenden dondurmaz.

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

`.github/workflows/windows-quality.yml`, her pull request'te, merge queue grubunda, `main` push'unda ve elle tetiklemede GitHub-hosted `windows-2025-vs2026` üzerinde çalışacak şekilde tanımlıdır. Exact SDK `10.0.302` kurulduktan sonra önce `Invoke-WindowsQualityGate.ps1`; yalnız bu job başarılıysa locked restore ve `Invoke-WindowsPackageSmoke.ps1` çalışır. Raw TRX upload edilmez; canary-taranmış minimal quality summary, fixture manifesti + license sidecar'ı ve packaged-smoke `last-success.json` artifact'ları yedi gün tutulur. `Required Windows gate`, quality fail veya smoke skip/fail durumunu tek coordinator sonucunda kırmızıya çevirir; bu check'in branch ruleset içinde gerçekten required seçilmesi repository dışı ayardır ve source ağacından doğrulanamaz.

Workflow'da bilerek üst seviye `paths`/`paths-ignore` filtresi yoktur: required check filtre nedeniyle hiç başlamazsa GitHub check'i `Pending` bırakıp merge'i engelleyebilir. Bu nedenle Windows gate her PR'da sonuç raporlar; ileride maliyet optimizasyonu gerekiyorsa workflow'u skip etmek yerine her zaman sonuç üreten job tasarımı gerekir.

Gerçekten yeşil bitmiş hosted package job'u, yalnız o runner/image ve o commit için signed framework-dependent x64 MSIX üretildiğini; targeted test payload/canary kontrolünü; install, AUMID ile görünür shell launch'ı, normal close ve exact cleanup zincirini kanıtlar. Feature UI davranışı, keyboard/UIA/accessibility, update/migration, WACK veya Store kabulü, non-admin/clean-machine matrisi, gerçek provider/internet, player/codec/HW decode, ARM64 ya da bütün OS/GPU kombinasyonlarını kanıtlamaz. Workflow dosyasının repository'de bulunması PASS kanıtı değildir; başarılı run ile yüklenmiş artifact gerekir ve bu M2 kanıtı henüz alınmamıştır.

## Disposable development identity

Manifestteki `IptvSuite.LocalDev.6f0d9a64` / `CN=IptvSuite Local Development` yalnız yerel M1 test lineage'ıdır. Partner Center rezervasyonu, Store association veya marka hakkı iddiası değildir. Gelecekteki Store identity farklı PFN/AUMID ve app-data alanı oluşturabilir; development paketinden in-place upgrade veya veri migration garantisi yoktur.

Private key, PFX, parola, certificate thumbprint, `Package.StoreAssociation.xml` veya Store identity dosyası commit edilmez. Script wildcard package/certificate silmez.
