# IPTV Suite — Product Brief

**Belge durumu:** Phase 0 baseline

**Tarih:** 2026-08-09

**Ad durumu:** “IPTV Suite” yalnızca doğrulanmamış iç codename'dir.

**Post-MVP successor — 2026-08-29:** Home hub, ayrı Source Manager CRUD, Live TV/Movie/Series ayrımı, Movie/Episode seek deneyimi ve ayrı açık-onaylı HTTP Xtream compatibility kapsamı [Post-MVP Content Experience Product Brief](POST_MVP_CONTENT_EXPERIENCE_BRIEF.md) ile M17–M19 için onaylanmıştır. Bu successor, aşağıdaki Windows MVP kapsamını veya M1–M16 tarihsel completion/blocked durumlarını geriye dönük değiştirmez.

## Ürün amacı

IPTV Suite, kullanıcının kendi yetkili IPTV hesabını veya uzak M3U/M3U8 playlist'ini ekleyip Live TV içeriğini güvenli ve kararlı biçimde oynatmasına yarayan ticari kalite hedefli bir medya oynatıcıdır. Uygulama bir IPTV hizmeti değildir; içerik, kanal paketi veya hesap satmaz ve ilk sürümde medya trafiğini bize ait bir sunucudan geçirmez.

Temel ürün ilkesi:

> Bring your own authorized playlist/account.

## Hedef kullanıcı

- Kendi yasal IPTV aboneliği ya da playlist'i bulunan Windows kullanıcısı.
- Büyük kanal listelerinde hızlı arama, kategori gezintisi ve anlaşılır playback durumu bekleyen kullanıcı.
- Teknik exception veya player ayrıntısı görmek yerine, bağlantı ve yayın sorununa ilişkin uygulanabilir Türkçe/yerelleştirilmiş mesaj bekleyen kullanıcı.

## Yasal ve etik ürün sınırı

Uygulama:

- hazır kanal, playlist, credential veya sağlayıcı kataloğu sunmaz;
- korsan hizmeti keşfetmez, sıralamaz, tanıtmaz veya üçüncü taraf marka/tasarımını kopyalamaz;
- DRM, bölgesel erişim kontrolü, sertifika doğrulaması ya da sağlayıcı güvenliğini aşmaz;
- yalnızca kullanıcının yetkili olduğu kaynaklarla kullanılacağına dair açık onboarding metni gösterir;
- üçüncü taraf içeriğin lisansını üstlenmez ve Store metadata'sında içerik sağlayıcısı gibi davranmaz;
- marka, codec patentleri, açık kaynak yeniden dağıtımı ve mağaza uygunluğu için yayın öncesi uzman incelemesini ayrı bir gate olarak tutar.

## Platform sırası

1. Windows — ilk gerçek ürün ve MVP.
2. Samsung Smart TV / Tizen — ayrı uygulama ve platform player yaklaşımı.
3. macOS, Android ve iOS.
4. Ürün talebine göre Android TV, tvOS ve LG webOS.

Kod paylaşımı hedef değil, sonuçtur. Playback kalitesi, stabilite, performans, mağaza uyumu ve platforma uygun UX; ortak UI kodundan daha önceliklidir.

## Windows MVP kapsamı

### Dahil

- Xtream-compatible kaynak: playlist adı, server URL, username ve password.
- Uzak M3U/M3U8 URL kaynağı.
- Form doğrulama ve iptal edilebilir, süre sınırlı connection test.
- Bounded playlist download; incremental M3U parsing ve normalize etme.
- Kategori ve Live TV kanal eşleme; 50.000+ öğeye uygun local index ve virtualized liste.
- Kontrollü concurrency, boyut limiti ve disk bütçesi olan logo lazy loading/cache.
- Kanal listesi ve seçim akışı.
- Play, pause, stop, volume, fullscreen ve kanal değiştirme.
- Loading/buffering durumu; timeout, cancellation ve sınırlı retry/reconnect.
- Domain seviyesinde kullanıcı dostu hata mesajları.
- Merkezi URL/header/log redaction ve hassas veri içermeyen development logları.
- Windows kullanıcı kapsamına bağlı güvenli credential/stream locator saklama.
- Local persistence, schema migration, kaynak silme ve cache temizleme yaşam döngüsü.

### Hariç

- Local M3U file import (sonraki aşama).
- VOD/movies, series, seasons, episodes ve EPG/XMLTV.
- Ödeme, abonelik, reklam, analytics ve cloud backend.
- Admin paneli, sosyal özellikler, recording veya download.
- DRM aşma, certificate bypass ve sağlayıcı kısıtlarını atlatma.
- Karmaşık animasyon, tema pazarı veya görsel efekt çalışması.

## Ana kullanıcı başarı zinciri

1. Uygulama açılır.
2. Kullanıcı hesap ya da playlist kaynağı ekler.
3. Girdi doğrulanır ve bağlantı güvenli biçimde test edilir.
4. Veri indirilir, parse edilir, normalize edilir ve local kataloğa atomik olarak alınır.
5. Kategoriler ve kanallar etkileşimli süre içinde gösterilir.
6. Kullanıcı kanal seçer; yayın başlar ve temel kontroller çalışır.
7. Kanal değişimi, buffering, kesinti ve yeniden bağlanma anlaşılır durumlarla gösterilir.
8. Kullanıcı kaynağı sildiğinde credential, kanal verisi ve ilişkili cache kaldırılır.

## MVP başarı ölçütleri

- Fonksiyonel zincirin tamamı sentetik ve yetkili fixture'larla x64 ve hedeflenen ARM64 cihazlarda geçer.
- Tier A playback corpus'u — HLS/MPEG-TS + H.264 + AAC — seçilen referans cihazlarda yüzde 100 açılır; diğer codec/container sonuçları capability olarak raporlanır, garanti edilmez.
- Download süresi hariç 50.000 kayıt parse + normalize + persist bütçesi referans cihazda p95 ≤ 5 saniye; cancellation gözlenme p95 ≤ 250 ms'dir.
- Cached katalogdan ilk görünür kanal p95 ≤ 500 ms; filtre/sorgu p95 ≤ 100 ms ve scroll sırasında kalıcı UI donması yoktur.
- Kontrollü LAN fixture'ında playback başlangıcı p95 ≤ 3 saniye; Release Candidate 24 saatlik soak testte crash olmaz ve ısınma döneminden sonra açıklanamayan sürekli bellek büyümesi görülmez.
- Secret/redaction test corpus'unda credential veya tam hassas URL çıktısı sıfırdır; TLS doğrulamasını kapatan kod yolu yoktur.
- Paket, Windows App Certification Kit ve dependency/license/SBOM incelemesinden geçer; Microsoft Store politika ve privacy alanları yayın tarihinde tekrar doğrulanır.
- Kullanıcı kaynak, credential, katalog ve cache'i uygulama içinden silebilir.

Performans ölçüm koşulları ve toleranslar [Quality and Performance Strategy](../quality/QUALITY_AND_PERFORMANCE_STRATEGY.md) belgesinde; hukuki ve teknik riskler [Risk Register](../risks/RISK_REGISTER.md) belgesinde tanımlıdır.
