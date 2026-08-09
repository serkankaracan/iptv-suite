# Windows development bootstrap

Bu klasör M1 kapsamındaki tek platform uygulamasını içerir. Uygulama yalnız gerçek assembly/package sürümünü, build configuration'ını ve process architecture'ını gösteren boş development shell'dir.

## Toolchain

| Bileşen | Exact sürüm / sınır |
|---|---|
| .NET SDK | `10.0.302` |
| .NET runtime/reference pack | `10.0.10` |
| Windows App SDK | `2.3.1` stable |
| Windows SDK BuildTools | `10.0.26100.8249` stable |
| MSTest | `4.3.3` stable |
| OS / architecture | Windows 11 `10.0.26100+`, x64 |

`global.json`, central package versions ve her projedeki `packages.lock.json` birlikte deterministic restore sınırını oluşturur. Preview/Experimental doğrudan bağımlılık yoktur. Resmî `winapp` CLI public preview olduğundan M1 build veya launch zincirine alınmamıştır.

## Restore, build ve test

Bütün komutları repository kökünden çalıştırın:

```powershell
dotnet --version
dotnet restore .\apps\windows\IptvSuite.Windows.sln --locked-mode -p:Platform=x64
dotnet build .\apps\windows\IptvSuite.Windows.sln -c Debug -p:Platform=x64 --no-restore
dotnet build .\apps\windows\IptvSuite.Windows.sln -c Release -p:Platform=x64 --no-restore
dotnet test .\apps\windows\tests\IptvSuite.ArchitectureTests\IptvSuite.ArchitectureTests.csproj -c Release --no-build --no-restore
```

Build içindeki analyzers/code-style denetimi lint gate'idir; ayrı bir linter dependency'si yoktur. SDK sürümü `10.0.302` değilse komutlara devam etmeyin.

Yeni package sürümü bilinçli değiştirildiğinde önce normal restore ile lock dosyaları güncellenir, diff incelenir ve ardından yukarıdaki `--locked-mode` akışı tekrar çalıştırılır.

## Signed MSIX smoke

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
6. Paketi kurar, AUMID ile görünür pencereyi açar ve normal window-close mesajıyla kapatır.
7. Exact development package'ı, package app-data'yı ve exact-thumbprint sertifikayı temizler.

Başarılı kanıt `.artifacts/msix-smoke/last-success.json` altında kalır; bütün `.artifacts` içeriği Git dışında build çıktısıdır. Shared `Microsoft.WindowsAppRuntime.2` framework paketi başka uygulamalarca kullanılabileceği için kaldırılmaz.

## Disposable development identity

Manifestteki `IptvSuite.LocalDev.6f0d9a64` / `CN=IptvSuite Local Development` yalnız yerel M1 test lineage'ıdır. Partner Center rezervasyonu, Store association veya marka hakkı iddiası değildir. Gelecekteki Store identity farklı PFN/AUMID ve app-data alanı oluşturabilir; development paketinden in-place upgrade veya veri migration garantisi yoktur.

Private key, PFX, parola, certificate thumbprint, `Package.StoreAssociation.xml` veya Store identity dosyası commit edilmez. Script wildcard package/certificate silmez.
