# Windows çalışma kuralları

Bu dosya `apps/windows/` ağacı için kök `AGENTS.md` kurallarını daraltır.

## Proje sınırları

- `IptvSuite.Domain`: plain `net10.0`, project/package dependency yok.
- `IptvSuite.Application`: yalnız `IptvSuite.Domain` project reference'ı.
- `IptvSuite.Infrastructure`: yalnız `IptvSuite.Application` project reference'ı; WinUI tipi yok.
- `IptvSuite.Windows`: tek Presentation/composition root; yalnız Application ve Infrastructure'a bağlı.
- M1 sırasında player, provider, parser, HTTP, database, secret store, DI/MVVM paketi, feature navigation veya yeni proje ekleme.

## Zorunlu doğrulama

Repository kökünden sırasıyla:

```powershell
dotnet restore .\apps\windows\IptvSuite.Windows.sln --locked-mode -p:Platform=x64
dotnet build .\apps\windows\IptvSuite.Windows.sln -c Debug -p:Platform=x64 --no-restore
dotnet build .\apps\windows\IptvSuite.Windows.sln -c Release -p:Platform=x64 --no-restore
dotnet test .\apps\windows\tests\IptvSuite.ArchitectureTests\IptvSuite.ArchitectureTests.csproj -c Release --no-build --no-restore
```

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
