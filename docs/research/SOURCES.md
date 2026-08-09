# Araştırma kaynakları

**Araştırma tarihi / son erişim:** 2026-08-09

**Kapsam:** Phase 0 mimari, playback, güvenlik, mağaza ve platform kararları

## Kanıt kullanma yöntemi

- **VERIFIED:** Aşağıdaki birincil/resmî kaynakta açıkça bulunan bilgi.
- **INFERENCE:** Birden çok doğrulanmış bilgiden üretilen mühendislik değerlendirmesi; örneğin puanlama matrisi veya ürün alt sınırı.
- **UNVERIFIED:** Resmî belgelerin garanti etmediği ve spike, gerçek cihaz, mağaza ön incelemesi ya da hukuk görüşü gerektiren konu.
- Sürüm ve politika bilgileri araştırma tarihindeki anlık durumdur. Dependency pinleme ve mağaza gönderimi sırasında tekrar doğrulanır.
- Kaynak varlığı; bütün stream varyantlarının, cihazların, ticari lisansların veya mağaza kabulünün garanti edildiği anlamına gelmez.

## Windows, .NET, UI ve dağıtım

| ID | Kaynak | Kurum/proje | Bağlantı | Kararı destekleyen bilgi | Sürüm/policy tarihi | Erişim |
|---|---|---|---|---|---|---|
| S01 | Get started with Windows apps | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/get-started/) | Yeni Windows uygulamalarında WinUI önerisi; WPF'nin aktif rolü; UWP'nin bakım konumu | Güncelleme 2026-02-20 | 2026-08-09 |
| S02 | Windows App SDK release channels | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels) | Stable/Preview/Experimental ayrımı, servicing koşulları | Stable 2.3.1, 2026-07-16; ilgili family servicing sonu 2027-04-29 | 2026-08-09 |
| S03 | Windows App SDK downloads | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads) | SDK/runtime edinme ve stable sürüm doğrulaması | 2.3.1 | 2026-08-09 |
| S04 | .NET 10 downloads and support policy | Microsoft | [Resmî indirme](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), [lifecycle](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) | Current SDK/runtime patch ve .NET 10 LTS destek süresi | SDK 10.0.302; runtime 10.0.10; destek sonu 2028-11-14 | 2026-08-09 |
| S05 | WinUI 3 overview | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/) | WinUI 3 konumu, C#/C++ desteği ve teknik Windows alt sınırı | Güncel stable doküman | 2026-08-09 |
| S06 | Package and deploy Windows apps | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/) | WinUI packaged/MSIX yaklaşımı ve dağıtım modeli | Güncel | 2026-08-09 |
| S07 | Choose a distribution path | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/choose-distribution-path) | Store/MSIX ve diğer dağıtım yollarının farkları | Güncel | 2026-08-09 |
| S08 | Deploy packaged Windows App SDK apps | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-packaged-apps) | Framework-dependent paketlerde runtime servicing | Güncel | 2026-08-09 |
| S09 | MSIX package requirements | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements) | Paket sürümü, mimari, Store imzalama ve WACK gereksinimleri | Güncel | 2026-08-09 |
| S10 | Store policies | Microsoft | [Resmî politika](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies) | İçerik, privacy, test edilebilirlik, üçüncü taraf hizmet ve IP yükümlülükleri | 7.19; yayımlanma 2025-09-10, yürürlük 2025-10-14 | 2026-08-09 |
| S11 | Support information and privacy policy | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/support-info) | Desktop/Store privacy ve support alanları | Güncel | 2026-08-09 |
| S12 | Prepare desktop app for MSIX | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-prepare) | Install dizininin salt okunur olması ve app-data gereksinimi | Güncel | 2026-08-09 |
| S13 | Windows app capabilities | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations) | En az yetki ve restricted capability incelemesi | Güncel | 2026-08-09 |
| S14 | Windows 11 release information | Microsoft | [Resmî lifecycle](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information) | Desteklenen Windows sürümleri ve servis tarihleri | 25H2 Home/Pro destek sonu 2027-10-12 | 2026-08-09 |
| S15 | Windows 10 end of support | Microsoft | [Resmî lifecycle](https://learn.microsoft.com/en-us/lifecycle/announcements/windows-10-end-of-support) | Windows 10 genel destek sonu | 2025-10-14 | 2026-08-09 |
| S16 | WPF overview | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/) | WPF'nin aktif Windows-only fallback oluşu | Güncel .NET 10 | 2026-08-09 |
| S17 | .NET MAUI overview/policy | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/dotnet/maui/what-is-maui?view=net-maui-10.0) | MAUI hedefleri; resmî Tizen hedefinin bulunmaması | .NET MAUI 10 | 2026-08-09 |
| S18 | Avalonia releases and Tizen change | Avalonia | [Releases](https://github.com/AvaloniaUI/Avalonia/releases), [breaking changes](https://docs.avaloniaui.net/docs/avalonia12-breaking-changes) | Güncel bakım ve Avalonia.Tizen paketinin v12'de kaldırılması | 12.1.1, 2026-07-29 | 2026-08-09 |
| S19 | Flutter supported platforms | Flutter | [Resmî belge](https://docs.flutter.dev/reference/supported-platforms) | Windows hedefi; resmî Tizen hedefinin bulunmaması | 3.44.x | 2026-08-09 |
| S20 | Electron release policy/security | Electron | [Release policy](https://www.electronjs.org/docs/latest/tutorial/electron-timelines), [security](https://www.electronjs.org/docs/latest/tutorial/security) | Hızlı major cadence ve renderer güvenlik yükü | Stable 43.3.0, 2026-08-04 | 2026-08-09 |

### M1 bootstrap ve paketleme kaynakları

| ID | Kaynak | Kurum/proje | Bağlantı | Kararı destekleyen bilgi | Sürüm/tarih | Erişim |
|---|---|---|---|---|---|---|
| S70 | Windows SDK BuildTools | Microsoft / NuGet | [Resmî paket](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/10.0.26100.8249) | Visual Studio kurulumu olmadan Windows SDK/MSIX build araçlarının exact stable paketi | 10.0.26100.8249, 2026-05-26 | 2026-08-09 |
| S71 | MSTest | Microsoft / NuGet | [Resmî paket](https://www.nuget.org/packages/MSTest/4.3.3) | .NET 10 uyumlu Microsoft-supported test metapaketinin current stable sürümü | 4.3.3, 2026-07-28 | 2026-08-09 |
| S72 | Single-project MSIX | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/single-project-msix) | Ayrı `.wapproj` olmadan packaged WinUI ve `GenerateAppxPackageOnBuild` yolu | Güncel | 2026-08-09 |
| S73 | Create a package-signing certificate | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/msix/package/create-certificate-package-signing) | Publisher/Subject eşitliği, code-signing EKU ve local test trust-store gereksinimi | Güncelleme 2025-04-22 | 2026-08-09 |
| S74 | MSIX signing overview | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview) | Signed ve trusted MSIX install zorunluluğu; self-signed local test ile Store signing ayrımı | Güncelleme 2026-04-15 | 2026-08-09 |
| S75 | Windows App Development CLI | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/) | `winapp` CLI'nin public preview olması; M1 stable-only build zincirine alınmama gerekçesi | Public preview, 2026 | 2026-08-09 |

