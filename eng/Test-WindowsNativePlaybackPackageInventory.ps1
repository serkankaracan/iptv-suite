#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [string]$RuntimePackagePath,

    [Parameter(Mandatory)]
    [string]$LockFilePath,

    [Parameter(Mandatory)]
    [string]$AssetsFilePath,

    [Parameter(Mandatory)]
    [string]$DepsFilePath,

    [Parameter(Mandatory)]
    [string]$ManifestPath,

    [Parameter(Mandatory)]
    [string]$SpecificationPath,

    [Parameter(Mandatory)]
    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:maximumArchiveBytes = 1GB
$script:maximumJsonBytes = 32MB
$script:maximumManifestBytes = 2MB
$script:maximumEntryBytes = 512MB
$script:maximumArchiveEntries = 10000
$script:maximumExpandedBytes = 2GB
$script:maximumNestedArchiveBytes = 256MB
$script:maximumNestedDepth = 2
$script:maximumNestedArchiveCount = 32
$script:maximumRecursiveRuntimeEntries = 20000
$script:maximumRecursiveRuntimeBytes = 4GB
$script:maximumPackageCount = 256
$script:maximumPayloadCount = 2048
$script:utf8NoBom = New-Object System.Text.UTF8Encoding($false, $true)
$script:foundationManifestNamespace =
    "http://schemas.microsoft.com/appx/manifest/foundation/windows10"

Add-Type -AssemblyName System.IO.Compression

function Assert-Condition {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Code
    )

    if (-not $Condition) {
        throw "Native package inventory validation failed: $Code."
    }
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $actual = @($Value.PSObject.Properties | ForEach-Object { $_.Name })
    Assert-Condition ($actual.Count -eq $Expected.Count) $Code
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        Assert-Condition `
            ([string]::Equals(
                    [string]$actual[$index],
                    [string]$Expected[$index],
                    [System.StringComparison]::Ordinal)) `
            $Code
    }
}

function Assert-ExactStringSet {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Actual,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $actualStrings = @($Actual | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
    $expectedStrings = @($Expected | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
    Assert-Condition ($actualStrings.Count -eq $expectedStrings.Count) $Code
    for ($index = 0; $index -lt $expectedStrings.Count; $index++) {
        Assert-Condition `
            ([string]::Equals(
                    $actualStrings[$index],
                    $expectedStrings[$index],
                    [System.StringComparison]::Ordinal)) `
            $Code
    }
}

function Test-ExactStringSet {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Actual,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Expected
    )

    $actualStrings = @($Actual | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
    $expectedStrings = @($Expected | ForEach-Object { [string]$_ } | Sort-Object -CaseSensitive)
    if ($actualStrings.Count -ne $expectedStrings.Count) {
        return $false
    }
    for ($index = 0; $index -lt $expectedStrings.Count; $index++) {
        if (-not [string]::Equals(
                $actualStrings[$index],
                $expectedStrings[$index],
                [System.StringComparison]::Ordinal)) {
            return $false
        }
    }
    return $true
}

function Assert-ExactJsonValue {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Actual,

        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Expected,

        [Parameter(Mandatory)]
        [string]$Code
    )

    if ($null -eq $Actual -or $null -eq $Expected) {
        Assert-Condition ($null -eq $Actual -and $null -eq $Expected) $Code
        return
    }

    $actualIsObject = $Actual -is [System.Management.Automation.PSCustomObject]
    $expectedIsObject = $Expected -is [System.Management.Automation.PSCustomObject]
    Assert-Condition ($actualIsObject -eq $expectedIsObject) $Code
    if ($actualIsObject) {
        $actualNames = @($Actual.PSObject.Properties | ForEach-Object { $_.Name })
        $expectedNames = @($Expected.PSObject.Properties | ForEach-Object { $_.Name })
        Assert-NoCaseCollision -Values $actualNames -Code $Code
        Assert-NoCaseCollision -Values $expectedNames -Code $Code
        Assert-ExactStringSet -Actual $actualNames -Expected $expectedNames -Code $Code
        foreach ($name in $expectedNames) {
            Assert-ExactJsonValue `
                -Actual (Get-ExactProperty -Value $Actual -Name $name -Code $Code) `
                -Expected (Get-ExactProperty -Value $Expected -Name $name -Code $Code) `
                -Code $Code
        }
        return
    }

    $actualIsArray = $Actual -is [System.Array]
    $expectedIsArray = $Expected -is [System.Array]
    Assert-Condition ($actualIsArray -eq $expectedIsArray) $Code
    if ($actualIsArray) {
        $actualItems = @($Actual)
        $expectedItems = @($Expected)
        Assert-Condition ($actualItems.Count -eq $expectedItems.Count) $Code
        for ($index = 0; $index -lt $expectedItems.Count; $index++) {
            Assert-ExactJsonValue `
                -Actual $actualItems[$index] `
                -Expected $expectedItems[$index] `
                -Code $Code
        }
        return
    }

    Assert-Condition ($Actual.GetType() -eq $Expected.GetType()) $Code
    Assert-Condition `
        ([string]::Equals(
                [string]$Actual,
                [string]$Expected,
                [System.StringComparison]::Ordinal)) `
        $Code
}

function Assert-NoCaseCollision {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Values,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $ordinal = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([System.StringComparer]::Ordinal)
    $ignoreCase = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($value in $Values) {
        Assert-Condition ($ordinal.Add($value)) $Code
        Assert-Condition ($ignoreCase.Add($value)) $Code
    }
}

function Resolve-RegularFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [long]$MaximumBytes,

        [Parameter(Mandatory)]
        [string]$Code
    )

    try {
        $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
        $item = Get-Item -LiteralPath $resolved.Path -Force -ErrorAction Stop
    }
    catch {
        throw "Native package inventory validation failed: $Code."
    }
    Assert-Condition (-not $item.PSIsContainer) $Code
    Assert-Condition `
        (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        $Code
    Assert-Condition ($item.Length -gt 0 -and $item.Length -le $MaximumBytes) $Code
    return $item
}

function Assert-RegularDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Code
    )

    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    }
    catch {
        throw "Native package inventory validation failed: $Code."
    }
    Assert-Condition $item.PSIsContainer $Code
    Assert-Condition `
        (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        $Code
    return $item
}

function Get-Sha256FromStream {
    param(
        [Parameter(Mandatory)]
        [System.IO.Stream]$Stream
    )

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($Stream)
        return ([System.BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-FileSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = New-Object System.IO.FileStream(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        return Get-Sha256FromStream -Stream $stream
    }
    finally {
        $stream.Dispose()
    }
}

function Get-BytesSha256 {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes
    )

    $stream = New-Object System.IO.MemoryStream(,$Bytes)
    try {
        return Get-Sha256FromStream -Stream $stream
    }
    finally {
        $stream.Dispose()
    }
}

function Get-StringSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    return Get-BytesSha256 -Bytes $script:utf8NoBom.GetBytes($Value)
}

function Read-BoundedText {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$File,

        [Parameter(Mandatory)]
        [string]$Code
    )

    try {
        return [System.IO.File]::ReadAllText($File.FullName, $script:utf8NoBom)
    }
    catch {
        throw "Native package inventory validation failed: $Code."
    }
}

function ConvertFrom-ExactJsonFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $file = Resolve-RegularFile -Path $Path -MaximumBytes $script:maximumJsonBytes -Code $Code
    $text = Read-BoundedText -File $file -Code $Code
    try {
        return $text | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Native package inventory validation failed: $Code."
    }
}

function ConvertFrom-SafeXmlText {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersFromEntities = 0
    $settings.MaxCharactersInDocument = $script:maximumManifestBytes
    $reader = [System.Xml.XmlReader]::Create((New-Object System.IO.StringReader($Text)), $settings)
    try {
        $document = New-Object System.Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    catch {
        throw "Native package inventory validation failed: $Code."
    }
    finally {
        $reader.Dispose()
    }
}

function ConvertFrom-StrictUtf8XmlBytes {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [string]$Code
    )

    Assert-Condition `
        ($Bytes.Length -gt 0 -and $Bytes.Length -le $script:maximumManifestBytes) `
        $Code
    try {
        $text = $script:utf8NoBom.GetString($Bytes)
    }
    catch {
        throw "Native package inventory validation failed: $Code."
    }
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xfeff) {
        $text = $text.Substring(1)
    }
    Assert-Condition ($text.IndexOf([char]0xfeff) -lt 0) $Code
    return ConvertFrom-SafeXmlText -Text $text -Code $Code
}

function ConvertFrom-SafeXmlFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $file = Resolve-RegularFile -Path $Path -MaximumBytes $script:maximumManifestBytes -Code $Code
    return ConvertFrom-SafeXmlText -Text (Read-BoundedText -File $file -Code $Code) -Code $Code
}

