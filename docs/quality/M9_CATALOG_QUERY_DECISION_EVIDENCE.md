# M9 indexed catalog query Decision evidence

**Tarih:** 2026-08-21  
**Durum:** `VERIFIED` local component evidence; M9 acceptance henüz tamamlanmadı

Clean commit `d3ebbdcf24bb842bbab8f39c3abbed47a085db65` üzerinde exact SDK `10.0.302` wrapper'ı ile Release/x64 `SqliteCatalogPerformanceDecisionTests.Measure50kIndexedCatalogQueryDecision` çalıştırıldı. Runner bir kez 50.000 kayıtlı sentetik M3U kataloğunu production parser + protected SQLite sink üzerinden kurdu; ardından source, category, first-page, category-page, exact search ve adapter reopen+first-page sorgularını 20 kez ölçtü. `1.457` byte `decision-summary.json` SHA-256 değeri `79aca1ad7090866a04ae661df9a6e026a955bbc9abc03c138d5843db8a0b66bb`dir.

## Sonuçlar

| Ölçüm | p50 | p95 | max | Bütçe |
|---|---:|---:|---:|---:|
| Source query | `0,921 ms` | `1,237 ms` | `1,952 ms` | indexed query `≤100 ms` |
| Category query | `0,790 ms` | `1,066 ms` | `1,132 ms` | indexed query `≤100 ms` |
| İlk 200 kanal | `6,241 ms` | `7,704 ms` | `8,322 ms` | indexed query `≤100 ms` |
| Category + offset 400 + 200 kanal | `7,357 ms` | `8,352 ms` | `8,353 ms` | indexed query `≤100 ms` |
| Exact `C49999` search | `21,121 ms` | `22,490 ms` | `23,495 ms` | indexed query `≤100 ms` |
| Adapter reopen + ilk 200 kanal | `6,601 ms` | `7,443 ms` | `7,779 ms` | cached first-visible `≤500 ms` |

**VERIFIED:** Her sample beklenen total/item count'unu doğruladı; page size production üst sınırı `200`dür. Plaintext locator canary taraması geçti. Wrapper başlangıç/son HEAD ve clean-worktree eşitliğini zorunlu tuttu. İlk koşudaki p95 `148,835 ms` bütçe ihlali, sorgunun `COLLATE NOCASE` sırasını karşılamayan ve categoriesiz sıralamada `category_id` ön eki nedeniyle kullanılamayan indekslerle ilişkilendirildi. Şema v3, `(snapshot_id, display_name COLLATE NOCASE, channel_id)` ve category eşleniğini atomik v1/v2 migration ile ekledi; yukarıdaki ölçüm bu düzeltmeden sonraki clean commit'e bağlıdır.

## Yorum sınırı

Bu component ölçümü WinUI layout/render, input-to-visible latency, realized container count, frame pacing, image fetch/decode/cache, packaged startup veya UI Automation içermez. Reopen ölçümü yeni query adapter'ı + SQLite open + ilk 200 satırdır; process cold-start değildir. Bu nedenle indexed query ve cached-data adapter bileşenleri bütçeyi karşılamıştır; M9'un frame/UIA/logo acceptance'ı açık kalır.
