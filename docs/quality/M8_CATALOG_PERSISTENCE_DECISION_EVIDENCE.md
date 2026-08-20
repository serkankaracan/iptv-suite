# M8 catalog persistence Decision kanıtı

**Durum:** `VERIFIED` local ölçüm; performance gate `CLOSED`; M8 daha sonra `COMPLETED`

## Kanıt bağı

2026-08-21 tarihinde güncel production şekli için clean commit `3f547a7ad625f04f929ade96ba0e2f4660ced24c` üzerinde aşağıdaki opt-in komut çalıştırıldı:

```powershell
.\eng\Invoke-WindowsCatalogPersistenceDecision.ps1 -AllowDecision
```

Koşu exact SDK `10.0.302` ve runtime `10.0.10` ile tamamlandı. Sanitized summary `.artifacts/m8-catalog-performance/evidence/decision-summary.json` dosyasında `40.929` byte olarak üretildi; dosyanın SHA-256 değeri `c65320b7e2a89fd640fa3bcd704aff17b39cdcbdca9071daf989d334ff878109`dur. Summary clean repository binding, `DELETE` journal mode, sentetik workload, canary, cleanup ve sink allocation attribution sonuçlarını taşır. Artifact repository'ye eklenmez; bu belge tekrar üretilebilir exact bağı kaydeder.

## Ölçülen sonuçlar

Her ölçekte 20 tur gerçek incremental M3U parser → normalize → AES-GCM protected locator → SQLite snapshot transaction zinciri ölçüldü.

| Kayıt | Import p95 | Allocation p95 | Working-set delta p95 | DB boyutu |
|---:|---:|---:|---:|---:|
| 5.000 | `141,540 ms` | `12,221 MiB` | `4,266 MiB` | `5.648.384` byte |
| 10.000 | `276,889 ms` | `23,349 MiB` | `4,434 MiB` | `11.116.544` byte |
| 20.000 | `810,005 ms` | `47,169 MiB` | `4,059 MiB` | `22.048.768` byte |
| 50.000 | `2.738,759 ms` | `116,330 MiB` | `6,164 MiB` | `55.148.544` byte |

20 cancellation örneğinde p95 `12,390 ms`; residual source/snapshot/category/channel/locator/favorite/sync-run satırı, WAL ve SHM sayısı `0`dır.

## Karar

`VERIFIED`: Bu commit, host ve sentetik workload sınırında 50k component import süresini `2.738,759 ms` ölçmüştür; birleşik normalize + protected persistence + index hedefi `≤3 s`, managed allocation `≤150 MiB`, working-set delta `≤250 MiB` ve cancellation p95 `≤250 ms` sınırlarının tamamı karşılanmıştır.

`VERIFIED`: 50k managed allocation p95 `116,330 MiB`dir. Attribution p95 değerlerinde bütün sink write katmanı `57,117 MiB`; preparation `36,234 MiB`, encrypted locator `19,355 MiB`, channel insert `0,006 MiB` ve hash yaklaşık `0 MiB`dir. Fixed SQL statement'ların aynı connection/transaction üzerindeki native prepared binding'e geçirilmesi, provider `ExecuteNonQuery` per-row allocation'ını kaldırmıştır.

`INFERENCE`: Same-SQLite-transaction yönünün component performans gate'i kapanmıştır. Bu ölçüm milestone'u tek başına tamamlamamıştır; takip process-crash ve lifecycle acceptance kanıtları [M8 completion evidence](M8_COMPLETION_EVIDENCE.md) ile daha sonra tamamlanmıştır.

Runner performans threshold'u uygulamaz; sıfır exit yalnız workload, invariant, canary, cleanup ve evidence üretiminin geçtiğini gösterir. Ölçüm sentetik local host kapsamındadır; UI, network, reference-device thermal/power davranışı veya gerçek kullanıcı verisi kanıtı değildir.
