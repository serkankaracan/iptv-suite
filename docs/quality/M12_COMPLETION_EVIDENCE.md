# M12 playback UX ve lifecycle kısmi kabul kanıtı

**Durum:** `PARTIAL VERIFIED, 2026-08-24`

## Doğrulanan sınır

M12'nin otomatikleştirilebilen production-package dilimi; app-owned playback kontrollerini, fullscreen enter/exit ve focus dönüşünü, iki pencere resize'ını, minimize/restore sırasında kanal ve playback state'inin korunmasını, 25 hızlı kanal geçişini ve aktif playback sırasında normal window-close teardown'unu kapsar. Application katmanındaki suspend policy yalnız `SystemSuspendStatus.Entering` olayında playback'i durdurur; resume otomatik playback başlatmaz. Exact source için release contract'ı replacement session'ı durdurmadan mevcut session'ı drain eder [S131]. Windows-native player sistemin varsayılan audio render endpoint'ini kullanır; gerçek endpoint değişimi aşağıdaki fiziksel matriste açık kalır [S132].

## Commit-bound hosted kanıt

- [GitHub Actions run `32764149667`](https://github.com/serkankaracan/iptv-suite/actions/runs/32764149667), commit `8d537010f64193f7bc9baf05bc3707fb31d5362f` için bütün zorunlu işleri ve Required Windows gate'i geçti.
- İndirilen `windows-msix-smoke-evidence/last-success.json`, exact commit ve SDK `10.0.302` bağını; valid x64 package signature'ını, payload leak gate'ini, normal close'u ve exact package cleanup'ını doğrular.
- `VERIFIED`: Bütün UI/control, fullscreen, resize, minimize/restore, state-preservation, resource-snapshot ve active-close Boolean alanları `true`dur.

| Otomatik acceptance alanı | Sonuç |
|---|---:|
| Rapid switch | `25`; p95 `2231,596 ms`; maksimum `2232,186 ms` |
| Fullscreen enter / exit / focus restore | `true / true / true` |
| Surface bounds / resize | `true / true`; `2` resize |
| Minimize / restore / state preservation | `true / true / true` |
| Volume / mute / aspect | `true / true / true` |
| Resource snapshot / active close | `true / true` |
| UI request / completed response | `79 / 79`; `52.052.020` byte |
| Normal close / package removed | `true / true` |

## Resource kalibrasyonu

| Sayaç | Baseline | Final | Delta |
|---|---:|---:|---:|
| Private bytes | `76.722.176` | `73.543.680` | `-3.178.496` |
| Working set | `190.877.696` | `198.156.288` | `+7.278.592` |
| Handle | `1.851` | `1.905` | `+54` |
| Thread | `51` | `49` | `-2` |

`VERIFIED`: Sayaçlar warmed playback sonrasındaki baseline ile 25 rapid switch ve explicit stop sonrasındaki final snapshot'tan alınmıştır; snapshot zinciri ve active-close doğrulaması geçmiştir.

`INFERENCE`: Bu tek hosted ölçüm kısa-run davranışı için kalibrasyondur. Private-bytes düşüşü, gözlenen working-set/handle farkı ve thread düşüşü bu koşuda belirgin bir runaway kaynak artışı göstermemiştir; ancak tek örnekten sayısal bir M12 bütçesi veya leak-yokluğu sonucu türetilemez. Bu nedenle mevcut acceptance threshold'ları değiştirilmemiş, yeni threshold eklenmemiş ve resource-budget kriteri henüz `PASS` sayılmamıştır.

## Açık acceptance matrisi

Aşağıdaki maddeler `NOT RUN` durumundadır ve M12'yi `COMPLETED` yazmayı engeller:

- fiziksel Windows Client'ta `%100 / %150 / %200` DPI ve mixed-DPI multi-monitor geçişleri;
- gerçek sleep/Modern Standby → wake davranışı ve kullanıcı intent'i olmadan autoplay yapılmaması;
- iki gerçek audio endpoint arasında default-output değişimi;
- Narrator ile exploratory accessibility/keyboard/focus akışı;
- product-level source delete entry point'i üzerinden stop/drain/delete uçtan uca zinciri.

Source-release coordinator contract'ının unit/race testleri product-level delete orchestration kanıtı değildir. Doğrudan SQLite/DPAPI kaydı silmek kabul testi yerine kullanılamaz. M13 reconnect kapsamı, M10'un koşullu relative-memory sapması, WACK/Store, gerçek cihaz/HW-decode ve M16 final soak ayrı kapılarda kalır.

## Kaynaklar

[S131–S132](../research/SOURCES.md)
