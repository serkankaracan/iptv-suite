# M4 completion evidence — secure-storage foundation

**Durum:** `COMPLETED, 2026-08-20`

## Karar özeti

M4, Windows `CurrentUser` protected-storage foundation'ını ve bu foundation'ın güvenlik sınırlarını tamamlar. Milestone; owner-bound exact-record CRUD, zeroing ve merkezi redaction, packaged `LocalCache` initialization, dar ve fail-closed deletion primitive'leri, gerçek farklı Windows hesabı altında raw DPAPI/production-adapter negatif sınırı ve bulk locator yerleşimi için ölçümlü kararı kapsar.

M4, production katalog veritabanını veya source-wide lifecycle transaction'ını sahiplenmez. Durable authoritative owner inventory, transactional `DeletionPending`, admission/drain, source-wide delete ve startup orphan reconciliation M8'e; owner/reference/endpoint ile resolved-origin transport politikası M5'e; production PFN, repair ve package identity matrisi M15'e devredilmiştir. Bu ayrım, eksik implementation'ı tamamlanmış saymaz; sorumluluğu onu uygulayacak milestone'a tekil biçimde bağlar.

ADR-003, M8 production transaction layout'u ve recovery matrisi tamamlanmadığı için `Proposed` kalır. M4 completion, ADR'nin bütününü `Accepted` yapmaz.

## Acceptance bağı

| M4 kabul alanı | Kanıt | Sonuç |
|---|---|---|
| CurrentUser secret create/read/update/delete, restart, semantic-owner/context binding, corruption ve cancellation | Unit/integration contract matrisi; packaged initialization ve lifecycle zinciri | **VERIFIED** |
| Plaintext/log/artifact sınırı, zeroing lease ve merkezi sanitizer | Architecture/unit/integration testleri; fixture, scanner ve canary gate'leri | **VERIFIED** |
| Dar configuration/channel delete primitive'leri | Yalnız `DeletionPending`; exact aggregate tuple; idempotency; partial failure; retained/`Importing`/`Rejected` snapshot cleanup | **VERIFIED** |
| Gerçek farklı Windows hesabı | Run `#31` (`31960438511`) ve indirilen sanitized DPAPI evidence; raw DPAPI reddi, adapter `ProtectedRecordUnavailable`, null lease, creator re-read/immutability ve exact cleanup | **VERIFIED** |
| Disposable package lifecycle | Signed test-family update, reset, live-state uninstall/reinstall ve fresh-state zincirleri | **VERIFIED** |
| 50k bulk locator layout kararı | Per-record baseline reddedildi; protected-catalog comparative Decision tamamlandı; immutable cross-file container production `NO-GO`; transactional SQLite yönü M8 için `Proposed` | **VERIFIED decision / INFERENCE handoff** |
| Clean commit-bound final gate | Run `#32` (`32397220243`), commit `9315bc75df1d22fada00be5f350fbb961fc6b816`; dört zorunlu iş başarıyla tamamlandı | **VERIFIED** |

## Final doğrulama

Yerel exact SDK `10.0.302` gate'i Debug/Release x64 build'lerinde sıfır warning/error verdi. Architecture `16`, unit `154`, integration `34` olmak üzere `204/204` test iki kez aynı `Passed` setiyle geçti; fixture determinism, sentinel fail/recovery, scanner self-test ve artifact canary kontrolleri geçti. `19.626` byte local summary SHA-256 `c25996dea148d3c131f84bfa215e2ebd0122790fb3058bd71d5969e97953652f` taşır; `commitSha=null` olduğu için bu yalnız local kanıttır.

Run `#32`, database ID `32397220243`, commit `9315bc75df1d22fada00be5f350fbb961fc6b816` üzerinde `2026-08-20T17:22:14Z`–`17:31:03Z` arasında tamamlandı. `Locked build and test gate` `3m21s`, `Packaged install and launch smoke` `5m8s`, `DPAPI real-user boundary smoke` `2m48s` ve `Required Windows gate` `4s` içinde geçti.

İndirilen sanitized artifact dosyalarının bağı:

- quality summary: `15.391` byte, SHA-256 `78f1225857807c6f70a8b5f1db9717088ff0773ffc9e1d8c4ab02208b7b49167`, commit `9315bc75df1d22fada00be5f350fbb961fc6b816`;
- DPAPI real-user summary: `1.766` byte, SHA-256 `3d6e0dbeb697daf09b5da23d06eb2e4f5898aeb45c6e6933bce16450fa24fd77`, exact SDK `10.0.302`, 32/32 Boolean `true`;
- package lifecycle summary schema v3: `2.483` byte, SHA-256 `ff4cf111e18860cc1fed8826cb5d548ef82e091d05c27a52c497563ca463db81`, bütün Boolean alanlar `true`;
- packaged smoke summary: `865` byte, SHA-256 `62a40ef44d26f396edb86f702d8bdb8a19e2addd2399b6a7c05c188e611f8b0e`.

## Açık ama M4 dışı işler

- M5: resolved-origin, redirect, TLS, endpoint ve credential-bearing transport policy.
- M8: durable owner inventory, SQLite transaction/state machine, source-wide lifecycle/reconciliation, crash/recovery ve gerçek parser→normalize→index end-to-end 50k kanıtı.
- M15: production package identity/PFN, repair ve clean-machine/Store lifecycle matrisi.
- Accepted residual risk: yükseltilmemiş user-local store'da same-user adversarial path/reparse TOCTOU; scope değişirse karar yeniden açılır.

Bu maddeler M4 acceptance'ının parçası değildir ve kendi milestone'larında `UNVERIFIED` kalır.
