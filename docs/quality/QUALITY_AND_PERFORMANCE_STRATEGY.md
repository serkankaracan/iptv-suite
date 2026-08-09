# Kalite ve performans stratejisi

**Tarih:** 2026-08-09

**Durum:** Phase 0 test/ölçüm sözleşmesi; bütçeler M14 kanıtıyla kalibre edilir

## 1. İlke

Bu ürün için genel bir “test pyramid yüzdesi” kullanılmaz. Riskler farklı kanıt ister: parser pure/deterministik test, HTTP local fixture server, storage gerçek Windows protection, player gerçek native engine ve codec corpus, mağaza ise packaged clean-machine testi gerektirir. Her test katmanı belirli bir failure mode'u kapatır.

Test verisi:

- sentetik, yeniden üretilebilir ve repository'de provenance/license kaydı olan fixture;
- yalnız sahte credential ve geliştiriciye ait endpoint;
- gerçek müşteri account'u, yakalanmış gerçek provider response'u veya yetkisiz yayın yok;
- deterministic seed, generator version ve expected-result manifesti;
- credential canary'leri açıkça test secret'ı olarak işaretli ve hiçbir production artifact'ına kopyalanmaz.

## 2. Somut test katmanları

| Katman | Ne kanıtlar | Araç/ortam sınırı | Ne kanıtlamaz |
|---|---|---|---|
| Specification/golden | Aynı M3U, terminology, normalization ve error contract'ı platformlarda aynı davranır | JSON Schema + golden input/output | OS/player davranışı |
| Domain/unit | Value object, validation, stable key, mapping, state transition | Process içi, no network/disk/UI | Integration/lifecycle |
| Component/integration | Parser stream, SQLite, migration, protected store, image cache | Temp app data + real/fake Windows API | Store/driver çeşitliliği |
| HTTP/provider contract | Endpoint, auth, redirects, timeout, response variants | Local fake HTTP(S) server + deterministic clock | Gerçek provider evreni |
| UI/accessibility | View-model, virtualization, keyboard/focus/UIA | Packaged WinUI test host | Codec doğruluğu |
| Performance/regression | 5k–50k import/query/memory/UI bütçesi | Release build, referans cihaz | Heterojen user network |
| Player adapter/compatibility | Engine state, codec/container, HW fallback, surface/lifecycle | Sentetik streams + real engine/device | Bütün dünya stream garantisi |
| Package/Store/manual | MSIX, architecture, update/uninstall, WACK, reviewer akışı | Clean VM/device + private submission | Kamuya açık mağaza kabul garantisi |
| Soak/fault | Leak, deadlock, reconnect storm, uzun yayın | 2/8/24 saat ve fault injection | Bilinmeyen her upstream bug |

Her production defect, uygun katmanda önce sentetik ve secret-free regression fixture'a dönüştürülür. Gerçek payload ancak hak/mahremiyet kontrolünden sonra tamamen sanitize ve küçültülmüş biçimde kullanılabilir.

## 3. Feature test planı

### 3.1 Validation ve endpoint construction

Table-driven testler:

- blank/whitespace, Unicode/IDNA host, IPv4/IPv6, default/non-default port;
- `http/https` dışı scheme, user-info, fragment, query credential;
- aşırı uzun name/URL/field, control/NUL, mixed normalization;
- trailing slash ve provider endpoint join; path traversal veya double-encoding;
- `.m3u8` uzantısının tek başına catalog/HLS ayrımı yapmaması;
- redirect target scheme/origin policy;
- error result'in raw input/credential taşımaması.

URL normalization stream'i bozacak biçimde query ordering/value değiştirmemeli; display-safe endpoint ayrı, raw protected locator ayrı olmalıdır.

### 3.2 M3U incremental parser

Golden corpus en az:

