# IPTV Suite

`IPTV Suite` henüz doğrulanmamış bir iç proje adıdır. **M1 — reproducible bootstrap** tamamlanmıştır; **M2 — test altyapısı ve quality gates** implementation in progress durumundadır. M2 test scaffold'u ürün özelliği değildir: repository hâlâ provider/parser mantığı, veritabanı, production credential store ve gerçek player içermez.

Windows uygulaması C# / .NET 10 LTS, WinUI 3 ve framework-dependent MSIX kullanır. Production bağımlılık yönü şöyledir:

```text
IptvSuite.Windows ──> IptvSuite.Application ──> IptvSuite.Domain
        │
        └──────────> IptvSuite.Infrastructure ──> IptvSuite.Application
```

## Hızlı doğrulama

Önkoşullar:

- x64 Windows 11 `10.0.26100` veya üstü;
- `global.json` ile exact pinlenen .NET SDK `10.0.302` (`rollForward: disable`, `allowPrerelease: false`);
- ilk restore için `https://api.nuget.org/v3/index.json` erişimi.

Repository kökünde:

```powershell
dotnet --version
dotnet restore .\apps\windows\IptvSuite.Windows.sln --locked-mode -p:Platform=x64
dotnet build .\apps\windows\IptvSuite.Windows.sln -c Debug -p:Platform=x64 --no-restore
dotnet build .\apps\windows\IptvSuite.Windows.sln -c Release -p:Platform=x64 --no-restore
dotnet test .\apps\windows\tests\IptvSuite.ArchitectureTests\IptvSuite.ArchitectureTests.csproj -c Release --no-build --no-restore
```

M2'nin exact-SDK restore/build ve iki ardışık unit/integration/architecture koşusu:

```powershell
.\eng\Invoke-WindowsQualityGate.ps1
```

Betik yalnız `10.0.302` SDK ile çalışır; locked restore, Debug/Release x64 build, ayrı TRX dizinlerinde iki test koşusu, fixture hash karşılaştırması, negatif sentinel fail/recovery provası ve quality-artifact canary taramasını tek gate'te yürütür. Özet kanıtı `.artifacts/quality-gates/evidence/quality-summary.json` altında üretir. Local gate 2026-08-09'da isolated exact SDK ile 22/22 testi iki koşuda geçmiştir; M2, hosted packaged-smoke gerçekten başarılı olup kanıt artifact'ı kaydedilene kadar tamamlanmış sayılmaz.

Restore, build ve test için Visual Studio gerekmez; Windows SDK build araçları exact stable NuGet paketiyle gelir. İmzalı gerçek MSIX install/launch/uninstall provası yönetici PowerShell'i gerektirir:

```powershell
.\eng\Invoke-WindowsPackageSmoke.ps1 -Configuration Release
```

Ayrıntılar, test katmanı sınırları, sentetik fixture provenance'i, canary'nin neyi taradığı, sertifika temizliği ve development identity sınırı için [Windows çalışma rehberine](apps/windows/README.md) bakın. Mimari karar ve aşamalar [Phase 0 belgelerinde](docs/architecture/ARCHITECTURE_REPORT.md) tutulur.
