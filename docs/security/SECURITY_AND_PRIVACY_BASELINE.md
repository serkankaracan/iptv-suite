# Güvenlik ve gizlilik baseline'ı

**Tarih:** 2026-08-09

**Durum:** M3 validation/redaction contract'ı implemented; M4 CurrentUser protected-storage foundation `IN PROGRESS`, exact layout/lifecycle acceptance pending

**Kapsam:** Windows MVP; platformlar aynı ilkeleri kendi güvenli API'leriyle uygular

## 1. Güvenlik hedefi ve sınırlar

Uygulama yalnız kullanıcının yetkili source'u ile cihazdan doğrudan haberleşir. Geliştirici backend'i, analytics, reklam SDK'sı veya cloud account ilk MVP'de yoktur. Bu yaklaşım üçüncü tarafa aktarılan veriyi azaltır; cihazdaki credential'ı, kötü niyetli playlist/logo/stream'i, native player'ı ve Store/privacy yükümlülüğünü ortadan kaldırmaz.

Baseline hedefleri:

- secret hiçbir zaman source code, Git, plaintext DB/config, log, telemetry, UI error veya test artifact'ına girmez;
- credential-bearing trafik doğrulanmış TLS olmadan çıkmaz;
- dış input bounded ve cancellation-aware işlenir;
- local data minimum süre tutulur, kullanıcı tarafından silinebilir ve migration sırasında açığa çıkmaz;
- exact native/OSS dependency seti izlenir ve güvenlik güncellemesi alır;
- uygulama content/provider sağlamaz, erişim kontrolü veya DRM aşmaz.

M3 local kabulü; raw locator/credential döndürmeyen source validation, scheme+IDNA host+effective port ile sınırlı `SafeEndpoint`, random opaque references, registry-only stable safe errors ve provider host'unu default gizleyen pure URI/header/untrusted-text redaction contract'ını 105 testlik iki-run gate içinde doğrulamıştır. M3 kapsamı production persistence veya log sink implementation'ı içermiyordu; sonradan eklenen M4 protected-storage foundation aşağıda ayrı kanıt ve sınırlarıyla kaydedilir. Wrong-user, source deletion/reconciliation, DB/WAL/SHM taraması ve retention enforcement hâlâ `UNVERIFIED` durumdadır.

M3 opaque reference'ın temsilini doğrular, fakat protected record binding'ini kanıtlamaz. M4 foundation store-owned issuance ile kriptografik source/purpose/reference context binding'ini ekler. Owner-bound v2 dilimi ayrıca source credential/remote playlist kaydını fresh `SourceConfigurationId`ye; stream/logo kaydını `ChannelId`ye bağlar. Exact owner kind/ID hem store key, filename digest ve DPAPI entropy'de hem encrypted envelope context'inde doğrulanır; same-source/same-purpose cross-owner read/update fail-closed, yanlış-owner delete doğru kayda dokunmadan idempotent success'tir. Owner integrity context'idir, authorization principal değildir. Güncel source-draft application operation'ı validation'ı store mutation'ından önce tamamlar; protected create başarısından sonra exact store-issued reference, `SourceId` ve configuration owner'ı validated draft'a birlikte bağlar. Doğrudan await edilen çağrıda commit sonrası cancellation yeniden gözlenmez ve reference başarılı sonuçla döner. Bu davranış caller abandonment/retry, aynı `SourceId` için yinelenen create, process crash/OOM, metadata/configuration persistence, update rollback, deletion veya reconciliation atomikliği sağlamaz. M5'te resolve edilen locator yeniden HTTPS validation'dan geçirilip beklenen endpoint/source ile eşleştirilir. Owner+reference+endpoint tuple'ının birlikte değiştirilmesi, missing-reference ve origin equality geçmeden opaque ID/owner tek başına authorization veya origin kanıtı sayılmaz.

