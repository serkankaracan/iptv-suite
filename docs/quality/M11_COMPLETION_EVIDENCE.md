# M11 production player ve temel playback completion kanıtı

**Durum:** `COMPLETED, 2026-08-24`

## Kabul edilen production sınırı

M11, engine-neutral Application contract'ını, app-owned tek-session coordinator'ı, protected Remote-M3U ve Xtream live binding'lerinin just-in-time çözümünü, Windows-native Tier A adapter'ı ve packaged WinUI playback composition'ını kapatır. Public Application/UI yüzeyi yalnız `SourceId + ChannelId`, typed state/result ve bounded opaque track ordinal'ları taşır; raw locator, credential, `Uri`, native/WinUI tipi, exception, HRESULT veya diagnostic text taşımaz.

Session lifecycle; monoton session ID, session-lifetime cancellation, serialized engine mutation, A→B exact stop-before-open, stale/wrong-order callback suppression ve idempotent disposal ile korunur. Volume, mute, `Fit`/`Fill` ve track işlemleri exact current session'a bağlıdır. Xtream live locator yalnız authoritative configuration owner/reference/endpoint/provider/container tuple'ından operation-local kurulur; `/live/{username}/{password}/{providerItem}.{m3u8|ts}` dar ve `UNVERIFIED` bir uyumluluk varsayımıdır, genel provider standardı iddiası değildir.

## Local ve commit-bound test kanıtı

- M11 implementation zinciri `e26701d` production-package candidate'i ile başladı; UIA timing/API ve runner-boundary düzeltmeleriyle clean head `5a07cae8138642d669f272eaee1340e87edcb47b` üzerinde kapandı.
- Exact SDK `10.0.302` ile locked restore, Debug/Release x64 build, architecture `35/35`, unit `171/171` ve integration `116/116` local olarak geçti; build'ler `0` warning/`0` error üretti.
- [GitHub Actions run `32754184474`](https://github.com/serkankaracan/iptv-suite/actions/runs/32754184474), aynı head üzerinde quality `354/354 × 2`, packaged install/launch/playback, protected-store lifecycle, DPAPI real-user boundary ve Required Windows gate işlerini geçti.
- Normal push'ta Windows Client-only M10 native smoke'unun `skipped` olması beklenen davranıştır. Bu iş kaldırılmamış veya gevşetilmemiştir; yalnız explicit dispatch ile `self-hosted/Windows/X64/iptv-windows-client` runner'da çalışır. M11'in production-package acceptance işi hosted x64 pakette ayrıca başarıyla tamamlanmıştır.

## Hosted packaged acceptance

`Packaged install and launch smoke` işi `5m1s` içinde tamamlandı. İndirilen `windows-msix-smoke-evidence/last-success.json`, exact commit ve SDK bağıyla aşağıdaki sonuçları taşır:

| Kontrol | Sonuç |
|---|---:|
| Signature / payload leak gate | `Valid` / `true` |
| Protected store initialization | `true` |
| Önceki M9 50k UIA contract | `true`; realized item `8` |
| Input / DWM p95 | `3,064 ms` / `31,25 ms` |
| DWM maximum / late-drop proxy | `156,25 ms` / `%0` |
| M11 playback UI acceptance | `true` |
| Loopback request / completed response | `1 / 1` |
| Completed body bytes | `1.002.980` |
| Normal close / exact package removal | `true / true` |

Protected-store lifecycle evidence aynı committe same-signer update, reset, uninstall/reinstall fresh-state, cross-owner rejection, `CurrentUser` protection ve exact process/account/package/certificate/output cleanup kontrollerini geçti. DPAPI evidence distinct standard-user cryptographic boundary ve repository-clean postcondition'ını geçti. Required coordinator bütün uygulanabilir işleri başarıyla zorunlu tuttu.

## Artifact bağı

| Artifact | ID | Metadata size | GitHub digest | İndirilen JSON SHA-256 |
|---|---:|---:|---|---|
| `windows-quality-evidence` | `9530346557` | `9026` byte | `4076e48e93f84d3cee912ed8c22774dd78fc70c6733705c45197d2bb9e59068e` | `5136be91f27ddde3f063c01e185a6b2f9b36b9fa9aef506a364059a55373c644` |
| `windows-msix-smoke-evidence` | `9530525310` | `842` byte | `df1aa19f1f5d46b9243f6aeef6e43d8f0dcf8afd5b2e72496cf534e03bcd42f6` | `2d87ff39675f23aff6690c471d5890ae3ca7f5a8fc91924dfebb7d0061bb8c38` |
| `windows-package-lifecycle-evidence` | `9530526044` | `1006` byte | `81694e143fabc230dc45473798e699c374f0cfc8ac2eed280e1e52ed9176c97e` | `4932b2eaddc7a5a3199354ea806ad52367b59eabd4b2a3cab3b2b015550925fe` |
| `windows-dpapi-user-boundary-evidence` | `9530424683` | `871` byte | `e77bc6e7bdafd49b0d475205636ffb548d178d3a43e4111113c4587fc370995b` | `0f1c72f75f02b6f1df45306eb82ffc318d4e9009218af12bc13056b07d565be2` |

GitHub digest'i artifact servisinin archive digest'idir; son sütun indirilen ilgili JSON payload'ının digest'idir.

## Kapsam sınırı

Bu kabul sentetik, lisans/provenance kayıtlı loopback HLS ve belirtilen x64 commit için production-package `play → pause → resume → stop` zincirini kanıtlar. Gerçek provider uyumluluğu, Tier B codec/subtitle/track desteği, fullscreen ve display/device lifecycle matrisi, reconnect policy, WACK/Store, ARM64 veya genel accessibility sertifikasyonu değildir. Bunlar M12–M16 kapılarında kalır. M10'daki `%10` relative-memory sapması da threshold değiştirilmeden R15/M16 hardening borcu olarak açıktır.
