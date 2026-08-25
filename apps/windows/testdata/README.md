# Sentetik test verisi

`m2/fixture-spec.json`, yalnız test-kit altyapısını doğrulayan küçük ve tamamen sentetik veri kümesinin sabit girdisidir. Gerçek provider cevabı, kullanıcı hesabı, credential, playlist veya medya içermez.

Generator aynı `seed`, sürüm ve algorithm version ile byte-identical `records.json` ve provenance manifesti üretir. Çıktılar build artifact'ıdır; source olarak commit edilmez. Lisans durumu private/internal kullanım için yerel `LicenseRef` ile açıkça `UNVERIFIED` tutulur ve public paylaşım öncesinde yeniden karara bağlanır.

Release build sonrasında repository kökünden üretim örneği:

```powershell
$fixtureRoot = Join-Path ".artifacts\fixtures" ([Guid]::NewGuid().ToString("N"))
$fixtureOutput = Join-Path $fixtureRoot "run-1"
$fixtureLicenseRoot = Join-Path $fixtureRoot "LICENSES"
New-Item -ItemType Directory -Path $fixtureLicenseRoot -Force | Out-Null
Copy-Item .\apps\windows\testdata\LICENSES\LicenseRef-IPTVSuite-Synthetic-Test-Only.txt $fixtureLicenseRoot
dotnet run --project .\apps\windows\tests\IptvSuite.Testing\IptvSuite.Testing.csproj `
    -c Release --no-build -- generate-fixtures `
    .\apps\windows\testdata\m2\fixture-spec.json $fixtureOutput
```

Bu dizilimde manifestin `../LICENSES/...` referansı kopyalanan sidecar'a çözülür. Manifestte generator `1.0.0`, algorithm version `1`, seed `20260809`, record file length/SHA-256 ve bütün provenance flag'leri bulunur. Bu M2 smoke corpus'u provider/parser/player uyumluluğu kanıtlamaz.

## M4 protected-store performans girdisi

`m4/secret-store-spike-spec.json`, yalnız opt-in protected-store performans spike'ının sabit ve sentetik girdisini tanımlar. Gerçek provider, kullanıcı account'u, credential, playlist veya medya içermez. Spike'ın ürettiği locator-benzeri byte dizileri yalnız `.invalid` test alanını ve deterministic seed'i kullanır; raw değerler, opaque referanslar ve absolute path'ler kanıt dosyasına yazılmaz.

`Smoke` modu en fazla 1.000 kayıtla tek tur geliştirici doğrulamasıdır ve mimari karar kanıtı değildir. `Decision` modu 5k/10k/20k/50k matrisini sabit 20 turla ölçer; opt-in çalışır, normal quality/CI akışına bağlı değildir ve sonuç tek başına 5 saniyelik bütün import bütçesini kanıtlamaz. Üretilen evidence `.artifacts/m4-secret-store-spike` altında kalır ve Git'e girmez.

## M14 katalog benchmark corpus'u

`m14/catalog-corpus-spec.json`, 100/5k/10k/20k/50k başarılı kataloglarını ve 100k fail-closed stress girdisini exact kapalı matris olarak tanımlar. Generator, corpus'ları satır satır `.artifacts` altında üretir; aynı seed, generator ve algorithm version byte-identical M3U dosyaları ile yalnız aggregate sayaç/hash taşıyan manifest verir. Manifestteki lisans dosyası repository köküne göre tanımlıdır ve test bu hedefin varlığını doğrular. Çıktılar gerçek provider, kullanıcı verisi, credential veya medya içermez ve source olarak commit edilmez.

Her ölçekte duplicate provider identifier, missing group, malformed metadata, Unicode normalization, bounded uzun ad, yalnız sentetik marker taşıyan query locator, geçersiz logo ve fallback stable-key collision varyantları sabit düşük ordinal'larda bulunur. Raw locator veya sentetik marker manifest, stdout ya da exception yüzeyine yazılmaz. 100k corpus release gate değildir; production parser'ın 50.001'inci geçerli entry'de typed fail-closed sınırını doğrular.