2026-08-13 M4 foundation; store-owned reference issuance, bounded input/record, owned-buffer zeroization, store-operation safe failure mapping ve central sanitizer'ı uygular. Güncel source-draft operation'ı strict UTF-8, versioned ve length-prefixed source payload v1'i `SecretStoreLimits` sınırında üretir; Xtream için full locator+username+password'ı, remote playlist için full locator'ı yalnız owner-bound protected-store create call'ına verir ve temporary byte buffer'ı `finally` içinde sıfırlar. Internal source-payload decoder exact format ve strict UTF-8/scalar/control kurallarını yalnız numeric slice metadata'sıyla fail-closed doğrular; managed string/URI veya ikinci plaintext buffer üretmez. Protected-store envelope/path/temp namespace ise semantic-owner binding nedeniyle v2'dir; composition root başarılı `LocalCache\ProtectedStore\v2` store'unu uygulama ömrü boyunca tutar. V1 pre-release namespace otomatik okunmaz, migrate edilmez veya silinmez; released user-data migration iddiası yoktur. Public resolve consumer'ı M5 origin ve lifecycle gate'leri tamamlanmadan açılmaz. Factory initialization başarısızsa pencereyi oluşturmadan fail-closed olur, daha zayıf fallback kullanmaz ve beklenen hataları raw path/message/store taşımayan typed sonuca eşler. Hosted run `31735655363`, owner-bound v2'den önceki 153-test/v1 snapshot'ı ve tarihsel packaged directory kanıtıdır. Owner-v2 snapshot'ı 171/171; packaged lifecycle harness dahil güncel local exact-SDK gate architecture 13, unit 127 ve integration 32 olmak üzere 172/172 testi iki deterministik koşuda geçmiştir. V2 hosted/package lifecycle kanıtı henüz pending'dir. Arbitrary mid-I/O cancellation/interleaving ve cross-process sıralama kanıtı yoktur. SafeEndpoint/origin equality M5; installed package update/reset/uninstall, second-user, source-deletion reconciliation ve 50k layout kararı ise açık M4 hard-gate'tir.

Ayrı `IptvSuite.PackageLifecycleHarness`, production package ve startup graph'ından ayrılmış disposable bir test MSIX'idir. Aynı package version/PFN ve CurrentUser bağlamında iki process launch arasında v2 record create/read, yanlış-owner read/delete negative, doğru-owner update/read/delete ve post-delete yokluk matrisini yürütür. Cross-launch source/owner/reference bilgisi ve payload digest'i DPAPI CurrentUser ile korunan bounded binary ticket'ta kalır; plaintext canary exact `ProtectedStore` veya lifecycle run-directory taramasında bulunursa lane fail eder. Windows-managed mutable package hive'ları bu owned-write-surface kanıtının dışındadır. Scanner bulgu, operasyonel hata ve CLI contract sapmasını ayrı stable code'larla fail-closed tutar. Fixed phase/run ID dışında process argument'i yoktur; raw value/reference/path/PID/SID/user/host/exception evidence'a yazılmaz. Test MSIX'i upload edilmez ve exact package/certificate/app-data/output cleanup başarısızsa success evidence oluşturulmaz. Bu tasarım same-version process restart kanıtıdır; signed package update/reset/reinstall, production PFN, second-user, same-user malicious process, crash/power-loss ve source-wide reconciliation kanıtı değildir. Hosted zero-exit sonucu henüz `PENDING`dir.

Mevcut managed containment + `ReparsePoint` kontrolleri check-to-use yarışını tamamen kapatmaz; aynı-user arbitrary path mutation hardening'i handle-relative Windows I/O veya açık threat-model kararı gerektirir. Exact stale-temp cleanup non-adversarial filesystem yarışlarında fail-closed/best-effort sınırındadır; active/recent temp korunur ve silinemeyen exact stale temp safe initialization failure üretir. Cleanup, exact lowercase current-namespace aday listesini 1.024 girdide sınırlar; 1.025'inci exact adayda hiçbir adayı silmeden fail-closed olur. Bu cap mutation öncesi aday belleğini sınırlar; dizin aramasının toplam süresini veya aynı-user candidate-fill DoS'unu çözmez. Metadata referansı kaybolmuş `.dpapi` source record'larının source-wide startup reconciliation'ı henüz uygulanmamıştır. Production composition factory expected initialization hatalarını safe typed sonuca eşler; test/adapter constructor'ı doğrudan çağrıldığında exception contract'ını korur. Process-local gate yalnız çakışan store operasyonlarını sıralar; caller'a daha önce teslim edilmiş plaintext lease geriye dönük revoke edilmez. Source deletion coordinator yeni resolve'ları durdurup in-flight operasyonların/lease kullanımının kapanmasını beklemelidir. Bunlar plaintext sızıntı kanıtı değildir, fakat M4 `Completed` öncesi çözülmesi gereken orphan/DoS/lifecycle yüzeyleridir.

## 2. Veri sınıflandırması

