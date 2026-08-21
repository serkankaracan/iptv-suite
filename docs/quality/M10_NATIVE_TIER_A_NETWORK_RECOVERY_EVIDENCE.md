# M10 Windows native Tier A network recovery kanıtı

**Durum:** `PARTIAL VERIFIED — tek kontrollü interruption/recovery, 2026-08-21`

## Kanıt bağı

- Parent commit: `ba561d088dcddaee83d550cc52ae102da4af2ea9`
- Controller: `eng/Invoke-WindowsNativePlaybackSmoke.ps1`
- Komut: `-Configuration Release -DotNetPath .\.artifacts\dotnet\dotnet.exe -SwitchCount 100 -SoakMinutes 0 -NetworkInterruptionCount 1`
- Evidence schema: `4`
- Evidence yazım zamanı: `2026-08-21T19:04:47.5969145Z`
- Evidence SHA-256: `3d6609908240c6e4ebb73ec3cab0429b16c3a9b677f91ae7886a0f59e436a166`
- Test MSIX SHA-256: `692452a4165d3b4c7de0dd72c28be4aa030a1237716bf82b83906fc8e7283780`

Controller, yalnız allowlist'teki mapped loopback media request'ine bir adet sentetik `503` enjekte etti. Enjeksiyon normal TLS/socket/IO failure sayaçlarından ayrıdır. Yeni fault ancak önceki fault gerçekten tüketilmiş ve ardından en az bir mapped medya yanıtı başarıyla tamamlanmışsa arm edilebilir. Evidence yayımlanmadan önce scheduled, injected ve recovered sayılarının istenen değere tam eşitliği fail-closed doğrulanır. Tarihsel schema-4'te `recovered`, injection sonrasında gözlenen ilk mapped HTTP response completion'ıdır; concurrent response'un fault'tan önce başlayıp başlamadığını ayıran request ordinal'i yoktur. Bu sayaç `MediaPlayer` session'ının veya belirli bir playback attempt'inin recovery sonucu değildir.

## Sonuçlar

| Ölçüm | Sonuç | Kabul |
|---|---:|---:|
| Scripted switch | 100 | 100 |
| Sentetik interruption | 1 | 1 |
| Post-fault mapped-response recovery | 1 | 1 |
| Surface transition | 6 | 6 |
| Startup p95 | `262.957 ms` | `≤3000 ms` |
| Startup maximum | `4546.690 ms` | `≤5000 ms` |
| Loopback media request | 399 | en az switch sayısı |
| TLS/server transport failure | 0 | 0 |

Sanitized evidence mevcut test-only artifact canary scanner ile exit code `0` verdi. Sonraki kontrolde exact disposable package ve koşuya ait ephemeral sertifika sayısı sıfırdı.

## Sınır

Bu tarihsel schema-4 koşusu, tek ve kısa bir HTTP-status interruption ile fault sonrasında en az bir mapped response completion'ının ve fail-closed controller tarafından ayrıca zorunlu tutulan 100/100 switch zincirinin aynı run'da tamamlandığını kanıtlar; response'un fault'tan sonra başladığını kanıtlamaz. HTTP sayaçları fault→playback retry nedenselliğini, user-visible kesintisizliği veya OS session recovery'sini tek başına kanıtlamaz. Takip eden [schema-7 source-detachment kanıtı](M10_NATIVE_TIER_A_SESSION_LIFECYCLE_EVIDENCE.md), request ordinal `54 → 56` ile recovery response'unun injected request'ten sonra başladığını, retry sayacını ve her başarılı switch için source detachment'ı ayrıca bağlar; o koşuda application retry sayısı sıfırdır. DNS/TLS/connect timeout, mid-body disconnect, stall, bandwidth/latency, birden fazla ardışık fault veya uzun soak sırasında saatlik interruption schedule'ı hâlâ kanıtlanmamıştır. Bu nedenle M10 network/failure matrisi `PARTIAL VERIFIED` kalır.
