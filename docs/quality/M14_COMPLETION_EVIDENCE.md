# M14 50.000+ katalog performansı completion kanıtı

**Durum:** `COMPLETED — bounded synthetic/reference acceptance, 2026-08-25`

## Kabul edilen sınır

M14; mevcut M3U→protected SQLite→virtualized WinUI zincirine yeni ürün özelliği eklemeden 50.000 kayıt için ölçülebilir import/query/UI bütçelerini, bounded image-cache lifecycle'ını ve aynı-profile regression gate'ini kapatır. Final kaynak checkpoint'i `912c25a661c08d2a693627f493fa1e2318061c7f` (`perf(windows): bind M14 regression and image lifecycle`) commit'idir.

Authoritative component benchmark Release x64 ve instrumentation kapalı çalışmıştır. Reference koşulu exact SDK `10.0.302`, `.NET 10.0.10`, Windows build `10.0.26200.0`, 24 logical processor, katalog schema `5`, 20 warm iteration ve caller-declared `m14-local-win11-client-a1` profile'ını kaydeder. `Warm / AcStable / Nominal / Controlled` koşulları caller declaration'dır; fiziksel makine kimliği bağımsız doğrulanmamıştır.

## 50k component bütçeleri

| Ölçüm | Sonuç | Bütçe | Karar |
|---|---:|---:|---|
| Parser p95 | `65,1227 ms` | `≤ 2.000 ms` | PASS |
| Normalize+protect+persist+index conservative upper bound p95 | `1.350,5073 ms` | `≤ 3.000 ms` | PASS |
| Combined import p95, download hariç | `1.350,5073 ms` | `≤ 5.000 ms` | PASS |
| Import allocation maximum | `153.415.576 byte` (`146,3085 MiB`) | `≤ 150 MiB` | PASS |
| Peak working-set delta | `3.149.824 byte` (`3,0039 MiB`) | `≤ 250 MiB` | PASS |
| Cancellation p95 | `15,6047 ms` | `≤ 250 ms` | PASS |
| First page p95 | `9,4470 ms` | `≤ 500 ms` | PASS |
| Category query p95 | `24,4343 ms` | `≤ 100 ms` | PASS |
| Search query p95 | `26,5633 ms` | `≤ 100 ms` | PASS |
| Reopen first-visible p95 | `9,4185 ms` | `≤ 500 ms` | PASS |

`1.350,5073 ms`, combined import ölçümünden türetilmiş conservative üst sınırdır; normalize, protect, persist ve index için ayrı exact stage süreleri iddia edilmez. Network download süresi bu bütçeye dahil değildir.

Generator; success beklenen `100 / 5.000 / 10.000 / 20.000 / 50.000` kayıt corpus'larını ve entry-limit'te fail-closed olması beklenen `100.000` stress corpus'unu exact count/hash/provenance alanlarıyla üretmiştir. Her ölçüm stage'i bir pre-warm gözlem ile 20 authoritative warm sample taşır. Pre-warm kayıt OS cache flush uygulanmış gerçek cold-machine sonucu değildir.

## Baseline ve regression bağı

Baseline ve candidate aynı commit, schema, workload, budget contract ve declared runner profile ile bağlandı. Baseline commit ancestor-or-self ve baseline content-stable kontrolleri geçti; sekiz p95 metriğinin tamamı `≤ +%10` regression eşiğini ve mutlak bütçelerini geçti.

| Metrik | Baseline | Candidate | Değişim |
|---|---:|---:|---:|
| Parser p95 | `65,2303 ms` | `65,1227 ms` | `-%0,1650` |
| Normalize/protect/persist/index upper-bound p95 | `1.428,1325 ms` | `1.350,5073 ms` | `-%5,4354` |
| Combined import p95 | `1.428,1325 ms` | `1.350,5073 ms` | `-%5,4354` |
| Cancellation p95 | `15,9574 ms` | `15,6047 ms` | `-%2,2103` |
| First page p95 | `10,0835 ms` | `9,4470 ms` | `-%6,3123` |
| Category p95 | `24,7382 ms` | `24,4343 ms` | `-%1,2285` |
| Search p95 | `34,9627 ms` | `26,5633 ms` | `-%24,0239` |
| Reopen p95 | `10,8938 ms` | `9,4185 ms` | `-%13,5426` |

