# Uyumluluk için kabul edilen ödünler

**Tarih:** 2026-08-29

**Durum:** Aktif kayıt; her yeni gevşeme bu listeye eklenmeden production kapsamına alınmaz

Bu belge, farklı IPTV sağlayıcı davranışlarıyla çalışabilmek için daha katı varsayımlardan
bilinçli olarak ayrıldığımız noktaları tek yerde tutar. Buradaki bir kayıt davranışı
"güvenli" yapmaz; kalan riski, korunan sınırları ve yeniden değerlendirme tetikleyicisini
görünür kılar.

## Kanıt sınıflandırması

- **VERIFIED:** CC-004, CC-005 ve CC-006'ya temel olan IPTVnator davranışları,
  exact `29ca94aa4371e9a71e24ccac4c529bd62f6fabd5` source snapshot'ında incelendi;
  kanıt kaydı [Research Sources S142 ve S143](../research/SOURCES.md) içindedir.
- **INFERENCE:** Bu davranışları Windows profile'a bounded fallback ve last-good
  korumasıyla alma kararı IPTV Suite'e ait mühendislik yorumudur; IPTVnator'ın güvenlik
  modelini veya birebir implementation'ını devraldığımız anlamına gelmez.
- **UNVERIFIED:** Proprietary provider evreninde bu varyantların yaygınlığı ve belirli
  gerçek hesaplarla uyumluluğu doğrulanmış değildir; repository'ye gerçek credential,
  provider URL'si veya response capture'ı alınmaz.

## Değişmeyen sert sınırlar

- TLS certificate/hostname doğrulaması kapatılmaz ve `accept all certificates` eklenmez.
- Credential taşıyan redirect, cross-origin HTTP, HTTPS→HTTP downgrade ve URI user-info
  kabul edilmez.
- `Authorization`, `Cookie` veya `Referer` için provider-specific workaround registry'si
  oluşturulmaz.
- Username/password, token, full URL, response body ve provider credential'ı UI, log,
  exception, test artifact'ı, fixture veya repository'ye yazılmaz.
- Response byte, decompression, JSON depth/string/item, timeout ve cancellation sınırları
  kaldırılmaz.
- Live/Movie/Series ayrımı display name, category, URL path veya extension heuristic'iyle
  yapılmaz; yalnız typed provider operation sonucundan gelir.
- DRM veya erişim kontrolü aşılmaz; kullanıcı trafiği izinsiz bir backend/proxy üzerinden
  geçirilmez.

## Aktif kayıtlar

