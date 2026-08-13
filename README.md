# IPTV Suite

`IPTV Suite` henüz doğrulanmamış bir iç proje adıdır. **M1 — reproducible bootstrap**, **M2 — test altyapısı ve quality gates** ve **M3 — domain terminology, validation ve safe errors** mühendislik kabulleri tamamlanmıştır. **M4 secure-storage foundation** ise `IN PROGRESS` durumundadır: Application katmanında typed/semantic-owner-bound `ISecretStore`, bounded/zeroing lease, merkezi sanitizer, source-draft koruma operation'ı ve `DeletionPending` aggregate'ten exact configuration kaydını silen dar primitive; Infrastructure katmanında Windows DPAPI `CurrentUser` adapter'ı; packaged composition root'ta `LocalCache\ProtectedStore\v2` initialization'ı ve üç ardışık activation kullanan ayrı test-only lifecycle hostu vardır. Lifecycle hostunun güncel lane'i aynı disposable package family ve signer ile `0.0.1.0` baseline'dan `0.0.2.0` sürümüne normal `Add-AppxPackage` update'i kodlar; update sonrası process protected kaydı okuyup günceller ve siler, fakat bu yeni lane'in hosted execution sonucu henüz `UNVERIFIED`dır. Repository hâlâ network/provider çağrısı, gerçek M3U parser, veritabanı, feature UI ve player içermez; production PFN update'i, reset/uninstall-reinstall, gerçek ikinci kullanıcı, source disable/drain + source-wide deletion/reconciliation ve 50k storage kararı tamamlanmadan M4 kabul edilmiş sayılmaz.

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

Betik yalnız `10.0.302` SDK ile çalışır; locked restore, Debug/Release x64 build, ayrı TRX dizinlerinde iki test koşusu, fixture hash karşılaştırması, negatif sentinel fail/recovery provası ve quality-artifact canary taramasını tek gate'te yürütür. Özet kanıtı `.artifacts/quality-gates/evidence/quality-summary.json` altında üretir. Tarihsel M2 gate'i 2026-08-09'da 22/22 testi iki koşuda geçmiş; aynı gün [hosted run `31327398270`](https://github.com/serkankaracan/iptv-suite/actions/runs/31327398270) commit `79cf619c6683fa9c4213846455e376fb1b0cb11c` için quality, signed packaged-smoke ve birleşik Windows gate işlerini başarıyla tamamlamıştır. M3 sonrası aynı local gate 105/105; ilk M4 foundation kaydı 135/135 testi iki deterministik koşuda geçmiştir. [Hosted run `31735655363`](https://github.com/serkankaracan/iptv-suite/actions/runs/31735655363), commit `6ac235f49ffd297196189e5e9441ad0c9e04f116` için source-draft protection, fail-closed packaged composition ve 1.024 startup temp-aday cap'i dahil tarihsel v1 store snapshot'ında 153/153 testi iki aynı sonuç setiyle; signed packaged-smoke ve required coordinator'ı başarıyla tamamlamıştır. Sanitized package evidence o committe beklenen v1 dizini için `ProtectedStoreDirectoryInitialized=true` taşır. Internal source-payload decoder ve semantic-owner-bound protected-store v2 snapshot'ı 171/171 testi geçmiştir. Exact configuration-record deletion primitive'i dahil güncel local exact-SDK gate architecture 13, unit 137 ve integration 33 olmak üzere 183/183 testi iki deterministik koşuda; sentinel, fixture ve canary kontrolleriyle geçmiştir. 2026-08-14 GitHub Actions UI kaydı, workflow run number `#15`in commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` için üç işin üçünü de başarıyla tamamladığını ve üç artifact ürettiğini gösterir; bu tarihsel kayıt ilk owner-v2 quality/package/lifecycle geçişini workflow/UI düzeyinde **VERIFIED** yapar. Aynı tarihli takip UI kaydı, exact deletion primitive'ini içeren commit `3e806edd9a9482b87cccbf000ac8a81823562f6a` için run number `#16`nın 3/3 işi yeşil, artifact sayısını üç ve toplam süreyi `8m24s` gösterdiğini doğrular; bu sonuç da yalnız workflow/UI düzeyinde **VERIFIED**dir. Bu kayıtlarda GitHub database run ID'si, job log/test sayısı, indirilen lifecycle JSON alanları veya artifact ZIP/hash değerleri bağımsız doğrulanmamıştır. Signed `0.0.1.0 → 0.0.2.0` test-family update lane'i `#16`dan sonra eklenmiştir; hosted execution'ı ile production PFN update, reset/uninstall-reinstall ve wrong-user kanıtları hâlâ pending'dir. Kalıcı M2 kanıt özeti [M2 completion evidence](docs/quality/M2_COMPLETION_EVIDENCE.md) belgesindedir.

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

Bu ikinci akış aynı disposable test package family, signer ve Windows kullanıcısında üç process activation çalıştırır. Baseline `0.0.1.0` ilk kaydı ve DPAPI-protected ticket'ı oluşturur; ikinci baseline process duplicate create'i reddeder. Betik daha sonra aynı family'nin daha yüksek `0.0.2.0` sürümünü normal `Add-AppxPackage -Path` ile kurar; updated process kaydı yeniden okuyup yanlış-owner, update, delete ve post-delete yokluk davranışını sınar. Sanitized evidence schema v2; iki package sürüm/hash/signature alanını, same-family/değişen-full-name update sonucunu ve post-update read/canary sonuçlarını allowlist'ler. Test MSIX'leri GitHub artifact'ına konmaz. Run number `#15` yalnız update lane'inden önceki same-version akışı commit `6da2fc92a223c23d07f7a902de7607e16b1cbb8a` için workflow/UI düzeyinde **VERIFIED** yapmıştır; `#16` da bu yeni lane'den önceki commit'e aittir. Signed update lane'inin hosted execution'ı `UNVERIFIED`; production PFN, reset/uninstall-reinstall ve ikinci kullanıcı kanıtı değildir.

Ayrıntılar, test katmanı sınırları, sentetik fixture provenance'i, canary'nin neyi taradığı, sertifika temizliği ve development identity sınırı için [Windows çalışma rehberine](apps/windows/README.md) bakın. Mimari karar ve aşamalar [Phase 0 belgelerinde](docs/architecture/ARCHITECTURE_REPORT.md) tutulur.