| Sınıf | Örnek | Saklama | Log/diagnostics |
|---|---|---|---|
| Secret | Password, token, Authorization, Cookie, URI user-info, credential-bearing full M3U/stream URL | Yalnız platform-protected store/blob; mümkün olan en kısa plaintext lifetime | Hiçbir koşulda yazılmaz |
| Sensitive metadata | Provider origin, channel list, categories, favorites, recently played, logo host | Device-local DB/cache; minimizasyon ve delete lifecycle | Default loglanmaz; source ID ve count kullanılır |
| Public/config | App version, schema/parser version, feature flag, player build | Secretsiz local config/package | Yapılandırılmış biçimde yazılabilir |
| Untrusted content | Channel/category name, provider error/body, playlist directive, image bytes | Length/type-bound; normalize veya cache | Raw değer yok; gerekirse hash/count/warning code |
| Operational | Random operation ID, duration, item count, stable domain error code | Bounded local log | İzinli, secret-free |

`SecretReference` ve `ProtectedLocatorReference` opaque değerlerdir; `ToString` veya serialization secret veremez. Channel/provider DTO'su credential taşımamalıdır.

## 3. Threat assumptions

| Threat | Varsayım/etki | Kontrol |
|---|---|---|
| Malicious playlist/API payload | Oversized line/body, parser ambiguity, unsafe URI, injection, memory exhaustion | Byte/line/item/depth limits, incremental parser, allowlisted schemes, parameterized SQL, cancellation |
| Malicious logo/image | Decompression bomb, malformed codec, local-network probing, cache abuse | MIME+magic validation, byte/pixel/dimension cap, bounded decode/concurrency, address policy, LRU |
| Malicious/broken stream | Native parser exploit, hang, endless buffer/retry, secret echo | Patched engine, bounded open/retry, adapter isolation boundary, sanitized logs, corpus/fuzz/soak |
| Credential leak | Query/user-info/header, exception, crash dump, clipboard, screenshot | Opaque refs, central sanitizer, no full memory dump support artifact, scan tests |
| Network attacker | HTTP sniffing, invalid certificate, downgrade, redirect theft | HTTPS credential gate, OS trust validation, no bypass, manual redirect, no cross-origin auth |
| Local DB theft | Channel preferences visible; protected secrets targeted | DPAPI CurrentUser blobs, app-data ACL, minimization, deletion; acknowledge same-user threat |
| Same-user malicious process | Can read app files/process memory under desktop threat model | DPAPI reduces at-rest theft but is not process sandbox; OS account security and short secret lifetime |
| Supply-chain compromise | Native DLL/plugin or NuGet update | Exact lock, official source, hashes/signatures where supplied, SBOM, license/CVE review |
| Store/reviewer data exposure | Real account shared for certification | Only developer-owned synthetic service and fake credentials |
| Unauthorized product use | User connects to unlicensed service | No provider discovery/list, clear BYO-authorized terms, no recording/DRM bypass; Store/legal review |

Not protected against: fully compromised Windows user/admin/kernel, screen capture by the OS/user, or a provider itself mishandling data. These limits are stated honestly; “encrypted” is not marketed as complete endpoint protection.

## 4. Credential ve locator storage

### 4.1 Windows kararı

[ADR-003](../adr/ADR-003-local-persistence-and-secure-storage.md) Proposed baseline'ı:

- metadata/snapshot/index: SQLite;
- source credential ve raw sensitive locator: DPAPI `CurrentUser` / Windows `LOCAL=user` protected blob;
- packaged app'in device-local ve cloud-backup dışı LocalCache alanı;
- app DB'de yalnız opaque reference;
- Xtream stream URL'si mümkünse provider key + source secret'tan just-in-time oluşturulur;
- M3U stream locator'ı protected reference ile çözülür.

Credential Locker primary değildir: roaming/bulk-locator uyumsuzluğuna ek olarak full-trust non-AppContainer process'te user locker isolation'ı beklenmemelidir. Belgelenen 20-record sınırı UWP/AppContainer desktop bağlamına özgüdür; mevcut full-trust package için otomatik gerekçe sayılmaz ve 20/21 probe'u açık kalır. `LocalMachine` kapsamı kullanılmaz. Kendi master-key/crypto formatı tasarlanmaz.

DPAPI-per-locator 50k bütçeyi geçmezse yeni security decision gerekir. Muhtemel seçenek yalnız standard authenticated encryption kullanan, per-source DEK'i DPAPI ile saran envelope modelidir. Nonce uniqueness, rotation, crash consistency, key deletion ve migration review edilmeden uygulanmaz.

### 4.2 Secret lifetime

