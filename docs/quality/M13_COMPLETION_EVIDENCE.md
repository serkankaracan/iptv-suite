# M13 error mapping, timeout, cancellation ve reconnect completion kanıtı

**Durum:** `COMPLETED — automated/deterministic MVP acceptance, 2026-08-25`

## Kabul edilen production sınırı

M13; canonical `DomainError`/retryability eşlemesini, bounded reconnect policy ve monotonic orchestration kernel'ini, exact logical/physical session ownership'ini, production countdown/cancel/manual-retry UI'ını, güvenli ve localized hata sunumunu, startup/rebuffer watchdog'unu ve Live TV için typed EOF sınırını kapatır.

Yalnız canonical `BoundedTransient` hata otomatik reconnect'e uygundur. Zincir en çok üç attempt, 30 saniye toplam karar penceresi, exact `1 s / 2 s / 4 s` taban gecikme ve caller-injected `0..250 ms` jitter ile sınırlıdır. Authentication/TLS/unsupported/malformed ve diğer `Never|Manual` sonuçlar otomatik retry almaz. Offline/online bilgisi yalnız kullanıcı ipucudur; retry authority değildir. Public state ve kullanıcı mesajı URL, locator, provider yanıtı, native diagnostic, HRESULT veya exception taşımaz.

Production Windows adapter ilk playable öncesi startup/EOF failure'ını `PlaybackStartFailed/Manual`; post-playable rebuffer timeout, `MediaFailed` veya Live EOF'yi `StreamInterrupted/BoundedTransient` olarak phase-based eşler. Reconnect başarısı yalnız exact current physical session'ın `Playing|Paused` callback'iyle kabul edilir; `Open`, `Play` dönüşü, `Buffering` veya stale callback yeterli değildir.

## Commit-bound quality ve hosted kabul

