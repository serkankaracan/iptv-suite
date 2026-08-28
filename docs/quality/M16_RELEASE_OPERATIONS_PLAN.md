# M16 Windows MVP release operasyonları planı

**Durum:** `RECORDED LOCALLY / EXECUTION BLOCKED — clean-commit RC evidence ve dış onaylar bekleniyor, 2026-08-26`

## Amaç ve karar sınırı

Bu plan; aynı Windows MVP release candidate'ı için yayımlama öncesi karar, geri çekme, güvenlik güncellemesi, incident/known-issue sınıflandırması ve kanıt saklama akışının rollerini ve fail-closed durma noktalarını kaydeder. Buradaki planın varlığı release yetkisi, Store submission, public availability veya herhangi bir dış onay değildir.

Plan yalnız rol ve karar sözleşmesidir. Gerçek kişi adları, production package/Store identity'si, signing materyali, Partner Center hedefi, privacy/support URL'leri, hukuki görüşler ve ülke/dağıtım girdileri bu belgede uydurulmaz. Bu girdiler onaylı kaynaklardan geldiğinde exact candidate release packet'ına bağlanır. O zamana kadar ilgili M15 blocker'ları ve `candidateReady=false` sonucu değişmez.

## Release packet ve değişmez aday bağı

Her karar tek bir immutable candidate packet'ına bağlanır. Packet aşağıdaki alanlar olmadan `GO` alamaz:

| Alan | Zorunlu bağ | Mevcut durum |
|---|---|---|
| Source/build | Clean commit SHA, exact SDK/configuration ve build sonucu | M16 final clean-commit bağı bekleniyor |
| Package | Exact package version, identity, architecture, byte length ve SHA-256 | Production identity/signing bekleniyor |
| Predecessor evidence | M15 ve M16 sanitized summary boyut/hash'leri | `26fedef...` blocked aggregate kaydedildi; final release packet bekleniyor |
| Supply chain | Package-bound SBOM digest'i, exact dependency graph ve fresh CVE review | Final candidate için yeniden doğrulama gerekir |
| Quality/reliability | Full gate, journey, canary/security scan, fiziksel matrix ve 24 saat soak sonucu | Açık M16 kapıları var |
| Release metni | Support matrix, known issues ve release notes'un exact revision/hash'i | Final candidate'a göre üretilecek |
| External decisions | Product, Legal/Privacy, Store/Release operator ve Security disposition'ları | Onaylı girdiler bekleniyor |
| Recovery | Superseding fix/withdrawal kararı, sorumlu rol ve doğrulama sonucu | Candidate'a özgü olarak doldurulacak |

Packet'taki commit, package veya kanıt digest'lerinden biri değişirse karar yeni candidate için baştan verilir. Önceki candidate'ın private-flight, WACK, soak, CVE veya cihaz sonucu yeni binary'ye taşınmaz.

## Release durum makinesi

1. `Draft`: geliştirme sürer; release iddiası yoktur.
2. `FrozenCandidate`: kapsam dondurulur, yalnız blocker/acceptance düzeltmesi alınır; her değişiklik candidate kimliğini yeniler.
3. `EvidenceComplete`: aynı candidate'ın bütün teknik ve operatör kanıtları packet'a bağlanmıştır; tek başına yayımlama onayı değildir.
4. `PrivateFlightApproved`: yalnız exact private-flight hedefi ve onaylı test grubu için dış Store/Release operator kararı kaydedilmiştir.
5. `PublicSubmissionApproved`: Product ve Legal/Privacy kararlarıyla ayrı, açık bir eylemdir; M16 otomatik sonucu bunu üretemez.
6. `Published`: dış platform sonucu ve exact package bağı packet'a sonradan kaydedilir.
7. `PromotionStopped`, `WithdrawalRequested` veya `Superseded`: incident disposition'ına göre yeni edinim/promosyon durdurulur, dış geri çekme talebi izlenir ya da forward-fixed paket hazırlanır.