1. Form input'u view state'te yalnız işlem süresince kalır; diagnostics ve navigation state'e serialize edilmez.
2. Başarılı explicit “save” işleminde protected store'a gider.
3. HTTP/player adapter secret'ı operation başlangıcında çözer, yalnız hedef request/session'a verir.
4. Raw locator tekrar domain/UI'ya dönmez.
5. Managed string'in güvenli wipe garantisi olmadığı kabul edilir; kopya sayısı ve lifetime azaltılır.
6. Native player'a verilen URL/header'ın engine loglarına düşmediği M10'da canary scan ile kanıtlanır.
7. Clipboard, drag/drop, toast, recent documents veya OS search index'e secret yazılmaz.

## 5. URL, header ve log redaction

### 5.1 Tek merkezî politika

Log çağıran her component kendi regex'ini yazmaz. Typed fields veri sınıfı taşır ve tek sanitizer çıkıştan önce uygular. Formatter'a raw secret verilmemesi ilk savunmadır; sanitizer ikinci savunmadır.

URI politikası:

- raw URI string'i log template'ine verilmez;
- parsing başarılıysa scheme ve gerekirse safe host/origin ayrı alan olur; path default redacted, fragment silinir;
- user-info bütünü kaldırılır;
- bütün query **değerleri** default `[REDACTED]` olur; key adları bile `password/token/key/auth/signature` gibi ise yalnız `secret-param` sayacı tutulur;
- parse başarısızsa input'un parçası yazılmaz, `invalid-uri` + length + random operation ID yazılır;
- URL-encoded, nested URL ve provider response içinde tekrar edilmiş credential ayrıca test edilir;
- source display name/channel name gibi untrusted text operational logda yer almaz.

Her zaman redacted header'lar:

- `Authorization`, `Proxy-Authorization`;
- `Cookie`, `Set-Cookie`;
- API key/token iması taşıyan custom header;
- provider tarafından geri yansıtılan request header/body.

Response body, playlist satırı, player command line, environment ve full exception `Data` dump edilmez. Exception type + mapped error code + safe stack, development build'de yalnız lokal ve bounded tutulur. Kullanıcı UI'ı exception/type/stack göstermez.

### 5.2 Log retention

- Production'da default ayrıntılı network/player debug kapalıdır.
- Secret-free local operational log için provisional rolling retention en fazla 7 gün ve 20 MiB'dir; M15 privacy review değiştirebilir.
- Support export explicit kullanıcı eylemidir; export öncesi tekrar sanitization ve canary scan uygulanır.
- Full process memory dump veya raw libVLC log paketi otomatik support artifact'i değildir.
- Analytics/remote telemetry yoktur. Eklenirse bu baseline, KVKK/store declarations ve threat model yeniden açılır.

## 6. Network güvenliği

### 6.1 HTTP client policy

- Long-lived/typed `HttpClient` ve bounded connection lifetime.
- OS trust store ve hostname validation her zaman açık; custom “accept all” callback yasak.
- Connect, request, total retry, response bytes, decompressed bytes ve item limitleri ayrıdır.
- `ResponseHeadersRead` ile streaming; cancellation bütün read/parse/write pipeline'ına akar.
- Automatic redirect kapalı veya uygulama tarafından manual doğrulanır; maximum hop ve loop detection vardır.
- Origin değişiminde Authorization/Cookie/user-info/query credential forward edilmez.
- HTTPS→HTTP downgrade reddedilir.
- Cookie default kapalı; kanıtlanmış provider ihtiyacında source-scoped container ve delete lifecycle gerekir.
- Retry yalnız safe/idempotent GET ve transient sınıflarda; exponential backoff + jitter + `Retry-After` cap. Auth, TLS, validation ve parse error retry edilmez.
- DNS sonucu logo/image için connect öncesi address policy'ye, redirect sonrası tekrar policy'ye tabi olur.

### 6.2 HTTP-only provider kararı

Credential, token veya kişisel playlist locator'ı taşıyan HTTP **reddedilir**; kullanıcı uyarısı trafiği şifrelemez. Anonymous media için HTTP compatibility ticari gereksinim haline gelirse:

- ayrı ADR/threat/Store review,
- açık per-source opt-in ve kalıcı warning,
- hiçbir sensitive header, cookie, user-info veya query değeri olmaması,
- HTTPS'ten sessiz downgrade olmaması,
- UI'da güvenli/güvensiz durumun açık ayrımı

olmadan açılmaz.

