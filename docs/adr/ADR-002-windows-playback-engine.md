# ADR-002 — Windows playback motoru

**Status:** Proposed

**Date:** 2026-08-09

## Context / Problem

Windows MVP; HLS/M3U8, direct MPEG-TS, H.264/HEVC, AAC/AC-3/E-AC-3, track seçimi, hardware decode, hızlı kanal değiştirme ve uzun canlı yayın stabilitesini hedefler. “Destek” tek bir player adına bağlanamaz; motor sürümü, OS, container/protocol, codec, donanım/driver ve dağıtılan plugin setinin birlikte gözlenen bir capability'sidir.

Windows native media API'leri temiz dağıtım sağlar; ancak güncel codec matrisi geniş IPTV kapsamını garanti etmez. FFmpeg ve mpv daha geniş kontrol sağlar fakat production player, native build, .NET surface, Store ve lisans sahipliği yükünü büyütür.

## Decision drivers

- HLS-TS, HLS-fMP4 ve direct HTTP MPEG-TS.
- H.264, HEVC, AAC, AC-3 ve E-AC-3 ile multi-audio/subtitle.
- D3D11/DXVA hardware decode ve software fallback.
- Bounded timeout/reconnect, cancellation ve teşhis sinyalleri.
- WinUI surface/lifecycle ve MSIX x64/ARM64.
- Ticari dağıtıma uygun exact OSS binary/license envanteri.
- Upstream bakım, CVE patch süresi ve ekip öğrenilebilirliği.

## Considered options

1. Windows `MediaPlayer` / Media Foundation.
2. libVLC + LibVLCSharp.
3. Doğrudan FFmpeg tabanlı custom player.
4. mpv/libmpv.
5. UI framework media wrapper'ları.

Puanlama [Architecture Report](../architecture/ARCHITECTURE_REPORT.md) bölüm D'dedir. Hard gate sonucu toplam puandan üstündür.

## Decision

Birincil aday:

- `LibVLCSharp.WinUI 3.10.0`,
- sürüm uyumlu `LibVLCSharp 3.10.0` başlangıç seti; core 3.10.1 yükseltmesi ayrı doğrulama,
- `VideoLAN.LibVLC.Windows 3.0.23.1`,
- yalnız **non-GPL** native paket,
- uygulamanın sahibi olduğu `IPlaybackEngine` adapter'ı

olarak **Proposed** seçilmiştir. Exact paketler ancak M10 compatibility/license/MSIX spike'ı geçerse pinlenir ve bu ADR `Accepted` yapılır. LibVLC log metinleri protokol gibi parse edilmez; typed event, state ve app-owned timeout/reconnect state machine kullanılır.

Windows native `MediaPlayer`, dar H.264/AAC HLS baseline/fallback ve karşılaştırma motorudur; geniş codec/container vaadi değildir. FFmpeg custom player MVP için mühendislik kapsamı nedeniyle, mpv ise first-party Windows binary/.NET surface ve kontrollü LGPL build yükü nedeniyle reddedilir. Framework wrapper'ı motor kararı sayılmaz.

## Consequences and trade-offs

- FFmpeg custom player'a göre hazır player lifecycle, track ve event yüzeyi kazanılır.
- Native API'ye göre daha geniş format/codec ihtimali ve platformlar arası player adapter benzerliği elde edilir.
- Native Windows paketi yaklaşık 128 MB'dir; paket boyutu ve attack surface artar.
- Plugin discovery, architecture layout, trimming/AOT ve update sahipliği oluşur.
- LGPL uyumu codec patent izni değildir; iki inceleme ayrı kapanır.
- LibVLC 4 stable geçişi gelecekte API/plugin migration gerektirebilir.

## Risks

- Non-GPL pakette bir hedef codec/plugin'in bulunmaması.
- WinUI surface, fullscreen, overlay, DPI veya hızlı switch sorunu.
- GPL plugin'in yanlışlıkla pakete girmesi ya da LGPL yükümlülüklerinin eksik karşılanması.
- H.264/HEVC/AAC/Dolby patent yükümlülüklerinin hedef ülke/iş modeline uymaması.
- ARM64 paketin bulunmaması veya D3D hardware decode'un cihaz/driver'a göre kırılması.
- Native crash, leak veya upstream security patch gecikmesi.
- Store WACK/certification veya paket boyutu engeli.

## Validation plan

M10 hard gate, hakları temiz sentetik corpus ve temiz Windows cihazlarında:

1. HLS-TS, HLS-fMP4 ve continuous HTTP TS.
2. H.264 High, HEVC Main/Main10, AAC-LC/HE-AAC, AC-3, E-AC-3.
3. Multi-audio, subtitle/CC, language metadata, aspect fit/fill ve runtime track switch.
4. Discontinuity, timestamp reset, kayıp/bozuk segment, EOF ve malformed manifest.
5. DNS/TLS/timeout/drop; 401/403/404/429/5xx; query/header auth; same-origin ve cross-origin redirect.
6. Bounded retry, user cancellation ve channel change sırasında eski session/retry iptali.
7. Intel/AMD/NVIDIA referanslarında D3D11 decode; failure'da software fallback.
8. Resize, DPI, fullscreen, multi-monitor, minimize/restore, sleep/resume ve audio-device değişimi.
9. En az 100 ardışık channel switch ve 8 saat soak; crash/deadlock/monoton bellek artışı yok.
10. Release MSIX install/update/uninstall; x64 ve hedefleniyorsa native ARM64; WACK/private submission dry run.
11. SBOM, binary-to-source mapping, notices/license text; GPL binary/plugin sıfır.
12. URL query, user-info, header veya credential sızdırmayan diagnostics.

Tier A HLS-TS/direct TS + H.264/AAC yüzde 100 geçmelidir. Diğer kombinasyonlar capability matrisine işlenir; ürünün yayın vaadi hukuk ve saha sonuçlarıyla belirlenir. Bir hard gate başarısızsa native dar kapsam, kontrollü mpv build veya ürün codec kapsamını daraltma seçenekleri yeni ADR ile değerlendirilir.

## Revisit triggers

- M10 hard gate'lerinden biri kapanmazsa.
- LibVLC 4 stable olur veya LibVLC 3 bakım/security desteği sona ererse.
- Microsoft temiz OS'te gerekli HEVC-in-TS/E-AC-3 kapsamını garanti ederse.
- Ürün kapsamı yalnız H.264/AAC HLS'ye daralırsa.
- Store, package size, ARM64, lisans ya da codec patent incelemesi dağıtımı engellerse.
- Saha crash/startup/stream-success oranı kabul bütçesini aşarsa.
- DRM, recording veya düşük gecikmeli custom pipeline kapsamı eklenirse.

## References

[S21–S32, S60–S62](../research/SOURCES.md)
