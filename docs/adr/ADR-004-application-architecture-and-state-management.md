# ADR-004 — Uygulama mimarisi ve state yönetimi

**Status:** Accepted

**Date:** 2026-08-09

## Context / Problem

MVP; iki kaynak türü, büyük katalog importu, local persistence ve native playback gibi farklı yaşam döngülerini içerir. Mimari bunları test edilebilir sınırlarla ayırmalı, fakat küçük ekipte microservice, CQRS, event sourcing, global store veya yüzlerce interface yükü oluşturmamalıdır.

UI state, kaynak senkronizasyonu ve player state aynı şey değildir. Özellikle async network/import ile native callback'lerin yarışması, “son istek kazanır”, cancellation ve tek aktif playback session kurallarını açık hale getirir.

## Decision drivers

- UI bağımsız domain/application testleri.
- Provider, storage ve playback gibi gerçek external variability noktaları.
- Cancellation, timeout ve state transition'ların deterministikliği.
- Büyük listelerde immutable tam-kopya üretmeden incremental/paged query.
- WinUI lifecycle ve thread-affinity'nin altyapı/domain'e sızmaması.
- Küçük ekibin okuyup değiştirebileceği düşük ceremony.

## Considered options

1. Katmanlı modular monolith + feature-scoped MVVM/state machine.
2. Global Redux benzeri immutable store.
3. Clean/hexagonal architecture'ın her sınıfa interface uygulayan katı yorumu.
4. CQRS/event sourcing.
5. Microservices + backend.

## Decision

Tek Windows process'i içinde **modular monolith** kullanılacaktır:

```text
Presentation (WinUI/MVVM)
        -> Application (use cases, orchestration, policies)
        -> Domain (terminology, invariants, errors)
Infrastructure -> Application/Domain ports
```

- Presentation; view, feature-scoped view-model ve yalnız UI state'i içerir.
- Application; `AddSource`, `ProbeSource`, `SynchronizeCatalog`, `QueryChannels` ve `StartPlayback` gibi use case'leri, cancellation ve policy'leri orkestre eder.
- Domain; value object, normalization kuralı, capability ve `DomainError` kodlarını içerir; WinUI, HTTP, SQLite ve libVLC bilmez.
- Infrastructure; HTTP/provider adapter'ları, parser input, SQLite, protected storage, image cache, logging ve platform player adapter'ını uygular.

State yaklaşımı:

- MVVM yalnız presentation pattern'idir; domain modeli değildir.
- Her feature küçük, explicit state kullanır: örneğin `Idle / Validating / Testing / Syncing / Ready / Failed`.
- Playback ayrı ve tek-session state machine'dir: `Closed / Opening / Buffering / Playing / Paused / Reconnecting / Stopping / Failed`.
- Her async intent monoton operation/session ID ve cancellation token taşır. Eski operation callback'i yeni state'i değiştiremez.
- Query sonuçları DB'den windowed/incremental gelir; 50.000 öğelik katalog global immutable state'te kopyalanmaz.

Dependency injection composition root'ta kullanılır. Interface yalnız gerçek variability veya test sınırında açılır: source adapter, transport, clock, parser/record stream, secret store, catalog store, image fetch/cache ve playback engine. Entity başına generic repository veya service/interface çifti yapılmaz.

Configuration strongly typed ve secretsizdir. Environment/appsettings içine credential konmaz. Kullanıcıya giden metin resource key üzerinden yerelleştirilir; domain error kodu sabit, presentation metni locale'e göre değişir.

M2 implementation test seam'lerini production contract'larından ayırır. `IptvSuite.Testing`, unit ve integration projeleri production graph'ının dışında kalır; production projeleri test assembly'sine referans veremez. Fake time resmî `TimeProvider` test implementation'ını, scripted transport yalnız in-process request/response'u, in-memory secret store yalnız test bytes'ını ve fake player yalnız explicit command/state recording'i sağlar. Bunlar provider/retry policy, DPAPI veya gerçek player state/codec davranışı uygulamaz. Loopback Kestrel yalnız integration harness'idir; production transport değildir.

## Consequences and trade-offs

- İş kuralları WinUI ve native player olmadan test edilir.
- State race'leri explicit transition ve session ownership ile görünür olur.
- Infrastructure değişebilir, ancak kullanılmayan soyutlamalar kurulmaz.
- Bütün ürün tek process olduğu için deploy/diagnostics basittir.
- Katman sınırlarını analyzer/dependency tests ile korumak gerekir.
- Global time-travel store veya event log yoktur; debugging structured event/state transition kayıtlarına dayanır.

## Risks

