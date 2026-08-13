# ADR-003 — Yerel persistence ve güvenli saklama

**Status:** Proposed

**Date:** 2026-08-09

## Context / Problem

Kaynak credential'ları ve query/user-info içinde token taşıyabilen M3U/stream locator'ları secret'tır. Buna karşılık 50.000+ kanal metadata'sı hızlı sorgu, atomik refresh ve migration gerektirir. Plaintext secret'ın DB, config, log, crash artifact veya migration dosyasına girmemesi; kaynak silmede geri çağrılamaz hale gelmesi gerekir.

Windows Credential Locker credential için resmî bir API'dir; ancak Microsoft-account ile roam edebilir ve on binlerce hassas locator için uygun değildir. Belgelenen 20-record sınırı UWP/AppContainer desktop bağlamına özgüdür; full-trust non-AppContainer process'te diğer user locker kayıtlarına erişim/isolation davranışı ayrıca sorun oluşturur. Tek mekanizma gibi kullanılması ürünün cihaz-içi saklama vaadi ve katalog ölçeğiyle çelişir; mevcut package token'ında 20/21 probe'u yine yapılır.

## Decision drivers

- Current Windows user'a bağlı OS koruması.
- Plaintext credential ve locator içermeyen queryable katalog.
- 50.000+ item import, index ve startup performansı.
- Atomik snapshot activation; failed/cancelled refresh'in aktif veriyi bozmaması.
- MSIX update/reset/uninstall ve kullanıcı kaynak-silme yaşam döngüsü.
- Forward-only migration ve crash recovery.
- Test double ve gerçek Windows integration test sınırı.

## Considered options

1. SQLite metadata + DPAPI/Windows Data Protection ile protected blobs.
2. Credential Locker/`PasswordVault` + SQLite.
3. SQLCipher veya başka encrypted database.
4. Düz dosya/JSON + OS protection.
5. Uygulamanın kendi master-key/AES tasarımı.

## Decision

Önerilen baseline:

- Queryable, secret olmayan metadata ve snapshot state için **SQLite**.
- Source credential, token ve tam hassas URL için `ISecretStore` arkasında **DPAPI CurrentUser / Windows `LOCAL=user` data protection**.
- Database ve protected blob'lar packaged app'in device-local, cloud-backup dışı **LocalCache** alanında; küçük secretsiz tercihler ayrı local settings alanında.
- Raw credential, full remote playlist URL veya raw stream locator DB'nin açık kolonuna, index'ine ya da loguna yazılmaz.
- Xtream playback locator'ı mümkün olduğunda source secret + provider item key'den just-in-time üretilir. M3U locator'ları opaque `ProtectedLocatorReference` ile erişilen korumalı kayıtlardır.
- Import staging snapshot'a bounded batch transaction ile yazar. Bütün referanslar ve sayımlar geçerse tek transaction aktif snapshot'ı değiştirir.
- WAL yalnız ölçülmüş concurrency yararıyla ve checkpoint/backup davranışı test edilerek açılır.
- Kaynak silme; kaynağı önce ağ/playback için devre dışı bırakır, protected secret/locator'ları siler, sonra katalog ve image cache'i temizler. Kısmi başarısızlık `DeletionPending` olur ve retry edilir.

Credential Locker primary değildir: roaming, bulk locator uyumsuzluğu ve full-trust user-locker isolation riski nedeniyle reddedilir; 20-record sınırı bu process modeli için tek başına karar gerekçesi değildir. Kendi kripto protokolü veya SQLCipher da ilk seçenek değildir. DPAPI-per-locator performansı bütçeyi geçmezse, güvenlik/hukuk review'undan geçen **DPAPI-wrapped per-source data-encryption key + standart authenticated encryption** ya da vetted encrypted database yeni karar olarak açılır; ad-hoc kripto uygulanmaz.

### M4 foundation implementation note — 2026-08-13

