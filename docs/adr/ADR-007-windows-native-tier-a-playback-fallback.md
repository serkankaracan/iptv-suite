# ADR-007 — Windows native Tier A playback fallback

**Status:** Proposed

**Date:** 2026-08-21

**Implementation note:** Hakları temiz `iptvsuite-tier-a-synthetic-v1` corpus'u reproducible biçimde üretildi. Direct MPEG-TS ile dört segmentli HLS-TS; H.264 High/yuv420p/640×360/25 fps ve AAC-LC/48 kHz/stereo tuple'ını taşır. Generator iki koşuda byte-identical hash üretti ve architecture guard'ı 6 media/playlist dosyasını manifest hash'lerine bağladı. Commit `077b3e9d5404055687f4b3e74db41e111a77cc8d` üzerindeki gerçek Windows Client disposable MSIX koşusu 100 switch'i ve 120 dakikalık developer soak'ı; startup p95 `263,972 ms`, 25 resource sample, 30 dakika sonrası `%2,894`/`6.885.376` byte net memory growth ve monoton artış olmamasıyla geçirdi. [Developer soak kanıtı](../quality/M10_NATIVE_TIER_A_DEVELOPER_SOAK_EVIDENCE.md) yalnız bu cihaz ve 2 saat kapısı içindir. Takip eden schema-3 packaged koşu iki resize, minimize/restore ve fullscreen/overlapped geçişlerinin ardından playback open/advance zincirini 100/100 switch ile geçirdi; [surface kanıtı](../quality/M10_NATIVE_TIER_A_SURFACE_EVIDENCE.md) multi-monitor/DPI/display-change iddiası değildir. Schema-4 kısa koşu bir sentetik HTTP interruption ve bir post-fault recovery ile geçti; [network kanıtı](../quality/M10_NATIVE_TIER_A_NETWORK_RECOVERY_EVIDENCE.md) geniş failure matrisi veya saatlik uzun-soak schedule'ı değildir. Schema-7 gerçek Windows Client koşusu 100 switch/100 source detachment, `0` playback retry, `7,986 ms` p95 ve `9,493 ms` maximum detachment süresi, post-fault `54 → 56` request ordinal ilerlemesi ve exact cleanup ile geçti; [session lifecycle kanıtı](../quality/M10_NATIVE_TIER_A_SESSION_LIFECYCLE_EVIDENCE.md) uygulama-owned source sahipliğinin bırakıldığını doğrular, OS media session quiescence veya ghost audio yokluğunu kanıtlamaz. Sekiz saatlik acceptance soak, device/HW-decode, WACK ve kalan surface matrisi açık olduğundan ADR `Proposed`, M10 in progress kalır; fallback acceptance geçmeden M11 başlamaz.

## Context / Problem

ADR-002'nin exact `VideoLAN.LibVLC.Windows 3.0.23.1` adayı M10 binary/license hard gate'ini geçemedi. Paket metadata'sı `LGPL-2.1-or-later` dese de x64 payload `libx26410b_plugin.dll` içeriyor; upstream GPL ayırma kaydı bu plugin ailesini GPL riski olarak sınıflandırıyor. Paket ayrıca exact binary-to-source/notices kabulünü tek başına kapatacak embedded envanteri sağlamıyor.

Bu bulgu libVLC'nin teknik playback yeteneğinin başarısız olduğu anlamına gelmez. Dağıtılacak exact binary setinin IPTV Suite'in “GPL plugin/binary sıfır” sınırına kanıtlanabilir biçimde uymadığı anlamına gelir.

## Proposed fallback

Windows `MediaPlayer` / Media Foundation yalnız Tier A için yeniden spike edilir:

- HLS + MPEG-TS ve direct HTTPS continuous MPEG-TS;
- H.264 High + AAC-LC/HE-AAC stereo;
- framework-dependent x64 MSIX ve WinUI surface;
- uygulama sahipliğinde bounded open/stop/cancel ve tek session;
- hakları temiz sentetik corpus, diagnostics canary ve packaged lifecycle;
- HEVC, AC-3/E-AC-3, geniş Tier B ve ARM64 için marketing garantisi yoktur.

