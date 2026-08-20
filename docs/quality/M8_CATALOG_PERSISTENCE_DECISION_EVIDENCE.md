# M8 catalog persistence Decision kanıtı

**Durum:** `VERIFIED` local ölçüm; M8 `IN PROGRESS`

## Kanıt bağı

2026-08-21 tarihinde clean commit `196666a1cc6361616647ecc380a6331a3f36add4` üzerinde aşağıdaki opt-in komut çalıştırıldı:

```powershell
.\eng\Invoke-WindowsCatalogPersistenceDecision.ps1 -AllowDecision
```

Koşu exact SDK `10.0.302` ve runtime `10.0.10` ile tamamlandı. Sanitized summary `.artifacts/m8-catalog-performance/evidence/decision-summary.json` dosyasında `18.793` byte olarak üretildi; dosyanın SHA-256 değeri `e516ee36a2cc1e889acbf215ecaf7762591617477e05ec2e293f574867580cf2`dir. Summary clean repository binding, `DELETE` journal mode, sentetik workload, canary ve cleanup sonuçlarını taşır. Artifact repository'ye eklenmez; bu belge tekrar üretilebilir exact bağı kaydeder.

## Ölçülen sonuçlar

Her ölçekte 20 tur gerçek incremental M3U parser → normalize → AES-GCM protected locator → SQLite snapshot transaction zinciri ölçüldü.

| Kayıt | Import p95 | Allocation p95 | Working-set delta p95 | DB boyutu |
|---:|---:|---:|---:|---:|
| 5.000 | `185,344 ms` | `27,558 MiB` | `5,270 MiB` | `5.632.000` byte |
| 10.000 | `293,934 ms` | `52,798 MiB` | `1,149 MiB` | `11.120.640` byte |
| 20.000 | `801,051 ms` | `106,068 MiB` | `4,320 MiB` | `22.044.672` byte |
| 50.000 | `2.735,953 ms` | `263,579 MiB` | `7,387 MiB` | `55.136.256` byte |

20 cancellation örneğinde p95 `13,020 ms`; residual source/snapshot/category/channel/locator/favorite/sync-run satırı, WAL ve SHM sayısı `0`dır.

## Karar

`VERIFIED`: Bu commit, host ve sentetik workload sınırında 50k component import süresi birleşik normalize + protected persistence + index hedefi olan `≤3 s` altında; working-set delta `≤250 MiB` ve cancellation p95 `≤250 ms` altında kalmıştır. Önceki per-record protected-file yerleşimine göre seçilen same-SQLite-transaction yönü süre açısından uygulanabilirdir.

`VERIFIED`: 50k managed allocation p95 `263,579 MiB` olup bütün-import `≤150 MiB` hedefini karşılamamıştır.

`INFERENCE`: Süre, working-set ve cancellation gözlemleri M8 yönünü destekler; allocation hedefi karşılanmadığı için M8 acceptance kapanmaz. Hedef sonuçtan sonra gevşetilmez. Kalan iş allocation azaltma veya ayrıca incelenmiş bir budget kararı, crash/fault/migration kanıtı ve diğer M8 acceptance maddeleridir.

Runner performans threshold'u uygulamaz; sıfır exit yalnız workload, invariant, canary, cleanup ve evidence üretiminin geçtiğini gösterir. Ölçüm sentetik local host kapsamındadır; UI, network, reference-device thermal/power davranışı veya gerçek kullanıcı verisi kanıtı değildir.
