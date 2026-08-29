# ADR-009 — Xtream-compatible kaynaklar için ayrı cleartext HTTP uyumluluğu

**Status:** `Accepted for M17 engineering / Release gated`

**Date:** 2026-08-29

## Context / Problem

M1–M16 Xtream-compatible source contract'ı HTTPS ile sınırlıdır. ADR-008, açık kullanıcı onayıyla yalnız Remote M3U locator/query ve onun dar initial-channel zincirine cleartext HTTP exception açmıştır; Xtream'i özellikle kapsam dışında bırakmıştır.

Kullanıcı 2026-08-29 tarihinde, kendi yetkili hesabı için HTTP Xtream-compatible desteğini istediğini açıkça belirtmiştir. Xtream akışında username/password yalnız bir playlist URL'sinde bulunmaz; account probe, Live/VOD/Series catalog çağrıları ve constructed playback locator'larında tekrar kullanılabilir. Bu nedenle Remote M3U onayı Xtream riski için yeterli veya devredilebilir değildir.

`VERIFIED`: HTTP secured connection değildir; authority DNS/routing saldırılarına açık olabilir. HTTPS origin authentication, confidentiality ve integrity sağlar; credential alışverişi secured connection gerektirir [S135].

`UNVERIFIED`: “Xtream-compatible” için bu repository'nin dayandığı resmi, açık ve normative bir protocol/API specification yoktur. Yaygın endpoint, action, JSON field ve playback path davranışları proprietary compatibility convention'larıdır; provider'lar arasında değişebilir. Belirli bir gerçek provider'ın davranışı veya uyumluluğu bu kararla doğrulanmaz.

## Decision drivers

- Kullanıcının kendi yetkili fakat yalnız HTTP sunan Xtream-compatible hesabını doğrudan cihazından kullanabilmesi.
- HTTPS'i varsayılan ve önerilen yol olarak korumak.
- Username/password cleartext riskini Remote M3U token riskinden ayrı ve açık anlatmak.
- Onayın başka kaynak, endpoint, account veya configuration'a taşınmaması.
- Provider-specific wire biçimini domain/UI/persistence contract'ına sızdırmamak.
- Source CRUD, catalog ve playback boyunca secret lifetime/redaction/address sınırlarını korumak.
- M1–M16 tarihsel kanıtlarını geriye dönük yeniden yazmamak.

## Considered options

1. Xtream için HTTPS-only davranışı korumak.
2. HTTP Xtream'i tek global setting ile açmak.
3. Remote M3U HTTP onayını Xtream için de kullanmak.
4. Exact Xtream source configuration'a bağlı, ayrı ve görünür cleartext onayla HTTP açmak.
5. Trafiği geliştirici backend'i/VPN/proxy üzerinden geçirmek.

Global veya paylaşılan onay, kullanıcının hangi credential trafiğini kabul ettiğini belirsizleştirir. Geliştirici backend'i direct-provider/no-backend privacy sınırını değiştirir. Seçilen yön dördüncü seçenektir.

## Decision

HTTPS bütün Xtream-compatible kaynaklar için varsayılan ve önerilen transport'tur. M17'den itibaren HTTP yalnız aşağıdaki dar sözleşmeyle kabul edilebilir:

### Ayrı ve configuration-scoped consent

- `XtreamCompatible + http` validation sonucu, ilk network isteğinden önce Xtream'e özgü blocking consent ister.
- Remote M3U HTTP onayı, private/local-network onayı, başka bir Xtream kaynağının onayı veya global app setting'i bu onayın yerine geçmez.
- Consent exact `SourceId + SourceConfigurationId + SafeEndpoint(scheme, IDNA host, effective port)` bağına kaydedilir.
- Endpoint, username veya password replace işlemi fresh `SourceConfigurationId` ve fresh consent ister. Yalnız display-name rename fresh consent istemez.
- Onay verilmezse probe, catalog, image veya playback isteği çıkmaz; secret durable source olarak commit edilmez.
- Persisted HTTP Xtream source, Home/source selector/Source Manager ayrıntısında kalıcı `Cleartext HTTP` warning taşır. Warning dismiss edilerek kalıcı biçimde saklanamaz.

