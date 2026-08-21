# M10 Windows native Tier A surface smoke kanıtı

**Durum:** `VERIFIED — packaged surface transitions, 2026-08-21`

## Kanıt bağı

- Parent implementation commit: `778aa39bb660dff6ae941b0467e9dc50bba36e5c`
- Test edilen worktree: yalnız bu kayıtta listelenen üç surface değişikliği; checkpoint commit'i aşağıdaki doğrulamadan sonra oluşturulur
- Controller: `eng/Invoke-WindowsNativePlaybackSmoke.ps1`
- Komut: `-Configuration Release -DotNetPath .\.artifacts\dotnet\dotnet.exe -SwitchCount 100 -SoakMinutes 0`
- Evidence schema: `3`
- Evidence yazım zamanı: `2026-08-21T17:56:51.5398735Z`
- Evidence SHA-256: `5b712dd8bdcd1f0b24a4b296e1b8f0cd5af055bc5ec4bb2097054401d96805a1`
- Test MSIX SHA-256: `5fa62fdfc4dd41c7734869a0154b838b46c048869b4b8854d7858fe7fd5d11a0`

Koşu gerçek Windows Client app-model activation ile disposable signed x64 MSIX üzerinde yürütüldü. Controller surface geçiş sayısını tam `6` olarak zorunlu tuttu; eksik geçiş veya typed `SurfaceLifecycleFailed` sonucu fail-closed'dur. Geçişler switch döngüsünün içinde gerçekleştiği için her geçişten sonraki medya açılışı ve en az 500 ms ilerleme mevcut 5 saniye open/3 saniye advance timeout'ları tarafından sınandı.

## Sonuçlar

| Ölçüm | Sonuç | Kabul |
|---|---:|---:|
| Scripted switch | 100 | 100 |
| Surface transition | 6 | 6 |
| Resize | 2 | 2 |
| Minimize/restore | 1/1 | 1/1 |
| Fullscreen/overlapped | 1/1 | 1/1 |
| Startup p95 | `265.492 ms` | `≤3000 ms` |
| Startup maximum | `3907.902 ms` | `≤5000 ms` |
| Loopback media request | 400 | en az switch sayısı; transport failure sıfır |

Sanitized evidence mevcut test-only artifact canary scanner ile exit code `0` verdi. Sonraki bağımsız kontrolde exact disposable package ve koşuya ait ephemeral sertifika sayısı sıfırdı.

## Değişen dosyalar

- `apps/windows/tests/IptvSuite.NativePlaybackCompatibilitySpike/MainWindow.xaml.cs`
- `eng/Invoke-WindowsNativePlaybackSmoke.ps1`
- `apps/windows/tests/IptvSuite.ArchitectureTests/DependencyRulesTests.cs`

## Açık acceptance

Bu koşu multi-monitor, 100/150/200% DPI, display hot-plug/change, overlay/z-order/black-frame görsel analizi, audio-device change, sleep/resume veya 8 saat soak kanıtı değildir. Surface acceptance bu nedenle `PARTIAL VERIFIED`, ADR-007 ise `Proposed` kalır.
