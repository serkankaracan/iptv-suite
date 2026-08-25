# M12 playback UX ve lifecycle kısmi kabul kanıtı

**Durum:** `PARTIAL VERIFIED, 2026-08-25`

## Doğrulanan sınır

M12'nin otomatikleştirilebilen production-package dilimi; app-owned playback kontrollerini, fullscreen enter/exit ve focus dönüşünü, iki pencere resize'ını, minimize/restore sırasında kanal ve playback state'inin korunmasını, 25 hızlı kanal geçişini, aktif playback sırasında normal window-close teardown'unu ve protected source-delete uçtan uca zincirini kapsar. Application katmanındaki suspend policy yalnız `SystemSuspendStatus.Entering` olayında playback'i durdurur; resume otomatik playback başlatmaz. Exact source için release contract'ı replacement session'ı durdurmadan mevcut session'ı drain eder [S131]. Windows-native player sistemin varsayılan audio render endpoint'ini kullanır; gerçek endpoint değişimi aşağıdaki fiziksel matriste açık kalır [S132].

## Commit-bound hosted kanıt

- [GitHub Actions run `32764149667`](https://github.com/serkankaracan/iptv-suite/actions/runs/32764149667), commit `8d537010f64193f7bc9baf05bc3707fb31d5362f` için bütün zorunlu işleri ve Required Windows gate'i geçti.
- İndirilen `windows-msix-smoke-evidence/last-success.json`, exact commit ve SDK `10.0.302` bağını; valid x64 package signature'ını, payload leak gate'ini, normal close'u ve exact package cleanup'ını doğrular.
- `VERIFIED`: Bütün UI/control, fullscreen, resize, minimize/restore, state-preservation, resource-snapshot ve active-close Boolean alanları `true`dur.
- [GitHub Actions run `32783701519`](https://github.com/serkankaracan/iptv-suite/actions/runs/32783701519), commit `b10b6ceb56eaba51668169dca904524808f4517b` için locked quality, signed package ve DPAPI boundary işlerini geçti. Bu checkpoint product source-delete UI route'unu build/architecture düzeyinde korur; product-level delete E2E kanıtı değildir.
- [GitHub Actions run `32785306860`](https://github.com/serkankaracan/iptv-suite/actions/runs/32785306860), commit `33afcef741f518b439c9934b0f4035fd92662586` için yeni signed short-run resource guard'ını ve bütün required işleri geçti. İndirilen evidence `PlaybackResourceBudgetVerified=true` taşır.
- [GitHub Actions run `32792088083`](https://github.com/serkankaracan/iptv-suite/actions/runs/32792088083), commit `4e96b27e1397c10df82b63531a3c3f227fe0f774` için locked quality, signed package, gerçek-user DPAPI boundary ve Required Windows gate işlerini geçti. İndirilen `3.522` byte MSIX evidence SHA-256 `0e07d37d388439bcbdd7d506c7b8d37a8b28e5e1b3f718dccc132aa212be8053` ve exact commit/SDK bağını taşır.
- [GitHub Actions run `32796910469`](https://github.com/serkankaracan/iptv-suite/actions/runs/32796910469), commit `45124f72cc62b21942790c39bd032242706af339` için locked quality, signed package, gerçek-user DPAPI boundary ve Required Windows gate işlerini geçti. İndirilen `3.973` byte MSIX evidence SHA-256 `882f42b5c649358cbb700459f012b3df694cf5da9fb298ec7ca313ec135ed618`, exact commit/SDK bağını ve pending-failure/restart/manual-retry source-delete sonuçlarını taşır.

| Otomatik acceptance alanı | Sonuç |
|---|---:|
| Rapid switch | `25`; p95 `2228,312 ms`; maksimum `2244,541 ms` |
| Fullscreen enter / exit / focus restore | `true / true / true` |
| Surface bounds / resize | `true / true`; `2` resize |
| Minimize / restore / state preservation | `true / true / true` |
| Volume / mute / aspect | `true / true / true` |
| Resource snapshot / active close | `true / true` |
| UI request / completed response | `88 / 88`; `57.987.980` byte |
| Normal close / package removed | `true / true` |

## Protected source-delete kabulü

`VERIFIED — run 32796910469`: Signed production package'ta 50k kayıtlı sibling source korunurken iki Tier-A kanallı protected hedef source için cancel ve dialog-close yolları read-only oracle ile mutasyonsuz kaldı. Controller'ın explicit arm işaretinden sonra harness exact configuration record üzerinde yalnız delete'i engelleyen bounded lease açtı. Confirm yolu aktif playback'i durdurdu, source'u durable `DeletionPending` durumuna aldı ve yeni admission'ı kapattı. Relaunch pending source'u yeniden kabul etmedi; read-only oracle exact hedef katalog grafiğini, configuration record'u, incomplete tombstone binding'ini ve sibling katalogu korundu olarak doğruladı. Fault bırakıldıktan sonra yalnız production manual-retry entry point'i kullanıldı. Son relaunch hedefi kabul etmedi; final read-only katalog/DPAPI oracle'ları hedef source, snapshot, snapshot key, category, channel, protected locator, favorite ve sync-run kayıtlarının yokluğunu, completed tombstone binding'ini ve sibling katalogdaki exact 50k kaydın korunduğunu doğruladı.

| Source-delete alanı | Sonuç |
|---|---:|
| Cancel no-mutation / dialog-close no-mutation | `true / true` |
| Pending failure / restart admission blocked | `true / true` |
| Pending catalog / configuration record preserved | `true / true` |
| Pending tombstone binding / sibling retained | `true / true` |
| Fault released / manual retry | `true / true` |
| Active playback drain / restart non-admission | `true / true` |
| Target catalog / protected records deleted | `true / true` |
| Tombstone binding completed / sibling catalog retained | `true / true` |

Bu kanıt test-only doğrudan kayıt silme değildir: kullanıcıya görünen production source-delete entry point'ini, gerçek packaged UIA akışını, production SQLite coordinator'ını ve DPAPI `CurrentUser` store'unu birlikte kullanır. Assertion, security boundary veya cleanup kapsamı gevşetilmemiştir.

## Resource kalibrasyonu

| Sayaç | Baseline | Final | Delta |
|---|---:|---:|---:|
| Private bytes | `76.849.152` | `74.043.392` | `-2.805.760` |
| Working set | `193.007.616` | `199.069.696` | `+6.062.080` |
| Handle | `1.943` | `1.934` | `-9` |
| Thread | `54` | `48` | `-6` |

`VERIFIED`: Sayaçlar warmed playback sonrasındaki baseline ile 25 rapid switch ve explicit stop sonrasındaki final snapshot'tan alınmıştır; snapshot zinciri ve active-close doğrulaması geçmiştir.

`INFERENCE`: Bu tablo tek hosted ölçümdür ve tek başına leak-yokluğu sonucu üretmez. Takip eden temiz hosted cohort'un gözlenen dağılımı yalnız kısa-run regresyon zarfını kalibre etmek için kullanılmıştır.

`VERIFIED — run 32785306860`: Packaged smoke, warmed baseline'dan explicit stop sonrası final snapshot'a kadar signed delta için private bytes `≤ +8 MiB`, working set `≤ +16 MiB`, handle `≤ +64` ve thread `≤ 0` üst sınırlarını fail-closed uyguladı. Ölçülen değerler sırasıyla `-860.160 byte`, `+4.169.728 byte`, `-30` ve `-7`; 25/25 rapid switch p95 `2229,342 ms`, maksimum `2230,524 ms` oldu. Negatif delta büyüme sayılmaz. Bu ayrı kısa-run guard, M10/M16 uzun-soak `%10 / 100 MiB / monotonic` kriterlerini değiştirmez ve leak-yokluğu ya da M12 completion kanıtı değildir.

## Açık acceptance matrisi

Aşağıdaki maddeler `NOT RUN` durumundadır ve M12'yi `COMPLETED` yazmayı engeller:

- fiziksel Windows Client'ta `%100 / %150 / %200` DPI ve mixed-DPI multi-monitor geçişleri;
- gerçek sleep/Modern Standby → wake davranışı ve kullanıcı intent'i olmadan autoplay yapılmaması;
- iki gerçek audio endpoint arasında default-output değişimi;
- Narrator ile exploratory accessibility/keyboard/focus akışı.

Source-release coordinator contract'ının unit/race testleri tek başına product-level delete orchestration kanıtı değildir; run `32796910469` success ve injected pending-failure/manual-retry yollarını production entry point üzerinden kapatır. Doğrudan SQLite/DPAPI kaydı silmek kabul testi yerine kullanılamaz. M13 reconnect kapsamı, M10'un koşullu relative-memory sapması, WACK/Store, gerçek cihaz/HW-decode ve M16 final soak ayrı kapılarda kalır.

## Kaynaklar

[S131–S132](../research/SOURCES.md)
