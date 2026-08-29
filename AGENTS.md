# Repository çalışma kuralları

Bu dosya repository kökünün tamamı için geçerlidir. Daha derindeki bir `AGENTS.md`, yalnızca kendi alt ağacında daha özel kurallar koyabilir.

## Başlamadan önce

- En yakın `AGENTS.md` dosyalarını, `git status --short --branch` çıktısını ve ilgili dosyaları incele; kullanıcıya ait ilgisiz değişiklikleri koru.
- Build, test ve lint komutlarını proje dosyalarından doğrula. Windows için gerçek komutlar aşağıdaki "Doğrulama komutları" bölümündedir; daha yakın `apps/windows/AGENTS.md` ek sınırları koyar.
- Phase 0 belgelerindeki ADR, risk ve kaynak kayıtlarını etkileyen bir değişiklikte ilgili belgeyi de güncelle.

## Ürün ve kapsam

- **IPTV Suite** yalnızca doğrulanmamış bir codename'dir. Marka doğrulamasından önce kalıcı package/store identifier, logo veya hukuki marka iddiası oluşturma.
- Ürün “bring your own authorized playlist/account” oynatıcısıdır. Hazır kanal/hizmet sağlama, korsan servis tanıtma, DRM veya erişim kontrolü aşma ve kullanıcı trafiğini izinsiz bir backend üzerinden geçirme kapsam dışıdır.
- İstenen milestone dışındaki feature, platform, dependency, scaffold veya refactor işini ekleme. M1–M16 tarihsel Windows MVP kapsamını geriye dönük genişletme: post-MVP Home/Source Manager foundation yalnız M17'de, Movie/VOD seek yalnız M18'de, Series/Season/Episode yalnız M19'da production kapsamına alınabilir. EPG/XMLTV, catch-up/timeshift, recording, download ve continue-watching ayrıca onaylanmış milestone olmadan specification düzeyini aşamaz.
- M17–M19 implementation veya local test sonucu M15/M16 completion sayılmaz. ADR-008/ADR-009 sonrası M15/M16 successor package/SBOM/CVE ve acceptance ledger'ları yeni exact source closure'larıyla yenilenmeden `current` veya `completed` yazma.

## Dil ve adlandırma

- Kullanıcı açıklamaları ve mimari belgeler Türkçe; kod sembolleri, dosya adları, komutlar, veri alanları ve test adları İngilizce olmalıdır.
- Yeni önemli kavramı kısa biçimde “nedir, burada neden var, hangi problemi çözer?” çerçevesinde açıkla.
- Production koduna yalnızca o milestone'da kullanılan modelleri ekle; gelecek kavramları specification düzeyinde tut.

## Güvenlik ve araştırma

- Gerçek credential, token, kullanıcı playlist'i veya yetkisiz servis URL'si kullanma ya da commit etme. Test verileri tamamen sentetik olmalı; seed/generator/provenance ve lisans durumu manifestte açıkça kaydedilmelidir. `UNVERIFIED` fixture lisansı yalnız internal test içindir ve public paylaşım hakkı sayılmaz.
- Password, token, `Authorization`/`Cookie` başlıkları ve credential içerebilen tam URL'ler log, telemetry, snapshot veya hata mesajına giremez. TLS doğrulamasını kapatma ve “accept all certificates” uygulama.
- Zamanla değişebilen teknik/policy kararlarında önce resmi platform belgesi, standart, resmi repository/release note ve lisansı kullan; erişim tarihini ISO biçiminde kaydet.
- Kanıtı `VERIFIED`, mühendislik yorumunu `INFERENCE`, doğrulanmamış maddeyi `UNVERIFIED` olarak ayır. Lisans/patent değerlendirmesi hukuki görüş değildir; gerekli yerde uzman incelemesi iste.

## Değişiklik ve doğrulama

- Küçük, geri alınabilir değişiklikler yap; ilgisiz dosyaları yeniden formatlama ve yeni dependency eklemeden önce ilgili ADR/milestone kapsamını doğrula.
- Her değişiklik için en dar ilgili testleri, ardından belgelenmiş build/lint komutlarını çalıştır.
- Docs-only işte mevcut Markdown linter varsa çalıştır; sırf doğrulama için yeni araç kurma. Çalıştırılmayan testleri sonuçta açıkça belirt.

## Doğrulama komutları

Repository kökünden:

```powershell
dotnet restore .\apps\windows\IptvSuite.Windows.sln --locked-mode -p:Platform=x64
dotnet build .\apps\windows\IptvSuite.Windows.sln -c Debug -p:Platform=x64 --no-restore
dotnet build .\apps\windows\IptvSuite.Windows.sln -c Release -p:Platform=x64 --no-restore
dotnet test .\apps\windows\tests\IptvSuite.ArchitectureTests\IptvSuite.ArchitectureTests.csproj -c Release --no-build --no-restore
```

`global.json` exact SDK `10.0.302`, `rollForward: disable` ve `allowPrerelease: false` ister. Yukarıdaki M1 akışını değiştirmeden, M2 quality gate'i locked restore, Debug/Release x64 build, unit/integration/architecture suite'lerinin iki koşusu, fixture determinism, sentinel fail/recovery ve artifact canary kontrollerini yürütür:

```powershell
.\eng\Invoke-WindowsQualityGate.ps1
```

Warnings-as-errors, SDK analyzers ve code-style denetimi build sırasında lint gate'ini oluşturur. Testlerde gerçek provider/internet/account/medya kullanılmaz; yalnız loopback ve sentetik fixture kullanılır. MSIX smoke yalnız package/manifest etkileyen değişiklikte, `apps/windows/README.md` güvenlik akışıyla yönetici PowerShell'de çalıştırılır.
