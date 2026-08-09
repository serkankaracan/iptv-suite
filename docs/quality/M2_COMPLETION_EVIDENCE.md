# M2 completion evidence

**Status:** PASS / Completed

**Date:** 2026-08-09

Bu kayıt, M2 mühendislik kabulini kapatan local ve hosted sonuçların sanitize edilmiş, kalıcı özetidir. GitHub artifact retention süresi dolduktan sonra da hangi commit ve çıktının doğrulandığını belirtir; generated artifact'ın kendisi veya yeniden çalıştırılabilir bir imza değildir.

## Kanıt kimliği

| Alan | Değer |
|---|---|
| Source commit | `79cf619c6683fa9c4213846455e376fb1b0cb11c` |
| GitHub Actions | [Run `31327398270`](https://github.com/serkankaracan/iptv-suite/actions/runs/31327398270) |
| Hosted işler | `Locked build and test gate`: success; `Packaged install and launch smoke`: success; `Required Windows gate`: success |
| Quality artifact | `windows-quality-evidence`, artifact id `9041952246` |
| MSIX artifact | `windows-msix-smoke-evidence`, artifact id `9041994494` |
| Package smoke completion | `2026-08-09T17:55:54.7544278Z` |

İki artifact güvenli ZIP-entry kontrolünden sonra indirildi. Archive SHA-256 değerleri quality için `fd938dd794716164c1cf86d83499c9ec9a5285844637793aedabffc93fbf494a`, MSIX için `4fa60f4ab2d70805b7b3530ba651a039f0539f4e12c0785bd1f0322914e500a6` olarak kaydedildi.

## Quality sonucu

- Exact .NET SDK `10.0.302`; Debug + Release x64 locked build.
- Architecture `8`, unit `9`, integration `5`: koşu başına `22/22 Passed`, iki temiz koşuda aynı sıralanmış sonuç seti.
- Sentinel: armed exact TRX `Failed`, disarmed exact TRX `Passed`.
- Scanner CLI: kontamine artifact exit `2`, cleanup sonrası exit `0`; final artifact-file canary scan PASS.
- Fixture records SHA-256: `1da91c57da1f704076600aab29cdd938851d75f765679ac2b79dc9cb9e908020`.
- Fixture manifest SHA-256: `b1f1513e786f3176c7275af927c4c93c847d0476858fe56701d2054128818438`; indirilen manifest aynı hash'i verdi.
- License sidecar, tracked `LicenseRef-IPTVSuite-Synthetic-Test-Only.txt` ile byte-identical doğrulandı. Lisans statüsü yine `UNVERIFIED` ve internal-only'dir.
- İndirilen `quality-summary.json` SHA-256: `7d2d9a1af53750b8a3fa81f937d63ee12c55ecd7f5797a29273462e619c69523`.

## Packaged-host sonucu

| Alan | Doğrulanan değer |
|---|---|
| Evidence run id | `79833460adfe4133915c63cf60af2ee4` |
| Package | `IptvSuite.Windows_0.1.0.0_x64.msix` |
| Package SHA-256 | `e04a7db236a263641e56e6c33fac4987ca774ba7a9af1bb963632ad8d1df8a2c` |
| SDK / configuration / architecture | `10.0.302` / `Release` / `x64` |
| Commit binding | Source commit ile exact eşleşti |
| Signature | `Valid` |
| Capability | Yalnız `runFullTrust` |
| Targeted payload leak gate | `true` |
| Visible launch ve normal close | `true` |
| Exact package cleanup | `true` |

İndirilen `last-success.json` SHA-256 değeri `ae3e58d4076a6504585c0eef93d9be021ef6538e35784d659ca166fa0b3ea973` oldu. Run id biçimi, commit bağı, SDK, package hash biçimi, signature, leak gate, normal close, removal, capability allowlist'i, quality sonuç şekli, fixture hashleri, sentinel, scanner ve license sidecar için toplam `14/14` assertion geçti.

## İddia sınırı

Bu PASS yalnız belirtilen commit ve GitHub-hosted runner için deterministic test zincirini ve signed framework-dependent x64 MSIX'in targeted payload inspection → install → visible AUMID launch → normal close → exact cleanup akışını kanıtlar. Şunları kanıtlamaz:

- feature UI, keyboard, UIA veya accessibility;
- update/migration, WACK, Store submission ya da production Store identity;
- non-admin sideload veya clean-machine/reference-hardware matrisi;
- gerçek provider/internet, production credential store, player, codec veya hardware decode;
- ARM64 ya da bütün OS/GPU kombinasyonları;
- genel secret/malware temizliği veya GitHub job logunda canary yokluğu;
- sentetik fixture'ın public redistribution hakkı.

Manifestte yalnız `runFullTrust` bulunması ek capability olmadığını gösterir; `runFullTrust` yine full-trust desktop yetkisidir ve sandbox/minimum-privilege iddiası değildir. `Valid` imza da ephemeral self-signed development certificate'in runner'daki geçici trust bağlamını kanıtlar; Store/production signing, public identity, WACK veya Store kabulü anlamına gelmez. MSIX, private key ve certificate artifact'ları repository'ye alınmamıştır.

`Required Windows gate` run içinde başarıyla sonuç üretmiştir. Buna karşın 2026-08-09 repository preflight'ında private repository için branch protection endpoint'i plan kısıtı nedeniyle `403` döndürdü; dolayısıyla merge enforcement etkin değildir. Uygun GitHub planı veya repository visibility kararı alınmadan “required by branch policy” iddiası yapılmaz.
