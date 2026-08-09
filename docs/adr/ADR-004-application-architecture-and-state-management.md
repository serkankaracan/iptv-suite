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
- M2 fake clock/transport/player ve local fixture server.
- M3 domain invariant/table tests.
- M5–M8 adapter contract ve cancellation/fault-injection testleri.
- M9 view-model + UI automation/virtualization testleri.
- M11 aynı player contract suite'inin fake ve gerçek adapter'a uygulanması.
- M12/M13 property/state transition ve rapid switch/cancel race testleri.

## Revisit triggers

- İkinci bağımsız process/backend gerçek güvenlik veya crash isolation ihtiyacı doğurursa.
- Feature state'leri arasında ölçülmüş ve tekrar eden transaction/undo gereksinimi oluşursa.
- Offline change sync veya audit history event log gerektirirse.
- Platform ekipleri aynı implementation'ı paylaşmaya karar verir ve sınırlar taşınabilirliğe engel olursa.
- Dependency kuralları sürekli ihlal ediliyor ve mevcut modül sınırları gerçek ownership'i yansıtmıyorsa.

## References

[DOMAIN_AND_DATA_CONTRACTS](../architecture/DOMAIN_AND_DATA_CONTRACTS.md), [S37–S41](../research/SOURCES.md)
