# M10 native Tier A cancellation/recovery kanıt kaydı

**Durum:** `IMPLEMENTED; HOSTED PACKAGED RESULT PENDING, 2026-08-22`

## Amaç ve kanıt durumu

Bu kayıt, ADR-007 Windows-native Tier A spike'ının test-only schema-10 cancellation/recovery sözleşmesini sınırlar. Kalıcı sanitized evidence schema sürümü `10`, packaged probe envelope sürümü `5`tir. Kod, controller, exact JSON validator ve architecture guard sözleşmesi hazırdır; ancak bu değişikliklere bağlı commit-bound GitHub-hosted packaged sonuç, artifact kimliği ve digest henüz yoktur. Bu nedenle aşağıdaki maddeler uygulanan contract'ı açıklar; hosted `PASS`, reference-device acceptance veya M10 completion iddiası değildir.

Schema-9 [startup attribution kaydındaki](M10_NATIVE_TIER_A_STARTUP_ATTRIBUTION.md) run #150, bu dilimin predecessor kapısını kapatmıştır. Schema-10 sonucu ayrı bir successor koşuda doğrulanacaktır.

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

Hosted packaged schema-10 sonucu **PENDING**dir. [ADR-007](../adr/ADR-007-windows-native-tier-a-playback-fallback.md) `Proposed`, R15 `ACTIVE` ve M10 `IN PROGRESS` kalır.
