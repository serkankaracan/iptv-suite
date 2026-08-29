# Windows çalışma kuralları

Bu dosya `apps/windows/` ağacı için kök `AGENTS.md` kurallarını daraltır.

## Proje sınırları

- `IptvSuite.Domain`: plain `net10.0`, project/package dependency yok; M3 terminology, pure validation, safe error ve redaction contract'ları burada kalır.
- `IptvSuite.Application`: yalnız `IptvSuite.Domain` project reference'ı.
- `IptvSuite.Infrastructure`: yalnız `IptvSuite.Application` project reference'ı; WinUI tipi yok.
- `IptvSuite.Windows`: tek Presentation/composition root; yalnız Application ve Infrastructure'a bağlı.
- `IptvSuite.Testing`: yalnız test destek executable/library'si; production proje reference'ı yoktur ve production tarafından referans alınamaz.
- `IptvSuite.UnitTests`: `IptvSuite.Application`, `IptvSuite.Domain`, `IptvSuite.Testing` ve MSTest'e bağlıdır; pure Application/Domain testleri ile M2 harness testlerini içerir. `IptvSuite.IntegrationTests`, yalnız test yönünde Application, Infrastructure ve Testing'e bağlıdır; architecture test ayrı kalır.
- `IptvSuite.SecretStoreSpike`: yalnız opt-in M4 ölçüm executable'ıdır; Application/Domain/Infrastructure ve test-only canary scanner'a bağlıdır. Quality/CI tarafından çalıştırılmaz, production payload'a giremez ve ağır `Decision` modu açık onay olmadan başlatılmaz.
- `IptvSuite.ProtectedCatalogSpike`: yalnız reddedilen bulk file-per-record düzeniyle karşılaştırılacak test-only immutable-container executable'ıdır; yalnız `IptvSuite.Testing` ve mevcut `ProtectedData` paketine bağlı, nonpackable/nonpublishable x64 kalır. Solution quality build'inde derlenebilir; fakat production projeleri tarafından referans alınamaz, production MSIX'e giremez, normal quality/hosted workflow executable'ı çalıştıramaz ve ağır `Decision` modu fresh açık onay olmadan başlatılmaz.
- `IptvSuite.PackageLifecycleHarness`: yalnız M4 packaged-process lifecycle kanıtı için kullanılan, ayrı disposable identity'li ve publish edilemeyen x64 MSIX test hostudur. Production `IptvSuite.Windows` tarafından referans alınamaz, production MSIX'e veya upload artifact'ına giremez.
- `IptvSuite.DpapiUserBoundaryHarness`: yalnız M4 gerçek Windows-account/DPAPI `CurrentUser` sınır kanıtı için kullanılan framework-dependent x64 test executable'ıdır. Production projeleri tarafından referans alınamaz, production MSIX'e giremez ve normal quality gate executable'ı çalıştırmaz; gerçek local user oluşturan controller yalnız açık onayla elevated hostta veya zorunlu hosted job'da çalıştırılır.
- `IptvSuite.CatalogCrashHarness`: yalnız M8 SQLite transaction'ını gerçek ayrı süreçte açık bırakıp test controller'ının süreci öldürmesi için kullanılan nonpackable/nonpublishable x64 executable'dır. Production projeleri tarafından referans alınamaz, production MSIX'e giremez ve sentetik fixture dışında veri kullanamaz.
- `IptvSuite.CatalogUiAcceptanceHarness`: yalnız M9 packaged UI virtualization/performance kabulü için disposable package `LocalCache` katalog yoluna 50.000 sentetik provider-key channel yazan nonpackable/nonpublishable x64 executable'dır. Production projeleri tarafından referans alınamaz, production MSIX'e giremez, credential/protected locator/network kullanamaz ve normal quality gate executable'ı çalıştırmaz.
- `IptvSuite.PlaybackUiAcceptanceHarness`: yalnız M11–M12 production-package playback kabulü için disposable package'ın exact `LocalCache` katalog/protected-store yollarına production Remote-M3U + DPAPI `CurrentUser` zinciriyle iki ayrı sentetik Tier A channel yazar ve OS-seçimli portta loopback HTTPS fixture'ını bounded süreyle sunar. Nonpackable/nonpublishable x64 executable'dır; production projeleri tarafından referans alınamaz, production MSIX'e giremez, raw locator/credential/port/path'i stdout, ticket veya evidence'a yazamaz ve normal quality gate executable'ı çalıştırmaz.
- `IptvSuite.PlaybackCompatibilitySpike`: yalnız M10 candidate kararını tekrar üretmek için korunan disposable, nonpackable/nonpublishable x64 WinUI test paketidir. Rejected native payload nedeniyle normal solution/workflow graph'ında değildir; yalnız explicit decision audit restore eder. Production projeleri tarafından referans alınamaz ve production MSIX'e giremez.
- `IptvSuite.NativePlaybackCompatibilitySpike`: ADR-007 Windows-native Tier A fallback acceptance için kullanılan disposable, nonpackable/nonpublishable x64 WinUI test paketidir. Production projeleri tarafından referans alınamaz, production MSIX'e giremez, yalnız loopback sentetik corpus kullanır ve acceptance tamamlanmadan production adapter sayılmaz.
- M2 test double'ları production `IPlayer`, `ISecretStore` veya provider contract'ı değildir. IntegrationTests içindeki M4 fake production `ISecretStore` contract senaryosudur, fakat gerçek DPAPI veya packaged lifecycle kanıtı değildir. M4 tamamlanmıştır. M5 boyunca yalnız bounded HTTP transport/probe katmanı eklenebilir; provider semantiği M6'ya, parser M7'ye, database M8'e, player M10'a ve feature navigation ilgili UI milestone'una kadar production kapsamına alınmaz.

