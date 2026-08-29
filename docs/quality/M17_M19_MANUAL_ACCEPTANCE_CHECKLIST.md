# M17–M19 manuel kabul kontrol listesi

**Durum:** `MANUAL CHECKLIST / NOT RUN, 2026-08-29`

## Amaç ve kanıt sınırı

Bu belge, mevcut Windows geliştirme paketinde M17 Home/Source Manager, M18 Movie oynatma/seek ve M19 Series/Season/Episode akışlarını kullanıcıyla adım adım gözlemlemek içindir. Her adım sorunsuzsa yalnız belirtilen exact `PASS` belirteci yazılır. Sorun varsa o adım için `PASS` yazılmaz; bunun yerine belirteç kimliği, `FAIL` ve credential içermeyen kısa semptom paylaşılır.

Bu manuel gözlem tek başına M17, M18 veya M19'u tamamlamaz. Locked build/test, sentetik integration corpus, signed-package yolculuğu, accessibility ve ilgili security/canary kapıları ayrı kanıt ister. M15/M16 status veya blocker'larını da değiştirmez.

## Güvenli test girdisi ve hazırlık

- Yalnız test sahibinin erişim yetkisi bulunan, disposable ve sentetik fixture'ları kullan. Gerçek provider hesabı, gerçek playlist, canlı credential veya yetkisiz servis kullanma.
- Bu belgeye, sohbete, ekran görüntüsüne veya hata kaydına URL, server adresi, username, password, token, `Authorization`/`Cookie` değeri yazma. Source editor açıkken ekran görüntüsü alma.
- `SYNTH-A`, en az bir Live, sonlu ve seek-capable bir Movie, bir Series, bir Season ve sonlu/seek-capable bir Episode içeren sentetik Xtream-compatible fixture'dır.
- `SYNTH-B`, kanal adları `SYNTH-A`'dan ayırt edilebilen en az bir Live kaydı içeren ikinci sentetik kaynaktır. Remote M3U olabilir; Remote M3U kayıtları Movie/Series diye heuristic olarak ayrılmaz.
- `SYNTH-CRUD`, create/rename/refresh/replace/delete için disposable sentetik kaynaktır. Replace fault denemesi için fixture controller'ın bilerek hata veren girdisi, success denemesi için geçerli alternatif girdisi kullanılır.
- Fixture manifestinden her kalıcı kaynak için yalnız hassas olmayan `Live / Movie / Series` beklenen sayılarını kaydet. Episode sayısı Home total hesabına eklenmez.
- Aynı Windows kullanıcısında x64 development paketini başlat; build/commit kimliğini hassas veri içermeyen test notuna yaz.

Hazırlık tamamlandığında exact yanıt:

`PREFLIGHT-01 PASS`

## M17-manuel-kabul

### M17-01 — Home sayıları

1. `SYNTH-A` ve `SYNTH-B` hazır durumdayken `Home` sayfasını aç.
2. `Live TV`, `Movies`, `Series`, `Catalog total` ve `Manage sources` kartlarının ayrı göründüğünü doğrula.
3. Source Manager'daki source-scoped sayılardan bekleneni hesapla:

   `LiveExpected = Σ Live_i`

   `MovieExpected = Σ Movie_i`

   `SeriesExpected = Σ Series_i`

   `TotalExpected = LiveExpected + MovieExpected + SeriesExpected`

4. Home'daki dört sayının beklenenlerle exact eşleştiğini doğrula. Aynı adlı cross-source kayıtlar düşülmez; Season/Episode sayıları total'a yeniden eklenmez. Loading veya unavailable sırasında geçici değeri kabul sonucu sayma.

Exact yanıt: `M17-01 PASS`

### M17-02 — Navigation ve ayrı Sources yüzeyi

1. Home kartlarından ve sol navigation'dan sırasıyla `Live TV`, `Movies`, `Series` ve `Sources` hedeflerine git; her seferinde doğru başlığı gör.
2. Aynı geçişleri klavye ile Tab/Enter kullanarak tekrarla; odak kaybolmamalı veya görünmeyen bir kontrole takılmamalıdır.
3. Playlist/account editor'ın yalnız ayrı `Sources` sayfasında, `Add source` veya `Replace or convert` seçiminden sonra açıldığını doğrula. Home, Live TV, Movies ve Series yüzeylerinde inline/overlay source formu görünmemelidir.
4. Editor'ı `Cancel` ile kapat ve Source listesine dön.

Exact yanıt: `M17-02 PASS`

### M17-03 — Ayrı Source Manager CRUD