Bir durum yalnız önceki durumun kanıtı ve gerekli rol kararları kaydedildiğinde ilerler. Belirsiz, kayıp veya uyuşmayan dış sonuç fail-closed biçimde ilerlemeyi durdurur.

## Rollback ve withdrawal sözleşmesi

### Terimler

- **Promotion stop**, candidate'ın yeni flight/public halkalara ilerletilmemesidir.
- **Withdrawal request**, Store/dağıtım operatörünün yeni edinim veya görünürlüğü durdurması için dış sistemde yaptığı ve sonucunu ayrıca doğruladığı eylemdir.
- **Superseding release**, aynı update lineage üzerinde sorunu düzelten daha yeni, yeniden kabul edilmiş pakettir.
- **Local data recovery**, reset/uninstall/update davranışının belgelenmiş lifecycle sözleşmesine göre doğrulanmasıdır; kullanıcı verisini sessizce silme yetkisi vermez.

MSIX downgrade'in mümkün olduğu, Store withdrawal'ın mevcut kurulumları kaldırdığı/devre dışı bıraktığı veya zorunlu güncelleme sağladığı varsayılmaz. Varsayılan teknik geri dönüş yolu, güvenli ise promotion stop/withdrawal ile birlikte yeniden kabul edilmiş forward-fixed superseding release'tir. Previous package kurulumu ancak production identity/update lineage ve veri lifecycle matrisi exact senaryoyu doğrularsa seçenek olabilir.

### Tetikleyici ve ilk disposition

| Tetikleyici | İlk zorunlu eylem | Release sonucu |
|---|---|---|
| Secret/PII canary bulgusu, signing veya supply-chain compromise şüphesi | Promotion'ı durdur; kanıtı sanitize et; Security ve Release owner incelemesini aç | `GO` yasak; gerekiyorsa withdrawal request |
| Veri kaybı/corruption, source-delete/lifecycle ihlali veya güvenli olmayan update | Promotion'ı durdur; etkilenen exact sürüm/işlemi sınırla; recovery doğrulaması yap | Fix ve yeniden kabul olmadan yayımlanamaz |
| Exploit edilebilir kritik media/parser/dependency CVE | Exact graph/exposure triage'ı; fresh audit/SBOM; Security disposition | Release blocker; yayımlanmışsa withdrawal veya superseding fix kararı |
| Crash/hang/deadlock, ghost audio, reconnect storm veya hard budget ihlali | Candidate evidence'ını invalid say; minimal deterministic repro ve owner ata | P0/P1 açıkken promotion yok |
| Store/Legal/Privacy/identity/signing kararının geri çekilmesi veya uyuşmaması | Dış dağıtım eylemini durdur; ilgili M15 blocker'ını yeniden aç | Yeni onay olmadan submission yok |
| Bounded, güvenlik/veri kaybı oluşturmayan P2 known issue | Owner, etki, workaround ve yazılı release acceptance kaydet | Ancak support matrix/release notes ile eşleşirse değerlendirilebilir |

### Uygulama sırası

1. Exact affected candidate/package digest'ini ve ilk safe failure code'unu kaydet; raw credential, locator, header/body, dump veya kullanıcı içeriğini toplama.
2. Yeni promotion/submission işlemini durdur ve release packet'ını `IncidentOpen` olarak işaretle.
3. Severity, etkilenen destek tuple'ı, data/security etkisi ve reproducibility durumunu ata.
4. Security/Legal/Privacy/Store kararı gerekiyorsa bunu açık `PENDING` girdi yap; mühendislik sonucu gibi yorumlama.
5. `NoAction`, `DocumentKnownIssue`, `PromotionStopped`, `WithdrawalRequested` veya `SupersedingFixRequired` disposition'larından birini owner ve gerekçeyle kaydet.
6. Fix varsa yeni commit/package olarak bütün drift-sensitive gate'leri yeniden çalıştır; eski evidence'ı yeniden kullanma.
7. Dış withdrawal/promotion sonucunu operator doğrulaması olmadan tamamlandı sayma.
8. Known issue, support matrix, release notes ve incident kaydını aynı final candidate revision'ına bağla.

## Dependency ve CVE response

