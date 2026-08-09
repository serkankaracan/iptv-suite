# Windows çalışma kuralları

Bu dosya `apps/windows/` ağacı için kök `AGENTS.md` kurallarını daraltır.

## Proje sınırları

- `IptvSuite.Domain`: plain `net10.0`, project/package dependency yok.
- `IptvSuite.Application`: yalnız `IptvSuite.Domain` project reference'ı.
- `IptvSuite.Infrastructure`: yalnız `IptvSuite.Application` project reference'ı; WinUI tipi yok.
- `IptvSuite.Windows`: tek Presentation/composition root; yalnız Application ve Infrastructure'a bağlı.
- `IptvSuite.Testing`: yalnız test destek executable/library'si; production proje reference'ı yoktur ve production tarafından referans alınamaz.
- `IptvSuite.UnitTests` ve `IptvSuite.IntegrationTests`: yalnız `IptvSuite.Testing` ile MSTest'e bağlı M2 harness testleridir; architecture test ayrı kalır.
- M2 test double'ları production `IPlayer`, `ISecretStore` veya provider contract'ı değildir. Ürün player/provider/parser/HTTP/database/secret-store mantığı, DI/MVVM paketi ve feature navigation ekleme.

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
