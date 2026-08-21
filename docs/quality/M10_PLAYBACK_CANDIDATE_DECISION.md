# M10 playback candidate decision evidence

**Karar:** `NO-GO — ExactBinaryLicenseBoundaryUnresolved`

**Tarih:** 2026-08-21

## Kanıt

- Exact aday: `LibVLCSharp 3.10.0`, `LibVLCSharp.WinUI 3.10.0`, `VideoLAN.LibVLC.Windows 3.0.23.1`.
- Locked restore ve Release x64 gerçek `VideoView` scaffold build'i başarılıdır; production dependency/payload izolasyonu korunur.
- Restored x64 native payload 323 plugin DLL içerir.
- `build/x64/plugins/codec/libx26410b_plugin.dll` SHA-256: `48e97b7d639e07a31e0a185016cf890c9c0cc625684af18ed5427acc08dda28b`.
- Upstream libvlc-nuget GPL-compliance kaydı `libx264_plugin.dll` ailesinin GPL paketine ayrılması gerektiğini bildirir [S110]. VideoLAN x264 sayfası x264'ın GNU GPL altında yayımlandığını, ticari lisansın ayrıca sunulduğunu bildirir [S111].
- Nupkg nuspec'i genel `LGPL-2.1-or-later` expression taşır; arşiv exact binary/plugin-to-source, COPYING/license ve notice kabulünü kapatan bir manifest sunmaz.

`eng/Invoke-WindowsPlaybackCandidateDecision.ps1` exact SDK/lock/package/version bağını doğrular, x64 plugin envanterini tarar, blocker hash'ini kaydeder ve sanitized `decision-summary.json` üretir. Scriptin başarılı çalışması candidate'in geçtiği değil, beklenen güvenlik kararının tekrarlanabilir biçimde **NO-GO** olduğu anlamına gelir.

## Sınır ve devam kararı

GPL kontrolü kapatılmadı, package adına güvenilmedi ve problemli DLL körlemesine çıkarılmadı. DLL silmek transitive lisans/source setini veya runtime plugin bütünlüğünü kanıtlamaz. Lisans hard gate'i erken başarısız olduğundan corpus/device/8 saat soak bu candidate için acceptance sonucu üretemez.

ADR-002 candidate'i reddedilir. [ADR-007](../adr/ADR-007-windows-native-tier-a-playback-fallback.md) ile Windows native Tier A fallback spike'ı açılır; o gate geçmeden M11 production adapter başlamaz.