1. `Sources > Add source` ile `SYNTH-CRUD` kaynağını ekle. `Validate and save` sonrası işlem boyunca spinner'ın görünür, edit kontrollerinin yeniden submit'e kapalı ve tamamlanma durumunun anlaşılır olduğunu doğrula.
2. Kaynağı `SYNTH-CRUD-RENAMED` olarak yeniden adlandır; URL/username/password değerlerinin ayrıntı panelinde tekrar gösterilmediğini doğrula.
3. `Refresh` uygula; source hazır kalmalı ve fixture değişmediyse count'lar aynı kalmalıdır.
4. `Replace or convert` ile fixture controller'ın bilerek hata veren sentetik alternatifini dene. Hata sonrası eski source listede ve oynatılabilir kalmalı; yarım/yinelenmiş source görünmemelidir.
5. Replace'i geçerli sentetik alternatifle yeniden yap. Authorization kutusunun yeniden işaretlenmesi gerektiğini, işlemin başarılı olduğunu ve listede tek etkin replacement kaldığını doğrula.
6. Disposable replacement'ı `Delete` ile sil ve onayla. Source listesi ve Home count'ları `SYNTH-CRUD` eklenmeden önceki baseline'a dönmelidir.

Exact yanıt: `M17-03 PASS`

### M17-04 — İki kaynağın birlikte görünmesi

1. `Sources` listesinde `SYNTH-A` ve `SYNTH-B` kaynaklarının ikisinin de hazır olduğunu doğrula.
2. `Live TV` source selector'da önce `SYNTH-A`, sonra `SYNTH-B`, sonra yeniden `SYNTH-A` seç. Her seçimde yalnız o fixture'a ait ayırt edilebilir kanallar gelmeli; ikinci source boş görünmemeli ve ilk source geri dönüşte kaybolmamalıdır.
3. Movies ve Series source selector'larında kaynak geçişinin eski category/search filtresinden kalan yanlış bir boş sonuç üretmediğini doğrula. Fixture'da o content kind gerçekten `0` ise görünür empty state beklenir.
4. Home'a dön ve aggregate sayıların M17-01 ile aynı olduğunu doğrula.

Exact yanıt: `M17-04 PASS`

### M17-05 — Remote M3U'dan typed Xtream kataloğuna dönüşüm

1. Yalnız sentetik fixture'ın yetkili `get.php` bootstrap varyantını önce `M3U URL` olarak ekle; kaynak sayımlarının yalnız Live TV altında olduğunu doğrula. Kategori veya ad üzerinden Movie/Series tahmini bekleme.
2. `Sources` içinde aynı kaynağı seçip `Replace or convert > Xtream API > Use an existing Xtream get.php M3U URL` akışını aç. Tam URL'yi yalnız editöre yapıştır; not, sohbet, ekran görüntüsü veya log'a kopyalama.
3. HTTP kullanılıyorsa fresh Xtream MITM onayını ve authorization kutusunu yeniden işaretle; `Validate and save` tamamlanırken spinner görünür olmalıdır.
4. Source listesinde tek kayıt ve aynı source kimliği kaldığını; sayımların fixture manifestindeki typed `Live / Movie / Series` değerleriyle eşleştiğini doğrula.
5. Live TV'de yalnız Live kayıtlarını, Movies'de Movie kayıtlarını ve Series'de Series kayıtlarını doğrula. Dönüşüm başarısız olursa önceki M3U snapshot'ı kullanılabilir kalmalı ve yarım ikinci source oluşmamalıdır.

Exact yanıt: `M17-05 PASS`

## M18-manuel-kabul

### M18-01 — Live TV regresyonu ve seek yasağı

1. `Live TV` içinde `SYNTH-A` veya `SYNTH-B` sentetik kanalını başlat; görüntü/ses ve `Channel is playing` durumunu doğrula.
2. Play, Pause, yeniden Play, Stop, volume +/− ve Mute kontrollerini birer kez kullan.
3. Live session'da timeline/seekbar'ın görünmediğini veya kullanılamadığını doğrula. Native duration/range görse bile Live TV için timeshift/catch-up seek açılmamalıdır.

Exact yanıt: `M18-01 PASS`

### M18-02 — Movie zaman bilgisi ve ileri/geri seek

1. `Movies` içinde `SYNTH-A` kaynağını ve sonlu/seek-capable sentetik Movie'yi seç.
2. Oynatma başladığında start değerinin `00:00`, current değerin ilerleyen bir zaman ve end/total değerin sonlu, sıfırdan büyük süre olduğunu doğrula.
3. Pause sırasında current değerin ilerlemeyi durdurduğunu, Play ile kaldığı yerden devam ettiğini doğrula.
4. Slider'ı en az 30 saniye veya toplam sürenin yaklaşık `%25`i kadar ileri taşı ve bırak. Keyframe toleransıyla current zamanın hedefe yakınsadığını ve eski konuma geri sıçramadığını doğrula.
5. Slider'ı daha erken bir zamana geri taşı; current zaman ve görüntü geri konuma yakınsamalıdır. Ardından Stop uygula.

Exact yanıt: `M18-02 PASS`

### M18-03 — Movie fullscreen auto-hide ve normal pencereye dönüş

1. Aynı sentetik Movie'yi yeniden başlat ve `Fullscreen` seç.
2. Mouse/klavye girdisini kısa süre bırak; üst ve alt uygulama chrome'u ile playback kontrolleri otomatik gizlenmeli, video dışında beyaz şerit kalmamalıdır.
3. Mouse'u hareket ettir; kontroller ve seek/time bilgisi geri gelmelidir. Tekrar idle bırakıldığında yeniden gizlenmelidir.
4. `Exit fullscreen` veya F11 ile normal pencereye dön. Movies kataloğu, player, seekbar ve zamanlar bozulmadan görünmeli; oynatma kendi kendine başka içeriğe geçmemelidir.