Consent metni en az şunları açıklar:

- username/password'ın API request'lerinde ağ üzerinde şifrelenmeden okunabileceği;
- Live/VOD/Series metadata ve playback locator/trafiğinin gözlemlenebileceği veya değiştirilebileceği;
- DPAPI/AES at-rest protection'ın transit confidentiality, integrity veya server authentication sağlamadığı;
- yalnız kullanıcının erişim yetkisi bulunan account için devam edilmesi gerektiği.

### Secret ve request sınırı

- Username/password ve full API/playback locator yalnız operation/session lifetime'ında, owner-bound protected credential lease'inden çözülür.
- Domain, presentation, SQLite metadata, navigation state, exception, log, telemetry, support export, screenshot automation veya test artifact'ı raw credential/full locator taşımaz.
- Credentials query/path içinde gerekiyorsa infrastructure adapter bu değeri request gönderilmeden hemen önce kurar; domain `player_api.php`, action adı, query key'i veya playback path'i bilmez.
- Request'e provider talep etse bile `Authorization`, `Cookie` veya `Referer` fallback'i eklenmez. Bu header/cookie olmadan çalışmayan variant safe `UnsupportedProviderVariant` benzeri stable error ile fail-closed kalır; workaround registry oluşturulmaz.
- TLS certificate/hostname validation hiçbir scheme veya provider için kapatılmaz. HTTP kaynağın HTTPS'e kullanıcı tarafından değiştirilmesi security upgrade'dir.

### Origin, redirect ve address policy

- HTTP Xtream API request'i yalnız approved exact `SafeEndpoint` origin'ine gider. Automatic redirect kapalıdır; credential-bearing query/path redirect target'a otomatik forward edilmez.
- Provider response'undaki absolute API/playback/image URL trusted değildir. Initial HTTP Live/Movie/Episode playback locator yalnız exact approved source origin'indeyse kabul edilir. HTTPS locator kendi scheme/origin/address policy'sinden yeniden geçer.
- Cross-origin HTTP API veya playback locator, HTTPS→HTTP downgrade, URI user-info ve general-purpose HTTP fallback reddedilir.
- HTTP image/logo/poster/backdrop fetch'i reddedilir. Görsel fetch yalnız mevcut credentialsız HTTPS image policy'siyle açılabilir.
- Private/local address erişimi cleartext consent'ten ayrıdır. Exact-origin private/local onboarding izni yoksa `PublicOnly` policy korunur; bu izin image veya cross-origin request'e taşınmaz.
- Native `MediaSource` sonrasındaki HLS/media subresource veya player-internal redirect zincirinde uygulama same-origin/`EndpointAddressPolicy` enforcement'ı iddia etmez. HTTP Xtream playback için bu yüzey `UNVERIFIED / HIGH RESIDUAL / RELEASE BLOCKER`dır.

### Proprietary adapter ve bounded data