Application `ISecretStore` contract'ı ile Infrastructure `System.Security.Cryptography.ProtectedData 10.0.10` / `DataProtectionScope.CurrentUser` adapter'ı uygulanmıştır. V1 binary envelope ve entropy source, purpose, reference kind ve opaque record ID'ye bağlıdır; payload ve envelope buffer'ları bounded/zeroed, disk dosya adları digest tabanlıdır. Windows composition factory beklenen initialization hatalarını typed `StorageUnavailable` sonucuna eşler ve caller cancellation'ını korur. Adapter startup'ı yalnız exact current-namespace, regular ve en az 24 saatlik ciphertext temp artığını siler; fresh/future/lookalike/nested/`.dpapi` girdilerini korur ve unsafe/silinemeyen exact entry'de fail-closed davranır. Normal Windows test host'unda fake + gerçek CRUD/restart/corruption/binding/canary matrisi, işlem başlamadan iptal edilmiş çağrıların mutation yapmaması, bounded startup temp cleanup, birbirinden bağımsız kayıtların concurrent create'i ve aynı süreçte iki adapter instance'ı için same-key update/read/delete sıralaması iki-run 135/135 quality gate içinde PASS'tir. Arbitrary mid-I/O cancellation/interleaving ve cross-process sıralama kanıtlanmış değildir.

Bu ara sonuç ADR'yi `Accepted` yapmaz. Packaged lifecycle, wrong-user ve 5k–50k karşılaştırmalı ölçüme ek olarak; source-wide delete/startup protected-record orphan reconciliation, aynı source/purpose referansını configuration/channel/endpoint owner'ına bağlayan semantic ref-swap politikası ve check-to-use yarışını kapatan handle-relative path/reparse kararı henüz yoktur.

## Consequences and trade-offs

- Katalog normal SQL ile hızlı aranır; secret material ayrı güvenlik sınırında kalır.
- Aynı Windows kullanıcı bağlamındaki kötü amaçlı süreçlere karşı DPAPI tek başına sandbox değildir; OS account güvenliği ve app-data ACL'leri threat modelin parçasıdır.
- Çok sayıda DPAPI çağrısı import süresini artırabilir.
- LocalCache cloud backup/roaming yapmaz; cross-device sync yoktur.
- App reset/uninstall veri kaybıdır ve ürün metninde açık olmalıdır.
- Encrypted DB kadar geniş at-rest gizlilik sağlamaz; kanal adları/izleme tercihleri kişisel metadata kabul edilip minimizasyon ve silme uygulanır.

## Risks

- 50.000 protected locator'da süre, allocation veya DB büyüklüğü bütçesinin aşılması.
- Migration sırasında geçici plaintext veya orphan ciphertext.
- Bilinen tekil referanslar dışındaki source-wide record/temp orphan'ların mevcut contract üzerinden reconcile edilememesi.
- Aynı source/purpose içindeki geçerli bir referansın yanlış configuration/channel owner'ına takılması.
- Yeni/öngörülmeyen store initialization hata tipinin safe result allowlist'i dışında raw OS exception üretmesi.
- Managed path/reparse kontrolü ile gerçek file open arasındaki check-to-use yarışı.
- App identity/publisher değişiminde korumalı verinin açılamaması.
- Uninstall/reinstall/reset yaşam döngüsünün varsayılandan farklı olması.
- Secret silinip metadata ya da cache'in kalması veya tersi.
- DB corruption/WAL checkpoint sonrası aktif snapshot kaybı.

## Validation plan

M4/M8'de:

- create/read/update/delete, process restart ve yanlış user/context negatif testleri;
- canary secret ile DB, WAL, SHM, app data, log ve crash artifact binary scan;
- app reset, clean uninstall/reinstall, signed package update ve package identity değişimi;
- 5k/10k/20k/50k protected locator benchmark'ı; allocation, working set ve cancellation;
- crash/fault injection ile staging, activation, deletion ve migration recovery;
- 20/21 hesap senaryosuyla Credential Locker kararının tekrar kontrolü;
- source silme ve startup orphan reconciliation contract testleri.

M4 bitmeden exact encryption layout `Accepted` yapılmaz. Performans alternatifi seçilirse threat model, key rotation, nonce uniqueness, authenticated encryption ve recovery için ayrı security review gerekir.

## Revisit triggers

- DPAPI throughput'u 50.000-item bütçesini geçemezse.
- Cloud/cross-device sync, backup/export veya multi-user paylaşım ürüne girerse.
- Compliance tüm katalog metadata'sının şifrelenmesini isterse.
- Windows App SDK/.NET data-protection API veya MSIX lifecycle davranışı değişirse.
- First-party account/backend ya da telemetry eklenirse.
- Credential Locker roaming davranışı ürün tercihi haline gelir ve account sayısı açıkça 20'nin altında sınırlandırılırsa.

## References

[S33–S38, S63–S65](../research/SOURCES.md)
