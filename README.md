# IPTV Suite

`IPTV Suite` henüz doğrulanmamış bir iç proje adıdır. **M1 — reproducible bootstrap**, **M2 — test altyapısı ve quality gates** ve **M3 — domain terminology, validation ve safe errors** mühendislik kabulleri tamamlanmıştır. **M4 secure-storage foundation** ise `IN PROGRESS` durumundadır: Application katmanında typed `ISecretStore`, bounded/zeroing lease ve merkezi sanitizer; Infrastructure katmanında Windows DPAPI `CurrentUser` adapter'ı ve packaged `LocalCache` factory'si vardır. Repository hâlâ network/provider çağrısı, gerçek M3U parser, veritabanı, feature UI ve player içermez; packaged update/reset/uninstall, gerçek ikinci kullanıcı ve 50k storage kararı tamamlanmadan M4 kabul edilmiş sayılmaz.

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

Betik yalnız `10.0.302` SDK ile çalışır; locked restore, Debug/Release x64 build, ayrı TRX dizinlerinde iki test koşusu, fixture hash karşılaştırması, negatif sentinel fail/recovery provası ve quality-artifact canary taramasını tek gate'te yürütür. Özet kanıtı `.artifacts/quality-gates/evidence/quality-summary.json` altında üretir. Tarihsel M2 gate'i 2026-08-09'da 22/22 testi iki koşuda geçmiş; aynı gün [hosted run `31327398270`](https://github.com/serkankaracan/iptv-suite/actions/runs/31327398270) commit `79cf619c6683fa9c4213846455e376fb1b0cb11c` için quality, signed packaged-smoke ve birleşik Windows gate işlerini başarıyla tamamlamıştır. M3 sonrası aynı local gate 105/105; M4 foundation sonrası 2026-08-10 local gate 130/130 testi iki deterministik koşuda geçmiştir. Son sonuç non-packaged CurrentUser DPAPI davranışını kapsar; packaged lifecycle veya wrong-user kanıtı değildir. Kalıcı M2 kanıt özeti [M2 completion evidence](docs/quality/M2_COMPLETION_EVIDENCE.md) belgesindedir.

Restore, build ve test için Visual Studio gerekmez; Windows SDK build araçları exact stable NuGet paketiyle gelir. İmzalı gerçek MSIX install/launch/uninstall provası yönetici PowerShell'i gerektirir:

```powershell
.\eng\Invoke-WindowsPackageSmoke.ps1 -Configuration Release
```

Ayrıntılar, test katmanı sınırları, sentetik fixture provenance'i, canary'nin neyi taradığı, sertifika temizliği ve development identity sınırı için [Windows çalışma rehberine](apps/windows/README.md) bakın. Mimari karar ve aşamalar [Phase 0 belgelerinde](docs/architecture/ARCHITECTURE_REPORT.md) tutulur.