- empty file, yalnız `#EXTM3U`, BOM/no BOM, CRLF/LF/CR;
- UTF-8/Unicode NFC-NFD, kontrollü legacy fallback ve invalid byte;
- quoted/unquoted/mixed-case attributes; escaped quote; extra whitespace;
- `tvg-id`, `tvg-name`, `tvg-logo`, `group-title`, channel-number hints;
- unknown directive/attribute;
- orphan `#EXTINF`, orphan locator, missing name/URL, comments/blank lines;
- relative URL, query token, user-info, invalid/unsafe scheme;
- duplicate exact entry, same name/different locator, conflicting provider ID;
- HLS master/media manifest'in channel catalog yerine single-stream source olarak sınıflandırılması;
- oversized line/attribute/value/item count; truncated input;
- cancellation her pipeline stage'inde;
- 5k/10k/20k/50k generator outputs.

Property/fuzz invariants:

- parser crash/hang etmez ve budget üstünde allocation yapmaz;
- accepted entry sayısı + rejected entry sayısı input assembly sonucuyla tutarlıdır;
- secret locator output/log string'e çıkmaz;
- aynı input + version aynı normalized result/stable key üretir;
- cancellation active snapshot'ı değiştirmez.

### 3.3 Xtream-compatible API

Local fake server fixture'ları:

- successful auth/account info, live categories ve live streams;
- 200 içinde “auth failed”, 401/403, 404, 429 + Retry-After, 5xx;
- empty/missing/null field; bool/int/string dönüşümleri; unknown extra fields;
- duplicate/missing category ve stream IDs; unusual stream extension;
- malformed/truncated JSON, excessive depth/array/string, gzip;
- slow headers/body, mid-body disconnect, timeout/cancellation;
- same-origin/cross-origin redirect, HTTPS downgrade ve cookie attempt;
- large Live-only catalog; VOD/Series çağrılmadığının request assertion'ı.

Endpoint builder ayrı pure tests alır. Provider DTO'su domain/persistence'e sızamaz. Contract corpus sürümlenir; gerçek provider adı taşımaz.

### 3.4 Normalization ve persistence

- text normalization/control removal/length; localized “Uncategorized” display key;
- stable-key priority, collision discriminator ve version migration;
- exact duplicate collapse, conflicting ID retain+warning;
- category/channel foreign keys ve indexes;
- fresh DB, N-1→N migration, failure rollback, corruption/recovery;
- staging + atomic activation, concurrent read/refresh, app termination fault injection;
- content hash/parser/schema version cache invalidation;
- source deletion, protected-store failure, `DeletionPending` ve startup orphan reconciliation;
- DB/WAL/SHM/log içinde canary secret binary scan.

SQL tests gerçek SQLite ile çalışır; yalnız mocked repository “DB doğru” kanıtı değildir.

### 3.5 Secure storage ve redaction

Ortak secret-store contract:

- create/read/update/delete/idempotency;
- missing/corrupt/wrong-user/wrong-context;
- restart, packaged update, reset, uninstall/reinstall;
- deletion crash ve orphan recovery;
- 5k–50k locator throughput/cancellation.

Redaction corpus:

- URI user-info;
- case/encoding varyantlı password, username, token, auth, key, signature query;
- nested/percent-encoded URL;
- Authorization/Proxy-Authorization/Cookie/Set-Cookie/custom API key;
- provider body/exception içinde echo edilen secret;
- JSON, structured scope, interpolated string ve native player log;
- Unicode homoglyph testleri tanılama için; allowlist güvenlik kontrolü yerine kullanılmaz.

Canary secret; app data, DB/WAL, logs, screenshots metadata, crash/support artifact ve MSIX content üzerinde aranır. Beklenen plaintext yalnız test process'inin kontrollü memory/input boundary'sidir.

### 3.6 HTTP, timeout, cancellation ve retry

Fake server/network fault:

