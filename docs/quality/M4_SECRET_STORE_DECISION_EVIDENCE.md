# M4 secret-store Decision kanıtı

**Tarih:** 2026-08-14

**Durum:** Decision workload `VERIFIED`; per-record DPAPI yerleşimi 50k hedefi için `REJECTED` (`INFERENCE`); M4 foundation `COMPLETED`; ADR-003 `Proposed`

## 1. Sonuç

`eng/Invoke-WindowsSecretStoreSpike.ps1 -Mode Decision -AllowDecision` koşusu clean commit `fc96a211171d1e4f5e5f02174da6c565ef2d59bb` üzerinde sıfır exit code ile tamamlandı. Bu sonuç workload'un sözleşmeye uygun tamamlandığını, canary kontrollerinin ve cleanup'ın geçtiğini **VERIFIED** yapar; herhangi bir performans threshold'unun geçtiği anlamına gelmez. Runner performans threshold'u uygulamaz.

50k ölçümünde yalnız per-record DPAPI create aşamasının p95 süresi `151,447 s`, p95 managed allocation'ı `668,007 MiB` oldu. Önceden ilan edilen bütçeler 50k normalize + protected persistence + index için p95 `≤ 3,0 s`, end-to-end import için p95 `≤ 5,0 s` ve bütün import managed allocation'ı için `≤ 150 MiB`dir. Tek bir alt aşama sırasıyla bu birleşik bütçelerin yaklaşık `50,5`, `30,3` ve `4,45` katına ulaştığı için mevcut “her locator için ayrı DPAPI blob/dosya” yerleşiminin 50k channel locator persistence yolu olarak kullanılmaması kararı **INFERENCE**dır.

Bu negatif karar DPAPI `CurrentUser` korumasını veya mevcut dar secret-store contract'ını genel olarak reddetmez. 50k channel verisi için güvenlik incelemesinden geçmiş farklı bir persistence yerleşimi ve ayrıca end-to-end referans-cihaz ölçümü gerekir. Bu kayıt ADR-003'ü kabul edilmiş duruma getirmez ve M4'ü tamamlamaz.

## 2. Değişmez kanıt bağı

| Alan | Değer |
|---|---|
| Repository commit | `fc96a211171d1e4f5e5f02174da6c565ef2d59bb` |
| Başlangıç repository durumu | clean; `decisionEligible=true` |
| Evidence schema / mode | `1` / `Decision` |
| Summary path | `.artifacts/m4-secret-store-spike/evidence/decision-summary.json` |
| Summary byte length | `23.402` |
| Summary SHA-256 | `8cd4c6d86b813fd07794217a71a824e7368694363f89a16be36cb8a311d67460` |
| Global workload SHA-256 | `eb6a4eaaecf437e80ef01feb00c6d1453e41994682a76ed08f81c1808a372f3f` |
| Specification SHA-256 | `0447355215f8c744340a39640e55bc798916638b48e5386b213e7d3f06c7a568` |
| License SHA-256 | `0ee38448ce47fb7c98e56984a84819138e0f7eec085b03d75607d3f5f1d0dba3` |
| Runner assembly SHA-256 | `3df0676151a906f815bd0881994ffd3f7f347f2f7121a494409f85afcdeca119` |
| SDK / runtime | `.NET SDK 10.0.302` / `.NET 10.0.10` |
| Host | Windows `10.0.26200.0`, OS/process `X64`, 24 logical processor |
| Configuration / protection | `Release`, `x64`, DPAPI `CurrentUser` |

Kalıcı kayıt summary dosyasının kendisini repository'ye eklemez; yukarıdaki commit ve hash bağı, daha sonra elde edilen exact dosyanın doğrulanması içindir. Runner assembly hash'i yalnız çalıştırılan spike DLL'ini bağlar; dependency setinin veya reproducible build'in kanıtı değildir.

Payload 256 byte, sentetik ve deterministiktir: generator `IptvSuite.SecretStoreSpike.DeterministicPayloadGenerator`, algorithm version `1`, seed `20260813`. Evidence; üçüncü taraf içerik, kişisel veri, gerçek credential veya yetkisiz medya içermediğini kaydeder. `LicenseRef-IPTVSuite-Synthetic-Test-Only` lisans durumu `UNVERIFIED`dır ve public redistribution hakkı sayılmaz.

Production generator assembly'sini kullanmayan ayrı bir doğrulayıcı, ölçek workload'larını yeniden üretti. Aşağıdaki scale hash'leri ve global workload hash'i summary ile birebir eşleşti:

| Kayıt sayısı | Workload SHA-256 |
|---:|---|
| 5.000 | `80f110a11351dd95b3489f0a8973cc826f096334da0b4363e1d4b24e98082fe1` |
| 10.000 | `c4084013d6205597e412d47ec65329b8d671b9e0edb551a6d29c54cf34cd1512` |
| 20.000 | `94bb81ddc7d2afe6fc4b2935dd9d2dec5f1bf8e80b5444cf90e8e860b9512c86` |
| 50.000 | `88b5fad60d89e2fb6c16e9dac1a3372abb0779cdd216424833555b8f906ab232` |

