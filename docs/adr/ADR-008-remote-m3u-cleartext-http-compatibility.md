# ADR-008 — Remote M3U için açık onaylı cleartext HTTP uyumluluğu

**Status:** Accepted

**Date:** 2026-08-28

## Context / Problem

Windows MVP'nin M3, M5 ve M7 kabul zinciri remote playlist kaynaklarını HTTPS ile sınırlar. Gerçek kullanıcı geri bildirimi, yetkili bir Remote M3U kaynağının yalnız `http` locator sunduğunu göstermiştir. HTTPS-only davranış bu kaynağı daha ağ isteği başlamadan reddeder.

Remote playlist locator'ı query içinde token, username veya password taşıyabilir. Locator'ın DPAPI ile korunması yalnız cihazdaki at-rest kopyayı korur; cleartext HTTP request'i, response playlist'i ve HTTP channel trafiğini ağ üzerindeki gözlemci veya aracıya karşı gizli ya da bütünlüklü yapmaz. Kullanıcı uyarısı da bu eksikliği teknik olarak gidermez.

`VERIFIED`: RFC 9110, HTTPS iletişimini origin authentication ile confidentiality/integrity sağlayan secured connection olarak tanımlar; HTTP origin authority'sinin DNS ve routing saldırılarına açık olduğunu ve credential alışverişinin secured connection gerektirdiğini kaydeder [S135]. Microsoft Store Policies 7.19, ürün güvenliği ile personal-information transmission için modern cryptography yükümlülükleri taşır [S10].

`INFERENCE`: HTTPS sunmayan yetkili Remote M3U kaynaklarına dar, görünür ve geri alınabilir bir compatibility exception sağlamak ürün kullanılabilirliğini artırır. Bu mühendislik kararı Store politikasının belirli bir playlist token'ına nasıl uygulanacağını veya hukuki kabulü çözmez.

## Decision drivers

- Kullanıcının kendi yetkili playlist'ini doğrudan cihazdan açabilmesi.
- HTTPS'i güvenli varsayılan olarak korumak ve cleartext istisnayı kaynak türüne daraltmak.
- Query/token içeren full locator'ın log, diagnostic ve at-rest persistence yüzeylerine sızmaması.
- Redirect, origin ve channel locator kurallarının sessiz downgrade veya kapsam genişlemesi üretmemesi.
- UI'ın güvenli ve güvensiz transport'u açıkça ayırması.
- M3/M5/M7 tarihsel kabul kanıtlarını geriye dönük yeniden yazmamak.

## Considered options

1. HTTPS-only davranışı korumak.
2. Yalnız query/user-info taşımayan anonymous Remote M3U için HTTP açmak.
3. Açık kullanıcı onayıyla query/token taşıyabilen Remote M3U HTTP locator'ını dar policy ile açmak.
4. HTTP kaynağını geliştirici backend'i üzerinden HTTPS'e proxy etmek.

Backend proxy seçeneği ürünün direct-provider ve no-backend gizlilik sınırını değiştirir; kapsam dışıdır. Anonymous-only seçenek gerçek query-token playlist'ini desteklemez. Seçilen yön üçüncü seçenektir.

## Decision

HTTPS bütün source türleri için varsayılandır. Cleartext istisna yalnız `RemotePlaylist` source kind'ına uygulanır:

