# M10 native Tier A cancellation/recovery kanıt kaydı

**Durum:** `HOSTED PACKAGED VERIFIED; ACCEPTANCE OPEN, 2026-08-22`

## Amaç ve kanıt durumu

Bu kayıt, ADR-007 Windows-native Tier A spike'ının test-only schema-10 cancellation/recovery sözleşmesini sınırlar. Kalıcı sanitized evidence schema sürümü `10`, packaged probe envelope sürümü `5`tir. Commit `29bb89ce5a2c411f000c5678f4a4eb482e6a9a61` için GitHub Actions [run #151 (`32549714175`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32549714175), exact controller/validator contract'ını commit-bound hosted packaged koşuda doğruladı. Bu başarı reference-device acceptance veya M10 completion iddiası değildir.

Schema-9 [startup attribution kaydındaki](M10_NATIVE_TIER_A_STARTUP_ATTRIBUTION.md) run #150, bu dilimin predecessor kapısını kapatmıştır. Run #151 schema-10 successor kapısını aşağıdaki exact evidence ile geçti.

## Hosted doğrulama

`VERIFIED`: Run #151'de beş workflow işinin tamamı, required coordinator dahil yeşildir. [Native job `96974702542`](https://github.com/serkankaracan/iptv-suite/actions/runs/32549714175/job/96974702542) `7m0s` sürdü. `windows-native-playback-evidence` artifact'ı ID `9469790188`, boyut `1567` byte ve ZIP digest `sha256:c5c3d29c2f487ee7cc23147d41abd7aacac7673750c365db9c91b17a7de1bb21` ile kaydedildi. İçindeki sanitized JSON `3969` byte ve SHA-256 `b32044049659158a0a0d9a79b6f3c24fad588d0aa08434f22b47aeac426c5c78`dir.

Commit-bound schema-10/envelope-v5 sonucu:

- `SwitchCount=100`; startup p95 `2149.623 ms`, maximum `4974.486 ms`, HLS/direct p95 `2167.69/2140.485 ms`;
- `DetachedSourceCount=102`, `PlaybackRetryCount=0`; detach p95/maximum `4.984/9.278 ms`;
- exact bir network interruption/recovery ve request ordinal `54→56`;
- `CancellationProbeCount=1`; observed, cancellation detach, recovery ve recovery detach count'larının dördü de exact `1`;
- cancellation latency `2.834 ms`, application-owned quiescence `5.392 ms`, no-auto-restart observation `1000 ms`, cancellation detach `2.224 ms`;
- fresh recovery startup `20.583 ms`, exact current-source advance `2616.587 ms`, recovery detach `3.938 ms`;
- `CancellationSourceNullAfterObservation`, `CancellationRecoveryUsedFreshSource` ve `CancellationNoAutomaticRestart` değerlerinin üçü de `true`;
- bütün cleanup Boolean'ları `true`; runtime baseline korundu, disposition `SharedAdditionsPreserved` ve shared addition count `2` oldu.

Bu değerler short lane'in cancellation ve network fault'larını ayrı sayaç/ordinal sınırlarıyla tamamladığını, iki ek cancellation-owned detach'ın toplam `102` detached source formülüne girdiğini ve security/cleanup gate'lerinin success evidence öncesinde geçtiğini doğrular.

## Kısa lane profili ve bağımsız fault'lar

Schema-10 kısa lane'i exact şu profile bağlıdır:

- `SwitchCount=100`;
- `SoakMinutes=0`;
- `NetworkInterruptionCount=1`;
- `CancellationProbeCount=1`.

`NetworkInterruptionCount=1`, TLS loopback sunucusunun mapped media isteğine uyguladığı tek-response sentetik `503` ve takip eden request-ordinal recovery sözleşmesidir. `CancellationProbeCount=1` ise spike içindeki ayrı, uygulama-sahipli managed operation cancellation ve fresh-source recovery zinciridir. Biri diğerini tetiklemez, temsil etmez veya sayaç olarak doyurmaz. Mevcut yalnız-ilk-explicit-`MediaFailed` retry sınırı, timeout retry yasağı ve network interruption/recovery invariant'ları değişmez.

`CancellationProbeCount=1` yalnız `100/0` kısa profilde geçerlidir. Mode `0`, eski kısa/soak çağrılarını korur ve bütün cancellation alanlarının numeric `0`, Boolean `false` olmasını ister.

## Cancellation sınırı

Probe, caller/lifetime token'ından bağlı fakat kendisine ait bir `CancellationTokenSource` ile uygulama-sahipli, sonsuz bekleyen bir managed operation başlatır. Exact bir source ataması ve exact bir `Play()` çağrısından sonra yalnız bu local token iptal edilir. Başarı için:

1. `OperationCanceledException` exact local token'a bağlı olarak gözlenir; caller/lifetime token iptal edilmiş olamaz.
2. Aynı attempt-owned `MediaSource`, `MediaPlayer.Source = null` postcondition'ıyla exact bir kez detach edilir ve ardından exact owned instance dispose edilir.
3. İstekten managed cancellation gözlemine kadar süre ve istekten detach/dispose tamamlanmasına kadar uygulama-owned quiescence süresi ayrı ölçülür; ikisi de en fazla `1000 ms`dir.
4. Detach sonrasında hedef `1000 ms` boyunca `Source` null kalır ve source-assignment/`Play()` sayaçları ilerlemez. Scheduler toleransı nedeniyle persisted `CancellationObservationMilliseconds` aralığı `1000..1500 ms`dir.