- DNS/connect/TLS/header/body timeout ayrımı;
- user cancellation'ın bütün read/parse/write/retry zincirini kesmesi;
- redirect loop/max hop/origin değişimi/downgrade;
- 408/429/selected 5xx bounded retry; `Retry-After` cap;
- auth/TLS/parse error'da retry olmaması;
- disconnect/stall/partial body/decompression bomb/oversize;
- cookies default disabled ve cross-source isolation;
- simultaneous source refresh ve app shutdown.

Deterministic fake clock backoff testini hızlı yapar; en az bir packaged end-to-end test gerçek timer/cancellation davranışını doğrular.

### 3.7 Image pipeline

- scheme/address/redirect/DNS rebinding policy;
- credential/cookie/referer gönderilmemesi;
- MIME/magic mismatch, oversized bytes, excessive dimensions/pixels ve malformed image;
- concurrency cap, priority, visible-item cancellation ve rapid scroll;
- LRU eviction, cache version/corruption, source delete;
- placeholder ve decode error'ın UI virtualization'ı bozmaması.

### 3.8 Player adapter

Aynı contract suite fake ve gerçek adapter'a uygulanır:

- legal state transitions, tek active session;
- open/play/pause/stop/volume/mute/track/aspect;
- open→cancel, rapid open A→B, stop/dispose callback race;
- stale session event'inin yeni session'ı değiştirmemesi;
- engine error'ın stable `DomainError`'a mapping'i;
- native event thread'inden UI dispatcher sınırı;
- repeated dispose ve app/window lifecycle;
- raw URI/header/native log redaction.

Unit fake player codec desteğini kanıtlamaz; compatibility corpus gerçek engine/device ister.

## 4. Büyük playlist sentetik corpus'u

### 4.1 Boyutlar

| Corpus | Channel | Category | Logo oranı | Amaç |
|---|---:|---:|---:|---|
| Small | 100 | 10 | %50 | Developer smoke |
| Medium | 5.000 | 100 | %70 | Normal regression |
| Large | 10.000 | 200 | %80 | Scale trend |
| Very large | 20.000 | 300 | %90 | Memory/DB pressure |
| MVP gate | 50.000 | 500 | %100 reference, fetch edilmez | Acceptance |
| Stress | 100.000 | 1.000 | %100 | Limit davranışı; release gate değil |

Her boyutta duplicate, missing group, malformed entry, Unicode, long text, token-bearing locator, invalid logo ve stable-key collision varyantı bulunur. Generator seed ve version report'a kaydedilir. Logo URL sayısı, 50.000 image'ın aynı anda indirileceği anlamına gelmez.

### 4.2 Ölçüm aşamaları

Zamanlar ayrı ölçülür:

1. DNS/connect/TTFB/download bytes.
2. Encoding + line tokenization.
3. Directive parse + validation.
4. Normalize + stable key.
5. Secret protection.
6. SQLite staging/index + activation.
7. Cached DB open/first query.
8. UI realization/search/filter/scroll.
9. Image fetch/decode/cache.

Network total'i parse/import budget'ına eklenmez. Her stage wall-clock, CPU, allocation, peak working set, bytes read/written, DB size ve cancellation latency raporlar.

## 5. Provisional performans bütçeleri

Referans: Release x64, telemetry/profiler instrumentation'ın ölçümü bozmadığı mod, güncel servisli Windows 11, 4 logical core, 8 GiB RAM, SSD ve entegre GPU sınıfı clean cihaz. Commit, OS build, .NET/WASDK, DB schema, player, GPU/driver ve power mode rapora yazılır.