## 3. VERIFIED gözlemler

Warmup ölçüm dışı tamamlandı. Her scale 20 iteration çalıştı; create/restart/256-record bounded read/delete aggregate'leri üretildi.

| Kayıt sayısı | Iteration | Create p95 |
|---:|---:|---:|
| 5.000 | 20 | `16,041 s` |
| 10.000 | 20 | `30,509 s` |
| 20.000 | 20 | `60,777 s` |
| 50.000 | 20 | `151,447 s` |

50k scale için ek gözlemler:

| Ölçüm | p95 |
|---|---:|
| Create managed allocation | `700.456.336 byte` (`668,007 MiB`) |
| Delete | `14,821 s` |
| 256-record bounded read | `78,853 ms` |
| Store adapter yeniden oluşturma | `0,416 ms` |

Cancellation workload'u 20 örnekte 10 committed record sonrasında tetiklendi. Request-to-completion p95 `1,067 ms`; cancellation sonrasında commit üst sınırı `0`, completion sonrasında mutation sayısı `0` ve temporary artifact sayısı `0` oldu. Cancellation cleanup'ı geçti.

Protected-store canary scan'i delete öncesinde, evidence canary scan'i publish öncesinde geçti. Final cleanup geçti; Decision başlangıcındaki ve sonundaki repository doğrulamaları clean kaldı.

## 4. Yorum ve karar sınırı

**VERIFIED:** Yukarıdaki süre, allocation, cancellation, canary ve cleanup değerleri exact summary'ye bağlı gözlemlerdir. Cancellation sonucu ilan edilmiş p95 `≤ 250 ms` bütçesinin içindedir. Adapter-restart ve bounded-read süreleri yalnız ölçüldükleri dar workload'a aittir.

**INFERENCE:** 50k create süresi ve allocation'ı tek başına birleşik normalize + protected persistence + index süresi ile bütün-import allocation bütçelerini aştığı için per-record DPAPI blob/dosya yerleşimi hedef katalog ölçeğinde uygulanabilir değildir. Bu yerleşim 50k channel locator persistence için reddedilir; güvenlik özelliklerini koruyan alternatif tasarım ayrı threat model, failure/recovery sözleşmesi ve aynı büyüklüklerde yeni Decision ölçümü olmadan seçilmiş sayılmaz.

Delete p95 `14,821 s` için önceden ilan edilmiş ayrı threshold yoktur; değer operasyonel tasarım girdisidir, tek başına formal PASS/FAIL üretmez. Bounded-read ve adapter-restart sonuçları tüm 50k kaydın okunması, cold process startup veya cached-first-visible bütçesi yerine kullanılamaz.

## 5. Sınırlamalar ve açık kanıtlar

- Koşu dedicated/reference benchmark cihazında değil, developer workstation'da yapıldı; background load, power ve thermal koşulları kontrollü değildi.
- Scale'ler sabit küçükten büyüğe sırayla çalıştı; OS cache cold/warm etkisi ayrıştırılmadı.
- Harness unpackaged Release x64 DPAPI-only ölçümüdür. Parser, normalization, SQLite/index, UI, network ve production package lifecycle maliyetleri kapsam dışıdır.
- Restart metriği aynı executable içindeki store adapter construction sınırıdır; yeni process veya packaged cold start değildir.
- Her iteration yalnız 256 deterministik kaydı okur; tüm 50k read/query davranışına extrapolate edilmez.
- Evidence aggregate değerler taşır; raw sample, trace ve outlier nedenleri yoktur. Update, disk footprint, crash/power-loss ve concurrent access ölçülmedi.
- `CurrentUser` aynı kullanıcı kapsamıdır; gerçek ikinci kullanıcı/wrong-user, production PFN update/reset/reinstall/repair ve same-user malicious-process sınırı bu koşuyla kapanmaz.
- Sonuç bir security review, ADR kabulü veya M4 milestone acceptance değildir. Source disable/drain, source-wide deletion/reconciliation, authoritative owner metadata ve tuple/origin policy açık kalır.

## 6. Takip koşulu

Bir sonraki storage adımı, 50k channel locator verisi için daha az koruma operasyonu ve dosya metadata maliyeti üreten bir yerleşimi threat model üzerinden karşılaştırmalıdır. Alternatif; key lifecycle, rotation, atomic commit, crash recovery, corruption isolation, deletion/reconciliation, same-user saldırgan ve package lifecycle sınırlarını yazılı olarak kapatmadan production implementation'a alınmaz. Seçilen aday aynı 5k/10k/20k/50k × 20 workload'u ve ardından parser + normalize + index içeren end-to-end referans-cihaz bütçesini geçmelidir.