- Remote M3U locator'ı `https` veya açık UI onayıyla `http` olabilir. Xtream-compatible endpoint yalnız `https` kalır.
- `http` locator query/token taşıyabilir; URI user-info her iki scheme için reddedilir. Full locator yine yalnız owner-bound protected store'da tutulur ve raw diagnostic, log, UI status, telemetry veya artifact'a yazılmaz.
- Ağ isteğinden önce scheme-specific kullanıcı onayı gerekir. UI, HTTP'nin locator/query, playlist içeriği ve HTTP channel trafiğini ağda görünür ve değiştirilebilir bıraktığını açıkça söyler.
- Onay yalnız oluşturulan exact source configuration içindir. Persisted `SafeEndpoint.Scheme == "http"` kaynak seçildiğinde ve ayrıntısı gösterildiğinde kalıcı bir insecure-transport uyarısı üretir; kaynak varlığı güvenli transport iddiası değildir.
- HTTP request'e `Authorization`, `Cookie` veya `Referer` eklenmez. Query token locator'ın parçası olarak gönderilir; bu nedenle wire üzerinde cleartext olduğu uyarıda açıkça belirtilir.
- HTTP source'taki initial absolute veya relative HTTP channel locator yalnız source'un exact `SafeEndpoint` değeriyle aynı scheme, IDNA host ve effective port origin'ine resolve olursa kabul edilir. HTTPS source'tan HTTP channel'a geçiş ve başka HTTP origin'e initial channel locator reddedilir.
- HTTPS channel locator initial URI/scheme validation ile kabul edilir. HTTP source'tan HTTPS channel'a geçiş transport upgrade'idir; fakat locator native `MediaSource`'a verildikten sonra uygulamanın source-download transport'undaki `EndpointAddressPolicy` garantisi player trafiği için iddia edilmez.
- HTTP logo locator her durumda reddedilir. Logo fetch yalnız HTTPS, credentialsız ve mevcut address/image budget policy'siyle çalışır.
- Remote M3U playlist download'unun initial HTTP request'indeki same-origin HTTP redirect bounded policy içinde kabul edilebilir. Cross-origin HTTP redirect ve HTTPS→HTTP downgrade reddedilir. HTTP→HTTPS redirect, target yeniden doğrulanarak upgrade olarak kabul edilebilir.
- Remote M3U onboarding, scheme'den bağımsız olarak locator'ı preliminary full-body probe ve import için iki kez göndermez. Local validation — HTTP için ayrıca açık onay — tamamlandıktan sonra locator owner-bound protected store'da stage edilir ve tek bounded streaming import isteği network otoritesi olur; import sonucu açıkça `NotCommitted` ise exact protected kayıt cancellation'dan bağımsız cleanup ile silinir, commit durumu belirsizse recovery için korunur.
- Remote M3U yolu HTTP/HTTPS ayrımı olmadan decompressed response'u `128 MiB`, toplam request süresini iki dakika ile sınırlar. Genel HTTP/Xtream response bütçesi `4 MiB` ve varsayılan request süresi 15 saniye olarak kalır; remote-playlist bütçesi başka factory veya feature'a taşınmaz.
- Incremental parser'ın fiziksel satır tavanı `65.536` UTF-16 kod birimidir. Bu, birden fazla bounded metadata alanı taşıyan geçerli `#EXTINF` satırları için dar bir compatibility düzeltmesidir; unbounded read değildir. Locator ve generic metadata value tavanı `4.096`, `tvg-id` `512`, channel/group name `256`; decoded toplam `128 Mi` UTF-16 kod birimi ve decompressed response `128 MiB` olarak değişmeden kalır. `65.536` üzerindeki tek satır raw içerik taşımayan `PlaylistLineLimitExceeded` ile fail-closed reddedilir.
- Açık-onaylı HTTP compatibility kaynağında 50.000 geçerli entry persistence sınırı aşılırsa provider sırasındaki ilk 50.000 entry sink'e yazılır; sonraki entry'ler yazılmadan sayılır ve kalan stream encoding, line ve `128 MiB` body sınırları içinde sonuna kadar tüketilir. Commit sonucu `EntryLimitReached` taşır, parser-skipped toplamı snapshot/sync warning sayısına eklenir ve UI kullanıcının eksik katalog gördüğünü açıkça bildirir. HTTPS/direct parser'ın 50.001'inci geçerli entry'de atomik fail-closed davranışı tarihsel M14 sözleşmesi olarak kalır; sıfır kullanılabilir entry hiçbir scheme'de `Ready` snapshot oluşturmaz.
- Parser/import failure'ı raw response, playlist satırı, URL/host, credential veya exception metni taşımayan stable `DomainErrorCode` ile ayrılır. Final response address, `#EXTM3U` header, strict UTF-8, fiziksel satır (`PlaylistLineLimitExceeded`), decoded toplam (`PlaylistTotalLimitExceeded`), entry (`PlaylistEntryLimitExceeded`), structure, HLS-manifest, zero-usable ve tüm channel locator'larının URL/origin policy'since reddedilmesi ayrı append-only kodlardır. Bu tanı yüzeyi policy'yi gevşetmez ve Operation ID'yi diagnostic log korelasyonu diye sunmaz.
- Automatic redirect, cookies ve proxy kapalı; bounded response/retry, endpoint-address policy, cancellation, redaction ve hostile-input kontrolleri değişmez. HTTP olması private/local address iznini kendiliğinden vermez; mevcut exact-origin onboarding onayı ayrıca gerekir.
- Cleartext HTTP sonucu güvenli, encrypted veya private diye adlandırılmaz. At-rest protection yalnız cihazdaki kaydı korur; transit confidentiality, integrity veya server authentication sağlamaz.