Exact yanıt: `M18-03 PASS`

## M19-manuel-kabul

### M19-01 — Series lazy detail, Season ve Episode listesi

1. `Series` içinde `SYNTH-A` kaynağını seç ve daha önce bu process'te açılmamış sentetik Series kaydına tıkla.
2. Seçilen Series için bounded loading spinner/state göründükten sonra top-level liste yerine Season listesi gelmelidir; tüm Series kayıtları için toplu detail isteği yapılmış gibi UI donmamalıdır.
3. Bir Season seç; Episode listesi doğru sıra ve görünür Episode numaralarıyla gelmelidir. Aynı Series'i tekrar açmak duplicate Season/Episode üretmemelidir.

Exact yanıt: `M19-01 PASS`

### M19-02 — Episode play ve seek

1. Sonlu/seek-capable sentetik Episode'u seç ve oynatmanın başladığını doğrula.
2. Start/current/end değerlerini M18-02 ile aynı biçimde kontrol et.
3. Bir kez ileri, bir kez geri seek yap; current zaman ve görüntü her iki hedefe yakınsamalı, Movie veya önceki Episode session'ından stale position sıçraması olmamalıdır.
4. Pause, yeniden Play ve Stop kontrollerini kullan; uygulama `Playback paused` durumunda takılı kalmamalıdır.

Exact yanıt: `M19-02 PASS`

### M19-03 — Hiyerarşik Back

1. Episode listesindeyken `Back` seç; aynı Series'in Season listesine dönmelidir.
2. Bir kez daha `Back` seç; Series top-level listesine dönmelidir.
3. Source, category ve search bağlamı beklenmedik biçimde başka source'a geçmemeli; liste boş overlay/spinner durumunda takılmamalıdır.

Exact yanıt: `M19-03 PASS`

## Kapanış ve yeniden açma stabilitesi

### STABILITY-01 — Multi-source restart

1. `SYNTH-A` ve `SYNTH-B` kalıcı, `SYNTH-CRUD` silinmiş durumdayken bir sentetik Movie veya Episode oynat.
2. Uygulamayı normal pencere kapatma yoluyla kapat; process kendi kendine crash etmemeli ve ghost audio kalmamalıdır.
3. Uygulamayı aynı Windows kullanıcısıyla yeniden aç. Home count'ları M17-01 ile exact aynı, Sources listesi `SYNTH-A` + `SYNTH-B`, Live source geçişi M17-04 ile aynı olmalıdır.
4. Bir Live kanalı, bir Movie ve bir Episode'u sırasıyla yeniden aç; stale editor/overlay/spinner, kendiliğinden kapanma veya önceki session'dan ses/görüntü kalmamalıdır.

Exact yanıt: `STABILITY-01 PASS`

## 4K codec/çevre gözlemi — non-blocking

### OBS-4K-01 — Ortam sonucu

1. Yetkili sentetik corpus'ta 4K olarak tanımlı bir Movie/Episode varsa bir kez aç. Yoksa sonucu `NOT_AVAILABLE` kaydet; sırf bu kontrol için gerçek provider veya credential kullanma.
2. Sonucu yalnız `PLAYED`, `NOT_PLAYED` veya `NOT_AVAILABLE` olarak kaydet. Mümkünse Windows sürümü, GPU/driver ve yüklü codec bileşeni gibi hassas olmayan ortam bilgisini ekle; uygulama göstermiyorsa codec/resolution tahmin etme.
3. İçerik adındaki `4K` etiketi tek başına gerçek resolution, codec veya decode desteği kanıtı değildir. `NOT_PLAYED` veya `NOT_AVAILABLE`, M17–M19 fonksiyonel manuel akışını tek başına bloke etmez; codec, Windows bileşeni, GPU/driver, medya profili ve fixture availability ayrı incelenir. Uygulamanın crash/hang etmesi ise `STABILITY-01` için failure'dır.

Gözlem tamamlandığında exact yanıt: `OBS-4K-01 PASS`

Ek olarak tek outcome satırı yaz: `OBS-4K-OUTCOME PLAYED`, `OBS-4K-OUTCOME NOT_PLAYED` veya `OBS-4K-OUTCOME NOT_AVAILABLE`.

## Toplu yanıt şablonu

Yalnız gerçekten geçen adımları aşağıdaki exact formatta gönder:

```text
PREFLIGHT-01 PASS
M17-01 PASS
M17-02 PASS
M17-03 PASS
M17-04 PASS
M17-05 PASS
M18-01 PASS
M18-02 PASS
M18-03 PASS
M19-01 PASS
M19-02 PASS
M19-03 PASS
STABILITY-01 PASS
OBS-4K-01 PASS
OBS-4K-OUTCOME <PLAYED|NOT_PLAYED|NOT_AVAILABLE>
```

Failure örneği yalnız formatı gösterir: `M18-02 FAIL — forward seek sonrası current zaman eski konuma döndü`. URL, provider adı, username, password, token veya full locator ekleme.
