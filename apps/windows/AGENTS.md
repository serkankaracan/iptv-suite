# Windows çalışma kuralları

Bu dosya `apps/windows/` ağacı için kök `AGENTS.md` kurallarını daraltır.

## Proje sınırları

- `IptvSuite.Domain`: plain `net10.0`, project/package dependency yok; M3 terminology, pure validation, safe error ve redaction contract'ları burada kalır.
- `IptvSuite.Application`: yalnız `IptvSuite.Domain` project reference'ı.
- `IptvSuite.Infrastructure`: yalnız `IptvSuite.Application` project reference'ı; WinUI tipi yok.
- `IptvSuite.Windows`: tek Presentation/composition root; yalnız Application ve Infrastructure'a bağlı.
- `IptvSuite.Testing`: yalnız test destek executable/library'si; production proje reference'ı yoktur ve production tarafından referans alınamaz.
- `IptvSuite.UnitTests`: `IptvSuite.Domain`, `IptvSuite.Testing` ve MSTest'e bağlıdır; M3 domain table testleri ile M2 harness testlerini içerir. `IptvSuite.IntegrationTests` yalnız `IptvSuite.Testing` ile MSTest'e bağlıdır; architecture test ayrı kalır.
- M2 test double'ları production `IPlayer`, `ISecretStore` veya provider contract'ı değildir. M3'te ürün player/provider/parser/HTTP/database/secret-store mantığı, DI/MVVM paketi ve feature navigation ekleme.

## M3 domain güvenlik sınırı

- `SafeEndpoint` yalnız HTTPS scheme, IDNA host ve effective port taşır; raw path, query, fragment veya user-info'dan locator üretmez.
- Username, password ve full remote locator yalnız validation çağrısının kısa ömürlü girdisidir; başarılı domain sonucu yalnız opaque `SecretReference`/`ProtectedLocatorReference` taşır.
- Domain error yalnız stable code, retryability ve resource key taşır; exception, provider text veya raw input context'i ekleme.
- `LiveChannel` playback için typed `ProviderItemKey` ile `ProtectedLocatorReference` alanlarından tam olarak birini taşır; identity metadata olan M3U `tvg-id` tek başına playback reference değildir.
- HTTP ETag opaque validator'dır; trim, Unicode normalization veya case dönüşümü uygulama.
- Source display name sınırı 100 Unicode scalar; locator 4096, username 256 ve password 1024 scalar'dır. Sınırı değiştirirsen contract testlerini ve ilgili mimari belgeyi birlikte güncelle.
- M3U/HLS ayrımı uzantıdan yapılmaz. M3 yalnız bounded content-prefix karar contract'ıdır; gerçek incremental parser M7'ye aittir.
- Movie, Series, Season, Episode ve EPG production type'larını ilgili post-MVP milestone'dan önce ekleme.

## Zorunlu doğrulama

Repository kökünden sırasıyla:

```powershell
dotnet restore .\apps\windows\IptvSuite.Windows.sln --locked-mode -p:Platform=x64
dotnet build .\apps\windows\IptvSuite.Windows.sln -c Debug -p:Platform=x64 --no-restore
dotnet build .\apps\windows\IptvSuite.Windows.sln -c Release -p:Platform=x64 --no-restore
dotnet test .\apps\windows\tests\IptvSuite.ArchitectureTests\IptvSuite.ArchitectureTests.csproj -c Release --no-build --no-restore
```

SDK resolver exact `10.0.302` kullanmalı; `rollForward` kapalı ve prerelease seçimi yasaktır. M2 değişikliğinde ayrıca iki-run quality gate'i çalıştır:

```powershell
.\eng\Invoke-WindowsQualityGate.ps1
```

Bu betik locked restore ile Debug/Release x64 build'i de tekrarlar; üç test projesini ayrı TRX dosyalarıyla iki kez çalıştırır, test sonuç setlerini ve iki generated fixture'ın SHA-256 değerlerini karşılaştırır, sentinel fail/recovery self-test'ini ve artifact canary taramasını uygular.

Signed install/launch/uninstall veya manifest değişikliğinde ayrıca yönetici PowerShell'de:

```powershell
.\eng\Invoke-WindowsPackageSmoke.ps1 -Configuration Release
```

## Paket güvenliği

- Yalnız x64, packaged, framework-dependent development MSIX üret.
- Manifestte M1 için yalnız `runFullTrust` capability'sine izin ver.
- Development `Name`/`Publisher` değerini Store identity sayma; Store association/reservation oluşturma.
- PFX/private key/parola/thumbprint commit etme. Paket ve sertifika kaldırırken wildcard değil exact identity/thumbprint kullan.
- Preview/Experimental dependency ekleme. Package değişikliğinde resmî stable kaynağı doğrula, central version ile lock dosyalarını birlikte güncelle.

## Test verisi ve sınırlar

- Test network'ü yalnız OS-seçimli portta loopback Kestrel'dir; gerçek provider host'u, account, playlist veya medya kullanma.
- `apps/windows/testdata/m2/fixture-spec.json` tamamen sentetiktir. Generator version, algorithm version, seed, SHA-256, provenance ve internal-only `UNVERIFIED` LicenseRef manifestte kalır.
- Canary gerçek secret değildir ve yalnız bilinen test marker'ının artifact'a sızmasını sınar; genel credential detector, redactor veya production güvenlik kanıtı değildir.
- Fake clock/transport/secret store/player yalnız deterministik harness'tir; gerçek timer/network/DPAPI/player/codec davranışını kanıtlamaz.