Bu karar Remote M3U download ve ondan türetilen initial channel locator admission zincirini kapsar. Kabul edilen same-origin initial HTTP locator native player'a verilir. Bu izin kullanıcıdan gelen arbitrary/cross-origin HTTP playback URL'ı için genel admission değildir. Xtream HTTP, HTTP logo, header/cookie authentication, cross-origin HTTP playlist redirect, backend proxy ve TLS bypass kapsam dışıdır.

## Consequences and trade-offs

- HTTPS-only provider uyumsuzluğu dar Remote M3U yolunda azalır.
- Büyük fakat bounded `m3u_plus` katalogları genel 4 MiB response tavanına takılmaz; yaygın whitespace-ayrımlı `#EXTM3U` header öznitelikleri kabul edilir. Parser limitini aşan tekil `tvg-id`/`group-title` kaydı bütün importu bozmak yerine atlanır.
- Birden çok izinli metadata alanı nedeniyle `8.192` kod birimini aşabilen `#EXTINF` satırları artık `65.536` kod birimine kadar incremental ve bounded işlenebilir; hard cap'in korunması bellek tüketimini ve saldırgan girdiyi sınırsız bırakmaz. Bu değişiklik gerçek bir provider response'unun incelendiği veya belirli bir kullanıcı kaynağının kesin olarak düzeldiği garantisi değildir.
- Büyük HTTP `m3u_plus` kataloğu 50.000 persistence bütçesini aşsa bile bütün transaction geri alınmaz; ilk 50.000 geçerli entry kullanılabilir kalır ve truncation sessiz değildir. Bu tercih provider sırasını authoritative yapar: refresh sırasında sıra değişirse limit sınırındaki kanallar değişebilir ve birleşik Live/VOD/Series listesinde Live kayıtların bir kısmı cap dışında kalabilir. Generic M3U'da güvenilir içerik-türü ayrımı olmadığı için `/live`/`movie`/`series` path heuristic'i uygulanmaz.
- Her Remote M3U locator için preliminary full-body probe'un kaldırılması yinelenen network isteğini önler; HTTP query locator açısından bu, cleartext credential maruziyetini azaltır fakat tek istek dahi ağ üzerinde gizli veya bütünlüklü değildir. Importtan önce oluşan protected staging kaydı, açık `NotCommitted` cleanup ve indeterminate-commit retention kurallarına tabidir.
- Query token, playlist içeriği ve same-origin HTTP channel trafiği on-path gözlem ve değiştirmeye açık kalır; uygulama bunu teknik olarak önleyemez.
- Malicious intermediary playlist channel locator'ını değiştirmeye çalışabilir. Same-origin HTTP sınırı, HTTPS locator policy'si, address checks, parser budget'ları ve protected persistence etki alanını daraltır; cleartext bütünlük sağlamaz.
- Same-origin kuralı yalnız uygulamanın parse ettiği initial channel locator'da uygulanır. Native `MediaSource` sonrasındaki HLS/media alt-kaynak URI'leri ile player-internal redirect zinciri uygulama tarafından güvenilir biçimde gözlemlenmez; aynı-origin veya `EndpointAddressPolicy` burada enforce edilmiş sayılmaz. Bu yüksek residual risk release blocker'dır.
- `SafeEndpoint` HTTP/HTTPS origin'i secretsiz biçimde ayırmaya devam eder. Scheme'i yok sayan origin equality yasaktır.
- UI ve persistence, HTTP durumunu source yaşam döngüsü boyunca görünür tutmalıdır.
- Store/private-flight, privacy ve hukuk değerlendirmeleri yeniden gözden geçirilmeden bu karar release kabulü sayılmaz.

## Risks

- Query içindeki credential veya token'ın yerel ağ, ISP, captive portal, proxy ya da saldırgan aracı tarafından okunması.
- Playlist veya HTTP channel response'unun değiştirilerek farklı içeriğe yönlendirilmesi.
- Native player'ın görünmeyen HLS/media alt-kaynak veya redirect zincirinin initial same-origin sınırından çıkması ya da special-use adrese yönelmesi.
- Genel transport factory'sindeki HTTP izninin Xtream, logo veya başka feature'a yanlışlıkla yayılması.
- Bir warning checkbox'ının kullanıcı tarafından güvenlik garantisi gibi yorumlanması.
- Provider sırası değiştiğinde ilk-50.000 diliminin değişmesi veya birleşik katalogda Live kayıtların cap dışında kalması.
- Store certification veya privacy metninin gerçek cleartext davranışla çelişmesi.

Bu riskler [R14 ve R30](../risks/RISK_REGISTER.md) altında izlenir.

## Validation plan