İki kayıt aynı committe olduğu için bu sonuç same-profile repeatability ve regression-gate wiring kanıtıdır; cross-commit hızlanma iddiası değildir.

## Signed package UI ve working-set kabulü

[GitHub Actions run #217 (`32858287103`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32858287103) final committe başarıyla tamamlandı:

- `Locked build and test gate` (`97835604132`): architecture `49`, unit `359`, integration `158`; toplam `566/566 × 2`, fixture determinism, sentinel, scanner ve artifact-canary PASS.
- `Packaged install and launch smoke` (`97837683109`): signed production MSIX, payload leak gate, 50k seed/UIA/input/scroll/player-off working-set, normal close ve cleanup PASS.
- `DPAPI real-user boundary smoke` (`97837683021`) ve `Required Windows gate` (`97840474101`) PASS.
- Windows Client-only native lane normal push policy'sine göre `skipped`; M14 sonucu için gerekli değildir.

Normal hosted package koşusunda M14 trace instrumentation'ı kapalıydı:

| Kontrol | Sonuç | Bütçe |
|---|---:|---:|
| Input response p95 | `3,305 ms` | `≤ 100 ms` |
| DWM frame proxy p95 / maximum | `15,625 / 31,250 ms` | `≤ 33,3 / 200 ms` |
| DWM late/drop proxy | `%0`; `368` interval | `< %1` |
| UI-thread responsiveness WM_NULL proxy p95 / maximum | `3,597 / 4,604 ms` | `≤ 100 / 200 ms` |
| Realized container | `8` | `≤ 300` |
| Player-off steady working-set average / maximum | `164.397.533,867 / 164.458.496 byte` | maximum `≤ 350 MiB` |
| Working-set sample | `60`; process/state korunmuş | exact bounded sample contract |

DWM ve WM_NULL değerleri fail-closed package proxy'leridir; app-specific ETW frame attribution'ı sayılmaz.

## Opt-in ETW/XAML tanısı

Aynı committe, kullanıcı tarafından elevated interactive Windows oturumunda `-EmitM14TraceMarkers` ile signed package smoke çalıştırıldı. Marker'lar yalnız exact activation PID `28080` için input/search ve scroll etkileşim aralığını kapsadı; player-off idle working-set örnekleri marker dışındadır [S133][S134].

- ETL: `4.183.818.240 byte`; marker begin/end/count `true / true / 2`.
- WPA exporter marker aralığı: yaklaşık `74,105–85,399 s`; XAML event coverage `11,2874282 s`.
- PID-bound XAML detail: `620` Frame + `639` UpdateLayout; Frame p95 `2,1269 ms`, bütün XAML event'lerinde maximum `9,6794 ms`.
- Exact marker penceresinde `>200 ms` XAML Frame/UpdateLayout event'i `0`; UI Delays export'unda data row `0`.
- Trace-on package proxy'leri de input p95 `6,674 ms`, DWM p95 `16,673 ms`, dropped proxy `%0`, realized container `20` ve player-off working-set maximum `204.283.904 byte` ile bütçeleri geçti.

Bu trace hot-path'in bounded tanısıdır. Tüm uygulama/session/sistem için sıfır stall, eksiksiz .NET allocation attribution veya belirli bir optimizasyonun nedensel etkisi iddia edilmez. Normal hosted instrumentation-off sonuç authoritative UI kabulüdür.

## Image lifecycle kabulü

Deterministik unit/architecture matrisi visible-window image yükünü exact `4` concurrent fetch, memory-only `32 MiB / 128-entry` LRU, queued/in-flight cancellation, recycle/source-delete/page-dispose invalidation ve cancellation'a uymayan provider'ın stale dönüşünü cache'e almama sınırlarında geçti. Durable disk image cache MVP'de kapalıdır (`0` byte).

Packaged 50k seed logo reference taşımadığından signed package koşusu image fetch/decode/network veya disk-cache kabulü değildir. MIME/pixel/dimension ve genel DNS/address-policy non-claim'leri değişmez.

## Artifact ve digest bağı

| Yerel/indirilen kayıt | Boyut | SHA-256 |
|---|---:|---|
| Candidate benchmark summary | `275.341` byte | `78532C2E1593F27446C16FB7AF21A933DCABD4A25E1D49B11EF99D93138869B6` |
| Baseline benchmark summary | `275.657` byte | `4840CB6E7D4E62C625B57F47E29FEAC9745FEBE03C011FF4ED1A4B46DF1D1439` |
| Regression summary | `5.293` byte | `2BF6B4E42A4A2DFD5FCFF9F54DB7483772D8A617540F72BC4C137C30C9F7DE1F` |
| Hosted packaged `last-success.json` | `15.824` byte | `42CDECD3E00B63C69C6664ABE3753C59E08A40CFAED09608BDF3B208498D4E82` |
| Hosted quality summary | `42.361` byte | `DBA18354E22405AAA7A1B3DDD7C7399150A9D992823FD6099FA7E851CDDE29A1` |
| Hosted DPAPI `last-success.json` | `1.766` byte | `805585E7A41E369213DA655A031B98B3B643DD688E7A57297F695C2F134328B1` |
| Hosted lifecycle `last-success.json` | `2.483` byte | `FD498ABCC5EE0A16C627F1E574536FDFFD414A165430F3E3240300D93EAA368B` |
| Local ETL | `4.183.818.240` byte | `1525BC0DD704A1853E9178E56CAA58853B1D93BBA819E0AAF91B2DEE8D53A034` |
| PID-bound XAML CSV | `124.215` byte | `2100CC9BA9EDAAC9D32C97247F084A0595B4C259E064A3AD991A5EA9074FD4AC` |
| Marker-window UI Delays CSV | `115` byte | `F8F03C556567075A00F04463004BC4EEDD4CE809B815B9496D21F0F7574043FC` |

`.artifacts` içeriği Git'e alınmayan local/downloaded build kanıtıdır. Baseline da Git'e commit edilmez; regression runner exact digest, commit ve schema bağını fail-closed yeniden doğrular.

## Kapsam ve non-claims

- Sonuç sentetik, bounded 50k workload ve tek declared Windows 11 Client x64 profile'ı içindir; gerçek provider/account/internet, farklı fiziksel cihaz/GPU/driver, ARM64 veya clean VM matrisi değildir.
- Runner profile fiziksel host kimliğini bağımsız doğrulamaz. Processor/OS/runtime metadata'sı raporlanır; hostname, kullanıcı, serial veya MachineGuid tutulmaz.
- Cold observation yalnız explicit warm-up öncesidir; OS cache-flushed cold benchmark değildir.
- Exact per-stage normalize/protect/persist/index süreleri ve network download süresi ölçülmemiştir.
- ETW, yalnız exact PID/marker etkileşim penceresini açıklar; full-session stall/leak yokluğu veya causal optimization kanıtı değildir.
- DWM dropped-frame ve WM_NULL UI-thread değerleri package proxy'sidir. XAML export yalnız marker-window app PID frame/layout event'lerini bağlar.
- M10 relative-memory sapması, M12 fiziksel UX/device matrisi ve M15/M16 Store/legal/device/final-soak kapıları kapanmaz.

Bu sınırlar içinde M14 acceptance kriterleri tamamlanmıştır; sıradaki milestone M15 MSIX ve Microsoft Store readiness'tir.