Schema-9 packaged cleanup sahipliği shared Windows App Runtime'ı disposable test varlığı saymaz. Koşu öncesindeki `Microsoft.WindowsAppRuntime.2` exact package-full-name baseline'ının her kaydı korunur; koşu sonrası ekler yalnız exact Microsoft publisher/family, framework, `>=2.3.1.0` version ve X64/X86 architecture doğrulamasını geçebilir, X86 ise aynı version'da exact X64 sibling gerektirir. Doğrulanmış shared ekler korunur ve `Remove-AppxPackage` ile kaldırılmaz; exact disposable test MSIX'i, app data, process, sertifika ve output cleanup'ı zorunlu kalır [S116]. Evidence `RuntimePackageBaselinePreserved=true`, `RuntimePackageGraphDisposition=ExactRestored|SharedAdditionsPreserved` ve bounded `RuntimePackageSharedAdditionCount` taşır. Bu sözleşme hosted PASS değildir.

`VERIFIED`: Run `32537541013` ilk attempt'inde loopback HLS manifesti ve dört segmentin beş yanıtı eksiksiz tamamlandığı halde ilk `MediaOpened` 5 saniyede gelmedi. Bounded rerun 100 switch ve schema-9 cleanup yolunu tamamladı, ancak startup maximum `5056,3179 ms` ile değişmeyen 5 saniye tavanını aştı; p95 `2157,0052 ms` idi. Lifecycle incelemesinden sonra ilk measured source önüne bounded `MediaPlayerElement.Loaded` bariyeri kondu; fakat `55945b9` checkpoint'inin hosted run'ı `32540016736`, bariyeri geçip yine ilk HLS `MediaOpenTimeout` verdi. Beş response/`940229` byte tamamlandı, transport failure sıfırdı ve runner Windows Server'dı. Bu sonuç, eksik `Loaded` sınırının tek başına neden olmadığını doğrular. Explicit `MediaPlayer.RealTimePlayback=true` checkpoint'inin run'ı `32540817711`, 100 switch probe invariant'larını tamamladı; p95 `2162,9392 ms`, HLS/direct p95 `2165,6151/2162,2629 ms` iken maximum `5102,2433 ms` ile hard tavanı `102,2433 ms` aştı. Bu nedenle real-time konfigürasyonu hosted root-cause düzeltmesi veya PASS sayılmaz. Commit `12fac08` run'ı `32542190663`, tamamlanmış maximum attribution alanlarına ulaşmadan ilk HLS `MediaOpenTimeout` verdi; buna rağmen loopback `5/5` response ve `940229` byte tamamlandı, transport failure sıfırdı. Probe envelope v2'nin neutral diagnostic sonucu failure-path gözlem boşluğunu doğruladı. [Startup attribution kaydı](../quality/M10_NATIVE_TIER_A_STARTUP_ATTRIBUTION.md), envelope v3 ile completed maximum semantiğini korurken reset/dispose öncesi active startup stage/switch/fixture/attempt/surface ile source-create/source-assign/`Play`/active-stage sürelerini fail-closed ölçer. Kalıcı schema-9 success şekli, başlangıç bütçeleri, HLS-first ve yalnız explicit `MediaFailed` retry kuralı değişmez. Successor hosted doğrulaması beklenir.

Bu ADR henüz `Accepted` değildir. Native Tier A corpus %100 başlangıç, controlled-LAN p95 ≤3 saniye, 100 switch/8 saat soak, surface/lifecycle ve package gate'leri geçmeden production adapter yazılmaz.

## Consequences

- GPL-risk native payload ürün graph'ına ve MSIX'e girmez.
- Paket boyutu ve third-party native attack surface küçülür.
- MVP codec/container vaadi Tier A ile sınırlanabilir; Tier B yalnız ayrı kanıt ve hukuk kararıyla açılır.
- Native baseline başarısız olursa controlled LGPL build veya ürün kapsamı için yeni karar gerekir.

## References

[S21–S23, S27, S110–S112, S115–S118](../research/SOURCES.md)
