# M9 katalog UI completion kanıtı

**Durum:** `COMPLETED, 2026-08-21`

## Kabul edilen production sınırı

M9, player eklemeden device-local SQLite katalog üzerinde bounded source/category/search/page sorgusunu ve virtualized WinUI kanal tarayıcısını kapatır. Production sorgusu en çok 200 kayıt döndürür; arama 250 ms debounce ve generation-bound stale-result suppression kullanır. Logo provider yalnız active snapshot'a bağlı encrypted locator tuple'ını çözer; aynı HTTPS origin, 512 KiB response, bounded signature, dört eşzamanlı fetch, 128-entry memory cache ve page cancellation sınırlarını korur.

## Local component kanıtı

- Clean commit `d3ebbdcf24bb842bbab8f39c3abbed47a085db65` üzerindeki 5k/10k/20k/50k ×20 query Decision koşusunda 50k first-page/category/search p95 sırasıyla `7,704`/`8,352`/`22,490 ms`, adapter reopen+first-page p95 `7,443 ms` ölçülmüştür. Query `≤100 ms` ve cached component `≤500 ms` bütçeleri karşılanmıştır. Exact ölçüm ve kapsam sınırı [M9 query Decision evidence](M9_CATALOG_QUERY_DECISION_EVIDENCE.md) belgesindedir.
- Exact SDK `10.0.302` local gate architecture `24`, unit `162`, integration `106` olmak üzere `292/292 ×2` test; Debug/Release x64 `0` warning/`0` error; deterministic fixture, armed/disarmed sentinel, scanner CLI ve artifact canary kontrollerini geçmiştir.

## Hosted packaged acceptance

GitHub Actions run `32443355378`, commit `2ceaffddb8752dd2163a007d8ea458aaea70cc74` için dört zorunlu işi geçmiştir: locked build/test, packaged install/launch/lifecycle, gerçek-user DPAPI boundary ve required coordinator. Package işi `5m31s`, toplam run yaklaşık `10m` sürmüştür.

İndirilen `windows-msix-smoke-evidence/last-success.json` 1.327 byte ve SHA-256 `371fab2380a7c07ce06233da457685d967a2f0427ec1e5f896b5e71e30c9975c` değerindedir. Evidence exact commit ve SDK'ya bağlıdır; signature valid, payload leak gate ve protected-store initialization geçmiştir. M9 alanları:

| Kontrol | Hosted sonuç | Bütçe |
|---|---:|---:|
| UIA name/role contract | `true` | zorunlu |
| Source→category→search Tab sırası | `true` | zorunlu |
| Sentetik katalog | `50.000` doğrulandı | `50.000` |
| Realized automation item | `8` | `≤300` |
| Input-call p95 | `16,284 ms` | `≤100 ms` |
| DWM compositor frame p95 | `31,25 ms` | `≤33,3 ms` |
| DWM maximum interval | `171,875 ms` | `≤200 ms` |
| DWM late/drop proxy | `%0` | `<%1` |
| Frame interval sample | `404` | `>30` |
| Normal close / package removal | `true` / `true` | zorunlu |

DWM ölçümü Windows 8.1+ için zorunlu NULL-HWND çağrısında 1-byte-packed 292-byte native ABI ile `qpcVBlank`, refresh ve late-frame sayaçlarını kullanır. Bu sistem-compositor proxy'sidir; app-specific ETW/UI-thread attribution veya bütün cihaz/GPU matrisi değildir.

## Kapsam sınırı

Bu kabul yalnız GitHub-hosted Windows runner ve belirtilen commit için data-bearing packaged UIA/virtualization/input/compositor davranışını kanıtlar. Narrator exploratory conformance, genel accessibility sertifikasyonu, multi-DPI/multi-monitor/device matrix, gerçek provider/internet, playback/codec/HW decode, WACK/Store veya ARM64 sonucu değildir. Bunlar sonraki milestone ve dış doğrulama kapılarında kalır.