1. Triage yalnız exact source-controlled lockfiles, package-bound SBOM ve yayımlanan package graph'ından yapılır; package adı/sürümü tahmin edilmez.
2. Resmî vendor/NuGet advisory girdisi ve sorgu zamanı kaydedilir. M15'in yedi günlük CVE freshness predicate'i release değerlendirmesi anında geçmiyorsa `CveReviewPending` yeniden açılır.
3. Bulguda package path'i, direct/transitive rol, etkilenen binary/surface, exploitability ve mevcut mitigation ayrıştırılır. “Paket mevcut” tek başına exploitable; “0 known finding” ise genel veya kalıcı `CVE-free` iddiası değildir.
4. Exploit edilebilir kritik media/parser veya credential/data-boundary açığı release blocker'dır. Yayımlanmış candidate için Security ve Release owner withdrawal/superseding-fix disposition'ı verir.
5. Dependency değişikliği yeni candidate'dır: locked restore, license/notice, binary inventory/signature, SBOM, CVE, package smoke, playback/lifecycle ve ilgili acceptance yeniden çalışır.
6. Suppression/waiver ancak kaynak-kontrollü, süreli, exact advisory/package bağlamlı ve Product/Security tarafından açıkça kabul edilmiş ayrı karardır. Bu plan waiver üretmez.

External advisory/feed URL'leri final packet'ta onaylı exact reference olarak kaydedilecektir; bu checkpoint herhangi bir URL'yi doğrulanmış release girdisi saymaz.

## Incident ve known-issue triage

| Seviye | Tanım | Karar kuralı |
|---|---|---|
| `P0` | Credential/PII disclosure, signing/supply-chain compromise, yetkisiz erişim veya yaygın geri döndürülemez veri kaybı şüphesi | Derhal promotion stop; açıkken release yasak; Security + Product + gerekli dış uzman disposition'ı |
| `P1` | Ana source→catalog→play/delete zincirini bozan crash/hang/data lifecycle sorunu, kritik exploitable CVE veya Store hard blocker | Release yasak; fix/yeniden kabul ya da doğrulanmış withdrawal gerekir |
| `P2` | Güvenlik/veri kaybı oluşturmayan bounded compatibility/UX sapması ve uygulanabilir workaround | Yalnız owner, scope, workaround, support-matrix etkisi ve yazılı release acceptance ile kalabilir |
| `P3` | Release davranışını değiştirmeyen dokümantasyon/cosmetic sapma | Owner ve hedef revision ile izlenir; daha yüksek severity bulgusunu düşürmek için kullanılamaz |

Her kayıt en az stable incident ID, first-seen UTC, exact candidate/package digest'i, severity, safe symptom/failure code, affected support tuple, owner rolü, workaround, evidence fingerprint'leri, disposition ve kapanış doğrulaması taşır. Kullanıcı playlist'i, credential, full URL/query, raw log/dump veya gerçek provider hesabı kayda girmez. Severity düşürme gerekçesi ve onaylayan roller ayrıca yazılır.

Known-issue listesi release notes ve support matrix ile aynı revision/hash'e bağlanır. Bir issue'nun belgelenmesi P0/P1'i kabul edilebilir hale getirmez.

## Evidence retention ve veri minimizasyonu

| Kanıt sınıfı | Saklama davranışı | Kesin sınır |
|---|---|---|
| Source-controlled sanitized ledger/decision | Repository history'sinde hash-bound tutulur | Secret, PII, absolute path, kullanıcı/makine kimliği veya raw Store report'u içermez |
| Hosted build/test artifact'ı | Exact platform retention ayarı ve gerekli export hedefi release packet'ta operator tarafından kaydedilir | Süre/hedef henüz onaylanmadı; kalıcı erişim varsayılmaz |
| Local `.artifacts` çıktısı | Sanitized ledger/hash doğrulandıktan sonra release kanıtının tek kopyası sayılmaz | Commit edilmez; kişisel veri/secret içeren raw çıktı korunmaz |
| WACK/Store dış raporu | Yalnız allowlist'li sanitized disposition ve dış reference/fingerprint kaydedilir | Ham stdout/stderr/XML veya hesap/tenant ayrıntısı repository artifact'ı değildir |
| Support export | Yalnız açık kullanıcı eylemi, ikinci sanitization ve canary scan ile üretilir | Otomatik upload/telemetry yoktur; raw playlist/locator/header/body/dump yoktur |
| Incident hold | Security/Legal tarafından exact scope ve sonlandırma kararıyla uygulanır | Hukuki/privacy retention süresi henüz onaylanmamıştır; keyfî sınırsız saklama yoktur |

