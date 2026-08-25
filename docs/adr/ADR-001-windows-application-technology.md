# ADR-001 — Windows uygulama teknolojisi

**Status:** Accepted

**Date:** 2026-08-09

## Context / Problem

Windows ilk gerçek ürün ve ilk mağaza hedefidir. UI teknolojisi; Windows'a uygun UX, büyük liste performansı, native player yüzeyi, test edilebilirlik, MSIX/Store dağıtımı ve en az birkaç yıllık bakım açısından seçilmelidir. Bu karar playback motorundan ayrıdır: UI framework'ünün varsayılan media control'ü motor seçimini belirlemez.

## Decision drivers

- Güncel ve üretici tarafından desteklenen Windows-only stack.
- Fluent/native Windows deneyimi, klavye, focus, DPI, multi-monitor ve accessibility erişimi.
- 50.000+ öğede virtualization ve UI thread kontrolü.
- Native video surface ile interop ve uygulama lifecycle'ı.
- Framework-dependent MSIX, Store servicing ve x64/ARM64 paketleme.
- .NET 10 LTS boyunca sürdürülebilir domain/application kodu.
- UI dışı katmanların hızlı ve deterministik test edilebilmesi.

## Considered options

1. WinUI 3 + C# + Windows App SDK.
2. WPF + .NET 10.
3. .NET MAUI.
4. Avalonia.
5. Flutter.
6. Electron.
7. UWP/WinForms gibi elenen eski veya ürün odağına uymayan yollar.

Karşılaştırma ve puanlama [Architecture Report](../architecture/ARCHITECTURE_REPORT.md) bölüm C'dedir. UWP yeni özellik geliştirmede stratejik yol değildir; WinForms media-centric, modern Windows UX için ek yük getirir. Cross-platform adayların hiçbiri Samsung Tizen'i ve platforma özel player ihtiyacını ortadan kaldırmaz.

## Decision

Windows istemcisi:

- **C# + .NET 10 LTS**,
- **WinUI 3**,
- yalnız Windows App SDK'nin güncel **Stable** kanalının en son compatible patch'i,
- **packaged, framework-dependent MSIX**, Store-first dağıtım

ile geliştirilecektir.

2026-08-09 M1 baseline'ı .NET SDK `10.0.302` / runtime `10.0.10`, Windows App SDK `2.3.1` ve Windows SDK BuildTools `10.0.26100.8249`'dur. SDK `global.json` içinde `rollForward: disable` ve `allowPrerelease: false` ile exact seçilir; direct paketler central versions ve bütün transitive graph `packages.lock.json` ile pinlenir. Preview/Experimental dependency alınmaz. Public preview durumundaki `winapp` CLI/package build veya launch zincirinde kullanılmaz.

Domain, application, provider ve player sözleşmeleri WinUI tiplerine bağımlı olmayan düz .NET projelerinde kalır. WinUI yalnız presentation/composition ve Windows lifecycle işlerini üstlenir. UI ve playback kararları ayrı ADR'lerde tutulur.

İlk engineering target **x64 Windows 11, minimum build `10.0.26100`**'dır. ARM64, seçilen native player zinciri M10 ve M15'i geçerse aynı ürün kapsamında sunulur; emulation başarı kriteri sayılmaz. Bu M1 engineering alt sınırı nihai pazar/support sözü değildir; ürün minimumu kullanıcı/Store verisi ve clean-device matrisiyle M15'te yeniden doğrulanır.

**M15 release-architecture disposition, 2026-08-25:** Windows MVP release seti yalnız `x64`tır. Production project `Platforms=x64`, `PlatformTarget=x64`, `RuntimeIdentifier=win-x64` ve `AppxBundle=Never` olarak kalır. ARM64 desteği iptal edilmiş bir gelecek özelliği değildir; native ARM64 build/package, gerçek ARM64 cihaz playback/lifecycle/resource ve Store zinciri ayrı bir acceptance ile geçene kadar `DeferredUntilNativeArm64ChainAccepted` durumunda ertelenmiştir. x64 emulation sonucu ARM64 desteği sayılmaz. Bu karar yeni mimari veya bundle eklemez; doğrulanmamış ARM64 support iddiasını Windows MVP kapsamından çıkarır.

