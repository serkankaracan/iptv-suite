# M10 Windows native Tier A sekiz saat acceptance soak denemesi

**Durum:** `VERIFIED NEGATIVE — ResourceBudgetExceeded, 2026-08-22`

## Kanıt bağı

- Commit: `c9d5cd67607f97be9c1d49b68587f91c7515846c`
- Controller: `eng/Invoke-WindowsNativePlaybackSmoke.ps1`
- Komut: `-Configuration Release -DotNetPath .\.artifacts\dotnet\dotnet.exe -SwitchCount 100 -SoakMinutes 480 -NetworkInterruptionCount 7`
- Probe envelope: `7`
- Kalıcı success evidence: yok; resource gate başarısız olduğu için yayımlanmadı
- Stable failure evidence: `Stage=ProbeValidation`, `Code=ProbeInvariantFailed`

Koşu elevated ve interactive Windows Client üzerinde disposable signed x64 MSIX, TLS 1.2 loopback ve tamamen sentetik Tier A corpus ile yürütüldü. Package build, exact package inventory, mevcut Windows App Runtime dependency reuse, install ve activation adımları geçti. Controller, sonunda `ResourceBudgetExceeded` sonucunu döndürdü; bu bir acceptance PASS değildir.

## Doğrulanmış sonuçlar

| Ölçüm | Sonuç |
|---|---:|
| Tamamlanan scripted switch | `100 / 100` |
| Enjekte edilen interruption / recovery | `7 / 7` |
| Son injected / recovery request ordinal | `12665 / 12666` |
| Mapped request / completed response | `14425 / 14369` |
| I/O abort / intentional `503` / transport failure | `49 / 7 / 0` |
| Tamamlanan response body | `3.526.023.403 byte` |
| H.264 / AAC decoder registration | `true / true` |
| Audio service / endpoint service | `true / true` |
| Sonuç | `ResourceBudgetExceeded` |

Transport muhasebesi kapanır: `14369 completed + 49 I/O abort + 7 intentional 503 = 14425 mapped request`. Tarihsel konsol etiketindeki `droppedAfterCapacity=14393`, istek veya response kaybı değildir; `14425 - 32 = 14393` eşitliğiyle yalnız first-HLS attribution için ayrıntısı tutulmayan trace record sayısıdır. İlk `32` accept sonrasında HTTP handler ve response akışı devam eder. Takip düzeltmesi etiketi `traceRecordsOmittedAfterCapacity` olarak açıklaştırır; kapasiteyi veya transport davranışını değiştirmez.

İlk HLS penceresi `5` tamamlanmış response (`1` playlist + `4` segment), `978.421 byte`, source-open öncesi `2` ve `MediaOpened` öncesi `5` response ölçtü. Startup→ilk accept/header `3691,7948 / 3737,6028 ms`; maximum/toplam TLS authentication `43,1353 / 46,1114 ms`; son flush→source-open `-23,0213 ms` ve son flush→`MediaOpened` `332,5856 ms` oldu. Negatif sonuca rağmen bu bounded QPC attribution penceresi controller invariant'larını geçti; genel startup, soak veya playback acceptance kanıtı değildir.

## Resource failure sınıflandırması

`VERIFIED`: `ResourceBudgetExceeded` yalnız 30 dakika warm-up sonrasındaki private-bytes örneklerinin mevcut fail-closed değerlendirmesinden üretildi. Gate en az iki post-warm sample, net büyüme `≤100 MiB`, net büyüme `≤%10` ve bütün ardışık örneklerin strict artmamasını ister. Handle büyümesi kaydedilir fakat bu failure kategorisini tetiklemez.

`UNVERIFIED`: Hangi private-memory predicate'inin aşıldığı belirlenemedi. Envelope alanları `ResourceSampleCount`, `WarmupPrivateBytes`, `MemoryNetGrowthBytes`, `MemoryNetGrowthPercent` ve `MemoryMonotonicIncrease` değerlerini taşıdığı halde eski generic failure logu bunları yazmadan durdu; package app-data cleanup'ı transient envelope'u kaldırdı. Aynı nedenle tarihsel logdaki `detachedSources=0`, `surfaceTransitions=0` ve startup maximum sıfırları gerçek lifecycle sonucu değildir; resource-failure factory'sinin tamamlanmış sonucu korumayan varsayılan alanlarıdır.

Takip düzeltmesi eşikleri değiştirmeden tamamlanmış probe sonucunu korur ve yalnız `Success=false`, `Failure=ResourceBudgetExceeded` durumuna çevirir. Controller safe aggregate resource/process alanlarını konsola yazar ve stable failure noktasını `SoakValidation/ResourceBudgetExceeded` yapar. Kalıcı success schema `10`, transient envelope `7`, HLS-first sırası, timeout/retry, TLS, fixture ve cleanup sınırları değişmez.

## Karar

Sekiz saatlik acceptance gate **FAIL** durumundadır. ADR-007 `Proposed`, R15 `ACTIVE`, M10 `IN PROGRESS` kalır ve M11 production adapter başlamaz. Diagnostic düzeltmesi temiz bir commit üzerinde doğrulandıktan sonra aynı `480 dakika / 100 switch / 7 interruption` profili yeniden çalıştırılmalı; exact resource değerleri görülmeden threshold, fixture veya player davranışı değiştirilmemelidir.