| Alan | Bütçe / kabul |
|---|---|
| 50k local bytes parse | p95 ≤ 2,0 s |
| 50k normalize + protected persistence + index | p95 ≤ 3,0 s |
| 50k end-to-end parse+normalize+persist | p95 ≤ 5,0 s; download hariç |
| Import managed allocation | ≤ 150 MiB |
| Import peak working-set delta | ≤ 250 MiB; player kapalı |
| Parser/import cancellation observed | p95 ≤ 250 ms |
| Cold shell interactive | p95 ≤ 2,0 s |
| Cached DB'den first visible content | p95 ≤ 500 ms |
| Indexed search/filter/category query | p95 ≤ 100 ms |
| Input-to-visible response | p95 ≤ 100 ms |
| Scripted scroll frame | p95 ≤ 33,3 ms; dropped frame < %1 |
| UI thread stall | >200 ms event sıfır; test trace ile |
| Realized channel containers | ≤ 300, viewport'a bağlı assertion |
| Player kapalı steady catalog working set | ≤ 350 MiB |
| Image network concurrency | 4 |
| Image memory/disk budget | 32 MiB / 200 MiB LRU |
| Tier A playback start, controlled LAN | p95 ≤ 3,0 s |
| Channel switch, controlled LAN | p95 ≤ 3,0 s |
| User retry/reconnect cancel | ≤ 1,0 s; yeni session açılmaz |
| M10 soak | 8 saat, 100 switch, crash/deadlock sıfır |
| M16 RC soak | 24 saat, 200 switch, crash/hang sıfır |
| Soak memory | 30 dk warm-up sonrası monoton trend yok; net büyüme hem ≤%10 hem ≤100 MiB |

Bunlar garanti değil, tasarım bütçesidir. İlk ölçüm bütçeyi geçerse sayı sessizce gevşetilmez: trace ile bottleneck, cihaz temsiliyeti ve kullanıcı etkisi raporlanır; optimize edilir veya Product/Architecture yazılı değişiklik yapar.

### 5.1 Benchmark yöntemi

- Clean build + Release artifact; debug/JIT warm-up etkisi ayrı.
- Cold ölçümde process ve OS-cache koşulu raporlanır; warm ölçüm ayrıca.
- En az 20 iteration; median, p90, p95, max ve coefficient of variation.
- Outlier silinmez; açıklanır. Background process/power/thermal koşulu kaydedilir.
- Baseline artifact saklanır; CI smoke threshold daha gevşek olabilir, M14 dedicated runner authoritative'dir.
- Regression gate: aynı cihaz/baseline'a göre p95'te >%10 ve mutlak kullanıcı bütçesi ihlali review gerektirir.
- ETW/.NET counters/profiler trace yalnız ihlali açıklamak için; benchmark sonucu instrumentation'sız tekrar doğrulanır.

## 6. Playback compatibility matrix

### 6.1 Tier'ler

| Tier | Protocol/container | Codec/track | Beklenti |
|---|---|---|---|
| A — MVP hard gate | HLS + MPEG-TS; direct HTTPS continuous MPEG-TS | H.264 High + AAC-LC/HE-AAC, stereo; basic aspect | Referans matrix'te %100 |
| B — Hedef capability | HLS fMP4/CMAF; HLS/TS | HEVC Main/Main10, AC-3/E-AC-3, multi-audio, subtitle/CC | Sonuç capability olarak yayımlanır; legal + device gate |
| C — Robustness | Discontinuity, timestamp reset, variant switch, corrupt/missing segment, EOF | Mixed tracks/metadata | Graceful error/recovery; crash/hang yok |
| Out | DRM, recording, timeshift, unauthorized streams | — | Test edilmez/vaat edilmez |

Bir format “pass” olsa bile exact fixture sonucu; player/build, OS, CPU/GPU, driver, hardware decode state ve package architecture ile kaydedilir.

### 6.2 Cihaz/architecture matrisi

- Clean Windows 11 reference; optional codec package yok.
- Intel iGPU, AMD GPU/APU ve NVIDIA GPU temsilcisi.
- x64 mandatory.
- ARM64 yalnız native LibVLC/plugin zinciri varsa gerçek ARM64 cihaz; x64 emulation ARM64 pass değildir.
- Supported minimum OS ve current OS build.
- Multi-monitor, 100/150/200% DPI, SDR; HDR yalnız gelecek scope.
- Speaker/headphone ve playback sırasında audio-device değişimi.

### 6.3 Network/failure matrisi

