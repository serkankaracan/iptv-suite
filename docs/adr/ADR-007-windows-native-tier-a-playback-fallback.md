# ADR-007 — Windows native Tier A playback fallback

**Status:** Proposed

**Date:** 2026-08-21

**Implementation note:** Hakları temiz `iptvsuite-tier-a-synthetic-v1` corpus'u reproducible biçimde üretildi. Direct MPEG-TS ile dört segmentli HLS-TS; H.264 High/yuv420p/640×360/25 fps ve AAC-LC/48 kHz/stereo tuple'ını taşır. Generator iki koşuda byte-identical hash üretti ve architecture guard'ı 6 media/playlist dosyasını manifest hash'lerine bağladı. Commit `077b3e9d5404055687f4b3e74db41e111a77cc8d` üzerindeki gerçek Windows Client disposable MSIX koşusu 100 switch'i ve 120 dakikalık developer soak'ı; startup p95 `263,972 ms`, 25 resource sample, 30 dakika sonrası `%2,894`/`6.885.376` byte net memory growth ve monoton artış olmamasıyla geçirdi. [Developer soak kanıtı](../quality/M10_NATIVE_TIER_A_DEVELOPER_SOAK_EVIDENCE.md) yalnız bu cihaz ve 2 saat kapısı içindir. Takip eden schema-3 packaged koşu iki resize, minimize/restore ve fullscreen/overlapped geçişlerinin ardından playback open/advance zincirini 100/100 switch ile geçirdi; [surface kanıtı](../quality/M10_NATIVE_TIER_A_SURFACE_EVIDENCE.md) multi-monitor/DPI/display-change iddiası değildir. Schema-4 kısa koşu bir sentetik HTTP interruption ve bir post-fault recovery ile geçti; [network kanıtı](../quality/M10_NATIVE_TIER_A_NETWORK_RECOVERY_EVIDENCE.md) geniş failure matrisi veya saatlik uzun-soak schedule'ı değildir. Sekiz saat, kalan surface/lifecycle, network-fault ve device/HW-decode matrisi açık olduğundan ADR `Proposed` kalır.

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

Bu ADR henüz `Accepted` değildir. Native Tier A corpus %100 başlangıç, controlled-LAN p95 ≤3 saniye, 100 switch/8 saat soak, surface/lifecycle ve package gate'leri geçmeden production adapter yazılmaz.

## Consequences

- GPL-risk native payload ürün graph'ına ve MSIX'e girmez.
- Paket boyutu ve third-party native attack surface küçülür.
- MVP codec/container vaadi Tier A ile sınırlanabilir; Tier B yalnız ayrı kanıt ve hukuk kararıyla açılır.
- Native baseline başarısız olursa controlled LGPL build veya ürün kapsamı için yeni karar gerekir.

## References

[S21–S23, S27, S110–S112](../research/SOURCES.md)
