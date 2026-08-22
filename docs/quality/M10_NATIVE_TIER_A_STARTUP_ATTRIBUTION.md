# M10 native Tier A hosted startup attribution

**Durum:** `IMPLEMENTED; HOSTED EXECUTION UNVERIFIED, 2026-08-22`

Bu checkpoint bir acceptance sonucu veya threshold değişikliği değildir. Amacı, hosted Windows Server koşusunda tek startup örneğinin `≤5000 ms` hard maximum sınırını neden az farkla aştığını fixture ya da retry tahminiyle örtmeden ölçmektir.

## Tetikleyen kanıt

`VERIFIED`: Commit `16e22625d4330826ac100648ef21b3038beb8606` için [GitHub Actions run 32540817711](https://github.com/serkankaracan/iptv-suite/actions/runs/32540817711), quality, packaged lifecycle ve DPAPI işlerini geçti. Native probe 100 switch, source-detachment ve surface invariant'larını tamamladı; aggregate startup p95 `2162,9392 ms` idi. Hard maximum `5102,2433 ms` ile `5000 ms` sınırını `102,2433 ms` aştı; HLS p95 `2165,6151 ms`, direct p95 `2162,2629 ms` ölçüldü. Controller success evidence hazırlama ve cleanup publication zincirine ulaşmadığından bu koşu schema-9 hosted PASS değildir.

Önceki bounded rerun `5056,3179 ms` maximum ve `2157,0052 ms` p95 üretmişti. `MediaPlayer.RealTimePlayback=true` sonrasında ilk HLS hard timeout'unun tek koşuda görülmemesi nedensel düzeltme kanıtı değildir; aggregate değerler anlamlı bir iyileşme de göstermez.

## Ölçüm sınırı

`VERIFIED` statik kod sınırı: `startupStarted`, `MediaSource.CreateFromUri`, `MediaPlayer.Source` ataması ve `Play()` çağrısından önce alınır. Beş saniyelik `WaitAsync` saati ise bu işlemlerden sonra başlar. Dolayısıyla başarılı bir `MediaOpened` wait'i kendi beş saniyelik sınırında tamamlanırken toplam ölçüm pre-wait maliyeti nedeniyle `5000 ms` üzerinde olabilir. Bunun gerçek outlier açıklaması olup olmadığı henüz `UNVERIFIED`dır.

Packaged probe envelope schema `2`, en yavaş örnek için secretsiz ve bounded şu alanları taşır:

- 1-based switch ordinal'i ve HLS-first ile uyumlu fixture enum'u;
- attempt sayısı ve o switch'teki surface transition sayısı;
- toplamı maximum startup'a eşit pre-wait ile `MediaOpened` wait süreleri;
- HLS ve direct fixture maximum süreleri.

Controller exact property/type/parity/component invariant'larını doğrular ve alanları hosted loga yazar. Kalıcı success evidence schema `9` ve 71 alan olarak kalır; yalnız `ProbeEnvelopeSchemaVersion=2` olur. `≤3000 ms` p95, `≤5000 ms` maximum, HLS-first sıra, timeout retry yasağı, tek `MediaPlayer`, network fault sayısı ve required workflow davranışı değişmez.

## Karar sınırı

Bu instrumentation hosted outlier'ı sınıflandırmadan performans bütçesini yükseltmeyi, ölçüm başlangıcını ileri taşımayı, warm-up ile örneği dışlamayı veya retry eklemeyi yasaklayan mevcut kabul sınırını korur. Successor hosted run'ın diagnostic satırı incelenip minimal düzeltme ayrı checkpoint'te uygulanmalıdır. Sonraki schema-10 cancellation/recovery işi bu startup gate'i yeşil olmadan başlamaz. Sekiz saatlik acceptance soak ayrıca kullanıcı tarafından açıkça başlatılmadı.