### 6.3 Image ve local-network policy

Logo fetch player request'inden ayrıdır:

- yalnız `http/https`; `file/data/javascript/ftp/smb` reddedilir;
- credential, Cookie, Authorization ve Referer verilmez;
- loopback, link-local, multicast, unspecified ve private address default reddedilir;
- kullanıcı açıkça private source origin yapılandırdıysa yalnız aynı-origin erişim yeni policy ile değerlendirilebilir;
- DNS rebinding/redirect'te final IP tekrar kontrol edilir;
- MIME/magic eşleşmesi, byte ≤ provisional 5 MiB, dimension/pixel/decode-time budget;
- concurrency 4, memory cache 32 MiB, disk LRU 200 MiB provisional;
- source silme cache namespace'ini temizler.

## 7. Local data lifecycle

| Olay | Secret | SQLite/snapshot | Image/download cache | Log |
|---|---|---|---|---|
| Draft/validation fail | Persist edilmez | Draft ancak secretsizse | Yok | Safe error code |
| Successful source save | Protected create/update | Source metadata | Yok | Source ID |
| Successful refresh | Protected locator atomic set | New snapshot activate | Lazy | Counts/duration |
| Failed/cancelled refresh | Yeni orphan protected data temizlenir | Staging rollback; old active kalır | Partial temp silinir | Safe stage/error |
| Source delete | Önce network-disabled, sonra secrets/locators delete | Source/snapshot/favorites policy delete | Namespace delete | Deletion result |
| “Clear all local data” | Bütün protected refs delete | DB close/delete/recreate | Tümü | Son güvenli kayıt ardından log delete |
| Upgrade/migration | Plaintext temp yok | Forward migration + backup/recovery policy | Version invalidation | Schema version only |
| Reset/uninstall | Platform davranışı M4/M15'te doğrulanır | MSIX app data temizliği doğrulanır | Temizlenir | Temizlenir |

Bu tablo hedef lifecycle policy'sidir; mevcut M4 foundation yalnız exact stale ciphertext temp cleanup'ını uygular; source-wide deletion, `DeletionPending` orchestration veya startup protected-record orphan reconciliation uygulamaz. Tamamlandığında silme kısmi başarısızsa source `DeletionPending` ve network/playback-disabled olur; startup reconciliation DB refs ile protected records arasındaki orphan'ları idempotent temizler. Secret silmeden katalog kaydını yok ederek ulaşılamaz secret bırakılmaz.

Snapshot retention provisional olarak active + bir önceki complete snapshot'tır; eski snapshot protected locator'ları pruning işleminde silinir. Favorites ve recently-played için retention/privacy product kararı gerekir; recently-played MVP'de zorunlu değildir.

## 8. Parser, database ve player güvenliği

### Parser/API

- Response body, JSON depth/string/array, M3U line/attribute/item ve total byte cap.
- UTF-8/BOM; legacy fallback yalnız explicit warning ve corpus kanıtıyla.
- Unicode NFC, control removal, length cap; display string SQL/log/markup olarak yorumlanmaz.
- Unknown directive ignored + bounded count; unbounded diagnostics yok.
- Parameterized SQL; provider string'i identifier/order clause olmaz.
- HLS media manifest channel catalog olarak import edilmez.

### SQLite

- App-owned schema version ve forward migration.
- Foreign keys ve integrity checks; atomic snapshot activation.
- DB/WAL/SHM canary secret scan.
- Install directory'ye yazım yok.
- Backup/export MVP dışıdır; debug DB artifact'ı gerçek kullanıcı verisi içeremez.

### Native player

- Exact non-GPL package/plugin allowlist ve SBOM.
- Unused protocol/access plugin'leri mümkünse build/package review ile çıkarılır; fonksiyon kaybı corpus'ta ölçülür.
- Engine option/URL logları sanitized; raw log shipping yok.
- Open/stop/retry bounded; close/dispose session ownership.
- Güvenlik update SLA: critical exploitable media-parser CVE release öncesi blocker; yayın sonrası triage ve gerekirse Store withdrawal/forced update planı.
- Codec/parser fuzz corpus ve malformed stream testleri hakları temiz fixture kullanır.

## 9. Privacy ve mağaza hazırlığı

Yayın öncesi uygulama içinde ve Store listing'de erişilebilir privacy policy bulunur. En az:

