# ADR-006 — Samsung TV platform stratejisi

**Status:** Proposed

**Date:** 2026-08-09

## Context / Problem

Samsung ikinci platformdur; Windows'un UI, input, player veya secure-storage stack'ini paylaşmaz. Tizen TV model yılı, OS, Chromium engine ve codec matrisi parçalıdır. Mağaza yalnız Tizen Web `.wgt` veya Tizen .NET `.tpk` uygulama tipini kabul eder ve tip sonradan değiştirilemez. Türkiye dağıtımı ayrıca Partner Seller/Content Manager ve offline sözleşme sürecine bağlıdır.

Güvenli credential storage hard gate'tir. Samsung `WidgetData` encrypted CRUD API'si Tizen 4.0'dan itibaren bulunur; bu teknik ürün alt sınırını doğrudan etkiler.

## Decision drivers

- Seller Office tarafından desteklenen, güncel ve test edilebilir paket modeli.
- HLS/TS, track, 4K/adaptive playback ve uzun canlı yayın.
- Smart Remote, görünür focus, Back/Exit ve 10-foot UX.
- Credential'ı platform korumalı saklama.
- 2018–2026 model/engine/codec parçalanmasını yönetilebilir tutma.
- Store signing, review erişimi ve Türkiye ülke dağıtımı.
- Windows'la varsayımsal code share yerine platform kararlılığı.

## Considered options

1. Tizen Web `.wgt` + Samsung AVPlay.
2. Tizen Web + HTML5 `video`.
3. Tizen .NET `.tpk` + Samsung Player/JuvoPlayer.
4. Tizen Web + Tizen WASM Player/custom FFmpeg pipeline.
5. Bağımsız native C/C++ veya ortak cross-platform framework.

## Decision

Samsung uygulaması Windows'tan ayrı:

- **paketlenmiş Tizen Web application (`.wgt`)**,
- **Samsung AVPlay** platform player'ı,
- minimum **2018 model yılı / Tizen 4.0**,
- platforma özel remote/focus/lifecycle ve `WidgetData` secret-store adapter'ı

olarak **Proposed** seçilir.

AVPlay için `prepareAsync` ve explicit player state machine kullanılır. Standard HTML5 video yalnız AVPlay comparison/baseline'dır. Tizen .NET, parçalı framework/debug/player ve native ABI riskleri nedeniyle seçilmez. Tizen WASM Player; AVPlay gerçek corpus'ta yetersiz kalır ve custom demux/buffering/sync bütçesi onaylanırsa revisit seçeneğidir, başlangıç motoru değildir.

Ortak runtime/UI/player kodu yoktur. Versioned terminology, provider contracts, error registry, sentetik M3U/provider fixtures ve playback corpus manifesti paylaşılır. Tizen implementation 2018 web-engine baseline'ına transpile edilir; unsupported API/polyfill bütçesi explicit tutulur.

Bu karar iki hard gate kapanmadan `Accepted` olmaz:

1. Türkiye/başlangıç ülkeleri için Partner Seller, Content Manager ve sözleşme yolunun yazılı olarak açılması.
2. WGT + AVPlay + WidgetData'nın gerçek cihaz/store spike'ını geçmesi.

## Consequences and trade-offs

- En resmî TV web/player yoluyla Samsung dokümanı ve cihaz araçları izlenir.
- Windows code share azalır; aynı davranış test vectors ile doğrulanır.
- Minimum 2018, daha eski TV'leri dışarıda bırakır fakat secure storage hard gate'ini korur.
- Eski Chromium baseline bundle/polyfill ve performans disiplinini artırır.
- Model/OS upgrade/codec sonucu tek “Samsung supported” sözüne indirgenemez.
- Mağaza/sözleşme teknik geliştirmeden önce ticari blocker olabilir.

## Risks

- Direct TS, bozuk HLS, HEVC/AC-3/E-AC-3 veya multi-track'in model bazında başarısız olması.
- `WidgetData` update/uninstall/reset/OS-upgrade yaşam döngüsünün belirsizliği.
- 20.000 karakter/yazım sınırı ve bulk locator tasarımı.
- 2023+ cihazın fabrika ve upgrade OS davranışının ayrışması.
- 2018 Chromium engine'de UI/bundle performansı.
- Smart Remote focus trap, yanlış Back/Exit davranışı veya emulator-gerçek cihaz farkı.
- Samsung'un BYO IPTV ürününü ya da hedef ülke dağıtımını kabul etmemesi.
- 2026 native GCC ABI değişiminin future WASM/native dependency'yi kırması.

## Validation plan

Önce P0 business preflight:

- Samsung 1:1 Q&A/Content Manager'a “içerik/hesap sağlamayan, recording/DRM bypass içermeyen, kullanıcının yetkili endpoint'iyle çalışan player” açıklaması;
- Partner Seller, Türkiye ve diğer ülkeler, test erişimi, privacy ve rating gereksinimlerinin yazılı teyidi.

Teknik spike:

- 2018/Tizen 4.0, orta nesil, 2023 fabrika/upgraded OS ve 2025/2026; mümkünse Basic/Premium;
- HLS-TS/fMP4, direct TS, H.264/HEVC, AAC/AC-3/E-AC-3, multi-audio/subtitle ve discontinuity;
- reconnect/cancel, 100 switch, 2 saat ardından 8 saat soak;
- remote yön/Enter/Back/Exit, focus görünürlüğü ve accessibility;
- `WidgetData` write/read/remove, limit, update, uninstall/reinstall, Smart Hub reset ve OS upgrade;
- certificate/signing, WGT update ve Seller Office test submission.

Simulator/emulator sonucu gerçek cihaz geçişi sayılmaz.

## Revisit triggers

- Partner Seller/ülke dağıtımı veya BYO IPTV review yazılı olarak engellenirse.
- AVPlay Tier A corpus başarı oranını geçemezse.
- Tizen 4.0 `WidgetData` yaşam döngüsü güvenlik beklentisini karşılamazsa.
- 2015–2017 desteği ticari zorunluluk olursa; alternatif secure storage yazılı kanıt gerektirir.
- Samsung güncel ve daha uzun ömürlü yeni uygulama/player modeli sunarsa.
- Tizen WASM Player/custom pipeline için ölçülmüş, fonlanmış ihtiyaç oluşursa.

## References

[S42–S53](../research/SOURCES.md)
