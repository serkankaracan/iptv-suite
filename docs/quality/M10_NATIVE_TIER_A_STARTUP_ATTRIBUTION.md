# M10 native Tier A hosted startup attribution

**Durum:** `IMPLEMENTED; HOSTED FAILURE PATH VERIFIED, HOSTED SUCCESS UNVERIFIED, 2026-08-22`

Bu checkpoint bir acceptance sonucu veya threshold değişikliği değildir. Amacı, hosted Windows Server koşusunda tek startup örneğinin `≤5000 ms` hard maximum sınırını neden az farkla aştığını fixture ya da retry tahminiyle örtmeden ölçmektir.

## Tetikleyen kanıt

`VERIFIED`: Commit `16e22625d4330826ac100648ef21b3038beb8606` için [GitHub Actions run 32540817711](https://github.com/serkankaracan/iptv-suite/actions/runs/32540817711), quality, packaged lifecycle ve DPAPI işlerini geçti. Native probe 100 switch, source-detachment ve surface invariant'larını tamamladı; aggregate startup p95 `2162,9392 ms` idi. Hard maximum `5102,2433 ms` ile `5000 ms` sınırını `102,2433 ms` aştı; HLS p95 `2165,6151 ms`, direct p95 `2162,2629 ms` ölçüldü. Controller success evidence hazırlama ve cleanup publication zincirine ulaşmadığından bu koşu schema-9 hosted PASS değildir.

Önceki bounded rerun `5056,3179 ms` maximum ve `2157,0052 ms` p95 üretmişti. `MediaPlayer.RealTimePlayback=true` sonrasında ilk HLS hard timeout'unun tek koşuda görülmemesi nedensel düzeltme kanıtı değildir; aggregate değerler anlamlı bir iyileşme de göstermez.

`VERIFIED`: Commit `12fac0861f859f379747873c160e054bb95795b8` için [run 32542190663](https://github.com/serkankaracan/iptv-suite/actions/runs/32542190663), quality, package lifecycle ve DPAPI işlerini geçti; native iş ilk switch'te `MediaOpenTimeout` verdi. Runner `Windows Server 2025 Datacenter` idi. HLS manifesti ile dört segment için `5/5` response ve `940229` byte tamamlandı; transport failure ve injected interruption sıfırdı. Probe envelope v2 yalnız tamamlanmış örneğin maximum'unu güncellediğinden ordinal/fixture/attempt/pre-wait/open-wait alanlarının tamamı `0/None` kaldı. Dolayısıyla bu run ağ teslimi eksikliği göstermedi, fakat timeout'un `MediaSource.CreateFromUri`, `Source`, `Play` veya `MediaOpened` wait parçalarından hangisinde zaman harcadığını da ölçemedi.

`VERIFIED (kullanıcının sağladığı manuel transcript; commit/evidence dosyası bağı bağımsız incelenmedi)`: interactive Windows Client üzerinde `SwitchCount=100`, `SoakMinutes=0`, `NetworkInterruptionCount=1` komutu 100 alternating switch ile geçti. Bu kısa koşu controlled interruption/recovery yolunun çalışabildiğini doğrular; hosted Server sonucu, schema-9 hosted success, iki saatlik developer soak veya sekiz saatlik acceptance soak yerine geçmez.

## Ölçüm sınırı

`VERIFIED` statik kod sınırı: `startupStarted`, `MediaSource.CreateFromUri`, `MediaPlayer.Source` ataması ve `Play()` çağrısından önce alınır. Beş saniyelik `WaitAsync` saati ise bu işlemlerden sonra başlar. Dolayısıyla başarılı bir `MediaOpened` wait'i kendi beş saniyelik sınırında tamamlanırken toplam ölçüm pre-wait maliyeti nedeniyle `5000 ms` üzerinde olabilir. Bunun gerçek outlier açıklaması olup olmadığı henüz `UNVERIFIED`dır.

Packaged probe envelope schema `3`, tamamlanmış en yavaş örnek için schema 2'deki secretsiz ve bounded alanları korur:

- 1-based switch ordinal'i ve HLS-first ile uyumlu fixture enum'u;
- attempt sayısı ve o switch'teki surface transition sayısı;
- toplamı maximum startup'a eşit pre-wait ile `MediaOpened` wait süreleri;
- HLS ve direct fixture maximum süreleri.

Schema 3 ayrıca tamamlanmamış failure örneğini maximum semantiğine karıştırmadan şu aktif startup snapshot'ını taşır:

- active stage, 1-based switch ordinal'i, fixture, attempt ve switch-local surface transition sayısı;
- toplam aktif startup süresi;
- tamamlanmış source creation, source assignment ve `Play()` çağrı süreleri;
- timeout gözlenirken aktif olan fazın süresi.

Snapshot, inner timeout catch'inde reset/source disposal başlamadan alınır. `MediaOpenTimeout` yalnız source creation/assignment, `Play` invocation veya `MediaOpenWait`; `PlaybackAdvanceTimeout` yalnız `PlaybackAdvanceWait`; surface readiness timeout'u yalnız `SurfaceReadiness` stage'i ile eşleşebilir. Controller exact property/type/enum/parity/attempt/transition/component invariant'larını log üretmeden önce fail-closed doğrular.

Kalıcı success evidence schema `9` ve 71 alan olarak kalır; yalnız `ProbeEnvelopeSchemaVersion=3` olur. `≤3000 ms` p95, `≤5000 ms` maximum, HLS-first sıra, timeout retry yasağı, tek `MediaPlayer`, network fault sayısı ve required workflow davranışı değişmez.

## Karar sınırı

Bu instrumentation hosted outlier'ı sınıflandırmadan performans bütçesini yükseltmeyi, ölçüm başlangıcını ileri taşımayı, warm-up ile örneği dışlamayı veya retry eklemeyi yasaklayan mevcut kabul sınırını korur. Schema-3 successor hosted run'ın active failure/maximum diagnostic satırı incelenip minimal davranış düzeltmesi ayrı checkpoint'te uygulanmalıdır. Sonraki schema-10 cancellation/recovery işi bu startup gate'i yeşil olmadan başlamaz. Sekiz saatlik acceptance soak kullanıcı tarafından açıkça başlatılmadı ve bu checkpoint kapsamında başlatılmaz.
