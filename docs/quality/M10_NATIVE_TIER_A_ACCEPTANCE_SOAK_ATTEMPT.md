# M10 Windows native Tier A sekiz saat acceptance soak denemesi

**Durum:** `UNVERIFIED NEGATIVE — kullanıcı tarafından sağlanan terminal transcript'i ResourceBudgetExceeded bildiriyor, 2026-08-22`

## Kanıt bağı

- Bildirilen commit: `c9d5cd67607f97be9c1d49b68587f91c7515846c`
- Controller: `eng/Invoke-WindowsNativePlaybackSmoke.ps1`
- Komut: `-Configuration Release -DotNetPath .\.artifacts\dotnet\dotnet.exe -SwitchCount 100 -SoakMinutes 480 -NetworkInterruptionCount 7`
- Bildirilen source probe envelope: `7`
- Kalıcı success evidence: yok; resource gate başarısız olduğu için yayımlanmadı
- Transcript'te bildirilen stable failure evidence: `Stage=ProbeValidation`, `Code=ProbeInvariantFailed`
- Provenance: `UNVERIFIED`; bildirilen commit amend sonrasında hiçbir branch/tag/ref tarafından tutulmuyor ve failure artifact'ı run ID, commit, controller/fixture hash'i veya transcript hash'i taşımıyor

Kullanıcının sağladığı terminal transcript'i koşunun elevated ve interactive Windows Client üzerinde disposable signed x64 MSIX, TLS 1.2 loopback ve tamamen sentetik Tier A corpus ile yürütüldüğünü; package build, exact package inventory, mevcut Windows App Runtime dependency reuse, install ve activation adımlarının geçtiğini bildirir. Transcript sonunda `ResourceBudgetExceeded` gösterir. Kalıcı artifact/commit bağı olmadığı için bu sayısal sonuçlar bağımsız olarak yeniden doğrulanamaz; her durumda negatif sonuç acceptance PASS değildir.

## Transcript'te bildirilen sonuçlar

| Ölçüm | Sonuç |
|---|---:|
| Tamamlanan scripted switch | `100 / 100` |
| Enjekte edilen interruption / recovery | `7 / 7` |
| Son injected / recovery request ordinal | `12665 / 12666` |
| Mapped request / completed response | `14425 / 14369` |
| I/O abort / injected-failure attempt / categorized transport failure | `49 / 7 / 0` |
| Tamamlanan response body | `3.526.023.403 byte` |
| H.264 / AAC decoder registration | `true / true` |
| Audio service / endpoint service | `true / true` |
| Sonuç | `ResourceBudgetExceeded` |

`INFERENCE`: Transcript sayaçları aritmetik olarak `14369 completed + 49 I/O abort + 7 injected-failure attempt = 14425 mapped request` eşitliğini verir. `InjectedFailureCount` response yazımı tamamlanmadan arttığı ve failure logu TLS server drain edilmeden üretildiği için bu eşitlik yedi `503` response'unun teslim edildiğini veya kategorilerin ayrık ve terminal olduğunu tek başına kanıtlamaz. `VERIFIED` kod incelemesi: tarihsel konsol etiketindeki `droppedAfterCapacity=14393`, istek veya response kaybı değil; `14425 - 32 = 14393` eşitliğiyle yalnız first-HLS attribution için ayrıntısı tutulmayan trace record sayısıdır. İlk `32` accept sonrasında HTTP handler ve response akışı devam eder. Takip düzeltmesi etiketi `traceRecordsOmittedAfterCapacity` olarak açıklaştırır; kapasiteyi veya transport davranışını değiştirmez.

Transcript, ilk HLS penceresi için `5` tamamlanmış response (`1` playlist + `4` segment), `978.421 byte`, source-open öncesi `2` ve `MediaOpened` öncesi `5` response bildirir. Startup→ilk accept/header `3691,7948 / 3737,6028 ms`; maximum/toplam TLS authentication `43,1353 / 46,1114 ms`; son flush→source-open `-23,0213 ms` ve son flush→`MediaOpened` `332,5856 ms` olarak yazılmıştır. Bu bounded QPC attribution satırının üretilmesi controller invariant'larının o noktaya kadar geçildiğini gösterir; genel startup, soak veya playback acceptance kanıtı değildir.

## Resource failure sınıflandırması