- “Clean architecture” adına gereksiz mapping/interface sayısının artması.
- View-model'ların use case, SQL veya libVLC callback sahiplenmesi.
- Native callback'in kapatılmış session'a state yazması.
- Tek process native crash'inin bütün uygulamayı düşürmesi.
- Feature'lar arası navigation/state koordinasyonunun zamanla global service locator'a dönüşmesi.

## Validation plan

- M1 — **PASS, 2026-08-09:** 6 project/package/toolchain/WinUI/manifest kuralı yeşil; Domain'e geçici yasak reference eklendiğinde gate'in kırıldığı doğrulandı.
- M2 — **PASS, 2026-08-09:** fake time/transport/test secret store/passive player, loopback fixture server, temp/timeout isolation, deterministic fixture/canary ve production→test dependency yasağı mevcut. Exact-SDK local iki-run/sentinel/fixture/canary gate'i 22/22; commit `79cf619c6683fa9c4213846455e376fb1b0cb11c` için [hosted run `31327398270`](https://github.com/serkankaracan/iptv-suite/actions/runs/31327398270) quality, packaged-smoke ve coordinator işlerini geçti. Test doubles ürün contract'ı değildir; run sınırları ve artifact doğrulaması [M2 completion evidence](../quality/M2_COMPLETION_EVIDENCE.md) belgesindedir.
- M3 — **PASS, 2026-08-09:** plain `net10.0` Domain'de typed Live TV contract'ları, NFC/Unicode scalar invariant'ları, source-scoped stable key, HTTPS-only safe configuration, opaque reference, stable error/resource key, pure redaction/redirect ve content-based catalog/HLS kararları uygulandı. Exact-SDK local gate architecture 9 + unit 91 + integration 5 = 105 testi iki koşuda aynı `Passed` setiyle tamamladı; network/storage/parser/UI/player kapsam dışı kaldı.
- M5–M8 adapter contract ve cancellation/fault-injection testleri.
- M9 view-model + UI automation/virtualization testleri.
- M11 — **COMPLETED, 2026-08-24:** Application lifecycle/control contract'ı; monoton session ID, session-lifetime cancellation, serialized engine mutation, exact stop-before-replace, stale/wrong-order callback suppression, safe typed failure ve idempotent async disposal uygular. Volume/mute/fit-fill ile bounded session-local track inventory/selection aynı cancellation ve session-correlation sınırına bağlıdır. Infrastructure, remote-M3U ve Xtream live locator'larını exact active binding/owner/reference/purpose/endpoint/provider/container zincirinden JIT çözer. Windows-native adapter secret/native kaynak sahipliğini ve exact session/generation/source callback bağını internal tutar. Packaged UI yalnız coordinator'a delege eder ve close'u playback/surface teardown sonrasına erteler. Public contract locator/native/WinUI/track-label taşımaz.
- M11 Xtream construction — **VERIFIED, 2026-08-24:** Provider-item playback yalnız authoritative `ConfigurationId`, `SecretReference`, safe endpoint, canonical provider item ve `Hls|MpegTs` binding'iyle kurulur. Credential lease kapanır, başarısız locator buffer'ı sıfırlanır. Route biçimi dar live-only `UNVERIFIED` uyumluluk varsayımıdır; farklı biçimler fail-closed kalır.
- M11 production-package acceptance — **VERIFIED, 2026-08-24:** Local architecture `35/35`, unit `171/171`, integration `116/116` geçmiştir. Clean head `5a07cae8138642d669f272eaee1340e87edcb47b` için [run `32754184474`](https://github.com/serkankaracan/iptv-suite/actions/runs/32754184474), quality `354/354 × 2`, signed production-package exact kanal `play → pause → resume → stop`, protected-store lifecycle, DPAPI real-user boundary ve required coordinator işlerini geçti. Eşikler ve önceki 50k UIA kabulü değişmemiştir; exact artifact bağı [M11 completion evidence](../quality/M11_COMPLETION_EVIDENCE.md) belgesindedir.
- M12 — **PARTIAL VERIFIED, 2026-08-25:** Application power policy'si yalnız suspend `Entering` durumunda exact current session'ı stop/drain eder; resume autoplay yapmaz. Exact source release replacement session'ı korur. Run `32764149667`/commit `8d537010f64193f7bc9baf05bc3707fb31d5362f`, 25 rapid switch ile controls/fullscreen/focus/resize/minimize/state/active-close otomasyonunu geçti. Run `32785306860` kısa-run resource budget'ını, run `32792088083` production source-delete success yolunu, run `32796910469`/commit `45124f72cc62b21942790c39bd032242706af339` ise deterministic pending failure, restart admission block, exact katalog/configuration/tombstone/sibling korunumu, fault release, manual retry ve final katalog/DPAPI cleanup zincirini geçti. Fiziksel DPI/multi-monitor/sleep/audio/Narrator `NOT RUN`dır. [Kanıt](../quality/M12_COMPLETION_EVIDENCE.md).
- M13 — **POLICY CHECKPOINT IMPLEMENTED, 2026-08-25:** Saf Application policy'si yalnız canonical güvenli `DomainError`, tamamlanan attempt sayısı, elapsed süre ve caller-injected `0..250 ms` jitter ile karar verir. Yalnız exact canonical `BoundedTransient` otomatik reconnect'e uygundur; `Never`, `Manual`, bilinmeyen/mismatched code-retryability-resource key ve aşılmış attempt/zaman bütçesi fail-closed kalır. Hard üst sınırlar üç attempt, 30 saniyelik karar penceresi ve `1 s / 2 s / 4 s` taban gecikmedir; delay yeni attempt için pozitif süre bırakmıyorsa `ReconnectExhausted` üretilir. Saf policy in-flight attempt runtime deadline'ını tek başına enforce etmez; monotonic `TimeProvider` ve deadline-linked cancellation orchestration checkpoint'inde bağlanacaktır. Policy timer, session, native/network çağrısı, `Retry-After`, URL, provider metni veya exception taşımaz. Fetch katmanındaki nested transport retry attempt'lerinin reconnect bütçesine nasıl sayılacağı orchestration öncesi uzlaştırılacaktır. Countdown/state, user cancel/manual retry, channel/source change ve close race orchestration'ı ile packaged acceptance sonraki M13 checkpoint'lerinde kalır.
- M13 — **UNWIRED ORCHESTRATION KERNEL CHECKPOINT IMPLEMENTED, 2026-08-25:** Saf Application kernel'i aynı aktif correlation için tek chain/task ve tek budget paylaşır; yeni correlation eski generation'ı senkron invalid edip iptal eder. Bütün elapsed/deadline/countdown hesabı yalnız monotonic `TimeProvider.GetTimestamp/GetElapsedTime` kullanır. Provider-aware exact 30 saniye deadline token'ı hem `Task.Delay` hem in-flight attempt executor'a ulaşır; attempt exact deadline'da veya sonrasında başarı sayılamaz. Countdown kalan süreden en çok bir saniyelik tick'lerle yeniden hesaplanır. Global attempt gate replacement/manual yarışında aynı anda yalnız bir executor çağrısına izin verir. User cancel chain'i senkron terminal yapar; fake time ileri alınsa yeni attempt çıkmaz. Manual retry yalnız terminal safe correlation için anında, yeni generation ve yeni 30 saniye budget ile başlar; aktif double-call mevcut task'la coalesce olur. Snapshot yalnız phase, monoton safe correlation ID, attempt/max, bounded kalan delay/budget ve terminal `DomainErrorCode` taşır; observer/jitter/executor exception'ları safe `DomainInvariantViolation` sınırında tutulur. Snapshot bildirimleri tek-owner, non-recursive FIFO pompasında sıralanır; generation değişince henüz dağıtılmamış eski bildirimler düşer, eski bir event yeni event'ten sonra veya eşzamanlı observer çağrısı olarak yayımlanmaz ve dispose aktif observer drain edilmeden tamamlanmaz. Bu kernel henüz `PlaybackSessionCoordinator`, player state, WinUI, native engine, source retirement, app close veya network'e bağlı değildir; gerçek user cancel `<=1 s`, no-later-open ve recovery packaged acceptance iddiası yoktur. Native playback bugün `BoundedHttpTransport` üzerinden geçmediğinden nested fetch retry sayımı icat edilmemiş, `Retry-After` fetch katmanında bırakılmıştır.
- M13 — **COORDINATOR-CAPABLE, PRODUCTION-DISABLED CHECKPOINT IMPLEMENTED, 2026-08-25:** `PlaybackSessionCoordinator` için explicit `(engine, policy, timeProvider, jitterSource)` construction path'i reconnect kernel'ini sahiplenir; mevcut tek argümanlı production/UI composition reconnect-disabled kalır. Yalnız canonical `BoundedTransient` engine failure'ı exact generation/correlation/logical-session/selection bağında tek chain başlatır. Public logical session ID zincir boyunca sabittir; her attempt exact eski physical session'ı başarıyla durdurduktan sonra fresh monoton physical ID ile açılır ve bütün command/control/track işlemleri exact logical↔physical binding'e yönlendirilir. Duplicate, stale ve replacement callback'leri yeni chain veya yanlış oturum işlemi üretemez. `Start`, `Stop`, matching source release ve `Dispose` context'i önce invalid edip kernel cancel/drain işlemini coordinator kilidi dışında yapar; non-matching release zinciri korur. Control state restore `Open → volume → mute → aspect → Play` sırasındadır; attempt failure rollback yapar, exact deadline'da geçersiz sayılan late-success physical binding terminal observer'lardan önce drain'e alınır ve `Succeeded` gelmeden recovered state yayımlanmaz. `PlaybackState.Reconnecting` yalnız Application session snapshot'ında aktif `Evaluating|Waiting|Attempting` progress ile geçerlidir; engine bu state'i sahiplenemez. Terminal context ilerideki manual UI retry için tutulur. MainWindow opt-in, countdown/cancel/manual-retry UX, native error mapping ve packaged recovery/no-later-open acceptance sonraki checkpoint'lerde kalır.
- M13 — **PRODUCTION RECONNECT UI CHECKPOINT IMPLEMENTED, 2026-08-25:** Windows composition root reconnect'i exact policy, `TimeProvider.System` ve kriptografik bounded `0..250 ms` jitter ile etkinleştirir. UI yalnız safe phase/attempt/countdown gösterir; saniyelik countdown live-region değildir, `StopAsync` reconnect sırasında erişilebilir `Cancel reconnect` eylemidir ve terminal exact context'te yalnız canonical `Manual` hata manual retry'ı görünür kılar. Retry admission 30 saniyelik chain'i beklemeden döner; duplicate çağrı aynı aktif chain'e coalesce olur. Full snapshot freshness, per-handler observer containment ve current-snapshot guard eski countdown/reentrant event'in yeni state'i ezmesini engeller. Exact lifecycle/source/session kontrolleri, bounded policy ve raw diagnostic yasağı değişmemiştir. Local exact-SDK locked restore, Debug/Release x64 build `0` warning/error, unit `335/335`, integration `142/142` ve architecture `43/43` geçmiştir. Native error mapping, offline hint ve signed packaged recovery/cancel/no-later-open kabulü sonraki M13 checkpoint'lerinde açıktır.
- M13 — **SAFE NATIVE PHASE MAPPING CHECKPOINT IMPLEMENTED, 2026-08-25:** Windows-native adapter, yalnız exact aktif context içinde daha önce gözlenen `Playing|Paused` fazını hatırlar; bundan sonraki `MediaFailed` olayı current state `Buffering|Playing|Paused` ise canonical `StreamInterrupted/BoundedTransient` olur. İlk açılış ve bütün `SourceFailed` yolları `PlaybackStartFailed/Manual` kalır. Native mesaj, HRESULT, exception veya locator ayrıştırılmadığı için güvenli tanılama sınırı korunur; exact HTTP/TLS/DNS attribution production `MediaSource` yolunda iddia edilmez. Stall/EOF, offline hint, localized presenter ve signed packaged fault kabulü hâlâ açıktır.
- M13 — **PLAYABLE RECOVERY HANDSHAKE + CONTROLLED FAULT FIXTURE CHECKPOINT IMPLEMENTED, 2026-08-25:** Coordinator recovery başarısını yalnız attempt-owned completion ve exact current physical session'ın `Playing|Paused` callback'iyle kabul eder; `Buffering`, `Open`/`Play` dönüşü veya stale callback yeterli değildir. Lifecycle invalidation ve deadline pending handshake'i fail-closed kapatır, late success physical session drain edilir. Test graph'ındaki opt-in controlled loopback stream; paced active response, exact abort, hold/reject/restore/disable ve yalnız bounded scalar outcome/ordinal/overlap kanıtı taşır. Abort sahipliği atomik, static fallback active-response quiescence sonrasıdır; hook kapalıyken eski fixture contract'ı korunur. Local exact-SDK doğrulaması Debug/Release x64 build'leri sıfır warning/error; unit `338/338`, integration `156/156` ve architecture `44/44` PASS'tir. Bu test seam'i production transport değildir ve signed package recovery/cancel/no-later-open kabulünü tek başına kanıtlamaz.
- M13 — **SAFE FAILURE PRESENTATION + UNWIRED WATCHDOG KERNEL CHECKPOINT IMPLEMENTED, 2026-08-25:** WinUI terminal playback presentation'ı canonical `DomainError` eşliğini zorunlu tutup `en-US`/`tr-TR` resource, generic fail-closed fallback ve terminal snapshot başına kriptografik 128-bit opaque operation ID üretir. UI freshness ile presentation cache aynı reference-identity sınırındadır; stale/value-equal clone yeni operation ID veya hint üretemez. Windows network availability salt read-only kullanıcı ipucudur ve retry/reconnect authority'sine girmez. Saf Application watchdog startup/rebuffer deadline'larını monotonic `TimeProvider` ile exact boundary'de uygular; early callback kalan süreyi yeniden kurar, stale generation/uncorrelated `Closed`/cancel/dispose/reentrant observer yarışlarını fail-closed filtreler ve cleanup'ı owner lock dışında yapar. Explicit süre aralığı `100 ms..2 dk`; canonical sonuçlar startup için `PlaybackStartFailed`, post-playable rebuffer için `StreamInterrupted`, scheduler contract failure için `DomainInvariantViolation`dır. Debug/Release x64 build sıfır warning/error; unit `349/349`, integration `156/156`, architecture `45/45` PASS'tir. Watchdog henüz production composition'a bağlı olmadığından stall/EOF ve signed package recovery/cancel/no-later-open acceptance iddia edilmez.
- M13 — **PRODUCTION STARTUP/REBUFFER WATCHDOG WIRING CHECKPOINT IMPLEMENTED, 2026-08-25:** Windows native engine watchdog'u private olarak sahiplenir ve production'da startup `10 s`, post-playable rebuffer `5 s`, `TimeProvider.System` ile çalıştırır. Native accepted state önce watchdog'a, sonra external observer'lara gider; Stop/replacement/native failure/dispose exact session generation'ını iptal eder. Expiry background thread'de yalnız dispatcher post'u yapar; UI tarafındaki ikinci exact context/session/generation/source doğrulamasından sonra canonical Failed snapshot kurulur, native teardown denenir ve terminal notification cleanup denemesinden sonra yayımlanır. Dispatcher kapanışındaki enqueue `false`/recoverable throw hiçbir sahte cleanup veya terminal bildirim üretmez. Startup/rebuffer/scheduler eşlemeleri sırasıyla `PlaybackStartFailed`, `StreamInterrupted`, `DomainInvariantViolation`dır; raw native diagnostic veya network hint retry authority'sine girmez. Exact-SDK full gate Debug/Release x64 build sıfır warning/error; unit `349`, integration `156`, architecture `46` toplam `551/551 × 2` deterministic run ile fixture/sentinel/canary kontrollerini geçmiştir. Typed EOF ile signed package recovery/cancel/no-later-open acceptance açık kalır.

- M13 — **TYPED LIVE EOF BOUNDARY CHECKPOINT IMPLEMENTED, 2026-08-25:** Windows native adapter yalnız exact aktif `MediaPlayer`/context/generation/session/source bağından gelen `MediaEnded` olayını terminal live-EOF olarak kabul eder. İlk playable durumdan önce EOF `PlaybackStartFailed/Manual`, daha önce `Playing|Paused` görülmüşken current `Buffering|Playing|Paused` fazındaki EOF `StreamInterrupted/BoundedTransient` üretir. Stop, replacement veya dispose sonrasındaki queued callback ikinci lock-içi dispose/generation/session doğrulamasında düşer. Handler teardown sırasında diğer native handler'larla birlikte detach edilir; duration, position, `MediaPlaybackState.None`, URL/locator veya raw native hata alanlarından completion/failure çıkarımı yapılmaz. Bu sözleşme yalnız mevcut Live TV kapsamıdır; gelecekteki VOD natural-completion semantiği explicit content intent gerektirir. Exact-SDK full gate Debug/Release x64 build sıfır warning/error; unit `349`, integration `156`, architecture `46` toplam `551/551 × 2` deterministic run ile fixture/sentinel/canary kontrollerini geçmiştir. Signed package recovery/cancel/no-later-open acceptance açık kalır.

## Revisit triggers

- İkinci bağımsız process/backend gerçek güvenlik veya crash isolation ihtiyacı doğurursa.
- Feature state'leri arasında ölçülmüş ve tekrar eden transaction/undo gereksinimi oluşursa.
- Offline change sync veya audit history event log gerektirirse.
- Platform ekipleri aynı implementation'ı paylaşmaya karar verir ve sınırlar taşınabilirliğe engel olursa.
- Dependency kuralları sürekli ihlal ediliyor ve mevcut modül sınırları gerçek ownership'i yansıtmıyorsa.

## References

[DOMAIN_AND_DATA_CONTRACTS](../architecture/DOMAIN_AND_DATA_CONTRACTS.md), [S37–S41, S76–S79](../research/SOURCES.md)
