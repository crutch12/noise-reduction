$ErrorActionPreference = 'Stop'
# rnnoise.dll берётся из колеса pyrnnoise 0.4.5 для Windows x64 и сверяется по хешу, как и при лабораторной сборке
$repoPath = Split-Path -Parent $PSScriptRoot
$expected = '06770aeb8855ef942b5dcdb436f84dfdd5867b716ba4b4c8a2c66ade03197f31'
$target = Join-Path $repoPath 'build/rnnoise.dll'
if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected) { Write-Output 'build/rnnoise.dll is up to date'; return }
$release = Invoke-RestMethod 'https://pypi.org/pypi/pyrnnoise/0.4.5/json'
$wheel = $release.urls | Where-Object { $_.filename -like '*win_amd64.whl' } | Select-Object -First 1
if (-not $wheel) { throw 'pyrnnoise 0.4.5 has no Windows x64 wheel' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('rnnoise-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $file = Join-Path $work $wheel.filename
    Invoke-WebRequest $wheel.url -OutFile $file
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $wheel.digests.sha256) { throw 'Wheel checksum does not match PyPI' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($file)
    try {
        $entry = $zip.Entries | Where-Object { $_.Name -eq 'rnnoise.dll' } | Select-Object -First 1
        if (-not $entry) { throw 'rnnoise.dll is missing in the wheel' }
        New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
    finally { $zip.Dispose() }
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'rnnoise.dll differs from the licensed pyrnnoise 0.4.5 payload' }
Write-Output 'Fetched build/rnnoise.dll'