CI/Store artifact retention süresi, onaylı saklama hedefi, erişim rolleri ve silme doğrulaması final release öncesi Product/Privacy/Release operator tarafından kaydedilmelidir. Buradaki kayıt M15 `PrivacyPolicyPending` veya `SupportUrlPending` blocker'ını kapatmaz.

## Support matrix sözleşmesi

MVP support matrix'i yalnız candidate-bound kanıtı olan tuple'ları içerir. Minimum alanlar: OS/build family, architecture, package channel/identity revision, display/input/accessibility yüzeyi, audio/video codec-container-protocol tuple'ı, hardware/software decode disposition, install/update/reset/uninstall sonucu ve known-issue reference'ıdır.

Şu an yapılabilecek exact iddialar sınırlıdır:

- Release architecture kararı `x64`tır; ARM64 `DeferredUntilNativeArm64ChainAccepted` ve support dışıdır.
- Ürün modeli yalnız kullanıcının yetkili playlist/account'unu getirdiği Live TV MVP'sidir.
- VOD, Series, EPG/XMLTV, catch-up, recording, timeshift, cloud/backend, analytics, ads, Tizen/mobile ve doğrulanmamış provider-specific davranış support matrix'e eklenmez.
- Native Tier A fixture/loopback sonuçları bütün gerçek stream, GPU/driver, cihaz veya ülke uyumluluğu sözü değildir.
- Fiziksel DPI/multi-monitor/sleep/audio-device/Narrator/UIA matrisi ve exact production package lifecycle sonucu tamamlanmadan ilgili satır `SUPPORTED` olamaz.

OS sürüm aralığı, gerçek cihazlar, codec/ülke sınırları, production identity/channel ve public support URL'si bu checkpoint'ta `PENDING`dir. Marketing veya Store metni matriste olmayan desteği vaat edemez.

## Release notes zorunlu şablonu

Her candidate için release notes aşağıdaki başlıkları exact packet revision'ıyla taşır:

1. Candidate kimliği: sürüm, commit, package digest, architecture ve channel.
2. Kapsam: bu sürümde doğrulanan Live TV davranışları ve özellikle kapsam dışı alanlar.
3. Supported matrix revision'ı ve bağlantılı known issues revision'ı.
4. Güvenlik/privacy özeti: cihaz-içi/direct-provider sınırı; telemetry/backend iddiası yalnız gerçek davranışla eşleşiyorsa.
5. Install/update/reset/uninstall veri davranışı ve kullanıcıya görünür recovery adımları.
6. Düzeltilen sorunlar: stable issue ID ve doğrulama kanıtı.
7. Açık P2 sorunlar: etki, affected tuple, workaround, owner ve acceptance kaydı.
8. Dependency/SBOM/CVE review zamanı ve digest bağları; `CVE-free` ifadesi kullanılmaz.
9. Rollback/withdrawal durumu ve onaylı support/privacy references.

Release notes; hazır kanal/hizmet/içerik sağlama, her provider/stream ile uyumluluk, DRM/erişim kontrolü aşma, evrensel codec/HW-decode, sıfır risk/sıfır CVE, veri kaybının imkânsızlığı veya Store acceptance garantisi iddia edemez.

## Rol sahipliği

