# M10 Windows native Tier A developer soak kanıtı

**Durum:** `VERIFIED — 120 dakika developer soak, 2026-08-21`

## Kanıt bağı

- Commit: `077b3e9d5404055687f4b3e74db41e111a77cc8d`
- Controller: `eng/Invoke-WindowsNativePlaybackSmoke.ps1`
- Komut: `-Configuration Release -DotNetPath .\.artifacts\dotnet\dotnet.exe -SwitchCount 100 -SoakMinutes 120`
- Evidence schema: `2`
- Evidence yazım zamanı: `2026-08-21T17:14:05.9198056Z`
- Evidence SHA-256: `f423fbef9b61cb2dfc69efb443b0a908061cc9d613caf6f9819435d75d189015`
- Test MSIX SHA-256: `1f32e58ef8b5b0f7576caa7e6e3c6468fc5da8d5b063d6f7b620965e0769b11e`
- SDK: exact `10.0.302`

Koşu ayrı bir yükseltilmiş Windows PowerShell oturumunda, gerçek Windows Client app-model activation ve disposable signed x64 MSIX ile yürütüldü. Controller sonunda exact package identity ile uninstall yaptı; ephemeral signing/TLS sertifikaları için exact thumbprint cleanup tamamlandı. Sonraki bağımsız kontrolde disposable package ve koşuya ait sertifika sayısı sıfır, repository temizdi. Sanitized evidence, mevcut test-only artifact canary scanner ile exit code `0` verdi.

## Ortam

- Windows Client build `26200.9168`, x64
- CPU: Intel Core i7-13700K, 24 logical processor
- Görünen display adapter'ları:
  - Intel UHD Graphics 770, driver `32.0.101.7082`
  - NVIDIA GeForce RTX 3060, driver `32.0.15.9186`
- Registered decoder önkoşulları: H.264 `true`, AAC `true`
- Transport: OS-seçimli portta TLS 1.2 loopback allowlist
- Fixture: hakları temiz sentetik direct MPEG-TS ve HLS-TS H.264/AAC Tier A corpus

Display adapter envanteri, playback'in hangi adapter/decode yolunu kullandığını kanıtlamaz. Hardware decode state ölçülmediği için Intel/NVIDIA hardware-decode hücreleri ve AMD cihaz hücresi açık kalır.

## Sonuçlar

| Ölçüm | Sonuç | Kabul |
|---|---:|---:|
| Scripted switch | 100 | 100 |
| Startup p95 | `263.972 ms` | `≤3000 ms` |
| Startup maximum | `3460.856 ms` | `≤5000 ms` |
| HLS startup p95 | `264.874 ms` | kayıt |
| Direct startup p95 | `263.680 ms` | kayıt |
| Resource sample | 25 | en az 22 |
| 30 dk warm-up private bytes | `237,940,736` | baseline |
| Post-warm net memory growth | `6,885,376` / `%2.894` | hem `≤100 MiB` hem `≤%10` |
| Post-warm monotonic growth | `false` | `false` |
| Warm-up/final handle | `2405` / `2492` | kayıt |
| Loopback media request | 3921 | en az switch sayısı; transport failure sıfır |

Bu sonuç 2 saatlik developer soak kapısını geçirir. İlk process sample'ından warm-up'a kadar oluşan decoder/player kaynakları post-warm leak kararı için kullanılmaz; fail-closed karar 30 dakika sonrasındaki ölçümlere dayanır.

## Açık acceptance

Bu kanıt aşağıdakileri tamamlamaz:

- 8 saat unattended/100-switch acceptance soak;
- AMD ve ayrıştırılmış Intel/NVIDIA hardware-decode/fallback matrisi;
- fullscreen, multi-monitor, DPI, minimize/restore ve display-change matrisi;
- saatlik network interruption/recovery;
- audio-device switch, sleep/resume veya WACK/private Store preflight;
- production player adapter.

Bu nedenle [ADR-007](../adr/ADR-007-windows-native-tier-a-playback-fallback.md) `Proposed` kalır ve M11 production adapter başlamaz.