- uygulamanın içerik/abonelik/provider sağlamadığı;
- credential ve katalog metadata'sının device-local saklandığı;
- trafiğin geliştirici backend'inden geçmediği ve user-selected provider'a doğrudan gittiği;
- amaç, retention, cache, delete/reset/uninstall davranışı;
- analytics/reklam/third-party crash SDK bulunmadığı;
- support/export süreci ve iletişim;
- kullanıcı hakları ve ilgili ülke mekanizmaları

açıklanır.

Microsoft certification notes için yalnız geliştiriciye ait sentetik M3U/Xtream fixture ve çalışan fake account sağlanır. Store policy'nin “server'dan gelen content” kapsamı bulunduğu kabul edilir; BYO model onay garantisi değildir.

Samsung tarafında Partner Seller/Content Manager/Türkiye dağıtımı ayrı P0 blocker'dır. Tizen `WidgetData` ve privacy/test account davranışı gerçek cihaz/store preflight ister.

KVKK'da saklama ve koruma veri işleme kapsamına girebilir. Aydınlatma ile açık rıza ayrı tutulur; her işlem için gereksiz rıza istenmez. Cihaz-içi modelde yayıncının veri sorumlusu rolü, VERBİS ve provider'a yurt dışı aktarımın kime atfedileceği **UNVERIFIED / hukuk görüşü gerekir**. “Backend yoksa KVKK yoktur” varsayımı yapılmaz.

## 10. Supply chain ve release gates

- Yalnız official source/repository/feed; exact version + lock.
- Dependency update PR'ında release notes, license, transitive/native binary ve CVE review.
- SBOM; binary-to-source mapping; license/notices; GPL plugin taraması.
- Codec patent değerlendirmesi OSS lisansından ayrı hukuk gate'i.
- Secrets/signing certificates repository'ye girmez; CI platform-scoped secret environment kullanır.
- M1 local signing key'i export edilemez ve `CurrentUser\My` içinde kısa ömürlüdür; yalnız public certificate install süresince `LocalMachine\TrustedPeople` deposuna alınır. Paket ve her iki certificate kaydı exact identity/thumbprint guard ile temizlenir.
- M1 dev package identity disposable'dır. Store identity'ye in-place update, PFN/AUMID veya app-data migration garantisi verilmez; `Package.StoreAssociation.xml` M1'de fail'dir.
- Source ve installed effective manifest birlikte doğrulanır; M1 capability allowlist'i yalnız `runFullTrust`'tır.
- Preview/Experimental dependency release'e girmez.
- WACK, clean install/update/uninstall, package content/architecture ve app-data scan.
- Store/privacy policy release günü tekrar doğrulanır.

## 11. Doğrulama kapıları

| Gate | Kanıt | Milestone |
|---|---|---|
| Redaction | Canary secret table tests + log/artifact binary scan | M3–M5, her release |
| Protected storage | Packaged add/read/update/delete/reset/update/uninstall + 50k performance | M4/M8 |
| Transport | TLS fail, downgrade, cross-origin redirect, timeout, cancellation, oversize | M5 |
| Hostile input | Parser/API/image malformed and budget corpus | M6–M8 |
| Native player | Codec/network/malformed corpus, diagnostics scan, SBOM/CVE | M10 |
| Data deletion | Source delete, clear-all, orphan reconciliation, migration fault injection | M8/M15 |
| Store/privacy | Policy checklist, privacy text, synthetic reviewer access, WACK/private preflight | M15 |
| Release | Secret scan, license/patent review, no critical open security issue | M16 |

## 12. UNVERIFIED ve reopen tetikleri

- DPAPI-per-locator throughput ve exact envelope layout.
- Source-wide deletion, `DeletionPending` ve startup record/temp orphan reconciliation.
- Owner+reference+endpoint tuple'ının birlikte değiştirilmesi, resolved-origin equality ve authoritative persisted owner doğrulaması.
- Store initialization için raw path/message taşımayan typed failure sonucu.
- Handle-relative path/reparse hardening veya aynı-user path mutation için açık threat-model acceptance.
- Credential Locker/AppData/WidgetData uninstall/reset/update sonucu.
- Microsoft/Samsung Store'un genel BYO IPTV acceptance sonucu.
- Codec patent sonucu ve exact license obligations.
- KVKK role/transfer sonucu.
- Backend, telemetry, crash SDK, ads, sync, export, DRM, recording/download veya anonymous HTTP scope'a girerse bu belge ve ilgili ADR'ler yeniden açılır.

## Kaynaklar

[S10–S13, S21, S27, S30, S33–S41, S47, S51–S52, S60–S65](../research/SOURCES.md)