M1 implementation, ayrı `.wapproj` içermeyen single-project, framework-dependent MSIX kullanır. `IptvSuite.LocalDev.6f0d9a64` identity'si yalnız disposable local lineage'dır; Partner Center/Store association ve dev→Store in-place migration garantisi yoktur.

## Consequences and trade-offs

- Modern Windows API/UX ve MSIX yolu kısa olur.
- .NET LTS ile UI dışı kodun bakım penceresi öngörülebilir olur.
- Windows App SDK servicing süresi .NET LTS'den kısadır; yıllık upgrade bütçesi gerekir.
- WinUI UI testleri app host/UI thread ister; iş mantığının katman dışına taşınması zorunludur.
- Player'ın HWND/surface, overlay, fullscreen ve z-order davranışı spike gerektirir.
- WPF kadar olgun bazı desktop kontrol/interop yolları bulunmayabilir.

## Risks

- Seçilen native player surface'inde black-frame, airspace, overlay veya lifecycle blocker'ı.
- Büyük Observable koleksiyonlarının virtualization'ı bozması.
- App SDK servicing bitişinin kaçırılması.
- ARM64 native player/plugin zincirinin tamamlanamaması.
- Store identity veya capability ihtiyacının geç keşfedilmesi.

## Validation plan

- M1 — **PASS, 2026-08-09:** build çıktısı/.git içermeyen ayrı temp workspace ve boş NuGet cache üzerinde locked restore; Debug/Release x64 sıfır warning build; 6/6 dependency/toolchain/manifest test; geçici yasak kenarla doğrulanmış red gate; ephemeral signed MSIX install → AUMID launch → normal close → uninstall/cert cleanup. Kurulan manifest x64, yalnız `runFullTrust` ve framework dependency `Microsoft.WindowsAppRuntime.2` minimum `2.3.1.0` olarak doğrulandı.
- M2 — **PASS, 2026-08-09:** test-only unit/integration/architecture scaffold'u ve exact-SDK local gate iki koşuda 22/22 geçti. Commit `79cf619c6683fa9c4213846455e376fb1b0cb11c` için [hosted run `31327398270`](https://github.com/serkankaracan/iptv-suite/actions/runs/31327398270) quality, signed package ve coordinator işlerini; indirilen sanitized artifacts da alan/hash doğrulamasını geçti. Başarılı smoke yalnız o hostta targeted production-payload inspection, install, visible launch, normal close ve cleanup'ı kanıtlar; keyboard/UIA/accessibility, update/migration, WACK/Store, provider/player/codec, ARM64 veya cihaz matrisi kanıtı değildir. Branch policy enforcement ayrıca açık değildir; kalıcı sonuç kaydı [M2 completion evidence](../quality/M2_COMPLETION_EVIDENCE.md) belgesindedir.
- M9: 50.000 sentetik kanalda virtualization, arama, focus ve accessibility.
- M10: gerçek player surface ile resize, fullscreen, DPI, multi-monitor, overlay, minimize/restore ve soak.
- M15: framework-dependent MSIX, clean install/update/uninstall, x64/ARM64 kararı, WACK ve private Store preflight.
- M15 architecture disposition — **DECIDED, 2026-08-25:** Windows MVP yalnız x64; ARM64 ayrı native acceptance'a kadar deferred. Validator schema v2, exact tek üyeli `releaseArchitectures=[x64]`, stable disposition, multi-RID/bundle reddi ve version 1 source-controlled MSBuild import-surface denetimini fail-closed kilitler.
- WinUI blocker bulunursa aynı `IPlaybackEngine` sözleşmesiyle en küçük WPF shell karşılaştırması.

## Revisit triggers

- M10'da Microsoft-destekli çözümü olmayan surface, z-order, fullscreen veya accessibility blocker'ı.
- WPF spike'ının aynı motorla ölçülebilir biçimde daha kararlı olması.
- Seçilen Windows App SDK family servicing sonundan 90 gün önce.
- Store/packaging politikasının packaged WinUI modelini engellemesi.
- Fonlanmış ARM64 hedefi ile native ARM64 playback/package/device acceptance planının onaylanması.
- Fonlanmış macOS/mobile yol haritasının ölçülmüş ciddi UI paylaşım getirisi oluşturması.

## References

[S01–S09, S14–S20, S70–S75, S80–S81](../research/SOURCES.md)