- Domain: Remote M3U `http` + query kabulü; user-info reddi; Xtream HTTP reddi; scheme-aware `SafeEndpoint` equality.
- Transport: direct HTTP, explicit port + Xtream-style query, same-origin HTTP redirect, cross-origin HTTP redirect reddi, HTTP→HTTPS upgrade, HTTPS→HTTP downgrade reddi, HTTP request'te Authorization/Cookie/Referer yokluğu, Remote M3U'ya özel `128 MiB`/iki dakika ile genel `4 MiB` sınırının ayrılığı.
- Parser/import: `#EXTM3U` header öznitelikleri, `8.192` üzeri fakat `65.536` dahil bounded fiziksel satır kabulü, `65.536` üzeri satırın exact `PlaylistLineLimitExceeded` reddi, downstream metadata sınırında tekil-entry skip, 4 MiB üzeri bounded katalog, 50.001 Xtream-shaped HTTP entry'de ilk 50.000 commit + tail drain + visible `EntryLimitReached`, aynı corpus'un HTTPS/direct yolunda `PlaylistEntryLimitExceeded` ile fail-closed kalması, decoded toplam aşımının `PlaylistTotalLimitExceeded` olması ve zero-usable-entry reddi; HTTP source relative ve same-origin absolute HTTP channel kabulü; cross-origin HTTP, HTTPS-source→HTTP ve HTTP logo reddi; HTTPS channel kabulü. Header/UTF-8/structure/HLS/no-usable/address-policy failure'larının exact safe code'a ayrıldığı ve raw input taşımadığı sentetik corpus ile doğrulanır.
- Onboarding: HTTP ve HTTPS locator için preliminary full-body probe olmadan tek bounded streaming import request'i; HTTP'de network öncesi açık onay, açık `NotCommitted` failure'da exact protected-record cleanup ve indeterminate commit'te retention.
- Native playback: initial locator admission'ının player-internal HLS/media subresource ve redirect enforcement kanıtı diye yorumlanmaması. Bu trafiği gözlemleyip aynı-origin/address policy uygulayan çözüm veya açık Product/Security/Privacy/Legal risk kabulü ve adversarial packaged-device kanıtı olmadan release blocker kapanmaz.
- UI/persistence: scheme-specific onay olmadan request çıkmaması; restart ve source selection sonrasında kalıcı insecure warning; raw locator/query'nin UI/log/artifact'a girmemesi.
- Security: hostile playlist, address-policy, redaction, canary ve source deletion/reconciliation regression'ları.
- En dar testlerden sonra exact-SDK locked restore, Debug/Release x64 build ve full quality gate.
- Production source/package değiştiği için M15 package/SBOM/CVE ve M16 final-artifact, security/architecture ve synthetic-journey acceptance closure'ları successor commit için yeniden üretilir. Önceki hosted sonuçlar yalnız bağlı oldukları exact commit/closure için tarihsel kanıttır.

## Evidence status

- **VERIFIED:** RFC 9110 ve Store Policy 7.19 metinleri, erişim 2026-08-28 [S10][S135].
- **INFERENCE:** Dar exception'ın compatibility faydasının residual cleartext riskine değdiği ürün/mühendislik kararı.
- **UNVERIFIED:** Native player'ın HLS/media alt-kaynak ve redirect zincirinin origin/address davranışı, kullanıcıya ait gerçek provider response'u ve bu bounded parser düzeltmesinin onu çözüp çözmediği, gerçek provider evreni, hostile ağdaki saha davranışı, Microsoft Store acceptance, KVKK/privacy ve hukuk sonucu.

Bu ADR hukuki görüş değildir. Public release öncesi Product/Security/Privacy/Legal ve Store review kapıları ayrıca kapanmalıdır.

## Revisit triggers

- Provider HTTPS sunduğunda veya HTTP kullanım oranı support verisinde anlamlı olmaktan çıktığında exception kaldırılır.
- HTTP izninin Xtream, logo, cross-origin stream, Authorization/Cookie ya da başka platforma genişletilmesi istenirse yeni karar gerekir.
- Backend proxy, VPN/tunnel, certificate bypass veya remote config gündeme gelirse threat/privacy architecture yeniden açılır.
- Store, privacy veya hukuk incelemesi cleartext credential/query taşınmasını kabul etmezse HTTP source admission release scope'undan çıkarılır.

## References

- [Security and Privacy Baseline](../security/SECURITY_AND_PRIVACY_BASELINE.md)
- [Domain and Data Contracts](../architecture/DOMAIN_AND_DATA_CONTRACTS.md)
- [Windows MVP Roadmap](../roadmap/WINDOWS_MVP_ROADMAP.md)
- [Risk Register](../risks/RISK_REGISTER.md)
- [S10, S135](../research/SOURCES.md)