## M4 protected-storage sınırı

- `ISecretStore` Application'da, Windows DPAPI adapter'ı Infrastructure'da, packaged `LocalCache` path seçimi yalnız Windows composition root'ta kalır. Domain'e IO/crypto/storage tipi ekleme.
- Yalnız `DataProtectionScope.CurrentUser`; `LocalMachine`, PasswordVault bulk store veya ad-hoc crypto kullanma.
- Protected record source + purpose + semantic owner + typed opaque reference ile bağlanır. Source credential/remote playlist owner'ı `SourceConfigurationId`, channel stream/logo owner'ı `ChannelId`dir. Raw locator, username, password, endpoint veya display name dosya adı/path/log/result içine girmez.
- Plaintext owned buffer'ları ve lease'leri `CryptographicOperations.ZeroMemory` ile best-effort sıfırla; `ToString`, debugger ve JSON yüzeyleri sensitive değeri döndürmesin.
- Normal test-host CRUD/restart sonucu packaged `LocalCache`, update/reset/uninstall veya gerçek wrong-user kanıtı değildir. Reddedilen per-record düzenin 50k sonucu, reviewed alternatifin fresh-onaylı comparative 50k kararı ve lifecycle matrisi birlikte değerlendirilmeden ADR-003/M4 `Completed` yazma.

## M3 domain güvenlik sınırı

