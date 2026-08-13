# Kalite ve performans stratejisi

**Tarih:** 2026-08-09

**Durum:** M2 quality infrastructure ve M3 domain gate implemented; M4 secure-storage foundation local gate PASS, milestone acceptance pending

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

### 2.1 M2 test harness baseline'ı

**Implementation status:** Completed / PASS, 2026-08-09; local exact-SDK iki-run 22/22 ve hosted packaged-host acceptance geçti.

M2 katmanları şu concrete sınırı kullanır:

| M2 katmanı | Proje/host | İzin verilen seam | Bu katmanın kanıtlamadığı |
|---|---|---|---|
| Unit | `IptvSuite.UnitTests` → `IptvSuite.Testing` | Resmî fake time, scripted transport, in-memory test secret store, passive fake player, generator/canary | Gerçek timer/network, DPAPI, ürün state policy'si, codec |
| Integration | `IptvSuite.IntegrationTests` → `IptvSuite.Testing` | Loopback Kestrel, OS-seçimli port, gerçek temp filesystem, timeout/cancellation/cleanup | Gerçek provider/internet, TLS policy, SQLite, packaged lifecycle |
| Architecture | `IptvSuite.ArchitectureTests` | Project/package/framework allowlist ve production→test yasağı | Runtime davranışı |
| Packaged launch | `Invoke-WindowsPackageSmoke.ps1` ile installed MSIX | Çalıştığı hostta sign, targeted production-payload inspection, install, AUMID visible launch, normal close, exact cleanup | Feature UI/UIA, update, WACK/Store, provider/player/codec, ARM64, host/device matrisi |

Test fake'leri production contract değildir. Özellikle fake secret store M4 DPAPI kanıtı, fake player M11 adapter/codec kanıtı ve scripted transport M5 HTTP güvenlik politikası yerine kullanılamaz. Local HTTP server yalnız `IPAddress.Loopback` ve port `0` ile çalışır; gerçek provider host'u fixture testine sokulmaz.

M2 fixture specification'ı generator `1.0.0`, algorithm version `1`, seed `20260809`, record count, provenance flag'leri ve output SHA-256'sını manifestte taşır. `LicenseRef-IPTVSuite-Synthetic-Test-Only` durumu `UNVERIFIED` ve internal-only'dir; public redistribution izni değildir. Deterministik fixture manifestine timestamp, absolute path veya makine bilgisi girmez; bunlar ayrı run evidence'ına aittir.

Canary açıkça test-only marker'dır. Scanner marker'ın UTF-8, UTF-16LE/BE, URI-escaped ve alignment-safe Base64 varyantlarını scope'tan bağımsız ve chunk boundary boyunca arar; marker taşıyan artifact path'ini fingerprint ile redakte eder. Library corpus'una ek olarak CLI, kontamine artifact'ta exact exit `2`, cleanup sonrası temiz artifact'ta `0` vermelidir. Bu kontrol genel secret discovery/redaction doğruluğu veya production artifact güvenliği iddiası değildir. GitHub job logları artifact scan kapsamına girmez ve canary sonucu `UNVERIFIED` kalır. Hang timeout'ta full/minidump üretilmez; test host `dump-type none` ile sonlandırılır.

Local M2 gate'i `eng/Invoke-WindowsQualityGate.ps1` komutudur. Exact SDK `10.0.302` (`rollForward: disable`, prerelease kapalı), locked restore, Debug/Release x64 build, aynı full suite'in ayrı TRX dizinlerinde iki ardışık geçişi, exact sentinel TRX'inde armed `Failed`/disarmed `Passed`, scanner CLI exit `2`/`0`, fixture byte/SHA-256 eşitliği ve quality-artifact canary taramasını tek akışta doğrular. TRX byte eşitliği beklenmez; sıralanmış `testName|Passed` seti karşılaştırılır. Sanitized local özet `.artifacts/quality-gates/evidence/quality-summary.json` altında üretilir.

M3 sonrası aynı gate high-core host restore/build process fan-out'unu sınırlamak için MSBuild node sayısını bire sabitler; method-level paralel unit/integration isolation davranışını değiştirmez. M3 summary'si hosted koşuda doğrulanmış 40-hex `GITHUB_SHA`, local koşuda `null` commit alanı taşır; dirty local değişikliği yanlış bir commit'e bağlamaz. 2026-08-09 local koşusunda architecture 9, unit 91 ve integration 5 olmak üzere 105/105 test iki kez aynı `Passed` setini üretmiş; sentinel, fixture ve canary kontrolleri de geçmiştir. Bu local sonuç hosted/package M3 kanıtı değildir ve M2'nin tarihsel 22-test artifact'ını değiştirmez.

2026-08-13 M4 foundation koşusu aynı exact gate içinde architecture 9, unit 100 ve integration 26 olmak üzere 135/135 testi iki kez aynı `Passed` setiyle üretmiştir. Summary `milestone=M4-foundation` ve local dirty tree nedeniyle `commitSha=null` taşır; sentinel, fixture ve artifact-file canary scan geçmiştir. Yeni testler safe initialization-result observable surface'i ile exact/case-sensitive/top-level/24-hour temp cleanup, locked/non-regular fail-closed ve pre-cancel no-mutation sınırını kapsar. Gerçek DPAPI testleri normal Windows test host'u + guarded temp root kapsamındadır; Windows composition factory'nin exception mapping'i doğrudan fault-injection seam'iyle test edilmemiştir. Installed MSIX `LocalCache`, second-user, update/reset/uninstall veya 50k performans kanıtı değildir.