### M2 test altyapısı kaynakları

| ID | Kaynak | Kurum/proje | Bağlantı | Kararı destekleyen bilgi | Sürüm/tarih | Erişim |
|---|---|---|---|---|---|---|
| S76 | FakeTimeProvider ile test | Microsoft / .NET | [Resmî belge](https://learn.microsoft.com/en-us/dotnet/core/extensions/timeprovider-testing), [resmî paket](https://www.nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing/10.8.0) | `TimeProvider` bağımlı kodda wall-clock beklemeden deterministic time advance/timer testi; test-only exact paket | 10.8.0 stable, MIT, 2026-07-14 | 2026-08-09 |
| S77 | MSTest execution control | Microsoft / .NET | [Resmî belge](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-writing-tests-controlling-execution) | Method/class parallelization, worker sayısı, timeout ve cooperative cancellation sınırları | MSTest 4.x güncel belge | 2026-08-09 |
| S78 | Kestrel endpoint configuration | Microsoft / ASP.NET Core | [Resmî belge](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0) | `IPAddress.Loopback` bind ve port `0` ile OS-seçimli local fixture port'u; `ListenLocalhost(0)` kısıtı | ASP.NET Core 10, güncel | 2026-08-09 |
| S79 | `dotnet test` with VSTest | Microsoft / .NET | [Resmî belge](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-vstest) | TRX results directory/logger ve hang timeout; `--blame-hang-dump-type none` ile dump toplamadan host termination | .NET 10 SDK, güncel | 2026-08-09 |
| S80 | `global.json` overview | Microsoft / .NET | [Resmî belge](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json) | Exact SDK version, `allowPrerelease: false`; `rollForward: disable` ile exact match ve lockfile graph'ıyla lockstep önerisi | Güncelleme 2026 | 2026-08-09 |
| S81 | Required status check troubleshooting | GitHub | [Resmî belge](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/troubleshooting-required-status-checks) | Path/branch filter nedeniyle skip edilen required workflow check'inin `Pending` kalıp merge'i engelleyebilmesi; required workflow'u üst seviyede skip etmeme gerekçesi | Güncel | 2026-08-09 |
| S82 | Secure use of GitHub Actions | GitHub | [Resmî belge](https://docs.github.com/en/actions/reference/security/secure-use) | Action'ları full commit SHA ile pinleme, untrusted input ve `pull_request_target` riskleri; least-privilege workflow sınırı | Güncel | 2026-08-09 |
| S83 | GitHub-hosted runners | GitHub | [Resmî belge](https://docs.github.com/en/actions/reference/runners/github-hosted-runners) | Her job için yeni hosted VM; Windows runner'da administrator/UAC sınırı ve self-hosted olmayan untrusted-PR lane gerekçesi | Güncel | 2026-08-09 |
| S84 | setup-dotnet cache davranışı | GitHub / actions | [Resmî action belgesi](https://github.com/actions/setup-dotnet#caching-nuget-packages) | Lockfile tabanlı global-packages cache ve implicit NuGet fallback klasörünü kapatma gereksinimi | `setup-dotnet` v6, 2026 | 2026-08-09 |

## Windows media ve player adayları

| ID | Kaynak | Kurum/proje | Bağlantı | Kararı destekleyen bilgi | Sürüm/tarih | Erişim |
|---|---|---|---|---|---|---|
| S21 | Supported codecs | Microsoft | [Resmî codec matrisi](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/supported-codecs) | H.264/TS ve AAC; HEVC'nin optional oluşu; HEVC/TS garantisinin bulunmaması; Win11 24H2'de AC-3 değişikliği | Güncelleme 2026-05-23 | 2026-08-09 |
| S22 | Adaptive streaming | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/adaptive-streaming) | Native HLS, custom HTTP ve tanılama yüzeyi | Güncel | 2026-08-09 |
| S23 | Media playback tracks | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/media-playback-with-mediasource) | Audio/video/metadata track seçimi | Güncel | 2026-08-09 |
| S24 | LibVLCSharp packages | VideoLAN/NuGet | [Core](https://www.nuget.org/packages/LibVLCSharp), [WinUI profili](https://www.nuget.org/profiles/videolan), [native Windows](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/) | Resmî .NET binding, WinUI adapter ve non-GPL native paket sürümleri/boyutu | Core 3.10.1; WinUI 3.10.0; native 3.0.23.1 | 2026-08-09 |
| S25 | LibVLCSharp versioning/API | VideoLAN | [Versioning](https://docs.videolan.me/libvlcsharp/docs/versioning.html), [MediaPlayer API](https://docs.videolan.me/libvlcsharp/api/LibVLCSharp.Shared.MediaPlayer.html) | LibVLC 3 stable / 4 preview; olaylar, track ve istatistik yüzeyi | 3.x stable | 2026-08-09 |
| S26 | VLC features/formats | VideoLAN | [VLC 3 features](https://images.videolan.org/vlc/releases/3.0.20.html), [formats](https://images.videolan.org/vlc/features.html) | HLS, TS, codec ve Windows hardware decode kapsamı | VLC 3.x | 2026-08-09 |
| S27 | libVLC licensing | VideoLAN | [API/license](https://videolan.videolan.me/vlc/master/index.html), [GPL package](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows.GPL) | LGPL çekirdek ile GPL plugin paketinin ayrımı | Güncel paket metadata'sı | 2026-08-09 |
| S28 | FFmpeg download/formats/codecs | FFmpeg | [Download](https://ffmpeg.org/download.html), [formats](https://ffmpeg.org/ffmpeg-formats.html), [codecs](https://ffmpeg.org/ffmpeg-codecs.html) | Geniş HLS/TS/codec kapsamı ve upstream'in Windows binary sağlamaması | 8.1.2, 2026-06-17 | 2026-08-09 |
| S29 | FFmpeg protocols | FFmpeg | [Resmî belge](https://ffmpeg.org/ffmpeg-protocols.html) | Timeout, reconnect ve HTTP/TLS seçenekleri | 8.1.x | 2026-08-09 |
| S30 | FFmpeg legal/license | FFmpeg | [Legal](https://ffmpeg.org/legal.html) | LGPL/GPL/nonfree build farkları ve redistribution checklist | Güncel | 2026-08-09 |
| S31 | mpv release/manual/license | mpv | [Release](https://github.com/mpv-player/mpv/releases/tag/v0.41.0), [manual](https://mpv.io/manual/stable/), [license](https://github.com/mpv-player/mpv/blob/master/Copyright) | libmpv kapsamı, Windows/HW/reconnect yüzeyi ve varsayılan GPL build riski | 0.41.0, 2025-12-21 | 2026-08-09 |
| S32 | HLS standard | IETF | [RFC 8216](https://www.rfc-editor.org/rfc/rfc8216.html) | HLS playlist/protocol terimleri ve temel davranış | RFC 8216 | 2026-08-09 |

## Yerel veri, HTTP ve güvenlik

| ID | Kaynak | Kurum/proje | Bağlantı | Kararı destekleyen bilgi | Sürüm/tarih | Erişim |
|---|---|---|---|---|---|---|
| S33 | Credential Locker | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/develop/security/credential-locker) | Credential amaçlı kullanım, roaming ve uygulama başına 20 kayıt sınırı | Güncelleme 2026-04-04 | 2026-08-09 |
| S34 | Windows Data Protection | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/develop/security/data-protection) | LOCAL=user kapsamlı data protection | Güncelleme 2026-07-17 | 2026-08-09 |
| S35 | .NET ProtectedData | Microsoft | [Resmî API](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata?view=windowsdesktop-10.0) | DPAPI CurrentUser ile secret koruma | .NET 10 | 2026-08-09 |
| S36 | Windows app data | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/store-and-retrieve-app-data) | LocalCache/LocalState yaşam döngüsü ve roaming ayrımı | Güncel | 2026-08-09 |
| S37 | Microsoft.Data.Sqlite overview | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) | Local relational metadata ve transaction yaklaşımı | .NET 10 | 2026-08-09 |
| S38 | SQLite WAL | SQLite | [Resmî belge](https://sqlite.org/wal.html) | WAL concurrency ve operasyonel trade-off'lar | Güncel | 2026-08-09 |
| S39 | HttpClient guidelines | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines) | Uzun ömürlü/pooled client, DNS yenileme ve connection lifetime | .NET 10 | 2026-08-09 |
| S40 | HTTP resilience | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience) | Retry/timeout/circuit davranışlarını sınırlama | .NET 10 | 2026-08-09 |
| S41 | Data redaction | Microsoft | [Resmî belge](https://learn.microsoft.com/en-us/dotnet/core/extensions/data-redaction) | Merkezî sınıflandırma/redaction ilkeleri | .NET 10 | 2026-08-09 |

## Samsung TV / Tizen

| ID | Kaynak | Kurum/proje | Bağlantı | Kararı destekleyen bilgi | Sürüm/tarih | Erişim |
|---|---|---|---|---|---|---|
| S42 | Registering applications | Samsung | [Seller Office](https://developer.samsung.com/tv-seller-office/guides/applications/registering-application.html) | .wgt/.tpk tipleri, tipin sonradan değişmemesi ve .NET model kapsamı | Güncel | 2026-08-09 |
| S43 | General specifications | Samsung | [Resmî belge](https://developer.samsung.com/smarttv/develop/specifications/general-specifications.html) | Model yılı/Tizen eşlemesi, HLS/codec farkları, OS upgrade ve 2026 GCC değişimi | 2018–2026 matrisi | 2026-08-09 |
| S44 | Web engine specifications | Samsung | [Resmî belge](https://developer.samsung.com/smarttv/develop/specifications/web-engine-specifications.html) | 2018–2026 Chromium engine parçalanması | M56–M130 | 2026-08-09 |
| S45 | Playback using AVPlay | Samsung | [Resmî rehber](https://developer.samsung.com/smarttv/develop/guides/multimedia/media-playback/using-avplay.html) | AVPlay kullanım alanı, async prepare ve player state modeli | AVPlay Tizen 2.3+ | 2026-08-09 |
| S46 | AVPlay API | Samsung | [Resmî API](https://developer.samsung.com/smarttv/develop/api-references/samsung-product-api-references/avplay-api.html) | Playback, event, track ve durum yüzeyi | Güncel | 2026-08-09 |
| S47 | WidgetData API | Samsung | [Resmî API](https://developer.samsung.com/smarttv/develop/api-references/samsung-product-api-references/widgetdata-api.html) | Şifreli yerel CRUD'nin Tizen 4.0+ olması ve 20.000 karakter sınırı | Tizen 4.0+ | 2026-08-09 |
| S48 | Remote and focus guidance | Samsung | [Remote](https://developer.samsung.com/smarttv/develop/guides/user-interaction/remote-control.html), [design](https://developer.samsung.com/smarttv/design/design-principles.html) | Smart Remote, görünür focus, Back/Exit davranışları | Güncel | 2026-08-09 |
| S49 | TV device/simulator testing | Samsung | [TV device](https://developer.samsung.com/smarttv/develop/getting-started/using-sdk/tv-device.html), [emulator limits](https://developer.samsung.com/smarttv/develop/getting-started/using-sdk/tv-emulator/emulator-limitations.html) | Gerçek cihaz ihtiyacı ve emulator sınırlamaları | Güncel | 2026-08-09 |
| S50 | Tizen WASM Player | Samsung | [Resmî belge](https://developer.samsung.com/smarttv/develop/extension-libraries/webassembly/tizen-wasm-player/overview.html) | Elementary packet API; download/demux/sync sorumluluğunun uygulamada kalması | Güncel | 2026-08-09 |
| S51 | Seller membership / partner | Samsung | [Membership](https://developer.samsung.com/tv-seller-office/guides/membership/becoming-seller-office-member.html), [Partner](https://developer.samsung.com/tv-seller-office/guides/membership/becoming-partner.html) | ABD dışı dağıtımda Partner Seller, Content Manager ve offline sözleşme engeli | Güncel | 2026-08-09 |
| S52 | Application information / launch checklist | Samsung | [App information](https://developer.samsung.com/tv-seller-office/guides/applications/entering-application-information.html), [checklist](https://developer.samsung.com/tv-seller-office/checklists-for-distribution/launch-checklist.html) | Privacy, test erişimi, player/model/seller metadata ve imzalama | Güncel | 2026-08-09 |
| S53 | Tizen .NET FAQ / JuvoPlayer | Samsung | [.NET FAQ](https://developer.samsung.com/smarttv/develop/faq/tizen-net-tv.html), [JuvoPlayer](https://developer.samsung.com/smarttv/develop/tizen-net-tv/extension-libraries/juvoplayer.html) | .NET framework/model/debug parçalanması | 2018+; bazı kabiliyetler 2019+ | 2026-08-09 |

## Gelecek platformlar ve mağazalar

| ID | Kaynak | Kurum/proje | Bağlantı | Kararı destekleyen bilgi | Sürüm/tarih | Erişim |
|---|---|---|---|---|---|---|
| S54 | Media3 playback guide/releases | Google | [Guide](https://developer.android.com/media/implement/playback-app), [releases](https://developer.android.com/jetpack/androidx/releases/media3) | Android'de ExoPlayer önerisi ve güncel stable | 1.10.1, 2026-07-22 | 2026-08-09 |
| S55 | Media3 HLS/formats | Google | [HLS](https://developer.android.com/media/media3/exoplayer/hls), [formats](https://developer.android.com/media/media3/exoplayer/supported-formats) | HLS TS/fMP4 ve cihaz decoder bağımlılığı | 1.10.1 | 2026-08-09 |
| S56 | Android network security | Google | [Resmî belge](https://developer.android.com/privacy-and-security/security-config) | Cleartext ve certificate policy | Güncel | 2026-08-09 |
| S57 | AVFoundation / AVPlayer | Apple | [AVFoundation](https://developer.apple.com/av-foundation/), [AVPlayer](https://developer.apple.com/documentation/avfoundation/avplayer/) | Apple native HLS/player yaklaşımı | Güncel SDK | 2026-08-09 |
| S58 | Apple App Review Guidelines | Apple | [Resmî politika](https://developer.apple.com/app-store/review/guidelines/) | Privacy, üçüncü taraf hizmet ve streaming content hakları | Güncelleme 2026-06-08 | 2026-08-09 |
| S59 | Google Play policy/data safety | Google | [Program policy](https://support.google.com/googleplay/android-developer/answer/17190352), [Data Safety](https://support.google.com/googleplay/android-developer/answer/10787469) | IP, privacy ve Data Safety gereksinimleri | Güncel | 2026-08-09 |
| S66 | Android Keystore | Google | [Resmî belge](https://developer.android.com/privacy-and-security/keystore) | Non-exportable/hardware-backed key materyali ve Android secret-store yönü | Güncel | 2026-08-09 |
| S67 | Apple Keychain Services | Apple | [Keychain](https://developer.apple.com/documentation/security/keychain-services), [TN3137](https://developer.apple.com/documentation/technotes/tn3137-on-mac-keychains) | iOS/macOS küçük secret saklama ve data-protection keychain | Güncel | 2026-08-09 |
| S68 | Apple network sandbox/security | Apple | [App Sandbox](https://developer.apple.com/documentation/security/app-sandbox), [ATS](https://developer.apple.com/documentation/security/preventing-insecure-network-connections) | macOS outgoing-network entitlement ve insecure connection review | Güncel | 2026-08-09 |
| S69 | Apple App Privacy Details | Apple | [Resmî belge](https://developer.apple.com/app-store/app-privacy-details/) | Privacy label ve device-only processing ayrımı | Güncel | 2026-08-09 |

## Lisans, patent ve Türkiye mevzuatı

| ID | Kaynak | Kurum/proje | Bağlantı | Kararı destekleyen bilgi | Tarih | Erişim |
|---|---|---|---|---|---|---|
| S60 | AVC/H.264 licensing | Via LA | [Program sayfası](https://www.via-la.com/licensing-programs/avc-h-264/) | Codec patent programının yazılım/media-player ürünlerinden ayrı değerlendirilmesi | Güncel | 2026-08-09 |
| S61 | AAC / HEVC licensing | Via LA / Access Advance | [AAC](https://www.via-la.com/licensing-programs/aac/), [HEVC/VVC](https://www.via-la.com/licensing-programs/hevc-vvc/), [Access Advance](https://accessadvance.com/) | OSS lisansından ayrı codec patent havuzları | Güncel | 2026-08-09 |
| S62 | Dolby licensing | Dolby | [Licensing](https://professional.dolby.com/licensing/), [patents](https://www.dolby.com/about/patent-licensing/) | AC-3/E-AC-3 dağıtımının ayrı uzman incelemesi gerektirmesi | Güncel | 2026-08-09 |
| S63 | 6698 sayılı KVKK | KVKK | [Kanun](https://www.kvkk.gov.tr/Icerik/6649/Personal-Data-Protection-Law) | Saklama/korumanın işleme olması; aydınlatma, güvenlik ve silme yükümlülükleri | Kabul 2016-03-24 | 2026-08-09 |
| S64 | Aydınlatma ve imha düzenlemeleri | KVKK | [Aydınlatma Tebliği](https://www.kvkk.gov.tr/Icerik/4132/aydinlatma-yukumlulugunun-yerine-getirilmesinde-uyulacak-usul-ve-esaslar-hakkinda-teblig), [silme yönetmeliği](https://www.kvkk.gov.tr/Icerik/5441/KISISEL-VERILERIN-SILINMESI-YOK-EDILMESI-VEYA-ANONIM-HALE-GETIRILMESI-HAKKINDA-YONETMELIK) | Aydınlatma, retention ve imha süreçleri | Güncel | 2026-08-09 |
| S65 | Açık rıza ve aydınlatmanın ayrılması | KVKK | [2026/347 İlke Kararı](https://www.kvkk.gov.tr/Icerik/8710/veri-sorumlulari-tarafindan-acik-riza-ve-aydinlatma-metinlerinin-ayri-ayri-duzenlenmesi-gerektigi-hakkinda-kisisel-verileri-koruma-kurulunun-18-02-2026-tarihli-ve-2026-347-sayili-ilke-kararina-iliskin-kamuoyu-duyurusu) | Aydınlatma ve açık rıza süreçlerinin ayrı tutulması | 2026-02-18 | 2026-08-09 |

## Araştırmanın karar üretmediği alanlar

Aşağıdaki maddeler kaynak taramasıyla kapanmamıştır:

- LibVLC non-GPL Windows paketinin bütün hedef codec'leri, plugin keşfini, x64/ARM64 MSIX'i ve Store sertifikasyonunu gerçekten geçmesi.
- Codec patent ücret/izin modelinin hedef ülkeler, gelir modeli ve dağıtılan binary seti için sonucu.
- Genel BYO-authorized IPTV player'ın Microsoft veya Samsung incelemesinden kabul garantisi.
- Samsung Partner Seller/Content Manager'ın Türkiye ürünü için sözleşme ve ülke onayı.
- Tizen `WidgetData` verisinin update, uninstall/reinstall, Smart Hub reset ve OS upgrade yaşam döngüsü.
- Windows'ta DPAPI-per-locator yaklaşımının 50.000 kayıt performansı ve atomik migration davranışı.
- Gerçek sağlayıcıların Xtream-compatible varyasyonları ve cookie/redirect beklentileri.
- Uygulama yayıncısının tamamen cihaz-içi akışta KVKK rolü, VERBİS ve yurt dışı aktarım sorumluluğu.

Bu maddeler ilgili ADR, risk kaydı ve milestone'da `UNVERIFIED` olarak izlenir.