`VERIFIED` kod incelemesi: `ResourceBudgetExceeded`, en az iki post-warm sample bulunması, net büyümenin `≤100 MiB` ve `≤%10` kalması ve bütün ardışık post-warm örneklerin strict artmaması koşullarını birlikte isteyen fail-closed değerlendirmeden üretilir. Handle büyümesi kaydedilir fakat bu failure kategorisini tetiklemez.

`UNVERIFIED`: Hangi resource-budget predicate'inin tetiklendiği belirlenemedi; yetersiz post-warm sample, byte sınırı, yüzde sınırı ve monotonic artış olasılıklarının hiçbiri dışlanamaz. Envelope alanları `ResourceSampleCount`, `WarmupPrivateBytes`, `MemoryNetGrowthBytes`, `MemoryNetGrowthPercent` ve `MemoryMonotonicIncrease` değerlerini taşıdığı halde eski generic failure logu bunları yazmadan durdu; package app-data cleanup'ı transient envelope'u kaldırdı. Aynı nedenle tarihsel logdaki `detachedSources=0`, `surfaceTransitions=0` ve startup maximum sıfırları gerçek lifecycle sonucu değildir; resource-failure factory'sinin tamamlanmış sonucu korumayan varsayılan alanlarıdır.

Takip düzeltmesi eşikleri değiştirmeden tamamlanmış probe sonucunu korur ve yalnız `Success=false`, `Failure=ResourceBudgetExceeded` durumuna çevirir. Controller bu failure sınıfını kabul etmeden önce completed lifecycle/cancellation/first-HLS ve gerçekten başarısız bir resource predicate invariant'ını doğrular; safe aggregate resource/process alanlarını konsola yazar ve stable failure noktasını `SoakValidation/ResourceBudgetExceeded` yapar. Kalıcı success schema `10`, transient envelope `7`, HLS-first sırası, timeout/retry, TLS, fixture ve cleanup sınırları değişmez.

## İkinci transcript ve exact predicate — 2026-08-23

Kullanıcının temiz local `856fb04` checkpoint'inden paylaştığı envelope-v7 transcript'i `100/100` switch, `7/7` interruption/recovery, `101` exact source detachment, altı surface transition ve sıfır playback retry sonrasında yeniden `ResourceBudgetExceeded` bildirdi. Startup maximum `2.508,366 ms` ile geçti. Resource sonucu:

| Ölçüm | Sonuç | Karar |
|---|---:|---:|
| Sample | `97` | yeterli |
| Warm-up private bytes | `168.304.640` | baseline |
| Net büyüme | `18.698.240 byte` / yaklaşık `17,8 MiB` | `≤100 MiB`, geçti |
| Göreli büyüme | `%11,109759` | `≤%10`, **kaldı** |
| Monotonic artış | `false` | geçti |
| Handle büyümesi | `-52` | kayıt |

Bu koşuda görünen tek resource-budget ihlali göreli büyümedir. `memoryMonotonicIncrease=false` ve negatif handle büyümesi tek başına sürekli leak kanıtı değildir. Eşik, warm-up veya sample exclusion değiştirilmez. Envelope-v8 takip teşhisi en çok `128` process sample'ını ordinal/QPC/elapsed/private-bytes/handle/phase/switch alanlarıyla ve en çok `7` injection/recovery olayını aynı QPC tabanında bağlar. Controller aggregate sonucu sample serisinden tekrar türetip fail-closed doğrular; her sample'ı recovery phase/ordinal ile, ayrıca post-warm minimum/maximum/peak/final noktalarını bounded konsol çıktısında gösterir. Kalıcı schema `10`, player/transport davranışı ve acceptance predicate'leri değişmez.

## Karar

Sekiz saatlik acceptance gate **FAIL** durumundadır: negatif transcript PASS kanıtı olamaz. ADR-007 `Proposed`, R15 `ACTIVE`, M10 `IN PROGRESS` kalır ve M11 production adapter başlamaz. Envelope-v8 bounded teşhis temiz checkpoint üzerinde doğrulandıktan sonra aynı `480 dakika / 100 switch / 7 interruption` profili yeniden çalıştırılmalı; sample seyri recovery olaylarıyla ayrılmadan threshold, fixture veya player davranışı değiştirilmemelidir.
