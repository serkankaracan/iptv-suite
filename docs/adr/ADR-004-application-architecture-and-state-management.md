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
- M11 — **IN PROGRESS, 2026-08-24:** Application lifecycle ve controls checkpoint'leri engine-neutral contract, monoton session ID, session-lifetime cancellation, serialized engine mutation, exact stop-before-replace, stale/wrong-order callback suppression, safe typed failure ve idempotent async disposal uygular. Volume/mute/fit-fill ile bounded session-local track inventory/selection aynı cancellation ve session-correlation sınırına bağlıdır; nonterminal control failure playback'i durdurmaz, engine `Failed` callback'i terminal kalır. Coordinator, open sonrasında desired engine-wide control state'ini play öncesi exact session'a yeniden uygular. Deterministik test-local engine ile 35 focused lifecycle/control/security/error testi geçmiştir. Infrastructure JIT resolver checkpoint'i remote-M3U stream lease'ini active snapshot + exact source/channel/owner/purpose/AEAD zincirine bağlar; Xtream provider-item kolunu exact configuration owner/reference/endpoint/provider/container tuple'ından JIT kurar ve stale/tampered/malformed kayıtları fail-closed reddeder. Internal Windows-native adapter checkpoint'i resolver lease'ini kısa ömürlü tüketir, bütün Media Foundation/WinUI erişimini captured dispatcher'a bağlar, callback'leri exact session+generation+source ile bastırır ve failure/stop/dispose kaynak sahipliğini fail-closed kapatır. Packaged UI composition checkpoint'i embedded surface'i yalnız coordinator'a delege eder, kanal başlatmayı `SourceId + ChannelId` ile sınırlar, native callback state'ini UI dispatcher'da güncel session ile doğrular ve ilk window-close isteğini playback/surface async teardown tamamlanana kadar erteler. Public contract hâlâ locator/native/WinUI/track-label taşımaz; gerçek production-package adapter kabul koşusunun yönetici/hosted yürütmesi açık kalır.
- M11 Xtream construction update — **IMPLEMENTED, 2026-08-24:** Provider-item playback yalnız active source configuration'ın exact `ConfigurationId` owner'ı, `SecretReference`, persisted safe endpoint, canonical provider item ve `Hls|MpegTs` container binding'iyle JIT kurulur. Credential lease işlem sonunda kapanır; başarısız locator buffer'ı sıfırlanır ve raw değer public contract/log/result yüzeyine çıkmaz. `/live/{username}/{password}/{providerItem}.{m3u8|ts}` formal/evrensel standard değil, dar live-only `UNVERIFIED` uyumluluk varsayımıdır; farklı route'lar fail-closed kalır. Production-package playback kabulü açık olduğundan M11 henüz tamamlanmış değildir.
- M11 production-package acceptance update — **IMPLEMENTED / LOCAL VERIFIED, 2026-08-24:** Non-production harness, sentetik protected Remote-M3U kaydını production parser/encryption/DPAPI `CurrentUser` zinciriyle exact package LocalCache konumuna hazırlar; bounded loopback HTTPS fixture ve yalnız aggregate sayaçlı ticket üretir. Package smoke ikinci production activation'da exact kanal seçimi ile play/pause/resume/stop durumlarını UI Automation üzerinden doğrular, harness/test payload sızıntısını reddeder ve exact certificate/process/package cleanup uygular. Eşikler ve önceki 50k UIA kabulü değişmemiştir; yönetici/hosted gerçek MSIX yürütmesi açık olduğundan M11 henüz tamamlanmış değildir.
- M12/M13 property/state transition ve rapid switch/cancel race testleri.

## Revisit triggers

- İkinci bağımsız process/backend gerçek güvenlik veya crash isolation ihtiyacı doğurursa.
- Feature state'leri arasında ölçülmüş ve tekrar eden transaction/undo gereksinimi oluşursa.
- Offline change sync veya audit history event log gerektirirse.
- Platform ekipleri aynı implementation'ı paylaşmaya karar verir ve sınırlar taşınabilirliğe engel olursa.
- Dependency kuralları sürekli ihlal ediliyor ve mevcut modül sınırları gerçek ownership'i yansıtmıyorsa.

## References

[DOMAIN_AND_DATA_CONTRACTS](../architecture/DOMAIN_AND_DATA_CONTRACTS.md), [S37–S41, S76–S79](../research/SOURCES.md)
