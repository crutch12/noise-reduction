param([switch]$Unsigned)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'vs.ps1')
# Пакет эффектов Windows 11 для микрофонов, которыми Windows управляет через пакеты (USB и т.п.)
# Каталог подписывается новым сертификатом на каждую сборку, закрытый ключ сразу удаляется
$repoPath = Split-Path -Parent $PSScriptRoot
$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory | Where-Object Name -Match '^10\.' | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'x64' } | Where-Object { Test-Path (Join-Path $_ 'makecat.exe') } | Select-Object -First 1
if (-not $sdk) { throw 'Windows SDK makecat.exe and signtool.exe are required' }
$out = Join-Path $repoPath 'build/pack'
if (Test-Path -LiteralPath $out) { Get-ChildItem -LiteralPath $out -File | Remove-Item -Force }
New-Item -ItemType Directory -Force $out | Out-Null
$now = Get-Date
# версия растёт с каждой сборкой, иначе Windows оставит ранее установленный пакет
. (Join-Path $PSScriptRoot 'version.ps1')
$release = [version](Get-BuildVersion)
# третье число год и день, четвёртое минута дня: так версия растёт и через границу года
$version = '{0}.{1}.{2}.{3}' -f $release.Major, $release.Minor, ($now.Year % 100 * 1000 + $now.DayOfYear), ($now.Hour * 60 + $now.Minute)
foreach ($inf in 'NoiseReductionApo.inf', 'NoiseReductionEffectPack.inf') {
    $text = (Get-Content -LiteralPath (Join-Path $repoPath "installer/pack/$inf") -Raw) -replace 'DriverVer\s*=\s*[^\r\n]+', ('DriverVer   = {0},{1}' -f $now.ToString('MM/dd/yyyy'), $version)
    [IO.File]::WriteAllText((Join-Path $out $inf), $text, [Text.Encoding]::ASCII)
}
Copy-Item -LiteralPath (Join-Path $repoPath 'build/NoiseReduction.Apo.dll'), (Join-Path $repoPath 'build/rnnoise.dll'), (Join-Path $repoPath 'build/nr_deepfilter.dll') -Destination $out
$redist = Get-RedistPath
foreach ($name in 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') { Copy-Item -LiteralPath (Join-Path $redist $name) -Destination $out }
# каталог по всем файлам пакета; подпись либо временная для разработки, либо через SignPath в CI (-Unsigned)
$cdf = @('[CatalogHeader]', 'Name=NoiseReductionPack.cat', "ResultDir=$out", 'PublicVersion=0x0000001', 'EncodingType=0x00010001', 'CATATTR1=0x10010001:OSAttr:2:10.0', '', '[CatalogFiles]')
foreach ($file in Get-ChildItem -LiteralPath $out -File | Where-Object { $_.Extension -in '.inf', '.dll' }) { $cdf += "<hash>$($file.Name)=$($file.FullName)" }
$cdfPath = Join-Path $repoPath 'build/pack.cdf'
$cdf | Set-Content -LiteralPath $cdfPath -Encoding Ascii
& (Join-Path $sdk 'makecat.exe') $cdfPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makecat failed' }
if ($Unsigned) { Write-Output "Effect pack $version in $out, catalog left for external signing"; return }
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Noise Reduction Effect Pack' -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable -NotAfter $now.AddYears(30)
try {
    Export-Certificate -Cert $cert -FilePath (Join-Path $out 'NoiseReductionPack.cer') | Out-Null
    & (Join-Path $sdk 'signtool.exe') sign /q /s My /sha1 $cert.Thumbprint /fd sha256 (Join-Path $out 'NoiseReductionPack.cat')
    if ($LASTEXITCODE -ne 0) { throw 'Catalog signing failed' }
}
finally {
    # без закрытого ключа этим сертификатом больше ничего не подписать
    Remove-Item -LiteralPath ('Cert:\CurrentUser\My\' + $cert.Thumbprint) -DeleteKey
}
Write-Output "Effect pack $version in $out"
