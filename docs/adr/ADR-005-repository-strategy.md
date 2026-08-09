# ADR-005 — Repository stratejisi

**Status:** Accepted

**Date:** 2026-08-09

## Context / Problem

Windows, Tizen Web, Android ve Apple hedefleri farklı dil, player, signing, Store ve release cycle kullanacaktır. Buna karşılık terminology, provider davranışı, error registry, test fixture'ları, policy ve ADR'lerin birlikte evrilmesi gerekir. Küçük başlangıç ekibi için koordinasyon maliyeti ile platform izolasyonu dengelenmelidir.

## Decision drivers

- Contract/fixture değişikliklerini atomik gözden geçirme.
- Tek ürün backlog'u, security baseline ve ADR seti.
- Platform bazında bağımsız toolchain, dependency lock ve release.
- C#/TypeScript/Kotlin/Swift arasında sahte runtime-code sharing baskısını önleme.
- Signing credential ve Store secret'larını platform bazında ayırma.
- Gelecekte repository bölünmesine geri döndürülebilir sınırlar.

## Considered options

1. Platform sınırları belirgin monorepo.
2. Her platform için baştan ayrı repository.
3. Ortak cross-platform app/framework repository'si.
4. Tek source tree ve bütün platformların aynı domain implementation'ını kullanması.

## Decision

Başlangıçta **monorepo** kullanılacaktır. Bu Phase 0 turunda uygulama klasörü/scaffold oluşturulmaz; hedef sınırlar şunlardır:

```text
/apps/windows/
/apps/samsung-tizen/
/apps/android/
/apps/apple/                 # iOS + macOS/tvOS hedefleri
/shared/contracts/
/shared/test-vectors/
/docs/
/tools/
```

Kurallar:

- Her `apps/*` kendi toolchain, lockfile, build/test komutu, player/secret-store adapter'ı, signing ve release pipeline'ına sahiptir.
- Platformlar arası zorunlu paylaşım yalnız versioned terminology, JSON Schema/contract, error registry, sentetik provider fixture'ları, M3U golden corpus ve playback matrix manifestidir.
- Windows C#, Tizen JavaScript/TypeScript, Android Kotlin ve Apple Swift arasında runtime domain/UI/player kodu zorla paylaşılmaz.
- Apple hedefleri kendi içinde Swift Package; gelecekte .NET hedefleri yalnız ölçülmüş yararla C# package paylaşabilir.
- CI işleri platform-scoped kalır; ancak required-check workflow'u path filter nedeniyle skip edilip `Pending` kalmamak için her PR/`merge_group` olayında sonuç üretir. Bağımlı job skip/fail durumları `always()` coordinator ile tek required sonuca çevrilir; ruleset seçimi ayrıca yönetilir. Her platformın release artifact'i bağımsızdır.
- Store/signing credential'ları repository'ye girmez. CODEOWNERS review sahipliği sağlar ama path-level erişim gizliliği sağlamaz.
- Shared contract değişikliği semantic version ve bütün consumer contract testleriyle yapılır.

## Consequences and trade-offs

- İlk ekip tek issue/ADR/security alanında koordine olur.
- Spec ve fixture değişiklikleri consumer'larla aynı PR'da doğrulanabilir.
- Toolchain'ler ortak pipeline içine zorlanmaz.
- Clone/CI boyutu platform sayısıyla büyüyebilir.
- Repository düzeyi erişim, vendor/compliance izolasyonuna yetmez.
- Bağımsız platform release'leri için job içi değişiklik seçimi veya her zaman sonuç üreten coordinator, CODEOWNERS ve branch disiplini gerekir; required workflow'a üst seviye path filter konmaz.

## Risks

- “Monorepo”nun tek framework/tek runtime kullanma baskısı yaratması.
- Bütün toolchain'leri her PR'da çalıştıran yavaş CI.
- Platform signing secret'larının ortak environment'a sızması.
- Shared klasörün versiyonsuz utility çöplüğüne dönüşmesi.
- Vendor/contractor erişim ihtiyacının repository-level güvenlikle çelişmesi.

## Validation plan

- M1'de yalnız Windows paths ve docs için gerçek minimal yapı; hayali gelecek platform scaffold'u yok.
- M2 — **PASS, 2026-08-09:** [hosted run `31327398270`](https://github.com/serkankaracan/iptv-suite/actions/runs/31327398270) quality, package-smoke ve `Required Windows gate` coordinator işlerini geçirdi. Üst seviye path filter yoktur ve `merge_group` sonucu üretilir; ancak mevcut private-repository planında branch protection etkin olmadığı için check merge policy tarafından required değildir.
- İlk shared contract'ta semantic version, schema validation ve Windows consumer testi.
- Her yeni platform bootstrap'ında required check'i `Pending` bırakmayan platform seçimli CI ve bağımsız release dry run.
- Repository boyutu, checkout süresi ve cross-platform CI süresi üç ayda bir ölçülür.
- CODEOWNERS ve CI secret scope security review'dan geçer.

## Revisit triggers

- Platform ekipleri/release yönetimi bağımsızlaşırsa.
- Contractor/vendor diğer platform kodunu görmemeliyse.
- Compliance veya sözleşme path-level erişim izolasyonu isterse.
- Bir platform başka ekibe/şirkete devredilirse.
- Checkout/CI süresi kabul bütçesini ölçülebilir biçimde aşarsa.
- Ortak contracts/test-vectors bağımsız release cadence ve erişim isterse.

Bölünmede tarafsız `contracts-and-test-vectors` repository'si semantic versionlanır; platform repository'leri immutable release tüketir.

## References

[DOMAIN_AND_DATA_CONTRACTS](../architecture/DOMAIN_AND_DATA_CONTRACTS.md), [S81–S84](../research/SOURCES.md)