- `SafeEndpoint` yalnız normalized `http`/`https` scheme, IDNA host ve effective port taşır; raw path, query, fragment veya user-info'dan locator üretmez. HTTPS her source kind için varsayılandır. HTTP yalnız ADR-008 kapsamındaki açık-onaylı Remote M3U veya M17'den itibaren ADR-009 kapsamındaki **ayrı** açık-onaylı Xtream-compatible exact source configuration için açılabilir; iki consent türü birbirinin yerine geçemez.
- Remote M3U HTTP locator query/token taşıyabilir fakat user-info taşıyamaz. Initial HTTP channel locator yalnız HTTP source'un exact `SafeEndpoint` origin'iyle aynıysa kabul edilir; HTTPS channel initial URI/scheme validation ile kabul edilir. Remote M3U HTTP request'te `Authorization`/`Cookie`/`Referer`, HTTP logo, cross-origin HTTP playlist redirect ve HTTPS→HTTP downgrade yasaktır; revalidated HTTP→HTTPS upgrade mümkündür. M17 HTTP Xtream API/playback'i yalnız ADR-009 exact source/configuration/origin bağıyla açılır; automatic redirect, HTTP image, cross-origin HTTP locator ve Authorization/Cookie/Referer fallback'i yasaktır. Native `MediaSource` HLS/media alt-kaynakları ile player-internal redirect'lerinde same-origin veya `EndpointAddressPolicy` enforcement varmış gibi kod, test ya da belge iddiası oluşturma; bu yüzey her iki cleartext playback yolunda `UNVERIFIED` release blocker'dır.
- HTTP source ağ isteğinden önce source-kind-specific UI onayı ve source yaşam döngüsü boyunca kalıcı insecure-transport uyarısı ister. Remote M3U ile Xtream consent record/resource key'lerini ayır; Xtream endpoint veya credential replace işleminde fresh configuration owner ve fresh consent iste. Protected-at-rest locator/credential'ın transit confidentiality/integrity sağlamadığını gizleme veya HTTP'yi güvenli diye etiketleme.
- Remote M3U indirmesi scheme'den bağımsız olarak decompressed response için `128 MiB`, toplam request için iki dakika ile sınırlıdır. M17 sentetik loopback profili Xtream account response'unu `64 KiB`, category response'unu `1 MiB`, Live/VOD/Series top-level listelerini `64 MiB` ve yalnız seçilen `series_info` response'unu `16 MiB` ile sınırlar; varsayılan 15 saniye request süresi değişmez. Bu Xtream-only factory tavanlarını genel HTTP `4 MiB` veya Remote M3U `128 MiB` factory'sine taşıma. Hiçbir Xtream Live/VOD/Series response'u unbounded okunamaz. Remote M3U onboarding, HTTP/HTTPS ayrımı olmadan preliminary full-body probe yapmamalı; validation — HTTP için ayrıca açık onay — sonrasında protected locator'ı stage edip tek bounded streaming import isteği kullanmalı, açıkça `NotCommitted` sonuçta exact protected kaydı temizlemelidir.
- Incremental Remote M3U parser'ın fiziksel satır tavanı `65.536` UTF-16 kod birimidir; bu gerçek hard cap kaldırılmaz veya tüm response'u tek string'e çevirmez. Satır tavanını semantik alan tavanıyla karıştırma: locator ve generic metadata değeri `4.096`, `tvg-id` `512`, channel/group name `256` ile bounded kalır; decoded toplam `128 Mi` UTF-16 kod birimi ve decompressed response `128 MiB` bütçeleri değişmez. `65.536` üzerindeki satır `PlaylistLineLimitExceeded`, toplam-character ve entry bütçeleri sırasıyla ayrı append-only `PlaylistTotalLimitExceeded` ve `PlaylistEntryLimitExceeded` kodlarıyla fail-closed olmalıdır.
- Production katalog persistence bütçesi en çok 50.000 geçerli Remote M3U kaydıdır. Yalnız açık-onaylı HTTP compatibility yolunda limit sonrası geçerli kayıtlar sink'e yazılmadan sayılıp bounded stream sonuna kadar doğrulanır; ilk 50.000 kayıt commit edilir ve `EntryLimitReached` kullanıcıya görünür uyarı üretir. HTTPS/direct parser'ın 50.001'inci geçerli kayıtta fail-closed davranışı ile tarihsel M14 matrisi değişmez. Sıfır kullanılabilir kayıt `Ready` snapshot olarak commit edilemez.
- Username, password ve full remote locator yalnız validation çağrısının kısa ömürlü girdisidir; başarılı domain sonucu yalnız opaque `SecretReference`/`ProtectedLocatorReference` taşır.
- Domain error yalnız stable code, retryability ve resource key taşır; exception, provider text veya raw input context'i ekleme.
- `LiveChannel` playback için typed `ProviderItemKey` ile `ProtectedLocatorReference` alanlarından tam olarak birini taşır; identity metadata olan M3U `tvg-id` tek başına playback reference değildir.
- HTTP ETag opaque validator'dır; trim, Unicode normalization veya case dönüşümü uygulama.
- Source display name sınırı 100 Unicode scalar; locator 4096, username 256 ve password 1024 scalar'dır. Sınırı değiştirirsen contract testlerini ve ilgili mimari belgeyi birlikte güncelle.
- M3U/HLS ayrımı uzantıdan yapılmaz. M3 yalnız bounded content-prefix karar contract'ıdır; gerçek incremental parser M7'ye aittir.
- M17 yalnız Home/Source Manager/navigation, `ContentSection`/authoritative count ve Xtream Live/VOD/Series classification foundation'ını açar; URL/path/category/name heuristic'iyle content kind üretme. `Movie` ve Movie VOD playback/seek type'larını M18'den, `Series`/`Season`/`Episode` type'larını M19'dan önce production'a ekleme. EPG/XMLTV, catch-up/timeshift, recording/download ve persisted continue-watching için hâlâ ayrı milestone gerekir.
- Seek yalnız typed Movie/Episode session'ında `CanSeek`, pozitif finite `NaturalDuration` ve varsa seekable-range kontrolüyle etkinleşir. Live TV'de timeshift/catch-up milestone'u yokken native session geçici range/duration bildirse bile seek açma veya Live'ı VOD sayma.

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

Package lifecycle harness değişikliğinde aynı elevated hostta ayrıca:

```powershell
.\eng\Invoke-WindowsPackageLifecycleSmoke.ps1 -Configuration Release
```

Gerçek Windows-user DPAPI boundary harness/controller değişikliğinde local OS account oluşturma yetkisi ayrıca açıkça verilmişse aynı elevated hostta:

```powershell
.\eng\Invoke-WindowsDpapiUserBoundarySmoke.ps1 -Configuration Release
```

Bu açık local onay yoksa yalnız statik contract, locked build, protocol self-test ve normal quality gate çalıştırılır; gerçek farklı-user execution hosted job'a bırakılır.

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
