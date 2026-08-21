# M10 Windows native Tier A source-detachment kanıtı

**Durum:** `PARTIAL VERIFIED — packaged source detachment ve tek fault altında başarılı switch zinciri, 2026-08-21`

## Kanıt bağı

- Parent commit: `2add3299ff3c19ee2871588a22db2f1a138ba97c`
- Test edilen worktree: aşağıda listelenen üç source-detachment değişikliği; checkpoint commit'i bu doğrulamadan sonra oluşturulur
- Controller: `eng/Invoke-WindowsNativePlaybackSmoke.ps1`
- Komut: `-Configuration Release -DotNetPath .\.artifacts\dotnet\dotnet.exe -SwitchCount 100 -SoakMinutes 0 -NetworkInterruptionCount 1`
- Evidence schema: `7`
- Evidence dosyası yazım zamanı: `2026-08-21T19:49:50.9936783Z`
- Evidence SHA-256: `ac4648cdc03b6c915a30702c5fdfd42e419a1a94943d57efa122b22b62be9214`
- Controller SHA-256: `14d283f10d0a4bb62752f96ea074919f1c768940adca328cf1f9576fb72f207f`
- Test MSIX SHA-256: `41714214b6dffa915b648e1d4205e8e6371d114a038db6176bc8683f12b11b86`

## Root cause ve invariant

Önceki development diagnostic koşusunda `MediaPlayer.Source = null` tamamlandıktan sonra `PlaybackSession.PlaybackState` okunmuş; `StateInspection` aşamasında `COM E_FAIL` (`0x80004005`, signed `-2147467259`) üretilmiştir. Stage ve HRESULT gerçek Windows Client runtime gözlemidir. Microsoft kaynağı bu HRESULT'ı belgelememektedir; yalnız pause mümkünse `CanPause` ile guard etmeyi, source bağını `null` ile ayırmayı ve artık kullanılmayan source/player nesnelerini dispose etmeyi gösterir [S115].

Hardened invariant, playback-session state'ini yalnız source ayrılmadan önce tanı için okur. Sonrasında bounded `MediaPlayer.Source = null` ve getter ile exact-null postcondition'ını doğrular; post-detach `PlaybackSession` yüzeyini sorgulamaz. Her attempt kendi `MediaSource` nesnesine sahiptir ve başarılı source detachment sonrasında bu nesne dispose edilir. Typed probe sonucu best-effort final cleanup tarafından maskelenmez; controller exact process, package ve ephemeral certificate cleanup'ını ayrıca yürütür.

## Sonuçlar

| Ölçüm | Sonuç | Kabul |
|---|---:|---:|
| Scripted switch | 100 | 100 |
| Detached source | 100 | switch + retry + soak-final exact beklenen sayı |
| Playback retry | 0 | `≤` istenen interruption sayısı |
| Source-detachment p95 | `7,986 ms` | `≤3000 ms` |
| Source-detachment maximum | `9,493 ms` | `≤5000 ms` |
| Startup p95 | `261,169 ms` | `≤3000 ms` |
| Startup maximum | `2.576,309 ms` | `≤5000 ms` |
| Surface transition | 6 | 6 |
| Sentetik HTTP interruption/recovery | 1 / 1 | 1 / 1 |
| Injected/recovery request ordinal | `54 → 56` | recovery ordinal injection'dan sonra |
| Loopback media request | 401 | en az switch sayısı |

`PlaybackRetryCount=0`, application'ın `MediaFailed` tabanlı bounded retry yolunun bu koşuda devreye girmediğini gösterir. Exact 100 switch ile 100 source detachment tamamlanmıştır; HTTP-level injection/recovery sayaçları tek başına belirli bir playback attempt'iyle nedensellik kurmaz.

Koşu sonrasında exact disposable package, ephemeral package-signing certificate ve loopback TLS certificate sayıları ayrı kontrolde sıfırdı.

## Yerel doğrulama

- Exact SDK `10.0.302` ile locked solution restore: PASS.
- Debug/x64 ve Release/x64 solution build: PASS, `0` warning / `0` error.
- Native playback spike Release/x64 build: PASS, `0` warning / `0` error.
- Architecture suite: `28/28` PASS.
- Controller PowerShell parse ve embedded TLS server C# compile: PASS.
- `.artifacts/native-playback-smoke` canary scan: exit `0`.

## Değişen dosyalar

- `apps/windows/tests/IptvSuite.NativePlaybackCompatibilitySpike/MainWindow.xaml.cs`
- `eng/Invoke-WindowsNativePlaybackSmoke.ps1`
- `apps/windows/tests/IptvSuite.ArchitectureTests/DependencyRulesTests.cs`

## Sınır

Bu sonuç app-owned source bağının bounded biçimde ayrıldığını ve owned `MediaSource` yaşam döngüsünün kapandığını kanıtlar. Audio renderer'ın sessizleştiğini, native callback'lerin bütünüyle drain olduğunu, OS playback session'ının quiescent olduğunu, ghost/double audio bulunmadığını veya kullanıcı cancellation davranışını ölçmez. DNS/TLS/connect timeout, mid-body disconnect, stall, ardışık fault, 8 saat acceptance soak, device/HW-decode, WACK ve kalan surface matrisi de açık kalır. Bu nedenle M10 tamamlanmış sayılmaz ve ADR-007 `Proposed` kalır.
