# M2 sentetik test verisi

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