M4 per-record DPAPI ölçümü `eng/Invoke-WindowsSecretStoreSpike.ps1` ile quality/CI dışındaki ayrı Release x64 executable'da çalışır. `Smoke` yalnız 1.000 kayıt/tek tur ve harness doğrulamasıdır. `Decision`; clean worktree + explicit switch ile ölçüm dışı warmup sonrasında sabit 5k/10k/20k/50k × 20 tur, aggregate create/restart/bounded-read/delete ölçümleri ve 20 cancellation-boundary örneği üretir. Evidence; başlangıç/son commit-clean doğrulaması, exact SDK/runtime/OS/architecture, runner/spec/license/workload SHA-256, duration/throughput/allocation/GC/boundary working-set, request→completion cancellation latency, committed alt/üst sınırları, cleanup ve pre-publish canary-scan durumunu taşır; raw sample, locator/reference/path, user veya hostname taşımaz. Tek-process lock eşzamanlı run'ların ortak work/evidence yüzeyini silmesini veya ezmesini engeller. Bu unpackaged, cache-controlsüz DPAPI-only sonuç normalize/parser/index/packaged lifecycle maliyetini kanıtlamaz ve tek başına ADR kabulü değildir.

Spike summary'sinin diskte bulunması PASS değildir; başarısız yeni koşu önceki temiz summary'yi koruyabilir. Yalnız wrapper'ın sıfır exit code'u ve summary içindeki commit/hash bağları birlikte değerlendirilir. Runner assembly SHA-256 yalnız çalıştırılan spike DLL'ini bağlar; bütün dependency seti veya reproducible build kanıtı olarak yorumlanmaz.

Hosted `windows-quality.yml` bütün pull request, `merge_group`, `main` push ve manual dispatch olaylarında `windows-2025-vs2026` üzerinde exact SDK ile quality gate'i çalıştırır; package job'u quality başarısına bağlıdır. `always()` coordinator'ı quality fail veya package skip/fail sonucunu tek `Required Windows gate` check'inde kırmızıya çevirir. Workflow düzeyinde path filter bilerek yoktur, çünkü filtre nedeniyle skip edilen required check `Pending` kalıp merge'i engelleyebilir [S81]. Raw TRX upload edilmez; scanner'dan geçmiş minimal summary, manifest + license sidecar ve package success evidence yedi gün tutulur. Yeşil hosted package job'u yalnız o commit/runner için signed x64 MSIX'in hedefli test-payload/canary inspection, install, `EnableLUA=1` önkoşulu altında resmi AUMID `LOCAL_SERVER` activation API'sinden dönen exact PID ile visible launch, normal close ve exact cleanup zincirini kanıtlar [S86][S87]. Gözlemlenebilen erken exit safe numeric exit-code ile, activation ile process gözlemi arasındaki çok kısa çıkış ise sabit bir hata sınıfıyla ayrılır; activation tanısı uygulama path/title/event-log metnini sorgulamaz. Workflow source'u, başarılı run/artifact olmadan kanıt değildir. Bu smoke; feature UI/UIA/accessibility, update/migration, WACK/Store, non-admin veya clean-machine matrisi, gerçek provider/internet, player/codec/HW decode, ARM64 ve genel credential yokluğu kanıtı değildir.

[Run `31327398270`](https://github.com/serkankaracan/iptv-suite/actions/runs/31327398270), commit `79cf619c6683fa9c4213846455e376fb1b0cb11c` için quality, packaged-smoke ve `Required Windows gate` işlerini başarıyla tamamladı; indirilen iki allowlist'li artifact `14/14` alan/hash assertion'ını geçti. Kalıcı değerler [M2 completion evidence](M2_COMPLETION_EVIDENCE.md) belgesindedir. Coordinator'ın yeşil olması merge policy enforcement kanıtı değildir: 2026-08-09 preflight'ında mevcut private-repository planı branch protection endpoint'inde `403` verdiği için check henüz branch policy tarafından required değildir.

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
| M2 | PASS, 2026-08-09: local exact-SDK iki-run 22/22, fixture/canary CLI/sentinel exact-TRX; hosted quality + signed packaged-smoke + coordinator success ve iki doğrulanmış sanitized artifact |
| M3 | PASS, 2026-08-09: local exact-SDK iki-run 105/105; domain invariants, terminology, safe endpoint/configuration, opaque references, bounded playlist-kind decision, redirect policy ve redaction corpus |
| M4 | IN PROGRESS: local fake + CurrentUser DPAPI foundation 135/135 iki-run PASS; typed initialization + exact stale-temp cleanup ve opt-in 1k spike smoke dahil; packaged lifecycle, wrong-user, source deletion/record reconciliation ve 5k–50k Decision/ADR kararı pending |
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
- M2 yalnız packaged visible-launch smoke kullanır. Feature-level UIA aracının exact seçimi M9/M12 öncesi bakım/flakiness spike'ıyla yapılacak; launch smoke UIA/accessibility kanıtı değildir.

## Kaynaklar

[S14–S16, S21–S32, S33–S41, S76–S84](../research/SOURCES.md)
