# M8 catalog persistence Decision kanıtı

**Durum:** `VERIFIED` local ölçüm; M8 `IN PROGRESS`

## Kanıt bağı

2026-08-21 tarihinde güncel production şekli için clean commit `c1752a4d86d32af4eaae875ae394a17e957c4c66` üzerinde aşağıdaki opt-in komut çalıştırıldı:

```powershell
.\eng\Invoke-WindowsCatalogPersistenceDecision.ps1 -AllowDecision
```

Koşu exact SDK `10.0.302` ve runtime `10.0.10` ile tamamlandı. Sanitized summary `.artifacts/m8-catalog-performance/evidence/decision-summary.json` dosyasında `18.798` byte olarak üretildi; dosyanın SHA-256 değeri `50690c72a5aec12e287922397347ed6ba47cfa5b731ea3d42bda7ff9b271d27f`dir. Summary clean repository binding, `DELETE` journal mode, sentetik workload, canary ve cleanup sonuçlarını taşır. Artifact repository'ye eklenmez; bu belge tekrar üretilebilir exact bağı kaydeder.

## Ölçülen sonuçlar

Her ölçekte 20 tur gerçek incremental M3U parser → normalize → AES-GCM protected locator → SQLite snapshot transaction zinciri ölçüldü.

| Kayıt | Import p95 | Allocation p95 | Working-set delta p95 | DB boyutu |
|---:|---:|---:|---:|---:|
| 5.000 | `194,634 ms` | `24,740 MiB` | `5,317 MiB` | `5.640.192` byte |
| 10.000 | `369,556 ms` | `47,461 MiB` | `1,301 MiB` | `11.120.640` byte |
| 20.000 | `890,747 ms` | `95,392 MiB` | `1,613 MiB` | `22.056.960` byte |
| 50.000 | `3.052,637 ms` | `236,880 MiB` | `4,512 MiB` | `55.103.488` byte |

20 cancellation örneğinde p95 `16,285 ms`; residual source/snapshot/category/channel/locator/favorite/sync-run satırı, WAL ve SHM sayısı `0`dır.

## Karar

`VERIFIED`: Bu commit, host ve sentetik workload sınırında 50k component import süresini `3.052,637 ms` ölçmüştür; bu değer birleşik normalize + protected persistence + index hedefi olan `≤3 s` sınırını az farkla aşar. Working-set delta `≤250 MiB` ve cancellation p95 `≤250 ms` altında kalmıştır.

`VERIFIED`: 50k managed allocation p95 `236,880 MiB` olup bütün-import `≤150 MiB` hedefini karşılamamıştır. Identifier-binding reuse öncesindeki clean ölçüme göre allocation azalmıştır; bu iyileşme acceptance için yeterli değildir.

`INFERENCE`: Same-SQLite-transaction yönü önceki per-record protected-file yerleşimine göre güçlü kalır; ancak güncel duration ve allocation hedefleri karşılanmadığı için M8 acceptance kapanmaz. Hedefler sonuçtan sonra gevşetilmez. Kalan iş hot-path duration/allocation azaltma veya ayrıca incelenmiş bir budget kararı, crash kanıtı ve diğer M8 acceptance maddeleridir.

Runner performans threshold'u uygulamaz; sıfır exit yalnız workload, invariant, canary, cleanup ve evidence üretiminin geçtiğini gösterir. Ölçüm sentetik local host kapsamındadır; UI, network, reference-device thermal/power davranışı veya gerçek kullanıcı verisi kanıtı değildir.