- Xtream-compatible behavior formal standard sayılmaz. Endpoint/action/DTO/playback-path construction Infrastructure adapter'ında versioned compatibility profile olarak kalır.
- `Live`, `Movie`, `Series`, `Season` ve `Episode` türü yalnız typed provider operation sonucundan üretilir; display name, category, URL path veya extension heuristic'i kullanılmaz.
- Source Manager'daki açık `Xtream API` seçimi, exact root `/get.php` biçimindeki yetkili bir M3U URL'sini yalnız bootstrap girdisi olarak kabul edebilir. Bu akış URL'den origin, tekil `username` ve tekil `password` alanlarını transient olarak çıkarır; user-info, fragment, nested/prefix path, duplicate parametre ve malformed percent-encoding fail-closed reddedilir. `type`/`output`, stream path'i, display name ve category hiçbir content-kind kararı üretmez. Yeni Xtream configuration fresh consent ve protected credential owner'ı alır; mevcut Remote M3U source dönüştürülüyorsa `SourceId` korunur ve eski locator yalnız atomic snapshot activation sonrasında retirement journal ile temizlenir.
- Missing field, string/number/bool varyasyonu, duplicate ID, unknown category, malformed nesting ve provider error body untrusted input'tur.
- `VERIFIED — local synthetic contract, 2026-08-29`: account `64 KiB`, category `1 MiB`, Live/VOD/Series top-level list `64 MiB` ve yalnız seçili `series_info` `16 MiB` hard response cap'i kullanır. Declared/body `64 MiB + 1` Xtream response body okunmadan/okunurken fail-closed; genel HTTP `4 MiB` izolasyonu korunur. Varsayılan request süresi 15 saniyedir; JSON depth/string/array/item ve total catalog ayrıca bounded kalır. Unbounded response hiçbir milestone'da kabul edilmez.
- Repository testleri yalnız loopback sentetik provider kullanır. Gerçek provider URL'si, response capture'ı, credential'ı veya content'i commit edilmez.

### Source CRUD ve lifecycle

- Create/replace akışı local validation → consent → protected staging → bounded validation/sync → atomic source/configuration activation sırasındadır.
- Replace başarısız/cancelled olursa eski configuration ve active complete snapshot çalışır kalır; staged secret exact commit-state contract'ıyla temizlenir veya reconciliation'a bırakılır.
- Delete önce yeni admission'ı kapatır ve in-flight operation/playback'i drain eder; sonra source credential'ı, Live/Movie/Series/Season/Episode snapshot'larını, user-state'i ve owned cache'i idempotent temizler.
- Cleartext consent kaydı source configuration'ından bağımsız orphan bırakılamaz ve başka configuration'a kopyalanamaz.

## Consequences and trade-offs

- HTTPS sunmayan yetkili Xtream-compatible hesaplarla compatibility artar.
- Kullanıcının credential'ı local ağ, ISP, proxy, captive portal veya saldırgan aracı tarafından okunabilir; response veya playback hedefi değiştirilebilir. Onay bu riski teknik olarak azaltmaz.
- Remote M3U ve Xtream warning/consent modelleri görsel olarak benzer olabilir, fakat farklı resource key, policy type ve persisted evidence kullanır.
- HTTP image'ların reddi bazı provider kataloglarında poster/logo eksikliği yaratabilir; güvenlik sınırı sessizce gevşetilmez.
- Same-origin initial playback sınırı bazı cross-origin CDN kullanan provider'larla uyumsuz olabilir. Bu uyumsuzluk credential'ı başka HTTP origin'e göndermek için gerekçe değildir.
- Proprietary adapter değişkenliği M17–M19 sentetik contract corpus'u ve capability sonucu gerektirir; “Xtream supported” global sözü verilmez.
- Bu production-source genişlemesi karar anında M15 package/SBOM/CVE ve M16 successor acceptance closure'larını yeniden stale bırakmıştır. Zincirler 2026-08-29 successor hosted kanıtıyla exact yeni closure'lara yeniden bağlanmıştır; sonraki source/package drift'i ilgili kapıyı yine fail-closed `stale-reopen` yapar. Önceki hosted kayıtlar yalnız exact eski source closure'larında tarihsel kanıttır.

## Risks

- Username/password'ın HTTP API/playback request'lerinde açığa çıkması.
- Malicious intermediary'nin content metadata'sını veya initial playback locator'ını değiştirmesi.
- Native player alt-kaynak/redirect trafiğinin approved origin/address sınırından çıkması.
- Remote M3U consent'in yanlışlıkla Xtream consent sayılması.
- Configuration replacement sırasında eski/yeni secret veya consent'in yanlış aggregate'e bağlanması.
- Proprietary response boyutu/nesting/item sayısının bellek, disk veya UI budget'ını aşması.
- Store/privacy/listing metninin gerçek cleartext credential davranışıyla çelişmesi.