| Rol | Karar sorumluluğu |
|---|---|
| Product owner | Scope freeze, P2 acceptance, support/known-issue/release-note tutarlılığı ve public submission talebi |
| Release owner | Immutable packet, promotion state, external operator sonuçları, withdrawal/superseding akışı ve evidence inventory |
| Engineering owner | Repro/fix, exact build/test yeniden doğrulaması ve lifecycle/rollback teknik sonucu |
| Security owner | Canary/incident/CVE/exposure triage, severity ve security disposition |
| Quality owner | Candidate-bound gate/matrix/soak evidence'ı ve eski kanıtın taşınmaması |
| Support/incident owner | Safe issue intake, workaround/known-issue senkronizasyonu ve kullanıcı iletişimi taslağı |
| Legal/Privacy owner | Codec/IP, license/NOTICE, privacy/retention, marka/ülke ve dış beyan kararları |
| Store operator | Production identity, signing execution, WACK/Partner Center/private-flight/publication/withdrawal dış sistem sonucu |

Roller kişi veya ekip ataması değildir. Final packet her rolün yetkili assignee/reference bilgisini dış kaynaktan alır; bu belgede kişi, tenant, certificate veya URL uydurulmaz.

## Activation ve kapanış kontrol listesi

- [ ] Exact candidate packet bütün digest ve revision'larla immutable kaydedildi.
- [ ] M15 blocker seti ve evaluation-time CVE freshness sonucu yeşil.
- [ ] M16 journey, final canary/security scan, 24 saat soak ve fiziksel accessibility/device matrix aynı candidate'a bağlı.
- [ ] P0/P1 açık değil; varsa her P2 owner/workaround/support etkisi ve Product acceptance taşıyor.
- [ ] Support matrix, known issues ve release notes aynı revision'a bağlı.
- [ ] Rollback/withdrawal disposition'ı ve dış operator owner'ı kayıtlı.
- [ ] Evidence retention ayarı/hedefi/erişim/silme kararı Privacy ve Release owner tarafından kayıtlı.
- [ ] Production identity, signing, WACK, Partner Center/private flight ve dış publication sonucu exact candidate'a bağlı.
- [ ] License/NOTICE, codec/IP, privacy/support/listing ve marka/ülke kararları yetkili dış girdilerle kapalı.
- [ ] Public submission için Product + Legal/Privacy ayrı ve açık onay verdi.

Bu checklist'in kendisi işaretlenmiş sayılmaz. Local belgenin oluşturulması yalnız release-operations karar modelini kaydeder; `M16ReleaseOperationsPlanPending` ancak clean commit-bound RC evidence/review sözleşmesi bunu kabul ettiğinde kapanabilir.

## Exact non-claims

- Bu belge M15/M16 completion, `candidateReady=true`, feature freeze, WACK, Store/private-flight, signing veya public submission değildir.
- Rollback, downgrade, forced update, remote disable, mevcut kurulumların withdrawal ile silinmesi veya kullanıcı verisinin geri getirilebilmesi garanti edilmez.
- Support matrix henüz tamamlanmamıştır; x64 release disposition'ı tek başına OS/device/codec/provider desteği değildir.
- Dependency taramasındaki sıfır known finding genel, geleceğe dönük veya hukuki `CVE-free` beyanı değildir.
- SBOM license/NOTICE, redistribution, codec/patent veya marka onayı değildir.
- Sentetik loopback/reviewer verisi gerçek provider/content/account uyumluluğu veya kullanım hakkı kanıtı değildir.
- Repository'de gerçek signing secret'ı, Store credential'ı, kullanıcı playlist'i/credential'ı, raw support dump'ı veya dış tenant/account kimliği tutulmaz.
- Dış URL, identity, signer, Store, privacy, support, legal ve ülke girdileri açıkça `PENDING`dir; mühendislik varsayımıyla kapatılamaz.

## Bağlı kayıtlar

- [M16 blocked RC baseline](M16_RELEASE_CANDIDATE_BASELINE.md)
- [M15 release-readiness baseline](M15_RELEASE_READINESS_BASELINE.md)
- [Windows MVP roadmap](../roadmap/WINDOWS_MVP_ROADMAP.md)
- [Security and privacy baseline](../security/SECURITY_AND_PRIVACY_BASELINE.md)
- [Risk register](../risks/RISK_REGISTER.md)
