# M7 remote M3U parser completion evidence

**Durum:** `COMPLETED, 2026-08-20`

## Tamamlanan sınır

M7, authoritative `ContentSource` içindeki configuration owner + opaque remote-playlist reference üzerinden protected locator lease'ini okuyan internal loader'ı tamamlar. Loader bounded streaming HTTP response'u strict UTF-8/BOM incremental parser'a verir. Parser `#EXTM3U`/`#EXTINF`, bounded metadata, final authorized origin'e relative HTTPS resolution, unsafe locator skip, duplicate `tvg-id` warning ve 50.000 entry cap uygular. HLS master/media içerikten typed route edilir; channel catalog olarak içeri alınmaz.

Production loader accepted entry'leri mandatory internal `IRemoteM3uEntrySink` sınırına tek tek aktarır. Böylece response tek string'e ve katalog tam object graph'ına dönüştürülmez. Diagnostic `ParseAsync` overload'u test/performance gözlemi için materialized liste üretir; loader'ın bunu çağırması architecture testinde yasaktır.

## Local doğrulama

Exact SDK `10.0.302` ile Debug/Release x64 build'leri 0 warning/error tamamlandı. Quality gate iki deterministik turda architecture `19`, unit `157`, integration `84` olmak üzere **260/260 ×2** testi geçirdi; fixture determinism, deliberate sentinel fail/recovery ve artifact canary kontrolleri PASS oldu.

Local `quality-summary.json` uzunluğu `25.024` byte, SHA-256 değeri `f537bcd14f5e0a4de58e8b651bcb04b7f730162a29ad39e014ec1cdc2b237977` ve `commitSha=null`dur; bu local kanıt tek başına commit-bound değildir. Parser suite exact 50k ve limit+1, 10 örnekli 50k p95 `≤2 s` guard'ı, 20 örnekli mid-stream cancellation p95 `≤250 ms` guard'ı ve seed `20260820` ile 256 bounded malformed-byte input içerir. Bunlar bu makinedeki test threshold'larıdır; reference-device end-to-end import ölçümü değildir.

## Hosted doğrulama

[GitHub Actions run `32409222785`](https://github.com/serkankaracan/iptv-suite/actions/runs/32409222785), exact commit `fc4e2e360e1b0283369058e6bef349e667f3a764` için `Locked build and test gate`, `Packaged install and launch smoke`, `DPAPI real-user boundary smoke` ve `Required Windows gate` işlerinin 4/4'ünü başarıyla tamamladı. Dört artifact yayımlandı. Bu commit-bound workflow sonucu M7 implementation/regression zincirini workflow seviyesinde **VERIFIED** yapar.

Artifact içerikleri bu kayıt için indirilip bağımsız hash doğrulamasından geçirilmedi; bu belge hosted test sayısını veya local summary hash'ini artifact içeriğine bağlamaz.

## Açık ve sonraki kapsam

- `IRemoteM3uEntrySink` bir handoff boundary'sidir; production SQLite sink, protected per-row locator yazımı, staging/activation transaction'ı, cache/index ve restart/crash/reconciliation M8'dir.
- HLS route typed sınıflandırmadır; segment/media parse veya playback değildir.
- Testler sentetik loopback/in-memory içerik kullanır; gerçek playlist/provider uyumluluğu iddia edilmez.
- UI, player, EPG, VOD/Series ve local-file import M7 kapsamında değildir.
