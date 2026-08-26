# M16 Native Tier A final acceptance profili

**Durum — TECHNICALLY PREPARED / NOT RUN, 2026-08-27**

Bu kayıt, M16'daki 24 saatlik native Tier A kabul koşusunun sabit teknik profilini tanımlar. Profilin kodda ve self-testlerde bulunması bir acceptance sonucu değildir; `M16TwentyFourHourSoakPending` bu exact koşu temiz bir commit üzerinde başarıyla tamamlanıp kalıcı kanıtı doğrulanana kadar açık kalır.

## Sabit profil

- Profil adı: `M16Final`
- Alternating switch: exact `200`
- Süre: exact `1440` dakika; probe başlangıcından itibaren ölçülür
- Sentetik loopback network interruption/recovery: exact `7/7`
- Inline cancellation probe: exact `0`; ayrı cancellation acceptance zinciri değiştirilmez
- Resource örnekleme: beş dakikada bir; transient envelope'da `286..290` sample
- Raw probe envelope: en çok `128 KiB`; persistent sanitized evidence raw sample veya locator içermez
- Persistent success evidence: schema `11`, stage `M16NativeTierAFinalAcceptance`

Kabul eşikleri M10/final-hardening kararından değiştirilmemiştir: startup p95 `≤3000 ms`, startup maximum `≤5000 ms`, source-detach p95 `≤3000 ms`, source-detach maximum `≤5000 ms`; 30 dakikalık warm-up sonrasında memory net growth hem `≤10%` hem `≤100 MiB` olmalı ve seri monoton artmamalıdır. Lifecycle, exact detach, decoder/runtime, TLS loopback, package/certificate cleanup ve repository-clean invariant'ları da fail-closed kalır.

Sample aralığı yeni bir resource-budget toleransı değildir. Beş dakikalık cadence'i taşıyan stopwatch switch fazından önce başlar; exact 24 saat sonundaki partial cadence sample'ı ile 200 switch'in ölçüm penceresinde tükettiği süre nedeniyle count tek bir sabit ordinal değildir. Controller'ın değişmeyen lower-bound formülü `(1440 / 5) - 2 = 286`, spike/controller'ın sabit üst kapasitesi `290`dır; ayrı validator aynı iki sınırı birebir uygular.

Varsayılan `M10` profilinin `2..100` switch, `0..480` dakika, kısa/sekiz saat parametreleri, schema `10` evidence'ı ve mevcut validator komutu aynen korunur. `M16Final` seçilmeden `200/1440` değerleri kabul edilmez; `M16Final` seçildiğinde dört profil parametresinden herhangi bir sapma kabul edilmez.

## Çalıştırma

Koşu otomatik başlatılmaz. Temiz worktree ve exact SDK `10.0.302` doğrulandıktan sonra, kullanıcı tarafından bilinçli olarak açılmış elevated ve interactive Windows Client PowerShell oturumunda repository kökünden çalıştırılacak exact komut:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\Invoke-WindowsNativePlaybackSmoke.ps1 -Configuration Release -DotNetPath .\.artifacts\dotnet\dotnet.exe -AcceptanceProfile M16Final -SwitchCount 200 -SoakMinutes 1440 -NetworkInterruptionCount 7 -CancellationProbeCount 0
```

Koşu başarıyla persistent evidence ürettikten sonra aynı commit, controller ve SDK bağı ayrı M16 validator ile doğrulanır:

```powershell
.\.artifacts\dotnet\dotnet.exe .\apps\windows\tests\IptvSuite.Testing\bin\Release\net10.0\IptvSuite.Testing.dll validate-m16-native-playback-evidence <evidence-json> .\eng\Invoke-WindowsNativePlaybackSmoke.ps1 <40-char-lowercase-commit-sha> 10.0.302
```

`<evidence-json>` ve commit SHA gerçek koşunun çıktısından alınmalıdır; placeholder ile doğrulama yapılmaz. 24 saatlik çalıştırma sırasında candidate kodu/build'i değiştirilmez. Başarısız, yarım veya provenance bağı kurulamayan bir koşu acceptance sayılmaz ve eşikler rerun elde etmek amacıyla gevşetilmez.

## Kapsam sınırı

Bu profil sentetik TLS loopback Tier A playback, process/resource ve owned-package cleanup zincirini ölçer. Exact `DetachedSourceCount = switch + retry + soak-source` bağı managed tek-source/detach sahipliğini doğrular; ancak evidence OS audio-session inventory'si taşımaz. Dolayısıyla bu validator double-audio/ghost-audio yokluğunu OS seviyesinde kanıtlamaz ve tek başına o M16 maddesini kapatmaz; operator/fiziksel-device disposition'ı ayrıca gerekir. Fiziksel cihaz/HW decode, Narrator/accessibility, multi-monitor/DPI, audio-device, sleep/resume, WACK/Store, hukuk veya release operasyon kabulünün yerine geçmez.
