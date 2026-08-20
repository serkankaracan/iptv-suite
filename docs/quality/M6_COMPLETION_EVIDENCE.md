# M6 Xtream-compatible Live TV adapter completion evidence

**Tarih:** 2026-08-20  
**Durum:** `COMPLETED`; local acceptance ve commit-bound hosted workflow `VERIFIED`

## Sonuç

M6'nın production sınırı; protected source credential'ını authoritative `ContentSource` configuration owner/reference tuple'ıyla kısa ömürlü lease olarak okuyup yalnız Xtream-compatible account, live category ve live stream contract'larına bağlar. Sonuç full URL veya credential taşımaz; live stream identity `ProviderItemKey` olarak döner. VOD, Series, EPG, gerçek provider/account, persistence, UI ve playback kapsam dışıdır.

## VERIFIED local kanıt

- Exact SDK: `.NET SDK 10.0.302`; Debug ve Release x64 build `0 warning / 0 error`.
- İki deterministic full-suite koşusu: architecture `18`, unit `157`, integration `68`; toplam `243/243 ×2`.
- Fixture determinism, armed-fail/disarmed-pass sentinel, scanner CLI self-test ve artifact canary scan PASS.
- Local summary: `23.204` byte; SHA-256 `6cc5c020cd15bcc2232205d41dae0ea48234e849ad8f66c0191450a2ee98e60b`; `commitSha=null`.
- Dar provider suite: account/live endpoint sırası; 200-body auth fail; 401/403/429/5xx/timeout/oversize mapping; cancellation sonrası credential lease zeroing; empty/partial/malformed; string-number-Boolean/null/unknown/duplicate policy; exact 50k valid live record ve limit+1 rejection.

## VERIFIED hosted kanıt

[GitHub Actions run `32404441692`](https://github.com/serkankaracan/iptv-suite/actions/runs/32404441692), commit `d5b1161e9ff88552cbebe52524b556e7aaf976bc` için `Locked build and test gate`, `Packaged install and launch smoke`, `DPAPI real-user boundary smoke` ve `Required Windows gate` işlerinin 4/4'ünü `9m05s` içinde başarıyla tamamlamıştır. Quality job M6 testlerini normal full suite içinde çalıştırmış; diğer işler package/lifecycle ve DPAPI güvenlik regresyonlarını korumuştur. Bu kayıt workflow/UI düzeyinde commit-bound execution kanıtıdır. Artifact içerikleri ve tam digest'ler bağımsız indirilip doğrulanmamıştır.

## Security invariant'ları

- Public provider operation ayrı source ID, owner veya credential reference kabul etmez; bunları `ContentSource` aggregate'inden türetir.
- Credential-bearing request URI operation-local managed zorunluluktur; safe transport observation URI/header taşımaz, provider result ve diagnostic surface credential/full URL yayımlamaz.
- Protected lease bütün provider operation'ını kapsar ve her çıkışta dispose edilir.
- Parser `direct_source` ve unknown alanları tutmaz. Stream result full locator yerine bounded `ProviderItemKey` taşır.
- Source endpoint origin'i request oluşturulmadan önce `HttpTransportRequest` expected-origin doğrulamasından geçer. Cross-origin/downgrade/redirect güvenliği M5 transport katmanında kalır.

## Sınırlar

Scripted transport ve sentetik payload gerçek provider uyumluluk kanıtı değildir. 50k test item/byte cap ve deterministic parsing contract'ını doğrular; reference-device end-to-end import, allocation/working-set bütçesi veya durable activation kanıtı değildir. Bunlar M8'de parser/normalize/index/SQLite zinciriyle ölçülür. M7 remote M3U parser'dır; M6 sonucu M7 acceptance'ı değildir.