Bu, native `MediaPlayer`/Media Foundation API'sine aktarılmış bir cancellation token'ı veya native open/decode operasyonunun işletim sistemi tarafından iptal edildiğini kanıtlamaz. Buradaki “quiescence”, yalnız bu managed operation ile exact application-owned detach/dispose zincirinin ölçümüdür; OS audio/session quiescence ya da ghost-audio yokluğu değildir.

## Fresh-source recovery bağı

Cancellation sonrası recovery, iptal edilen source instance'ını yeniden kullanmaz; aynı sentetik fixture için fresh bir `MediaSource` oluşturur. Recovery sonucu şu iki source-identity sınırına birlikte bağlıdır:

- `OpenOperationCompleted` handler'ı `Source` atamasından önce yalnız recovery source instance'ına bağlanır; yalnız aynı sender kabul edilir, deadline/exception/cleanup yollarında ayrılır ve event hata taşımamalıdır;
- source-open tamamlandıktan sonra `MediaPlayer.Source` hâlâ exact recovery source olmalıdır; `PlaybackSession.Position` baseline'ından en az `500 ms` ilerleme, aynı current source korunarak en fazla `3000 ms` içinde gözlenmelidir.

Fresh-source startup bütçesi en fazla `5000 ms`dir. Başarılı recovery de exact detach ve owned source dispose ile biter. Global/stale bir `MediaOpened` veya başka source'a ait `OpenOperationCompleted` recovery başarısı sayılmaz.

Bu recovery yalnız cancellation probunun kontrollü successor adımıdır. M13 general reconnect policy'si, production reconnect davranışı veya genel network recovery matrisi değildir.

## Evidence schema ve exact validation

Envelope v5, schema-9 probe sonucuna `SourceDetachMaximumMilliseconds` sonrasında aşağıdaki alanları exact sırayla ekler. Persisted schema-10 aynı contiguous bloğu `LastRecoveryRequestOrdinal` sonrasında taşır:

1. `CancellationProbeCount`
2. `CancellationObservedCount`
3. `CancellationSourceDetachCount`
4. `CancellationRecoveryCount`
5. `CancellationRecoverySourceDetachCount`
6. `CancellationLatencyMilliseconds`
7. `CancellationQuiescenceMilliseconds`
8. `CancellationObservationMilliseconds`
9. `CancellationSourceDetachMilliseconds`
10. `CancellationRecoveryStartupMilliseconds`
11. `CancellationRecoveryAdvanceMilliseconds`
12. `CancellationRecoverySourceDetachMilliseconds`
13. `CancellationSourceNullAfterObservation`
14. `CancellationRecoveryUsedFreshSource`
15. `CancellationNoAutomaticRestart`

Mode `1` için dört operation/recovery count alanı exact `1`, üç Boolean exact `true` olmalıdır. Latency ve application-owned quiescence `0..1000 ms`; observation `1000..1500 ms`; iki detach metriği `0..5000 ms`; recovery startup `(0..5000] ms`; recovery advance `(0..3000] ms` aralığındadır. Mode `0` için bu bloğun bütün numeric değerleri exact `0`, Boolean değerleri exact `false`tır.

Cross-field doğrulama ayrıca cancellation latency + cancellation detach toplamını quiescence ölçümüne, iki cancellation detach süresini genel source-detach maximum'una ve toplam detached-source sayısını `SwitchCount + PlaybackRetryCount + (2 * CancellationProbeCount)` formülüne bağlar. Controller, eksik/fazla property, yanlış JSON type, sıra/parity, request bağı veya bu invariant'lardan biri bozulursa fail-closed davranır. Failure envelope typed cancellation stage'ini koruyabilir; kalıcı success evidence ancak bütün probe, security ve cleanup postcondition'ları geçtiğinde yayımlanır.

## Korunan güvenlik ve cleanup invariant'ları

- Medya yalnız repository içindeki hakları temiz sentetik corpus'tan ve doğrulanan loopback TLS endpoint'inden gelir; TLS validation gevşetilmez.
- URI, header, credential, native extended error veya kullanıcı verisi evidence'a eklenmez.
- Sentetik `503` server/retry/request-ordinal sözleşmesi cancellation probundan bağımsız ve değişmeden kalır.
- Exact disposable package, process, app data, ephemeral certificate, output ve repository-clean kontrolleri success evidence öncesinde zorunludur; shared Windows App Runtime ownership sınırı korunur.

## Açık kabul sınırları

Bu checkpoint aşağıdakileri kanıtlamaz:

- native API cancellation veya OS audio/session quiescence;
- genel reconnect/failure matrisi ya da M13 reconnect policy'si;
- production playback adapter davranışı;
- reference-device, device/HW-decode, WACK, geniş surface/lifecycle veya uzun soak kabulü;
- M10 completion.

Run #151 schema-10 hosted packaged checkpoint'ini **VERIFIED** yapar. [ADR-007](../adr/ADR-007-windows-native-tier-a-playback-fallback.md) `Proposed`, R15 `ACTIVE` ve M10 `IN PROGRESS` kalır.