- Final clean head: `90198c0595293cc6ed265f462f1c2cbb3c3c647a`.
- Exact SDK `10.0.302`; locked restore ve Debug/Release x64 build'leri `0` warning / `0` error ile geçti.
- Architecture `47`, unit `349`, integration `157`: toplam `553/553 × 2` deterministik koşu geçti.
- Fixture records SHA-256 `1da91c57da1f704076600aab29cdd938851d75f765679ac2b79dc9cb9e908020`; armed/disarmed sentinel, scanner self-test ve artifact canary kontrolleri geçti.
- [GitHub Actions run #211 (`32839777516`)](https://github.com/serkankaracan/iptv-suite/actions/runs/32839777516) aynı committe `success` tamamlandı:
  - [Locked build and test gate `97776491535`](https://github.com/serkankaracan/iptv-suite/actions/runs/32839777516/job/97776491535): `5m13s`, success.
  - [Packaged install and launch smoke `97777859212`](https://github.com/serkankaracan/iptv-suite/actions/runs/32839777516/job/97777859212): `7m21s`, success.
  - [DPAPI real-user boundary smoke `97777859118`](https://github.com/serkankaracan/iptv-suite/actions/runs/32839777516/job/97777859118): `2m24s`, success.
  - [Required Windows gate `97779825455`](https://github.com/serkankaracan/iptv-suite/actions/runs/32839777516/job/97779825455): `5s`, success.
- Windows Client-only native job normal push'ta beklenen biçimde `skipped` kaldı; kaldırılmadı veya gevşetilmedi. M13'ün production-package kabulü hosted signed-MSIX job'ında çalıştı.

## Signed packaged recovery ve cancellation sonucu

Sentetik loopback Live HLS akışı production package içinde exact clean EOF ile kesildi. İlk EOF `StreamInterrupted` zincirini başlattı; held ikinci request restore edildikten sonra exact current session `Playing` oldu. İkinci clean EOF yeni attempt'i başlattı; kullanıcı `Cancel reconnect` yoluyla zinciri durdurdu. Üçüncü held request client detach ile kapandı ve 31 saniyelik gözlemde yeni open oluşmadı.

| Kontrol | Sonuç |
|---|---:|
| Recovery / reconnect cancellation | `true / true` |
| Cancel bütçesi / ölçülen süre | `1000 ms / 22,087 ms` |
| No-later-open | `true`; `31.009 ms` |
| Observation request count | `60 → 60` |
| Fault-stream ordinal | `3` |
| Clean completion / client detach | `2 / 1` |
| Fault capacity reject / unexpected failure | `0 / 0` |
| Normal-stream ordinal / detach | `17 / 17` |
| Normal capacity reject / unexpected failure | `0 / 0` |
| Exact request partition | `44 completed + 17 normal detach + 1 fault detach = 62` |
| Package signature / payload leak gate | `Valid / true` |
| Normal close / exact package removal | `true / true` |
| Short-run resource budget | `true` |

Acceptance transport abort'u reconnect kanıtı olarak kullanmaz: iki explicit clean completion normal response EOF'si üretir; expected abort sayısı exact `0` kalır. Üçüncü held request yalnız kullanıcı cancellation'ından sonra detach olur. Final protocol stop sinyali her fazda strict regular/zero-byte signal olarak doğrulanır; pre-finalde yasak, final accounting'de zorunludur.

Kontrol ticket'ları exact allowlist, en çok `4096` byte ve yalnız bounded Boolean/numeric alanlarla okunur. URL, route, port, path, provider text, header, locator, exception veya raw diagnostic kanıta giremez. Package payload, sanitized evidence ve repository postcondition canary taramaları değişmeden geçmiştir.

## Artifact bağı

| Artifact | ID | Metadata size | GitHub archive digest | İndirilen ilgili JSON SHA-256 |
|---|---:|---:|---|---|
| `windows-quality-evidence` | `9560174396` | `12.662` byte | `81b6e60031013a37b3c0763a2c24e54838f1e16205c79bf7dc5fc642ba8d03b7` | `017fafaba9ae5fafa8844af1edacd9967a8ccd7d4a198ca5a5b56fcf2e18817f` |
| `windows-msix-smoke-evidence` | `9560408397` | `1.551` byte | `49be13f83ef5c43cd465964bcbb497e3229d0cc2e91cdaac383dfe73c5d2c8a4` | `13c8c47857193d21c910742b1cf8c8ca98e809a0e98456315f0e7a93bf7afb09` |
| `windows-package-lifecycle-evidence` | `9560408907` | `1.007` byte | `d63ea3642275e1fba789e6d53597f1ffe58908904543bafb398736c778b83902` | `68d60a9a86d4dfea04a77820bcfecc64e38d53054003e571a40d8f0a6faecf4c` |
| `windows-dpapi-user-boundary-evidence` | `9560248802` | `875` byte | `d7c8870b42278475ed28df4395e0f67a45f1ec2403621febccff691ef56bb17d` | `97c77d38dcc54dea6fd4c021924655e3b3df5b584fd6e1cc8ca0290bdd2dc32a` |

GitHub digest'i artifact servisinin archive digest'idir. Son sütun sırasıyla indirilen `quality-summary.json` veya `last-success.json` payload'ının digest'idir; MSIX JSON payload'ı `4.929` byte'tır.

## Kapsam ve non-claims

- 401/403/404/429/5xx, TLS, timeout ve `Retry-After` semantiği M5 fetch/transport boundary'sinde deterministik olarak doğrulanır. Opaque production `MediaSource` yolunda exact HTTP/TLS/DNS attribution **UNVERIFIED** kalır; native raw hata/HRESULT ayrıştırmama güvenlik kararı değişmez.
- Signed acceptance sentetik, provenance kayıtlı loopback Live HLS içindir. Gerçek provider/account/internet, vendor workaround veya DRM erişim aşma kanıtı değildir.
- VOD natural-completion semantiği değildir; M13 yalnız mevcut Live TV intent'ini kapsar.
- Fiziksel NIC/offline-online, sleep/resume, audio endpoint, DPI/multi-monitor, Narrator, ARM64, gerçek cihaz/HW decode matrisi değildir.
- M10'daki relative-memory sapması, uzun HLS/native retention borcu, 8/24 saat soak ve final hardening kapanmaz; R15/M16'da açık kalır.
- WACK/Store, production identity/signing, clean-machine install/update ve hukuk/codec/privacy kabulü M15/M16 kapsamındadır.

Bu sınırlar içinde M13'ün otomatik/deterministik MVP acceptance kriterleri tamamlanmıştır; sıradaki planlı milestone M14'tür.
