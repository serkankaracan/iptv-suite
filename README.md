# IPTV Suite

`IPTV Suite` henüz doğrulanmamış bir iç proje adıdır. **M1 — reproducible bootstrap**, **M2 — test altyapısı ve quality gates** ve **M3 — domain terminology, validation ve safe errors** mühendislik kabulleri tamamlanmıştır. **M4 secure-storage foundation** ise `IN PROGRESS` durumundadır: Application katmanında typed/semantic-owner-bound `ISecretStore`, bounded/zeroing lease, merkezi sanitizer ve source-draft koruma operation'ı; Infrastructure katmanında Windows DPAPI `CurrentUser` adapter'ı; packaged composition root'ta `LocalCache\ProtectedStore\v2` initialization'ı ve ayrı test-only two-process lifecycle hostu vardır. Repository hâlâ network/provider çağrısı, gerçek M3U parser, veritabanı, feature UI ve player içermez; hosted lifecycle sonucu, gerçek packaged update/reset/uninstall, gerçek ikinci kullanıcı, source deletion/reconciliation ve 50k storage kararı tamamlanmadan M4 kabul edilmiş sayılmaz.

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

Betik yalnız `10.0.302` SDK ile çalışır; locked restore, Debug/Release x64 build, ayrı TRX dizinlerinde iki test koşusu, fixture hash karşılaştırması, negatif sentinel fail/recovery provası ve quality-artifact canary taramasını tek gate'te yürütür. Özet kanıtı `.artifacts/quality-gates/evidence/quality-summary.json` altında üretir. Tarihsel M2 gate'i 2026-08-09'da 22/22 testi iki koşuda geçmiş; aynı gün [hosted run `31327398270`](https://github.com/serkankaracan/iptv-suite/actions/runs/31327398270) commit `79cf619c6683fa9c4213846455e376fb1b0cb11c` için quality, signed packaged-smoke ve birleşik Windows gate işlerini başarıyla tamamlamıştır. M3 sonrası aynı local gate 105/105; ilk M4 foundation kaydı 135/135 testi iki deterministik koşuda geçmiştir. [Hosted run `31735655363`](https://github.com/serkankaracan/iptv-suite/actions/runs/31735655363), commit `6ac235f49ffd297196189e5e9441ad0c9e04f116` için source-draft protection, fail-closed packaged composition ve 1.024 startup temp-aday cap'i dahil tarihsel v1 store snapshot'ında 153/153 testi iki aynı sonuç setiyle; signed packaged-smoke ve required coordinator'ı başarıyla tamamlamıştır. Sanitized package evidence o committe beklenen v1 dizini için `ProtectedStoreDirectoryInitialized=true` taşır. Internal source-payload decoder ve semantic-owner-bound protected-store v2 snapshot'ı 171/171 testi geçmiştir. Packaged lifecycle harness dahil güncel local exact-SDK gate architecture 13, unit 127 ve integration 32 olmak üzere 172/172 testi iki deterministik koşuda; sentinel, fixture ve canary kontrolleriyle geçmiştir. Local summary `commitSha=null` olduğundan hosted kanıt değildir; v2 package/lifecycle hosted sonucu henüz yenilenmemiştir. Packaged update/reset/uninstall ve wrong-user kanıtı da pending'dir. Kalıcı M2 kanıt özeti [M2 completion evidence](docs/quality/M2_COMPLETION_EVIDENCE.md) belgesindedir.

M4 per-record DPAPI yaklaşımını hafif biçimde doğrulayan, quality/CI dışında kalan opt-in smoke:

```powershell
.\eng\Invoke-WindowsSecretStoreSpike.ps1 -Mode Smoke
```

Bu 1.000 kayıt/tek tur smoke yalnız harness doğrulamasıdır. Sabit 5k/10k/20k/50k × 20 `Decision` modu yoğun disk yazımı yapar; ayrı açık onay, temiz worktree ve `-AllowDecision` ister.

Restore, build ve test için Visual Studio gerekmez; Windows SDK build araçları exact stable NuGet paketiyle gelir. İmzalı gerçek MSIX install/launch/uninstall provası yönetici PowerShell'i gerektirir:

```powershell
.\eng\Invoke-WindowsPackageSmoke.ps1 -Configuration Release
```

M4'ün ayrı, production payload'a girmeyen packaged lifecycle hostu için yönetici PowerShell'de:

```powershell
.\eng\Invoke-WindowsPackageLifecycleSmoke.ps1 -Configuration Release
```

Bu ikinci akış aynı disposable test package ve Windows kullanıcısında iki ayrı process launch ile owner-bound v2 kaydı oluşturma, yeniden okuma, yanlış-owner reddi, update, delete ve post-delete yokluk davranışını sınar. Test MSIX'i GitHub artifact'ına konmaz; yalnız sanitized sonuç artifact'ı tutulur. Aynı-version process restart kanıtıdır; package update/reset/reinstall, production PFN veya ikinci kullanıcı kanıtı değildir. Hosted sonuç yeni commit çalışmadan `PENDING` kalır.

Ayrıntılar, test katmanı sınırları, sentetik fixture provenance'i, canary'nin neyi taradığı, sertifika temizliği ve development identity sınırı için [Windows çalışma rehberine](apps/windows/README.md) bakın. Mimari karar ve aşamalar [Phase 0 belgelerinde](docs/architecture/ARCHITECTURE_REPORT.md) tutulur.