Bu riskler R02, R12, R14, R25 ve yeni R31 altında izlenir.

## Validation plan

- Domain: HTTPS default; Xtream HTTP consent-required; Remote M3U/Xtream consent type isolation; exact source/configuration/origin binding; replace sonrası eski consent'in reddi.
- Transport: consent öncesi zero request; exact-origin HTTP API; redirect refusal; no Authorization/Cookie/Referer; HTTPS→HTTP/cross-origin/user-info/address-policy rejection; HTTP image rejection.
- Secret: create/replace/fault/cancel/restart/delete owner binding; raw username/password/full locator için DB/log/UI/TRX/artifact canary scan.
- Provider: sentetik Live/VOD/Series variants; tolerant bounded field parsing; malformed/deep/oversized/duplicate catalog fail-closed sonuçları; heuristic content typing yasağı.
- Playback: exact-origin initial Live/Movie/Episode locator; native subresource/redirect kontrolünün kanıtlanmadığına dair explicit release gate; HTTP/HTTPS capability matrix.
- UI: ayrı Xtream warning metni, blocking consent, persistent warning, keyboard/Narrator/UIA, endpoint/credential replace'te fresh consent.
- Lifecycle: failed replacement old configuration'ı korur; delete bütün content/secret/consent/cache'i temizler; process-crash reconciliation.
- Exact-SDK locked build, deterministic full quality gate, signed package end-to-end journey ve credential-free evidence.
- Public/private flight öncesi Product/Security/Privacy/Legal/Store explicit risk acceptance veya HTTP Xtream release scope'tan çıkarma kararı.

## Evidence status

- **VERIFIED:** RFC 9110'un HTTP/HTTPS güvenlik özellikleri ve URI/credential riskleri [S135].
- **ACCEPTED PRODUCT/ENGINEERING:** Kullanıcının HTTP Xtream compatibility talebi; ayrı consent ve yukarıdaki dar güvenlik sınırı.
- **UNVERIFIED:** Xtream-compatible endpoint/DTO/path davranışının provider evrenindeki kapsamı; gerçek provider uyumluluğu; native player alt-kaynak/redirect enforcement'ı; hostile ağ davranışı; Microsoft Store, KVKK/privacy ve hukuk sonucu.

Bu ADR hukuki görüş değildir ve cleartext credential taşınmasını güvenli ilan etmez.

## Revisit triggers

- Provider HTTPS sunduğunda source migration UX'i HTTPS'i önerir; kullanıcı onayıyla HTTP exception kaldırılabilir.
- Cross-origin HTTP API/playback, HTTP image, cookie/header auth veya redirect forwarding talep edilirse bu karar otomatik genişlemez; yeni threat/privacy kararı gerekir.
- Native player trafiğini gözlemleyip policy uygulayan adapter/proxy düşünülürse backend/data-flow/privacy architecture yeniden açılır.
- Store/privacy/hukuk incelemesi cleartext credential'ı reddederse HTTP Xtream release scope'tan çıkarılır.
- Resmi/normative bir provider specification veya versioned discovery mekanizması ortaya çıkarsa `UNVERIFIED proprietary` profile yeniden değerlendirilir.

## References

- [Post-MVP Content Experience Product Brief](../product/POST_MVP_CONTENT_EXPERIENCE_BRIEF.md)
- [ADR-008 — Remote M3U cleartext HTTP compatibility](ADR-008-remote-m3u-cleartext-http-compatibility.md)
- [Security and Privacy Baseline](../security/SECURITY_AND_PRIVACY_BASELINE.md)
- [Domain and Data Contracts](../architecture/DOMAIN_AND_DATA_CONTRACTS.md)
- [Windows roadmap M17–M19](../roadmap/WINDOWS_MVP_ROADMAP.md)
- [Risk Register](../risks/RISK_REGISTER.md)
- [S135](../research/SOURCES.md)