- fast/slow start, latency/jitter, bandwidth step-down;
- DNS/TLS/connect/first-byte/read timeout;
- 401/403/404/429/5xx;
- mid-stream disconnect, stall, EOF ve recovery;
- auth header/query, same-origin redirect ve rejected cross-origin secret forwarding;
- user channel switch/cancel sırasında in-flight retry;
- reconnect total budget ve attempt cap.

## 7. Soak ve lifecycle planı

### M10 spike

- 2 saat developer smoke, sonra 8 saat unattended.
- Her 5 dakikada state, safe error count, process private/working set, GC heap, thread, handle ve player stats snapshot.
- 100 scripted channel switch; fullscreen/minimize/restore/display change araları.
- Network interruption/recovery en az her saat.
- Crash, hang, deadlock, double audio, orphan session ve monotonic memory trend fail.

### M16 RC

- Packaged private-flight build ile 24 saat.
- 200 deterministic switch; Tier A rotation.
- Sleep/resume, network loss/recovery ve audio device change.
- Full logs canary scan; WER/support artifact policy.
- Aynı build clean install ve previous package upgrade senaryosunda çalışır.

Hang watchdog process'i otomatik öldürmeden önce safe stack/metric snapshot alabilir; full memory dump secret riski nedeniyle default değildir. Crash recovery eski stream'i kullanıcı niyeti olmadan otomatik başlatmaz.

## 8. Quality gates by milestone

| Milestone | Minimum green evidence |
|---|---|
| M1 | PASS: clean temp workspace + empty-cache locked restore, Debug/Release x64 build, 6/6 boundaries/toolchain/manifest, negative gate, signed install/launch/normal-close/uninstall |
| M2 | Unit/integration/UI smoke + deterministic fixtures |
| M3 | Validation, terminology, redaction unit corpus |
| M4 | Packaged secret-store + canary artifact scan |
| M5 | HTTP fault/redirect/TLS/timeout/cancel suite |
| M6 | Xtream Live contract variants |
| M7 | M3U golden/fuzz/large/cancel suite |
| M8 | Migration/atomic refresh/deletion/fault tests |
| M9 | 50k query/virtualization/UIA smoke |
| M10 | Compatibility/license/MSIX hard gate |
| M11 | Fake+real player adapter contract |
| M12 | Lifecycle/rapid-switch/UI automation |
| M13 | Retry/reconnect deterministic fault suite |
| M14 | Dedicated 50k budgets and traces |
| M15 | WACK/private Store/install-update-uninstall/SBOM |
| M16 | Full regression, 24h soak, secret/license/privacy gates |

## 9. Manual ve dış doğrulama

Otomasyon şunların yerini tutmaz:

- codec patent ve OSS redistribution hukuk incelemesi;
- Microsoft/Samsung Store preflight ve public acceptance;
- Intel/AMD/NVIDIA/ARM64 gerçek cihaz ve driver;
- Samsung gerçek TV/remote/model yılı;
- Narrator/UIA/keyboard exploratory accessibility;
- real-world provider uyumluluğu. Gerçek hesap ancak ayrı yazılı yetki, veri minimizasyonu ve sanitize süreçle; MVP fixture repository'sine alınmadan test edilir.

## 10. Açık kalemler

- DPAPI layout bütçeyi geçmezse envelope-encryption benchmark planı ayrıca yazılacak.
- Tier B hangi kombinasyonların marketing support matrix'ine gireceği M10 + hukuk sonucudur.
- Engineering minimum M1'de Windows 11 build 10.0.26100/x64 olarak pinlendi; product support minimumu, ARM64 ve reference hardware M15'te kapanacak.
- Player A/V sync için sayısal ölçüm düzeneği M10'da seçilecek; yalnız “gözle iyi” kabul değildir.
- UI automation aracının exact seçimi M2'de bakım/flakiness spike'ı ile yapılacak.

## Kaynaklar

[S14–S16, S21–S32, S33–S41](../research/SOURCES.md)