function Get-CanonicalArchivePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Code
    )

    Assert-Condition (-not [string]::IsNullOrWhiteSpace($Path)) $Code
    Assert-Condition (-not $Path.Contains("\")) $Code
    Assert-Condition (-not $Path.StartsWith("/", [System.StringComparison]::Ordinal)) $Code
    Assert-Condition (-not $Path.Contains(":")) $Code
    Assert-Condition (-not $Path.Contains([char]0)) $Code
    $segments = @($Path.Split('/'))
    Assert-Condition ($segments.Count -gt 0) $Code
    foreach ($segment in $segments) {
        Assert-Condition `
            (-not [string]::IsNullOrEmpty($segment) -and
                -not [string]::Equals($segment, ".", [System.StringComparison]::Ordinal) -and
                -not [string]::Equals($segment, "..", [System.StringComparison]::Ordinal)) `
            $Code
    }

    return [string]::Join("/", $segments)
}

function Test-IsExecutablePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return $Path.EndsWith(".dll", [System.StringComparison]::OrdinalIgnoreCase) -or
        $Path.EndsWith(".exe", [System.StringComparison]::OrdinalIgnoreCase) -or
        $Path.EndsWith(".winmd", [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-ZipEntryHasPortableExecutableHeader {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchiveEntry]$Entry
    )

    if ($Entry.Length -lt 2) {
        return $false
    }

    $stream = $Entry.Open()
    try {
        $first = $stream.ReadByte()
        $second = $stream.ReadByte()
        return $first -eq 0x4d -and $second -eq 0x5a
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-NoForbiddenMarker {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $lower = $Path.ToLowerInvariant()
    $forbidden = $lower.Contains("libvlc") -or
        $lower.Contains("videolan") -or
        $lower -match '(^|/)(plugins?|a?gpl|lgpl)(/|$)' -or
        $lower -match '(^|[._/-])(a?gpl|lgpl)([0-9._/-]|$)'
    Assert-Condition (-not $forbidden) "ForbiddenPayloadMarker"
}

function Assert-NoForbiddenLicenseText {
    param(
        [Parameter(Mandatory)]
        [string]$Text
    )

    $forbidden = $Text.IndexOf(
        "GNU GENERAL PUBLIC LICENSE",
        [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Text.IndexOf(
            "GNU AFFERO GENERAL PUBLIC LICENSE",
            [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Text.IndexOf(
            "GNU LESSER GENERAL PUBLIC LICENSE",
            [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Text.IndexOf("libvlc", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Text.IndexOf("VideoLAN", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    Assert-Condition (-not $forbidden) "ForbiddenLicenseMarker"
}

function Read-ZipEntryBytes {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchiveEntry]$Entry,

        [Parameter(Mandatory)]
        [long]$MaximumBytes,

        [Parameter(Mandatory)]
        [string]$Code
    )

    Assert-Condition ($Entry.Length -ge 0 -and $Entry.Length -le $MaximumBytes) $Code
    $input = $Entry.Open()
    $output = New-Object System.IO.MemoryStream
    try {
        $input.CopyTo($output)
        Assert-Condition ($output.Length -eq $Entry.Length) $Code
        return $output.ToArray()
    }
    finally {
        $input.Dispose()
        $output.Dispose()
    }
}

function Get-ZipInventory {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $entries = @($Archive.Entries)
    Assert-Condition ($entries.Count -gt 0 -and $entries.Count -le $script:maximumArchiveEntries) $Code
    $expandedBytes = [long]0
    $paths = New-Object 'System.Collections.Generic.List[string]'
    $records = @{}
    foreach ($entry in $entries) {
        $path = Get-CanonicalArchivePath -Path $entry.FullName -Code $Code
        Assert-Condition ($entry.Length -ge 0 -and $entry.Length -le $script:maximumEntryBytes) $Code
        Assert-Condition ($entry.CompressedLength -ge 0) $Code
        $expandedBytes += $entry.Length
        Assert-Condition ($expandedBytes -le $script:maximumExpandedBytes) $Code
        Assert-NoForbiddenMarker -Path $path
        $paths.Add($path)
        $records[$path] = $entry
    }

    Assert-NoCaseCollision -Values $paths.ToArray() -Code $Code
    return [pscustomobject]@{
        Paths = @($paths.ToArray())
        Records = $records
        ExpandedBytes = $expandedBytes
    }
}

function Get-ZipEntrySha256 {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchiveEntry]$Entry
    )

    $stream = $Entry.Open()
    try {
        return Get-Sha256FromStream -Stream $stream
    }
    finally {
        $stream.Dispose()
    }
}

function Get-XmlAttributeValue {
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlElement]$Element,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Code
    )

    Assert-Condition ($Element.HasAttribute($Name)) $Code
    $value = $Element.GetAttribute($Name)
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($value)) $Code
    return $value
}

function Get-ManifestContract {
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlDocument]$Document,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $package = $Document.DocumentElement
    Assert-Condition `
        ($null -ne $package -and
            [string]::Equals($package.LocalName, "Package", [System.StringComparison]::Ordinal)) `
        $Code
    $identityNodes = @($package.ChildNodes | Where-Object {
        $_ -is [System.Xml.XmlElement] -and $_.LocalName -eq "Identity"
    })
    Assert-Condition ($identityNodes.Count -eq 1) $Code
    $identity = $identityNodes[0]

    $dependencyContainers = @($package.ChildNodes | Where-Object {
        $_ -is [System.Xml.XmlElement] -and $_.LocalName -eq "Dependencies"
    })
    Assert-Condition ($dependencyContainers.Count -eq 1) $Code
    $dependencyNodes = @($dependencyContainers[0].ChildNodes | Where-Object {
        $_ -is [System.Xml.XmlElement]
    })
    Assert-Condition ($dependencyNodes.Count -ge 1 -and $dependencyNodes.Count -le 2) $Code
    $targetDeviceFamilies = New-Object 'System.Collections.Generic.List[object]'
    $packageDependencies = New-Object 'System.Collections.Generic.List[object]'
    foreach ($dependencyNode in $dependencyNodes) {
        if ([string]::Equals(
                $dependencyNode.LocalName,
                "TargetDeviceFamily",
                [System.StringComparison]::Ordinal)) {
            $targetDeviceFamilies.Add([ordered]@{
                    Name = Get-XmlAttributeValue -Element $dependencyNode -Name "Name" -Code $Code
                    MinVersion = Get-XmlAttributeValue -Element $dependencyNode -Name "MinVersion" -Code $Code
                    MaxVersionTested = Get-XmlAttributeValue -Element $dependencyNode -Name "MaxVersionTested" -Code $Code
                })
        }
        elseif ([string]::Equals(
                $dependencyNode.LocalName,
                "PackageDependency",
                [System.StringComparison]::Ordinal)) {
            $packageDependencies.Add([ordered]@{
                    Name = Get-XmlAttributeValue -Element $dependencyNode -Name "Name" -Code $Code
                    Publisher = Get-XmlAttributeValue -Element $dependencyNode -Name "Publisher" -Code $Code
                    MinVersion = Get-XmlAttributeValue -Element $dependencyNode -Name "MinVersion" -Code $Code
                })
        }
        else {
            throw "Native package inventory validation failed: $Code."
        }
    }

    $capabilityContainers = @($package.ChildNodes | Where-Object {
        $_ -is [System.Xml.XmlElement] -and $_.LocalName -eq "Capabilities"
    })
    Assert-Condition ($capabilityContainers.Count -eq 1) $Code
    $capabilityNodes = @($capabilityContainers[0].ChildNodes | Where-Object {
        $_ -is [System.Xml.XmlElement]
    })
    Assert-Condition ($capabilityNodes.Count -gt 0) $Code
    $capabilities = @($capabilityNodes | ForEach-Object {
        Assert-Condition `
            ([string]::Equals(
                    $_.LocalName,
                    "Capability",
                    [System.StringComparison]::Ordinal)) `
            $Code
        $name = Get-XmlAttributeValue -Element $_ -Name "Name" -Code $Code
        if ([string]::Equals($name, "runFullTrust", [System.StringComparison]::Ordinal)) {
            Assert-Condition `
                ([string]::Equals(
                        $_.NamespaceURI,
                        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities",
                        [System.StringComparison]::Ordinal)) `
                $Code
        }
        $name
    })
    $applicationNodes = @($package.SelectNodes("//*[local-name()='Application']"))
    Assert-Condition ($applicationNodes.Count -eq 1) $Code
    $application = $applicationNodes[0]
    $allowedApplicationAttributes = @(
        "Id", "Executable", "EntryPoint", "RuntimeBehavior", "TrustLevel")
    $actualApplicationAttributes = @($application.Attributes)
    foreach ($attribute in $actualApplicationAttributes) {
        Assert-Condition ([string]::IsNullOrEmpty($attribute.NamespaceURI)) $Code
        Assert-Condition `
            (@($allowedApplicationAttributes | Where-Object {
                    [string]::Equals(
                        $_,
                        $attribute.LocalName,
                        [System.StringComparison]::Ordinal)
                }).Count -eq 1) `
            $Code
    }
    $applicationContract = [ordered]@{
        Id = Get-XmlAttributeValue -Element $application -Name "Id" -Code $Code
        Executable = Get-XmlAttributeValue -Element $application -Name "Executable" -Code $Code
        EntryPoint = Get-XmlAttributeValue -Element $application -Name "EntryPoint" -Code $Code
        RuntimeBehavior = if ($application.HasAttribute("RuntimeBehavior")) {
            Get-XmlAttributeValue -Element $application -Name "RuntimeBehavior" -Code $Code
        }
        else {
            ""
        }
        TrustLevel = if ($application.HasAttribute("TrustLevel")) {
            Get-XmlAttributeValue -Element $application -Name "TrustLevel" -Code $Code
        }
        else {
            ""
        }
    }
    $extensionContainers = @($package.SelectNodes("//*[local-name()='Extensions']"))
    Assert-Condition ($extensionContainers.Count -le 1) $Code
    $allExtensionElements = @($package.SelectNodes("//*[local-name()='Extension']"))
    $extensions = New-Object 'System.Collections.Generic.List[object]'
    if ($extensionContainers.Count -eq 1) {
        $extensionContainer = $extensionContainers[0]
        Assert-Condition ([object]::ReferenceEquals($extensionContainer.ParentNode, $package)) $Code
        Assert-Condition `
            ([string]::Equals(
                    $extensionContainer.NamespaceURI,
                    $script:foundationManifestNamespace,
                    [System.StringComparison]::Ordinal)) `
            $Code
        Assert-Condition ($extensionContainer.Attributes.Count -eq 0) $Code
        $extensionElements = @($extensionContainer.ChildNodes | Where-Object {
            $_ -is [System.Xml.XmlElement]
        })
        Assert-Condition ($extensionElements.Count -gt 0) $Code
        foreach ($extension in $extensionElements) {
            Assert-Condition `
                ([string]::Equals($extension.LocalName, "Extension", [System.StringComparison]::Ordinal) -and
                    [string]::Equals(
                        $extension.NamespaceURI,
                        $script:foundationManifestNamespace,
                        [System.StringComparison]::Ordinal)) `
                $Code
            Assert-Condition ($extension.Attributes.Count -eq 1) $Code
            $categoryAttribute = $extension.Attributes[0]
            Assert-Condition `
                ([string]::IsNullOrEmpty($categoryAttribute.NamespaceURI) -and
                    [string]::Equals(
                        $categoryAttribute.LocalName,
                        "Category",
                        [System.StringComparison]::Ordinal) -and
                    -not [string]::IsNullOrWhiteSpace($categoryAttribute.Value)) `
                $Code
            $extensionChildren = @($extension.ChildNodes | Where-Object {
                $_ -is [System.Xml.XmlElement]
            })
            Assert-Condition ($extensionChildren.Count -eq 1) $Code
            $server = $extensionChildren[0]
            Assert-Condition `
                ([string]::Equals($server.LocalName, "InProcessServer", [System.StringComparison]::Ordinal) -and
                    [string]::Equals(
                        $server.NamespaceURI,
                        $script:foundationManifestNamespace,
                        [System.StringComparison]::Ordinal) -and
                    $server.Attributes.Count -eq 0) `
                $Code
            $serverChildren = @($server.ChildNodes | Where-Object {
                $_ -is [System.Xml.XmlElement]
            })
            Assert-Condition ($serverChildren.Count -ge 2) $Code
            $pathElement = $serverChildren[0]
            Assert-Condition `
                ([string]::Equals($pathElement.LocalName, "Path", [System.StringComparison]::Ordinal) -and
                    [string]::Equals(
                        $pathElement.NamespaceURI,
                        $script:foundationManifestNamespace,
                        [System.StringComparison]::Ordinal) -and
                    $pathElement.Attributes.Count -eq 0 -and
                    @($pathElement.ChildNodes | Where-Object {
                            $_ -is [System.Xml.XmlElement]
                        }).Count -eq 0) `
                $Code
            $serverPath = Get-CanonicalArchivePath -Path $pathElement.InnerText -Code $Code
            $classes = New-Object 'System.Collections.Generic.List[object]'
            for ($classIndex = 1; $classIndex -lt $serverChildren.Count; $classIndex++) {
                $class = $serverChildren[$classIndex]
                Assert-Condition `
                    ([string]::Equals(
                            $class.LocalName,
                            "ActivatableClass",
                            [System.StringComparison]::Ordinal) -and
                        [string]::Equals(
                            $class.NamespaceURI,
                            $script:foundationManifestNamespace,
                            [System.StringComparison]::Ordinal) -and
                        $class.Attributes.Count -eq 2 -and
                        @($class.ChildNodes | Where-Object {
                                $_ -is [System.Xml.XmlElement]
                            }).Count -eq 0) `
                    $Code
                foreach ($attribute in @($class.Attributes)) {
                    Assert-Condition ([string]::IsNullOrEmpty($attribute.NamespaceURI)) $Code
                }
                $classAttributeNames = @($class.Attributes | ForEach-Object { $_.LocalName })
                Assert-ExactStringSet `
                    -Actual $classAttributeNames `
                    -Expected @("ActivatableClassId", "ThreadingModel") `
                    -Code $Code
                $classes.Add([ordered]@{
                        Id = Get-XmlAttributeValue `
                            -Element $class `
                            -Name "ActivatableClassId" `
                            -Code $Code
                        ThreadingModel = Get-XmlAttributeValue `
                            -Element $class `
                            -Name "ThreadingModel" `
                            -Code $Code
                    })
            }
            Assert-NoCaseCollision `
                -Values @($classes.ToArray() | ForEach-Object { [string]$_.Id }) `
                -Code $Code
            $extensions.Add([ordered]@{
                    NamespaceUri = $extension.NamespaceURI
                    Category = $categoryAttribute.Value
                    InProcessServerPath = $serverPath
                    ActivatableClasses = @($classes.ToArray())
                })
        }
    }
    Assert-NoCaseCollision `
        -Values @($extensions.ToArray() | ForEach-Object {
                "$($_.NamespaceUri)`n$($_.Category)`n$($_.InProcessServerPath)"
            }) `
        -Code $Code
    Assert-Condition ($allExtensionElements.Count -eq $extensions.Count) $Code

    return [pscustomobject]@{
        Identity = [ordered]@{
            Name = Get-XmlAttributeValue -Element $identity -Name "Name" -Code $Code
            Publisher = Get-XmlAttributeValue -Element $identity -Name "Publisher" -Code $Code
            Version = Get-XmlAttributeValue -Element $identity -Name "Version" -Code $Code
            Architecture = Get-XmlAttributeValue -Element $identity -Name "ProcessorArchitecture" -Code $Code
        }
        TargetDeviceFamilies = @($targetDeviceFamilies.ToArray())
        PackageDependencies = @($packageDependencies.ToArray())
        Capabilities = $capabilities
        Application = $applicationContract
        Extensions = @($extensions.ToArray())
    }
}

function Assert-ManifestMatchesSpecification {
    param(
        [Parameter(Mandatory)]
        [object]$Contract,

        [Parameter(Mandatory)]
        [object]$Specification,

        [Parameter(Mandatory)]
        [bool]$IncludeRuntimeDependency,

        [Parameter(Mandatory)]
        [string]$Code
    )

    Assert-ExactProperties `
        -Value $Specification.PackageIdentity `
        -Expected @("Name", "Publisher", "Version", "Architecture") `
        -Code $Code
    foreach ($property in @("Name", "Publisher", "Version", "Architecture")) {
        Assert-Condition `
            ([string]::Equals(
                    [string]$Contract.Identity[$property],
                    [string]$Specification.PackageIdentity.$property,
                    [System.StringComparison]::Ordinal)) `
            $Code
    }
    Assert-Condition `
        ([string]::Equals(
                [string]$Contract.Identity.Architecture,
                "x64",
                [System.StringComparison]::OrdinalIgnoreCase)) `
        $Code

    Assert-ExactProperties `
        -Value $Specification.TargetDeviceFamily `
        -Expected @("Name", "MinVersion", "MaxVersionTested") `
        -Code $Code
    Assert-Condition ($Contract.TargetDeviceFamilies.Count -eq 1) $Code
    $targetDeviceFamily = $Contract.TargetDeviceFamilies[0]
    foreach ($property in @("Name", "MinVersion", "MaxVersionTested")) {
        Assert-Condition `
            ([string]::Equals(
                    [string]$targetDeviceFamily[$property],
                    [string]$Specification.TargetDeviceFamily.$property,
                    [System.StringComparison]::Ordinal)) `
            $Code
    }
    Assert-Condition `
        ([string]::Equals(
                [string]$targetDeviceFamily.Name,
                "Windows.Desktop",
                [System.StringComparison]::Ordinal)) `
        $Code
    if ($IncludeRuntimeDependency) {
        Assert-ExactProperties `
            -Value $Specification.RuntimeDependency `
            -Expected @("Name", "Publisher", "MinVersion") `
            -Code $Code
        Assert-Condition ($Contract.PackageDependencies.Count -eq 1) $Code
        $runtimeDependency = $Contract.PackageDependencies[0]
        foreach ($property in @("Name", "Publisher", "MinVersion")) {
            Assert-Condition `
                ([string]::Equals(
                        [string]$runtimeDependency[$property],
                        [string]$Specification.RuntimeDependency.$property,
                        [System.StringComparison]::Ordinal)) `
                $Code
        }
        Assert-Condition `
            ([string]::Equals(
                    [string]$runtimeDependency.Name,
                    "Microsoft.WindowsAppRuntime.2",
                    [System.StringComparison]::Ordinal)) `
            $Code
    }
    else {
        Assert-Condition ($Contract.PackageDependencies.Count -eq 0) $Code
    }

    Assert-ExactStringSet `
        -Actual @($Contract.Capabilities) `
        -Expected @($Specification.Capabilities) `
        -Code $Code
    Assert-Condition `
        (@($Contract.Capabilities).Count -eq 1 -and
            [string]::Equals(
                [string]$Contract.Capabilities[0],
                "runFullTrust",
            [System.StringComparison]::Ordinal)) `
        $Code

    Assert-ExactProperties `
        -Value $Specification.Application `
        -Expected @(
            "Id", "SourceExecutable", "SourceEntryPoint", "PackagedExecutable",
            "PackagedEntryPoint", "PackagedRuntimeBehavior", "PackagedTrustLevel") `
        -Code $Code
    Assert-Condition `
        ([string]::Equals(
                [string]$Contract.Application.Id,
                [string]$Specification.Application.Id,
                [System.StringComparison]::Ordinal)) `
        $Code
    $expectedExecutable = if ($IncludeRuntimeDependency) {
        [string]$Specification.Application.PackagedExecutable
    }
    else {
        [string]$Specification.Application.SourceExecutable
    }
    $expectedEntryPoint = if ($IncludeRuntimeDependency) {
        [string]$Specification.Application.PackagedEntryPoint
    }
    else {
        [string]$Specification.Application.SourceEntryPoint
    }
    $expectedRuntimeBehavior = if ($IncludeRuntimeDependency) {
        [string]$Specification.Application.PackagedRuntimeBehavior
    }
    else {
        ""
    }
    $expectedTrustLevel = if ($IncludeRuntimeDependency) {
        [string]$Specification.Application.PackagedTrustLevel
    }
    else {
        ""
    }
    foreach ($comparison in @(
            @("Executable", $expectedExecutable),
            @("EntryPoint", $expectedEntryPoint),
            @("RuntimeBehavior", $expectedRuntimeBehavior),
            @("TrustLevel", $expectedTrustLevel))) {
        Assert-Condition `
            ([string]::Equals(
                    [string]$Contract.Application[$comparison[0]],
                    [string]$comparison[1],
                    [System.StringComparison]::Ordinal)) `
            $Code
    }

    $expectedExtensions = @($Specification.Extensions)
    Assert-Condition ($expectedExtensions.Count -le 64) $Code
    if (-not $IncludeRuntimeDependency) {
        Assert-Condition ($Contract.Extensions.Count -eq 0) $Code
        return
    }

    Assert-Condition ($Contract.Extensions.Count -eq $expectedExtensions.Count) $Code
    $expectedExtensionKeys = New-Object 'System.Collections.Generic.List[string]'
    for ($extensionIndex = 0; $extensionIndex -lt $expectedExtensions.Count; $extensionIndex++) {
        $expectedExtension = $expectedExtensions[$extensionIndex]
        $actualExtension = $Contract.Extensions[$extensionIndex]
        Assert-ExactProperties `
            -Value $expectedExtension `
            -Expected @(
                "NamespaceUri", "Category", "InProcessServerPath", "ActivatableClasses") `
            -Code $Code
        Assert-Condition `
            ([string]::Equals(
                    [string]$expectedExtension.NamespaceUri,
                    $script:foundationManifestNamespace,
                    [System.StringComparison]::Ordinal)) `
            $Code
        $expectedPath = Get-CanonicalArchivePath `
            -Path ([string]$expectedExtension.InProcessServerPath) `
            -Code $Code
        Assert-Condition `
            $expectedPath.EndsWith(".dll", [System.StringComparison]::OrdinalIgnoreCase) `
            $Code
        foreach ($property in @("NamespaceUri", "Category")) {
            Assert-Condition `
                ([string]::Equals(
                        [string]$actualExtension[$property],
                        [string]$expectedExtension.$property,
                        [System.StringComparison]::Ordinal)) `
                $Code
        }
        Assert-Condition `
            ([string]::Equals(
                    [string]$actualExtension.InProcessServerPath,
                    $expectedPath,
                    [System.StringComparison]::Ordinal)) `
            $Code
        $expectedClasses = @($expectedExtension.ActivatableClasses)
        Assert-Condition ($expectedClasses.Count -gt 0 -and $expectedClasses.Count -le 256) $Code
        Assert-Condition ($actualExtension.ActivatableClasses.Count -eq $expectedClasses.Count) $Code
        $expectedClassIds = New-Object 'System.Collections.Generic.List[string]'
        for ($classIndex = 0; $classIndex -lt $expectedClasses.Count; $classIndex++) {
            $expectedClass = $expectedClasses[$classIndex]
            $actualClass = $actualExtension.ActivatableClasses[$classIndex]
            Assert-ExactProperties `
                -Value $expectedClass `
                -Expected @("Id", "ThreadingModel") `
                -Code $Code
            foreach ($property in @("Id", "ThreadingModel")) {
                Assert-Condition `
                    ([string]::Equals(
                            [string]$actualClass[$property],
                            [string]$expectedClass.$property,
                            [System.StringComparison]::Ordinal)) `
                    $Code
            }
            $expectedClassIds.Add([string]$expectedClass.Id)
        }
        Assert-NoCaseCollision -Values $expectedClassIds.ToArray() -Code $Code
        $expectedExtensionKeys.Add(
            "$($expectedExtension.NamespaceUri)`n$($expectedExtension.Category)`n$expectedPath")
    }
    Assert-NoCaseCollision -Values $expectedExtensionKeys.ToArray() -Code $Code
}

function Get-ExactProperty {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $matches = @($Value.PSObject.Properties | Where-Object {
        [string]::Equals($_.Name, $Name, [System.StringComparison]::Ordinal)
    })
    Assert-Condition ($matches.Count -eq 1) $Code
    return $matches[0].Value
}

function Get-OnlyChildElement {
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlNode]$Parent,

        [Parameter(Mandatory)]
        [string]$LocalName,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $matches = @($Parent.ChildNodes | Where-Object {
        $_ -is [System.Xml.XmlElement] -and $_.LocalName -eq $LocalName
    })
    Assert-Condition ($matches.Count -eq 1) $Code
    return $matches[0]
}

function Test-SafeReviewedSourceUri {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    $uri = $null
    if (-not [System.Uri]::TryCreate($Value, [System.UriKind]::Absolute, [ref]$uri)) {
        return $false
    }

    return [string]::Equals($uri.Scheme, "https", [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::IsNullOrEmpty($uri.UserInfo) -and
        [string]::IsNullOrEmpty($uri.Query) -and
        [string]::IsNullOrEmpty($uri.Fragment) -and
        -not [string]::IsNullOrWhiteSpace($uri.DnsSafeHost)
}

function Test-IsSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    return $Value -cmatch '^[0-9a-f]{64}$'
}

function Get-ResolvedPackageDirectory {
    param(
        [Parameter(Mandatory)]
        [object]$Assets,

        [Parameter(Mandatory)]
        [string]$LibraryKey,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $library = Get-ExactProperty -Value $Assets.libraries -Name $LibraryKey -Code $Code
    Assert-Condition `
        ([string]::Equals([string]$library.type, "package", [System.StringComparison]::Ordinal)) `
        $Code
    $libraryPath = Get-CanonicalArchivePath -Path ([string]$library.path) -Code $Code
    $folderProperties = @($Assets.packageFolders.PSObject.Properties)
    Assert-Condition ($folderProperties.Count -gt 0 -and $folderProperties.Count -le 16) $Code
    $candidates = New-Object 'System.Collections.Generic.List[string]'
    foreach ($folderProperty in $folderProperties) {
        $folder = Assert-RegularDirectory -Path $folderProperty.Name -Code $Code
        $root = $folder.FullName.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
            [System.IO.Path]::DirectorySeparatorChar
        $candidate = $folder.FullName
        foreach ($segment in @($libraryPath.Split('/'))) {
            Assert-Condition (-not [string]::IsNullOrWhiteSpace($segment)) $Code
            $candidate = [System.IO.Path]::GetFullPath((Join-Path $candidate $segment))
            Assert-Condition `
                ($candidate + [System.IO.Path]::DirectorySeparatorChar).StartsWith(
                    $root,
                    [System.StringComparison]::OrdinalIgnoreCase) `
                $Code
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                [void](Assert-RegularDirectory -Path $candidate -Code $Code)
            }
        }
        Assert-Condition `
            $candidate.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase) `
            $Code
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $candidateDirectory = Assert-RegularDirectory -Path $candidate -Code $Code
            $candidates.Add($candidateDirectory.FullName)
        }
    }

    Assert-Condition ($candidates.Count -eq 1) $Code
    return [pscustomobject]@{
        Directory = $candidates[0]
        Library = $library
    }
}

function Resolve-PackageFile {
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory,

        [Parameter(Mandatory)]
        [string]$RelativePath,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $canonical = Get-CanonicalArchivePath -Path $RelativePath -Code $Code
    $root = $PackageDirectory.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $candidate = [System.IO.Path]::GetFullPath(
        (Join-Path $PackageDirectory ($canonical.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
    Assert-Condition `
        $candidate.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase) `
        $Code
    $file = Resolve-RegularFile `
        -Path $candidate `
        -MaximumBytes $script:maximumEntryBytes `
        -Code $Code
    $current = $file.Directory
    $reachedRoot = $false
    while ($null -ne $current) {
        Assert-Condition `
            (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
            $Code
        if ([string]::Equals(
                $current.FullName.TrimEnd([System.IO.Path]::DirectorySeparatorChar),
                $PackageDirectory.TrimEnd([System.IO.Path]::DirectorySeparatorChar),
                [System.StringComparison]::OrdinalIgnoreCase)) {
            $reachedRoot = $true
            break
        }
        $current = $current.Parent
    }
    Assert-Condition $reachedRoot $Code
    return $file
}

function Get-NuspecMetadata {
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory,

        [Parameter(Mandatory)]
        [object]$AssetsLibrary,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $nuspecPaths = @($AssetsLibrary.files | Where-Object {
        ([string]$_).EndsWith(".nuspec", [System.StringComparison]::OrdinalIgnoreCase) -and
        -not ([string]$_).Contains("/")
    })
    Assert-Condition ($nuspecPaths.Count -eq 1) $Code
    $nuspecFile = Resolve-PackageFile `
        -PackageDirectory $PackageDirectory `
        -RelativePath ([string]$nuspecPaths[0]) `
        -Code $Code
    $document = ConvertFrom-SafeXmlFile -Path $nuspecFile.FullName -Code $Code
    $package = $document.DocumentElement
    Assert-Condition ($package.LocalName -eq "package") $Code
    $metadata = Get-OnlyChildElement -Parent $package -LocalName "metadata" -Code $Code
    $id = Get-OnlyChildElement -Parent $metadata -LocalName "id" -Code $Code
    $version = Get-OnlyChildElement -Parent $metadata -LocalName "version" -Code $Code
    $licenseNodes = @($metadata.ChildNodes | Where-Object {
        $_ -is [System.Xml.XmlElement] -and $_.LocalName -eq "license"
    })
    $licenseUrlNodes = @($metadata.ChildNodes | Where-Object {
        $_ -is [System.Xml.XmlElement] -and $_.LocalName -eq "licenseUrl"
    })
    Assert-Condition `
        ($licenseNodes.Count -le 1 -and
            $licenseUrlNodes.Count -le 1 -and
            ($licenseNodes.Count + $licenseUrlNodes.Count) -ge 1) `
        $Code
    $kind = $null
    $value = $null
    if ($licenseNodes.Count -eq 1) {
        $kind = Get-XmlAttributeValue -Element $licenseNodes[0] -Name "type" -Code $Code
        Assert-Condition `
            ([string]::Equals($kind, "expression", [System.StringComparison]::Ordinal) -or
                [string]::Equals($kind, "file", [System.StringComparison]::Ordinal)) `
            $Code
        $value = $licenseNodes[0].InnerText
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($value)) $Code
        if ($licenseUrlNodes.Count -eq 1) {
            $expectedLegacyUrl = if ([string]::Equals(
                    $kind,
                    "file",
                    [System.StringComparison]::Ordinal)) {
                "https://aka.ms/deprecateLicenseUrl"
            }
            else {
                "https://licenses.nuget.org/" + [System.Uri]::EscapeDataString($value)
            }
            Assert-Condition `
                ([string]::Equals(
                    $licenseUrlNodes[0].InnerText,
                    $expectedLegacyUrl,
                    [System.StringComparison]::Ordinal)) `
                $Code
        }
    }
    else {
        $kind = "url"
        $value = $licenseUrlNodes[0].InnerText
        Assert-Condition (Test-SafeReviewedSourceUri -Value $value) $Code
    }
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($value)) $Code
    $licenseHash = if ([string]::Equals($kind, "file", [System.StringComparison]::Ordinal)) {
        $licenseFile = Resolve-PackageFile `
            -PackageDirectory $PackageDirectory `
            -RelativePath $value `
            -Code $Code
        Assert-Condition ($licenseFile.Length -le $script:maximumManifestBytes) $Code
        Assert-NoForbiddenLicenseText `
            -Text (Read-BoundedText -File $licenseFile -Code $Code)
        Get-FileSha256 -Path $licenseFile.FullName
    }
    elseif ([string]::Equals($kind, "expression", [System.StringComparison]::Ordinal)) {
        Assert-NoForbiddenMarker -Path $value
        Get-StringSha256 -Value $value
    }
    else {
        Assert-NoForbiddenMarker -Path $value
        Get-StringSha256 -Value $value
    }

    $repositoryNodes = @($metadata.ChildNodes | Where-Object {
        $_ -is [System.Xml.XmlElement] -and $_.LocalName -eq "repository"
    })
    $projectUrlNodes = @($metadata.ChildNodes | Where-Object {
        $_ -is [System.Xml.XmlElement] -and $_.LocalName -eq "projectUrl"
    })
    Assert-Condition ($repositoryNodes.Count -le 1 -and $projectUrlNodes.Count -le 1) $Code
    $projectUri = $null
    if ($projectUrlNodes.Count -eq 1) {
        $projectUri = $projectUrlNodes[0].InnerText
        Assert-Condition `
            (-not [string]::IsNullOrWhiteSpace($projectUri) -and
                (Test-SafeReviewedSourceUri -Value $projectUri)) `
            $Code
    }

    $sourceUri = $projectUri
    if ($repositoryNodes.Count -eq 1) {
        $repository = $repositoryNodes[0]
        $urlLikeAttributes = @($repository.Attributes | Where-Object {
            $_.LocalName.IndexOf("url", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        })
        $exactUrlAttributes = @($urlLikeAttributes | Where-Object {
            [string]::Equals($_.Name, "url", [System.StringComparison]::Ordinal) -and
            [string]::IsNullOrEmpty($_.NamespaceURI)
        })
        Assert-Condition `
            ($urlLikeAttributes.Count -eq $exactUrlAttributes.Count -and
                $exactUrlAttributes.Count -le 1) `
            $Code
        if ($exactUrlAttributes.Count -eq 1) {
            $sourceUri = $exactUrlAttributes[0].Value
            Assert-Condition `
                (-not [string]::IsNullOrWhiteSpace($sourceUri) -and
                    (Test-SafeReviewedSourceUri -Value $sourceUri)) `
                $Code
        }
        else {
            Assert-Condition ($projectUrlNodes.Count -eq 1) $Code
        }
    }

    return [pscustomobject]@{
        Id = $id.InnerText
        Version = $version.InnerText
        Kind = $kind
        Value = $value
        LicenseSha256 = $licenseHash
        NuspecSha256 = Get-FileSha256 -Path $nuspecFile.FullName
        SourceUri = $sourceUri
    }
}

function Get-NoticePaths {
    param(
        [Parameter(Mandatory)]
        [object]$AssetsLibrary,

        [Parameter(Mandatory)]
        [string]$Code
    )

    $noticePaths = @($AssetsLibrary.files | Where-Object {
        $path = Get-CanonicalArchivePath -Path ([string]$_) -Code $Code
        $leaf = $path.Substring($path.LastIndexOf('/') + 1)
        $leaf -match '^(?i:NOTICE.*|THIRD[-_. ]?PARTY[-_. ]?NOTICES?.*)$'
    } | ForEach-Object {
        Get-CanonicalArchivePath -Path ([string]$_) -Code $Code
    })
    Assert-NoCaseCollision -Values $noticePaths -Code $Code
    return @($noticePaths | Sort-Object -CaseSensitive)
}

function Assert-PackageSpecification {
    param(
        [Parameter(Mandatory)]
        [object]$PackageSpecification,

        [Parameter(Mandatory)]
        [object]$LockPackage,

        [Parameter(Mandatory)]
        [object]$Assets,

        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$PackageArchive,

        [Parameter(Mandatory)]
        [hashtable]$MappedPackageEntries
    )

    $code = "PackageTupleMismatch"
    Assert-ExactProperties `
        -Value $PackageSpecification `
        -Expected @(
            "Id", "Version", "Type", "ContentHash", "License", "SourceUri",
            "Notices", "NuspecSha256", "Payload") `
        -Code $code
    Assert-ExactProperties `
        -Value $PackageSpecification.License `
        -Expected @("Kind", "Value", "Sha256") `
        -Code $code
    Assert-Condition (Test-SafeReviewedSourceUri -Value ([string]$PackageSpecification.SourceUri)) $code
    Assert-NoForbiddenMarker -Path ([string]$PackageSpecification.Id)
    Assert-NoForbiddenMarker -Path ([string]$PackageSpecification.SourceUri)
    Assert-NoForbiddenMarker -Path ([string]$PackageSpecification.License.Value)
    Assert-Condition (Test-IsSha256 -Value ([string]$PackageSpecification.License.Sha256)) $code
    Assert-Condition (Test-IsSha256 -Value ([string]$PackageSpecification.NuspecSha256)) $code
    foreach ($tuple in @(
            @("resolved", "Version"),
            @("type", "Type"),
            @("contentHash", "ContentHash"))) {
        Assert-Condition `
            ([string]::Equals(
                    [string](Get-ExactProperty -Value $LockPackage -Name $tuple[0] -Code $code),
                    [string]$PackageSpecification.($tuple[1]),
                    [System.StringComparison]::Ordinal)) `
            $code
    }

    $libraryKey = "$($PackageSpecification.Id)/$($PackageSpecification.Version)"
    $resolved = Get-ResolvedPackageDirectory `
        -Assets $Assets `
        -LibraryKey $libraryKey `
        -Code $code
    Assert-Condition `
        ([string]::Equals(
                [string]$resolved.Library.sha512,
                [string]$PackageSpecification.ContentHash,
                [System.StringComparison]::Ordinal)) `
        $code
    $nuspec = Get-NuspecMetadata `
        -PackageDirectory $resolved.Directory `
        -AssetsLibrary $resolved.Library `
        -Code $code
    foreach ($property in @("Id", "Version")) {
        Assert-Condition `
            ([string]::Equals(
                    [string]$nuspec.$property,
                    [string]$PackageSpecification.$property,
                    [System.StringComparison]::Ordinal)) `
            $code
    }
    Assert-Condition `
        ([string]::Equals(
                [string]$nuspec.Kind,
                [string]$PackageSpecification.License.Kind,
                [System.StringComparison]::Ordinal)) `
        $code
    Assert-Condition `
        ((Test-SafeReviewedSourceUri -Value $nuspec.SourceUri) -and
            [string]::Equals(
                [string]$nuspec.SourceUri,
                [string]$PackageSpecification.SourceUri,
                [System.StringComparison]::Ordinal)) `
        $code
    Assert-Condition `
        ([string]::Equals(
                [string]$nuspec.Value,
                [string]$PackageSpecification.License.Value,
                [System.StringComparison]::Ordinal)) `
        $code
    Assert-Condition `
        ([string]::Equals(
                [string]$nuspec.LicenseSha256,
                [string]$PackageSpecification.License.Sha256,
                [System.StringComparison]::Ordinal)) `
        $code
    Assert-Condition `
        ([string]::Equals(
                [string]$nuspec.NuspecSha256,
                [string]$PackageSpecification.NuspecSha256,
                [System.StringComparison]::Ordinal)) `
        $code

    $actualNoticePaths = @(Get-NoticePaths -AssetsLibrary $resolved.Library -Code $code)
    $specifiedNotices = @($PackageSpecification.Notices)
    Assert-Condition ($specifiedNotices.Count -le 64) $code
    $specifiedNoticePaths = New-Object 'System.Collections.Generic.List[string]'
    foreach ($notice in $specifiedNotices) {
        Assert-ExactProperties `
            -Value $notice `
            -Expected @("Path", "Sha256") `
            -Code $code
        $noticePath = Get-CanonicalArchivePath -Path ([string]$notice.Path) -Code $code
        $noticeHash = [string]$notice.Sha256
        Assert-Condition (Test-IsSha256 -Value $noticeHash) $code
        $specifiedNoticePaths.Add($noticePath)
        $noticeFile = Resolve-PackageFile `
            -PackageDirectory $resolved.Directory `
            -RelativePath $noticePath `
            -Code $code
        Assert-Condition `
            ([string]::Equals(
                    (Get-FileSha256 -Path $noticeFile.FullName),
                    $noticeHash,
                    [System.StringComparison]::Ordinal)) `
            $code
    }
    Assert-NoCaseCollision -Values $specifiedNoticePaths.ToArray() -Code $code
    Assert-ExactStringSet `
        -Actual $actualNoticePaths `
        -Expected $specifiedNoticePaths.ToArray() `
        -Code $code

    $payload = @($PackageSpecification.Payload)
    Assert-Condition ($payload.Count -le $script:maximumPayloadCount) $code
    foreach ($mapping in $payload) {
        Assert-ExactProperties `
            -Value $mapping `
            -Expected @("PackageEntryPath", "RestoredPath", "Sha256") `
            -Code $code
        $entryPath = Get-CanonicalArchivePath `
            -Path ([string]$mapping.PackageEntryPath) `
            -Code $code
        Assert-Condition (Test-IsExecutablePath -Path $entryPath) $code
        Assert-Condition (Test-IsSha256 -Value ([string]$mapping.Sha256)) $code
        Assert-NoForbiddenMarker -Path $entryPath
        Assert-Condition ($null -ne $PackageArchive.GetEntry($entryPath)) $code
        Assert-Condition (-not $MappedPackageEntries.ContainsKey($entryPath)) $code
        $entryHash = Get-ZipEntrySha256 -Entry $PackageArchive.GetEntry($entryPath)
        Assert-Condition `
            ([string]::Equals(
                    $entryHash,
                    [string]$mapping.Sha256,
                    [System.StringComparison]::Ordinal)) `
            $code
        $restoredFile = Resolve-PackageFile `
            -PackageDirectory $resolved.Directory `
            -RelativePath ([string]$mapping.RestoredPath) `
            -Code $code
        Assert-Condition `
            ([string]::Equals(
                    (Get-FileSha256 -Path $restoredFile.FullName),
                    [string]$mapping.Sha256,
                    [System.StringComparison]::Ordinal)) `
            $code
        $MappedPackageEntries[$entryPath] = "$($PackageSpecification.Id)/$($PackageSpecification.Version)"
    }
}

function Get-DepsRuntimeFiles {
    param(
        [Parameter(Mandatory)]
        [object]$Library
    )

    $paths = New-Object 'System.Collections.Generic.List[string]'
    foreach ($groupName in @("runtime", "native", "runtimeTargets")) {
        $groups = @($Library.PSObject.Properties | Where-Object {
            [string]::Equals($_.Name, $groupName, [System.StringComparison]::Ordinal)
        })
        if ($groups.Count -eq 0) {
            continue
        }

        Assert-Condition ($groups.Count -eq 1) "DepsGraphInvalid"
        foreach ($property in @($groups[0].Value.PSObject.Properties)) {
            $paths.Add((Get-CanonicalArchivePath -Path $property.Name -Code "DepsGraphInvalid"))
        }
    }

    return @($paths.ToArray())
}

function Assert-ToolchainPayload {
    param(
        [Parameter(Mandatory)]
        [object]$Payload,

        [Parameter(Mandatory)]
        [object]$DepsTarget,

        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$PackageArchive,

        [Parameter(Mandatory)]
        [hashtable]$MappedToolchainEntries
    )

    $code = "ToolchainPayloadMismatch"
    Assert-ExactProperties `
        -Value $Payload `
        -Expected @("Path", "Sha256", "OriginKind", "OriginVersion") `
        -Code $code
    $path = Get-CanonicalArchivePath -Path ([string]$Payload.Path) -Code $code
    Assert-Condition (Test-IsExecutablePath -Path $path) $code
    Assert-Condition (Test-IsSha256 -Value ([string]$Payload.Sha256)) $code
    Assert-NoForbiddenMarker -Path $path
    Assert-Condition ($null -ne $PackageArchive.GetEntry($path)) $code
    Assert-Condition (-not $MappedToolchainEntries.ContainsKey($path)) $code
    Assert-Condition `
        ([string]::Equals(
                (Get-ZipEntrySha256 -Entry $PackageArchive.GetEntry($path)),
                [string]$Payload.Sha256,
                [System.StringComparison]::Ordinal)) `
        $code
    $originMatches = New-Object 'System.Collections.Generic.List[string]'
    foreach ($libraryProperty in @($DepsTarget.PSObject.Properties)) {
        $separator = $libraryProperty.Name.LastIndexOf('/')
        Assert-Condition ($separator -gt 0 -and $separator -lt ($libraryProperty.Name.Length - 1)) "DepsGraphInvalid"
        $version = $libraryProperty.Name.Substring($separator + 1)
        $libraryMetadata = $libraryProperty.Value
        $libraryKind = if ($null -ne $libraryMetadata.PSObject.Properties["type"]) {
            [string]$libraryMetadata.type
        }
        else {
            $depsLibrary = $script:deps.libraries.PSObject.Properties[$libraryProperty.Name]
            Assert-Condition ($null -ne $depsLibrary) "DepsGraphInvalid"
            [string]$depsLibrary.Value.type
        }
        if (-not [string]::Equals(
                $libraryKind,
                [string]$Payload.OriginKind,
                [System.StringComparison]::Ordinal) -or
            -not [string]::Equals(
                $version,
                [string]$Payload.OriginVersion,
                [System.StringComparison]::Ordinal)) {
            continue
        }

        $runtimePaths = @(Get-DepsRuntimeFiles -Library $libraryMetadata)
        foreach ($runtimePath in $runtimePaths) {
            $leaf = $runtimePath.Substring($runtimePath.LastIndexOf('/') + 1)
            $packageLeaf = $path.Substring($path.LastIndexOf('/') + 1)
            if ([string]::Equals($leaf, $packageLeaf, [System.StringComparison]::Ordinal)) {
                $originMatches.Add($libraryProperty.Name)
            }
        }
    }

    Assert-Condition ($originMatches.Count -eq 1) $code
    $MappedToolchainEntries[$path] = $originMatches[0]
}

function Get-RecursiveRuntimeExecutableInventory {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Prefix,

        [Parameter(Mandatory)]
        [int]$Depth
    )

    Assert-Condition ($Depth -le $script:maximumNestedDepth) "RuntimeArchiveDepthExceeded"
    $inventory = Get-ZipInventory -Archive $Archive -Code "RuntimeArchiveInvalid"
    $script:runtimeArchiveCount += 1
    $script:runtimeRecursiveEntryCount += $inventory.Paths.Count
    $script:runtimeRecursiveExpandedBytes += $inventory.ExpandedBytes
    Assert-Condition `
        ($script:runtimeArchiveCount -le $script:maximumNestedArchiveCount -and
            $script:runtimeRecursiveEntryCount -le $script:maximumRecursiveRuntimeEntries -and
            $script:runtimeRecursiveExpandedBytes -le $script:maximumRecursiveRuntimeBytes) `
        "RuntimeArchiveBoundsExceeded"
    $result = New-Object 'System.Collections.Generic.List[object]'
    foreach ($path in @($inventory.Paths | Sort-Object -CaseSensitive)) {
        $entry = $inventory.Records[$path]
        $qualifiedPath = if ([string]::IsNullOrEmpty($Prefix)) {
            $path
        }
        else {
            "$Prefix!/$path"
        }
        $isExecutable = Test-IsExecutablePath -Path $path
        $hasPortableExecutableHeader = Test-ZipEntryHasPortableExecutableHeader -Entry $entry
        $isLocalizedResource = $path.EndsWith(
            ".dll.mui",
            [System.StringComparison]::OrdinalIgnoreCase)
        Assert-Condition `
            (-not $hasPortableExecutableHeader -or $isExecutable -or $isLocalizedResource) `
            "UnsupportedExecutablePayload"
        if ($isExecutable) {
            $result.Add([pscustomobject]@{
                    Path = $qualifiedPath
                    Sha256 = Get-ZipEntrySha256 -Entry $entry
                })
        }

        if ($path.EndsWith(".msix", [System.StringComparison]::OrdinalIgnoreCase) -or
            $path.EndsWith(".appx", [System.StringComparison]::OrdinalIgnoreCase)) {
            Assert-Condition ($Depth -lt $script:maximumNestedDepth) "RuntimeArchiveDepthExceeded"
            $bytes = Read-ZipEntryBytes `
                -Entry $entry `
                -MaximumBytes $script:maximumNestedArchiveBytes `
                -Code "RuntimeNestedArchiveInvalid"
            $stream = New-Object System.IO.MemoryStream(,$bytes)
            $nestedArchive = $null
            try {
                $nestedArchive = New-Object System.IO.Compression.ZipArchive(
                    $stream,
                    [System.IO.Compression.ZipArchiveMode]::Read,
                    $false)
                $nestedRecords = @(Get-RecursiveRuntimeExecutableInventory `
                        -Archive $nestedArchive `
                        -Prefix $qualifiedPath `
                        -Depth ($Depth + 1))
                foreach ($record in $nestedRecords) {
                    $result.Add($record)
                }
            }
            catch {
                throw "Native package inventory validation failed: RuntimeNestedArchiveInvalid."
            }
            finally {
                if ($null -ne $nestedArchive) {
                    $nestedArchive.Dispose()
                }
                else {
                    $stream.Dispose()
                }
            }
        }
    }

    return @($result.ToArray())
}

function Assert-RuntimePackage {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$RuntimeFile,

        [Parameter(Mandatory)]
        [object]$RuntimeSpecification
    )

    $code = "RuntimePackageMismatch"
    Assert-ExactProperties `
        -Value $RuntimeSpecification `
        -Expected @(
            "FileName", "Sha256", "Identity", "Publisher", "Version", "Architecture",
            "AllowedTopLevelEntries", "ExecutableEntries") `
        -Code $code
    Assert-Condition (Test-IsSha256 -Value ([string]$RuntimeSpecification.Sha256)) $code
    Assert-Condition `
        ([string]::Equals(
                $RuntimeFile.Name,
                [string]$RuntimeSpecification.FileName,
                [System.StringComparison]::Ordinal)) `
        $code
    Assert-Condition `
        ([string]::Equals(
                (Get-FileSha256 -Path $RuntimeFile.FullName),
                [string]$RuntimeSpecification.Sha256,
                [System.StringComparison]::Ordinal)) `
        $code

    $stream = New-Object System.IO.FileStream(
        $RuntimeFile.FullName,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    $archive = $null
    try {
        $archive = New-Object System.IO.Compression.ZipArchive(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false)
        $topLevel = Get-ZipInventory -Archive $archive -Code "RuntimeArchiveInvalid"
        Assert-ExactStringSet `
            -Actual @($topLevel.Paths) `
            -Expected @($RuntimeSpecification.AllowedTopLevelEntries) `
            -Code $code
        Assert-Condition ($topLevel.Records.ContainsKey("AppxManifest.xml")) $code
        $manifestBytes = Read-ZipEntryBytes `
            -Entry $topLevel.Records["AppxManifest.xml"] `
            -MaximumBytes $script:maximumManifestBytes `
            -Code $code
        $runtimeManifest = ConvertFrom-StrictUtf8XmlBytes `
            -Bytes $manifestBytes `
            -Code $code
        $runtimePackage = $runtimeManifest.DocumentElement
        $identity = Get-OnlyChildElement `
            -Parent $runtimePackage `
            -LocalName "Identity" `
            -Code $code
        $identityValues = [ordered]@{
            Identity = Get-XmlAttributeValue -Element $identity -Name "Name" -Code $code
            Publisher = Get-XmlAttributeValue -Element $identity -Name "Publisher" -Code $code
            Version = Get-XmlAttributeValue -Element $identity -Name "Version" -Code $code
            Architecture = Get-XmlAttributeValue -Element $identity -Name "ProcessorArchitecture" -Code $code
        }
        foreach ($property in @("Identity", "Publisher", "Version", "Architecture")) {
            Assert-Condition `
                ([string]::Equals(
                        [string]$identityValues[$property],
                        [string]$RuntimeSpecification.$property,
                        [System.StringComparison]::Ordinal)) `
                $code
        }
        Assert-Condition `
            ([string]::Equals(
                    [string]$identityValues.Architecture,
                    "x64",
                    [System.StringComparison]::OrdinalIgnoreCase)) `
            $code

        $script:runtimeArchiveCount = 0
        $script:runtimeRecursiveEntryCount = 0
        $script:runtimeRecursiveExpandedBytes = [long]0
        $actualExecutables = @(Get-RecursiveRuntimeExecutableInventory `
                -Archive $archive `
                -Prefix "" `
                -Depth 0)
        Assert-Condition ($actualExecutables.Count -le $script:maximumPayloadCount) $code
        $actualPaths = @($actualExecutables | ForEach-Object { $_.Path })
        Assert-NoCaseCollision -Values $actualPaths -Code $code
        $expectedExecutables = @($RuntimeSpecification.ExecutableEntries)
        Assert-Condition ($expectedExecutables.Count -eq $actualExecutables.Count) $code
        $expectedPaths = New-Object 'System.Collections.Generic.List[string]'
        $expectedByPath = @{}
        foreach ($entry in $expectedExecutables) {
            Assert-ExactProperties `
                -Value $entry `
                -Expected @("Path", "Sha256") `
                -Code $code
            $segments = @(([string]$entry.Path).Split(@("!/"), [System.StringSplitOptions]::None))
            Assert-Condition ($segments.Count -ge 1 -and $segments.Count -le 3) $code
            foreach ($segment in $segments) {
                [void](Get-CanonicalArchivePath -Path $segment -Code $code)
            }
            $path = [string]$entry.Path
            Assert-Condition (Test-IsSha256 -Value ([string]$entry.Sha256)) $code
            Assert-NoForbiddenMarker -Path $path.Replace("!/", "/")
            $expectedPaths.Add($path)
            $expectedByPath[$path] = [string]$entry.Sha256
        }
        Assert-NoCaseCollision -Values $expectedPaths.ToArray() -Code $code
        Assert-ExactStringSet -Actual $actualPaths -Expected $expectedPaths.ToArray() -Code $code
        foreach ($entry in $actualExecutables) {
            Assert-Condition `
                ([string]::Equals(
                        [string]$entry.Sha256,
                        [string]$expectedByPath[$entry.Path],
                        [System.StringComparison]::Ordinal)) `
                $code
        }

        return [pscustomobject]@{
            EntryCount = $topLevel.Paths.Count
            ExecutableEntries = @($actualExecutables | Sort-Object Path | ForEach-Object {
                    [ordered]@{ Path = $_.Path; Sha256 = $_.Sha256 }
                })
        }
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        else {
            $stream.Dispose()
        }
    }
}

function Write-JsonAtomically {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    Assert-Condition (-not (Test-Path -LiteralPath $DestinationPath)) "EvidenceAlreadyExists"
    $fullPath = [System.IO.Path]::GetFullPath($DestinationPath)
    $directory = [System.IO.Path]::GetDirectoryName($fullPath)
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($directory)) "EvidencePathInvalid"
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [void][System.IO.Directory]::CreateDirectory($directory)
    }
    [void](Assert-RegularDirectory -Path $directory -Code "EvidencePathInvalid")
    $temporaryPath = Join-Path $directory (".{0}.tmp" -f [guid]::NewGuid().ToString("N"))
    $bytes = $script:utf8NoBom.GetBytes(($Value | ConvertTo-Json -Depth 12))
    $stream = New-Object System.IO.FileStream(
        $temporaryPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }

    try {
        [System.IO.File]::Move($temporaryPath, $fullPath)
    }
    catch {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            [System.IO.File]::Delete($temporaryPath)
        }
        throw
    }
}

$packageFile = Resolve-RegularFile `
    -Path $PackagePath `
    -MaximumBytes $script:maximumArchiveBytes `
    -Code "PackageFileInvalid"
$runtimeFile = Resolve-RegularFile `
    -Path $RuntimePackagePath `
    -MaximumBytes $script:maximumArchiveBytes `
    -Code "RuntimePackageFileInvalid"
$lockFile = Resolve-RegularFile `
    -Path $LockFilePath `
    -MaximumBytes $script:maximumJsonBytes `
    -Code "LockFileInvalid"
$assetsFile = Resolve-RegularFile `
    -Path $AssetsFilePath `
    -MaximumBytes $script:maximumJsonBytes `
    -Code "AssetsFileInvalid"
$depsFile = Resolve-RegularFile `
    -Path $DepsFilePath `
    -MaximumBytes $script:maximumJsonBytes `
    -Code "DepsFileInvalid"
$manifestFile = Resolve-RegularFile `
    -Path $ManifestPath `
    -MaximumBytes $script:maximumManifestBytes `
    -Code "ManifestFileInvalid"
$specificationFile = Resolve-RegularFile `
    -Path $SpecificationPath `
    -MaximumBytes $script:maximumJsonBytes `
    -Code "SpecificationFileInvalid"

$specification = ConvertFrom-ExactJsonFile `
    -Path $specificationFile.FullName `
    -Code "SpecificationInvalid"
Assert-ExactProperties `
    -Value $specification `
    -Expected @(
        "SchemaVersion", "Project", "LockTargets", "RuntimeIdentifier", "PackageIdentity",
        "TargetDeviceFamily", "RuntimeDependency", "Capabilities", "Application", "Extensions",
        "Packages", "ToolchainPayload", "AppOwnedFiles", "AllowedPackageEntries", "RuntimePackage") `
    -Code "SpecificationInvalid"
Assert-Condition ([int]$specification.SchemaVersion -eq 1) "SpecificationInvalid"
Assert-Condition `
    ([string]::Equals(
            [string]$specification.RuntimeIdentifier,
            "win-x64",
            [System.StringComparison]::Ordinal)) `
    "SpecificationInvalid"
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$specification.Project)) "SpecificationInvalid"
Assert-ExactProperties `
    -Value $specification.RuntimeDependency `
    -Expected @("Name", "Publisher", "MinVersion") `
    -Code "SpecificationInvalid"
Assert-ExactProperties `
    -Value $specification.RuntimePackage `
    -Expected @(
        "FileName", "Sha256", "Identity", "Publisher", "Version", "Architecture",
        "AllowedTopLevelEntries", "ExecutableEntries") `
    -Code "SpecificationInvalid"
Assert-Condition `
    ([string]::Equals(
            [string]$specification.RuntimeDependency.Name,
            [string]$specification.RuntimePackage.Identity,
            [System.StringComparison]::Ordinal) -and
        [string]::Equals(
            [string]$specification.RuntimeDependency.Publisher,
            [string]$specification.RuntimePackage.Publisher,
            [System.StringComparison]::Ordinal) -and
        [string]::Equals(
            [string]$specification.RuntimeDependency.MinVersion,
            [string]$specification.RuntimePackage.Version,
            [System.StringComparison]::Ordinal)) `
    "RuntimeDependencyBindingMismatch"

$sourceManifest = ConvertFrom-SafeXmlFile `
    -Path $manifestFile.FullName `
    -Code "SourceManifestInvalid"
$sourceContract = Get-ManifestContract -Document $sourceManifest -Code "SourceManifestInvalid"
Assert-ManifestMatchesSpecification `
    -Contract $sourceContract `
    -Specification $specification `
    -IncludeRuntimeDependency $false `
    -Code "SourceManifestMismatch"

$lock = ConvertFrom-ExactJsonFile -Path $lockFile.FullName -Code "LockFileInvalid"
Assert-Condition ([int]$lock.version -eq 2) "LockFileInvalid"
$lockTargetSpecifications = @($specification.LockTargets)
Assert-Condition `
    ($lockTargetSpecifications.Count -gt 0 -and $lockTargetSpecifications.Count -le 16) `
    "LockTargetsMismatch"
$specifiedLockTargetNames = @($lockTargetSpecifications | ForEach-Object { [string]$_.Name })
$actualLockTargetNames = @($lock.dependencies.PSObject.Properties | ForEach-Object { $_.Name })
Assert-NoCaseCollision -Values $specifiedLockTargetNames -Code "LockTargetsMismatch"
Assert-NoCaseCollision -Values $actualLockTargetNames -Code "LockTargetsMismatch"
Assert-ExactStringSet `
    -Actual $actualLockTargetNames `
    -Expected $specifiedLockTargetNames `
    -Code "LockTargetsMismatch"
foreach ($lockTargetSpecification in $lockTargetSpecifications) {
    Assert-ExactProperties `
        -Value $lockTargetSpecification `
        -Expected @("Name", "Packages") `
        -Code "LockTargetsMismatch"
    Assert-Condition `
        ($lockTargetSpecification.Packages -is [System.Management.Automation.PSCustomObject]) `
        "LockTargetsMismatch"
    $specifiedTargetPackageNames = @(
        $lockTargetSpecification.Packages.PSObject.Properties | ForEach-Object { $_.Name })
    Assert-Condition `
        ($specifiedTargetPackageNames.Count -le $script:maximumPackageCount) `
        "LockTargetsMismatch"
    Assert-NoCaseCollision -Values $specifiedTargetPackageNames -Code "LockTargetsMismatch"
    $actualLockTarget = Get-ExactProperty `
        -Value $lock.dependencies `
        -Name ([string]$lockTargetSpecification.Name) `
        -Code "LockTargetsMismatch"
    Assert-ExactJsonValue `
        -Actual $actualLockTarget `
        -Expected $lockTargetSpecification.Packages `
        -Code "LockTargetsMismatch"
}
$packageSpecifications = @($specification.Packages)
Assert-Condition `
    ($packageSpecifications.Count -gt 0 -and
        $packageSpecifications.Count -le $script:maximumPackageCount) `
    "SpecificationInvalid"
$specifiedIds = @($packageSpecifications | ForEach-Object { [string]$_.Id })
Assert-NoCaseCollision -Values $specifiedIds -Code "PackageTupleMismatch"
$metadataLockTargets = @($lockTargetSpecifications | Where-Object {
        $targetPackageNames = @($_.Packages.PSObject.Properties | ForEach-Object { $_.Name })
        Test-ExactStringSet -Actual $targetPackageNames -Expected $specifiedIds
    })
Assert-Condition ($metadataLockTargets.Count -eq 1) "PackageTupleMismatch"
$lockTarget = Get-ExactProperty `
    -Value $lock.dependencies `
    -Name ([string]$metadataLockTargets[0].Name) `
    -Code "PackageTupleMismatch"
$lockNames = @($lockTarget.PSObject.Properties | ForEach-Object { $_.Name })
Assert-ExactStringSet -Actual $lockNames -Expected $specifiedIds -Code "PackageTupleMismatch"

$assets = ConvertFrom-ExactJsonFile -Path $assetsFile.FullName -Code "AssetsFileInvalid"
Assert-Condition ($null -ne $assets.packageFolders -and $null -ne $assets.libraries) "AssetsFileInvalid"
$script:deps = ConvertFrom-ExactJsonFile -Path $depsFile.FullName -Code "DepsFileInvalid"
Assert-Condition `
    ([string]$script:deps.runtimeTarget.name).EndsWith(
        "/$($specification.RuntimeIdentifier)",
        [System.StringComparison]::Ordinal) `
    "DepsGraphInvalid"
$depsTarget = Get-ExactProperty `
    -Value $script:deps.targets `
    -Name ([string]$script:deps.runtimeTarget.name) `
    -Code "DepsGraphInvalid"

$packageStream = New-Object System.IO.FileStream(
    $packageFile.FullName,
    [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read,
    [System.IO.FileShare]::Read)
$packageArchive = $null
try {
    $packageArchive = New-Object System.IO.Compression.ZipArchive(
        $packageStream,
        [System.IO.Compression.ZipArchiveMode]::Read,
        $false)
    $packageInventory = Get-ZipInventory -Archive $packageArchive -Code "PackageArchiveInvalid"
    Assert-ExactStringSet `
        -Actual @($packageInventory.Paths) `
        -Expected @($specification.AllowedPackageEntries) `
        -Code "PackageEntryAllowlistMismatch"
    Assert-Condition `
        ($packageInventory.Records.ContainsKey("AppxManifest.xml")) `
        "PackageManifestMissing"
    $embeddedManifestBytes = Read-ZipEntryBytes `
        -Entry $packageInventory.Records["AppxManifest.xml"] `
        -MaximumBytes $script:maximumManifestBytes `
        -Code "PackageManifestInvalid"
    $embeddedManifest = ConvertFrom-StrictUtf8XmlBytes `
        -Bytes $embeddedManifestBytes `
        -Code "PackageManifestInvalid"
    $embeddedContract = Get-ManifestContract -Document $embeddedManifest -Code "PackageManifestInvalid"
    Assert-ManifestMatchesSpecification `
        -Contract $embeddedContract `
        -Specification $specification `
        -IncludeRuntimeDependency $true `
        -Code "PackageManifestMismatch"
    foreach ($extension in $embeddedContract.Extensions) {
        Assert-Condition `
            ($packageInventory.Records.ContainsKey([string]$extension.InProcessServerPath) -and
                (Test-IsExecutablePath -Path ([string]$extension.InProcessServerPath))) `
            "PackageExtensionPayloadMismatch"
    }
    $embeddedDepsPaths = @($packageInventory.Paths | Where-Object {
        $_.EndsWith(".deps.json", [System.StringComparison]::Ordinal)
    })
    Assert-Condition ($embeddedDepsPaths.Count -eq 1) "PackagedDepsMismatch"
    Assert-Condition `
        ([string]::Equals(
                (Get-ZipEntrySha256 -Entry $packageInventory.Records[$embeddedDepsPaths[0]]),
                (Get-FileSha256 -Path $depsFile.FullName),
                [System.StringComparison]::Ordinal)) `
        "PackagedDepsMismatch"

    $mappedPackageEntries = @{}
    foreach ($packageSpecification in $packageSpecifications) {
        $lockPackage = Get-ExactProperty `
            -Value $lockTarget `
            -Name ([string]$packageSpecification.Id) `
            -Code "PackageTupleMismatch"
        Assert-PackageSpecification `
            -PackageSpecification $packageSpecification `
            -LockPackage $lockPackage `
            -Assets $assets `
            -PackageArchive $packageArchive `
            -MappedPackageEntries $mappedPackageEntries
    }

    $mappedToolchainEntries = @{}
    $toolchainPayload = @($specification.ToolchainPayload)
    Assert-Condition ($toolchainPayload.Count -le $script:maximumPayloadCount) "SpecificationInvalid"
    foreach ($payload in $toolchainPayload) {
        Assert-ToolchainPayload `
            -Payload $payload `
            -DepsTarget $depsTarget `
            -PackageArchive $packageArchive `
            -MappedToolchainEntries $mappedToolchainEntries
    }

    $appOwnedMappings = @($specification.AppOwnedFiles)
    Assert-Condition ($appOwnedMappings.Count -le $script:maximumPayloadCount) "SpecificationInvalid"
    $appOwnedEntryPaths = New-Object 'System.Collections.Generic.List[string]'
    $appOwnedOutputPaths = New-Object 'System.Collections.Generic.List[string]'
    $mappedAppOwnedEntries = @{}
    foreach ($mapping in $appOwnedMappings) {
        Assert-ExactProperties `
            -Value $mapping `
            -Expected @("PackageEntryPath", "OutputSiblingPath") `
            -Code "AppOwnedPayloadMismatch"
        $entryPath = Get-CanonicalArchivePath `
            -Path ([string]$mapping.PackageEntryPath) `
            -Code "AppOwnedPayloadMismatch"
        $outputPath = Get-CanonicalArchivePath `
            -Path ([string]$mapping.OutputSiblingPath) `
            -Code "AppOwnedPayloadMismatch"
        Assert-Condition `
            (-not $entryPath.Contains("/") -and
                -not $outputPath.Contains("/") -and
                (Test-IsExecutablePath -Path $entryPath) -and
                (Test-IsExecutablePath -Path $outputPath)) `
            "AppOwnedPayloadMismatch"
        Assert-NoForbiddenMarker -Path $entryPath
        Assert-NoForbiddenMarker -Path $outputPath
        $appOwnedEntryPaths.Add($entryPath)
        $appOwnedOutputPaths.Add($outputPath)
        $outputFile = Resolve-PackageFile `
            -PackageDirectory $depsFile.Directory.FullName `
            -RelativePath $outputPath `
            -Code "AppOwnedPayloadMismatch"
        $mappedAppOwnedEntries[$entryPath] = [pscustomobject]@{
            OutputSiblingPath = $outputPath
            OutputFile = $outputFile
        }
    }
    Assert-NoCaseCollision -Values $appOwnedEntryPaths.ToArray() -Code "AppOwnedPayloadMismatch"
    Assert-NoCaseCollision -Values $appOwnedOutputPaths.ToArray() -Code "AppOwnedPayloadMismatch"
    $appOwnedEvidence = New-Object 'System.Collections.Generic.List[object]'
    $actualExecutablePaths = New-Object 'System.Collections.Generic.List[string]'
    foreach ($path in $packageInventory.Paths) {
        $entry = $packageInventory.Records[$path]
        $isExecutable = Test-IsExecutablePath -Path $path
        $hasPortableExecutableHeader = Test-ZipEntryHasPortableExecutableHeader -Entry $entry
        $isLocalizedResource = $path.EndsWith(
            ".dll.mui",
            [System.StringComparison]::OrdinalIgnoreCase)
        Assert-Condition `
            (-not $hasPortableExecutableHeader -or $isExecutable -or $isLocalizedResource) `
            "UnsupportedExecutablePayload"
        if ($isExecutable) {
            $actualExecutablePaths.Add($path)
        }
    }
    foreach ($path in $actualExecutablePaths.ToArray()) {
        $isPackage = $mappedPackageEntries.ContainsKey($path)
        $isToolchain = $mappedToolchainEntries.ContainsKey($path)
        $isAppOwned = $mappedAppOwnedEntries.ContainsKey($path)
        $classificationCount = @($isPackage, $isToolchain, $isAppOwned | Where-Object { $_ }).Count
        Assert-Condition ($classificationCount -eq 1) "UnknownOrAmbiguousExecutablePayload"
        if ($isAppOwned) {
            $outputBinding = $mappedAppOwnedEntries[$path]
            $entrySha256 = Get-ZipEntrySha256 -Entry $packageInventory.Records[$path]
            $outputSha256 = Get-FileSha256 -Path $outputBinding.OutputFile.FullName
            Assert-Condition `
                ([string]::Equals(
                        $entrySha256,
                        $outputSha256,
                        [System.StringComparison]::Ordinal)) `
                "AppOwnedPayloadMismatch"
            $appOwnedEvidence.Add([ordered]@{
                    Path = $path
                    OutputSiblingPath = [string]$outputBinding.OutputSiblingPath
                    Sha256 = $entrySha256
                })
        }
    }
    Assert-ExactStringSet `
        -Actual @($appOwnedEvidence | ForEach-Object { $_.Path }) `
        -Expected $appOwnedEntryPaths.ToArray() `
        -Code "AppOwnedPayloadMismatch"

    Assert-ExactStringSet `
        -Actual @($mappedPackageEntries.Keys) `
        -Expected @($packageSpecifications | ForEach-Object {
                @($_.Payload) | ForEach-Object { [string]$_.PackageEntryPath }
            }) `
        -Code "PackagePayloadMappingIncomplete"
    Assert-ExactStringSet `
        -Actual @($mappedToolchainEntries.Keys) `
        -Expected @($toolchainPayload | ForEach-Object { [string]$_.Path }) `
        -Code "ToolchainPayloadMappingIncomplete"

    $runtimeInventory = Assert-RuntimePackage `
        -RuntimeFile $runtimeFile `
        -RuntimeSpecification $specification.RuntimePackage

    $packageEvidence = @($packageSpecifications | Sort-Object Id | ForEach-Object {
            [ordered]@{
                Id = [string]$_.Id
                Version = [string]$_.Version
                Type = [string]$_.Type
                ContentHash = [string]$_.ContentHash
                License = [ordered]@{
                    Kind = [string]$_.License.Kind
                    Value = [string]$_.License.Value
                    Sha256 = [string]$_.License.Sha256
                }
                SourceUri = [string]$_.SourceUri
                Notices = @($_.Notices | Sort-Object Path | ForEach-Object {
                        [ordered]@{
                            Path = [string]$_.Path
                            Sha256 = [string]$_.Sha256
                        }
                    })
                NuspecSha256 = [string]$_.NuspecSha256
                Payload = @($_.Payload | Sort-Object PackageEntryPath | ForEach-Object {
                        [ordered]@{
                            Path = [string]$_.PackageEntryPath
                            Sha256 = [string]$_.Sha256
                        }
                    })
            }
        })
    $toolchainEvidence = @($toolchainPayload | Sort-Object Path | ForEach-Object {
            [ordered]@{
                Path = [string]$_.Path
                Sha256 = [string]$_.Sha256
                OriginKind = [string]$_.OriginKind
                OriginVersion = [string]$_.OriginVersion
            }
        })
    $evidence = [ordered]@{
        SchemaVersion = 1
        Stage = "NativePackageInventory"
        Result = "Pass"
        Project = [string]$specification.Project
        LockTargets = @($specifiedLockTargetNames | Sort-Object -CaseSensitive)
        RuntimeIdentifier = [string]$specification.RuntimeIdentifier
        PackageSha256 = Get-FileSha256 -Path $packageFile.FullName
        RuntimePackageSha256 = Get-FileSha256 -Path $runtimeFile.FullName
        LockFileSha256 = Get-FileSha256 -Path $lockFile.FullName
        AssetsFileSha256 = Get-FileSha256 -Path $assetsFile.FullName
        DepsFileSha256 = Get-FileSha256 -Path $depsFile.FullName
        ManifestSha256 = Get-FileSha256 -Path $manifestFile.FullName
        SpecificationSha256 = Get-FileSha256 -Path $specificationFile.FullName
        PackageEntryCount = $packageInventory.Paths.Count
        RuntimePackageEntryCount = $runtimeInventory.EntryCount
        Packages = $packageEvidence
        ToolchainPayload = $toolchainEvidence
        AppOwnedFiles = @($appOwnedEvidence | Sort-Object Path)
        RuntimeExecutables = @($runtimeInventory.ExecutableEntries)
    }
    Write-JsonAtomically -Value $evidence -DestinationPath $EvidencePath
    Write-Output "Native playback package inventory validation passed."
}
finally {
    if ($null -ne $packageArchive) {
        $packageArchive.Dispose()
    }
    else {
        $packageStream.Dispose()
    }
}