| ID | Kapsam | Kabul edilen ödün | Uyumluluk gerekçesi | Korunan kontroller | Kalan risk / yeniden değerlendirme |
|---|---|---|---|---|---|
| CC-001 | Remote M3U HTTP | Açık kullanıcı onayıyla exact source/origin için cleartext playlist ve initial channel trafiğine izin verilir | Yetkili kaynağın HTTPS sunmaması | HTTPS default; ayrı consent; user-info/header/cookie/referer/cross-origin redirect/downgrade reddi; bounded import; kalıcı warning | Token, katalog ve medya on-path okunabilir/değiştirilebilir; native alt-kaynak/redirect denetimi release blocker'ıdır. ADR-008/R30 yeniden değerlendirme kaynağıdır |
| CC-002 | Xtream-compatible HTTP | Açık kullanıcı onayıyla exact source configuration/origin için username/password içeren cleartext API ve playback isteğine izin verilir | Yetkili Xtream-compatible hesabın yalnız HTTP sunması | Fresh configuration owner ve ayrı consent; protected short lease; redirect/header/cookie/image/cross-origin reddi; exact endpoint; kalıcı warning | Credential ve içerik on-path okunabilir/değiştirilebilir; Store/privacy/hukuk ve native alt-kaynak yüzeyi release blocker'ıdır. ADR-009/R31 yeniden değerlendirme kaynağıdır |
| CC-003 | Xtream JSON şekli | Canonical JSON array'e ek olarak tek UTF-8 BOM ve en fazla 20 ASCII rakamlı benzersiz key'lerden oluşan bounded associative object koleksiyonu kabul edilir | Proprietary provider'ların aynı typed endpoint'i farklı JSON container biçimiyle döndürmesi | Existing response/item/depth/string cap'leri; yalnız object/null item; duplicate key, nonnumeric key, mixed/error object ve malformed JSON reddi; content kind endpoint'ten gelir | Resmî/normative Xtream specification yoktur; desteklenen provider evreni `UNVERIFIED`dır. Variant corpus veya gerçek hata sınıfı değişirse ADR-009/R02 yeniden açılır |
| CC-004 | Xtream boş koleksiyon sentinel'i | Başarılı account doğrulamasından sonraki catalog zincirinde exact `{}`, top-level `null` ve `false`, boş canonical array gibi sıfır item kabul edilir | Bazı adapter'ların bulunmayan Live/VOD/Series ailesini array yerine boş sentinel ile bildirmesi; IPTVnator karşılaştırmasında non-array yanıtların boş listeye indirgenmesi | Yalnız kesin boş sentinel'ler; non-empty error object, `true`, number, string, HTML, duplicate/mixed object reddi; bütün altı typed endpoint ayrı çağrılır; boyut ve item sınırları korunur. Existing non-empty family sentinel veya all-invalid page ile sıfırlanacaksa activation reddedilip last-good snapshot korunur; explicit `[]` authoritative empty kalır | İlk importta outage ile gerçek empty ayrımı yine yapılamaz; explicit `[]` hatalıysa provider içeriği silebilir. Kullanıcıya partial/empty warning ayrı milestone kararıdır. R02/R33 aktiftir |
| CC-005 | Xtream account discovery | Aynı exact `player_api.php` origin'inde en fazla üç credential-bearing account varyantı `get_account_info → action yok → get_profile` sırasıyla denenir | IPTVnator'ın bounded compatibility profile'ı ve proprietary provider action farklılıkları | Fallback yalnız HTTP başarıyla döndükten sonra body shape unsupported ise ilerler; explicit auth rejection, redirect/HTTP failure, timeout, rate-limit, service failure veya cancellation zinciri uzatmaz; her istek aynı origin/cap/redirect/header policy'sini kullanır; raw action/query loglanmaz | HTTP kaynağında credential aynı origin'e üç kez cleartext gönderilebilir; provider rate-limit/load artar. Action listesi genişletilirse yeni ADR ve sentetik corpus gerekir. R02/R31 aktiftir |
| CC-006 | Xtream typed field/hierarchy varyantı | Live/VOD için bounded string `name → title`, Series için `title → name` precedence'i; Series detail'de missing/null `seasons` metadata'sı kabul edilir | IPTVnator normalizer ve Series UI, bu iki proprietary varyantla çalışır; Windows mapper episode group'larından season türetebilir | Display name yalnız non-empty bounded string; number/bool kabul edilmez; content kind yalnız endpoint'ten gelir; `episodes` object zorunlu; raw season-group `100`, raw episode `5.000` hard cap; duplicate/non-numeric group reddi | Çelişkili `name/title` sağlayıcının gösterdiği adı değiştirebilir; missing season metadata türetilmiş season üretir. Precedence veya hierarchy genişlerse sentetik corpus ve ADR-009 yeniden açılır. R02 aktiftir |

## Sınıflandırma kararı

IPTVnator incelemesinden alınan uygulanabilir model, içeriği `get_live_*`, `get_vod_*`
ve `get_series*` operasyonlarıyla ayırmaktır. Remote M3U tek başına güvenilir Movie/Series
tipi taşımaz; bu kaynaklar Live-only kalır. Böylece uyumluluk için input toleransı
artırılırken yanlış kategori üretmek için güvenlik veya veri-bütünlüğü heuristic'i eklenmez.

## İşletim kuralı

Her yeni uyumluluk talebi için önce sentetik fixture ve bounded failure matrisi eklenir.
Yeni ödün bu tabloya; risk kaydına ve ilgili ADR/milestone'a bağlanır. Gerçek provider
credential'ı veya response capture'ı bu sürecin girdisi değildir.

## İlgili kayıtlar

- [ADR-008 — Remote M3U cleartext HTTP compatibility](../adr/ADR-008-remote-m3u-cleartext-http-compatibility.md)
- [ADR-009 — Xtream-compatible cleartext HTTP compatibility](../adr/ADR-009-xtream-cleartext-http-compatibility.md)
- [Security and Privacy Baseline](SECURITY_AND_PRIVACY_BASELINE.md)
- [Risk Register](../risks/RISK_REGISTER.md)
- [IPTVnator referanslı Windows yol haritası](../roadmap/IPTVNATOR_REFERENCE_WINDOWS_ROADMAP.md)
