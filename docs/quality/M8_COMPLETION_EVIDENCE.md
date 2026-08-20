# M8 local persistence completion kanıtı

**Durum:** `COMPLETED, 2026-08-21`; ADR-003 `Accepted`

## Kabul edilen production sınırı

M8 katalog yerleşimi versioned SQLite schema içinde source/snapshot/category/channel/favorite/sanitized sync-run metadata'sını tutar. Her snapshot fresh 256-bit DEK kullanır; DEK DPAPI `CurrentUser` ile sarılır, locator kayıtları AES-256-GCM ile korunur ve key state, ciphertext rows, snapshot state ile source active pointer aynı SQLite transaction domain'inde değiştirilir. Source credential ve remote-playlist configuration secret'ları düşük-cardinality owner-bound DPAPI store'da kalır.

## Acceptance kanıtı

- Fresh schema, v1→v2 forward migration, injected migration rollback ve corrupt/incomplete DB fail-closed testleri geçer.
- Streaming parser doğrudan transaction sink'e yazar; complete activation tek commit'tir. Concurrent reader refresh commit'ine kadar eski snapshot'ı görür.
- Cancellation/fault rollback, startup importing-snapshot reconciliation, retained snapshot prune, favorite reconciliation, HTTP validator/cache binding, bounded sync history ve idempotent `DeletionPending` source delete testleri geçer.
- Gerçek process-crash testi `IptvSuite.CatalogCrashHarness` ile önce `Old channel` snapshot'ını commit eder; replacement transaction içinde ilk yeni channel yazıldıktan sonra controller yalnız izlediği child process tree'yi öldürür. Yeni process startup'ı DB'yi açtığında source/snapshot/channel/sync-run sayıları yine bire birdir, active result `Old channel`dır, hot rollback journal yoktur ve WAL/SHM yoktur. Sıfırlanmış header taşıyan inert rollback journal SQLite recovery failure'ı sayılmaz.
- Production package taraması crash harness assembly/artifact adını reddeder; architecture guard solution x64 Build/no-Deploy, nonpackable/nonpublishable ve production dependency isolation sınırlarını kilitler.
- Clean HEAD `9042386f9a28ef77c68b894d8c5701312bd3d04f` üzerinde exact SDK `10.0.302` ile Debug/Release x64 build'leri `0` warning/`0` error; architecture `21`, unit `157`, integration `102` olmak üzere `280/280` test iki deterministic koşuda geçmiştir. Fixture hashleri eşleşmiş; armed/disarmed sentinel, scanner CLI ve artifact canary kontrolleri geçmiştir. Local quality summary `commitSha=null` ürettiği için bu test sayısı hosted commit-bound artifact iddiası değildir.

## Ölçüm kararı

Clean commit `3f547a7ad625f04f929ade96ba0e2f4660ced24c` üzerindeki 5k/10k/20k/50k ×20 Decision koşusunda 50k p95 import `2.738,759 ms`, allocation `116,330 MiB`, working-set delta `6,164 MiB`, cancellation `12,390 ms` ve DB boyutu `55.148.544` byte ölçülmüştür. Böylece birleşik normalize + protected persistence + index `≤3 s`, bütün-import allocation `≤150 MiB`, working-set delta `≤250 MiB` ve cancellation `≤250 ms` component hedefleri bu sentetik local-host kapsamında karşılanmıştır. Exact summary/hash ve yorum sınırları [M8 catalog persistence Decision evidence](M8_CATALOG_PERSISTENCE_DECISION_EVIDENCE.md) belgesindedir.

## Kapsam sınırı

Bu kabul M9 WinUI virtualization/scroll/UIA, M10 playback, M15 production PFN/Store identity/repair veya reference-device UI end-to-end ölçümü değildir. M8 yalnız device-local katalog persistence, cache/index ve lifecycle transaction sınırını kapatır.
