# M5 network/transport completion evidence

**Tarih:** 2026-08-20
**Milestone:** M5 — HTTP/networking ve connection probe
**Durum:** `COMPLETED`

## Tamamlanan sınır

- Application katmanında HTTPS expected-endpoint'e bağlı, response boyutu sınırlı ve optional authorization buffer'ını dispose sırasında sıfırlayan `HttpTransportRequest`; zeroing `HttpResponseLease`; typed `IHttpTransport` sonucu; URI, header ve credential taşımayan `HttpTransportObservation`; bounded GET kullanan `ConnectionProbeService` vardır.
- Infrastructure `BoundedHttpTransport`, tek uzun ömürlü client/handler kullanır; cookies ve automatic redirects kapalıdır. Connect timeout `5 s`, bütün operation timeout'u `15 s`, response cap'i en fazla `4 MiB`, redirect cap'i `5`, safe transient attempt cap'i `3` ve `Retry-After` cap'i `2 s`dir.
- HTTPS→HTTP downgrade ve credential-bearing cross-origin redirect fail-closed reddedilir. Same-origin redirect credential policy'sini korur. TLS, network, timeout, cancellation, status ve oversize sonuçları raw exception/URI/header yayımlamayan typed sözleşmeye eşlenir.
- Response stream'i handler decompression'ından sonra da bounded okunur; pooled temporary buffer'lar iade öncesi sıfırlanır. Caller cancellation exception olarak korunur; internal total-timeout retry delay sırasında da typed `RequestTimedOut` olur.

## Yerel doğrulama

Exact SDK `10.0.302` ile Debug/Release x64 build'leri sıfır warning/error verdi. Architecture `17`, unit `157`, integration `54` olmak üzere `228/228` test iki kez aynı `Passed` setiyle geçti. Fixture determinism, sentinel fail/recovery, scanner self-test ve artifact canary kontrolleri PASS'tir.

Local `.artifacts/quality-gates/evidence/quality-summary.json` dosyası `21.777` byte ve SHA-256 `384c50d38ae7771a0843481c285c6fdc91b38072959eb845da86f4a08eff7561` değerindedir. `commitSha=null` taşıdığı için bu dosya yalnız local kanıttır.

## Kanıt sınırı

M5 gerçek provider/internet/account uyumluluğu iddia etmez. Sentetik loopback fixture ve scripted handler; invalid TLS, timeout/cancellation, redirect/downgrade/origin, bounded retry, `Retry-After`, response cap'i ve safe observation sözleşmesini kanıtlar. Xtream endpoint/auth semantiği ve authoritative protected-credential resolve M6; parser M7; durable source lifecycle/database M8 kapsamındadır.
