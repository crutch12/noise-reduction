$ErrorActionPreference = 'Stop'
# rnnoise.dll берётся из колеса pyrnnoise 0.4.5 для Windows x64 и сверяется по хешу, как и при лабораторной сборке
$repoPath = Split-Path -Parent $PSScriptRoot
$expected = '06770aeb8855ef942b5dcdb436f84dfdd5867b716ba4b4c8a2c66ade03197f31'
$target = Join-Path $repoPath 'build/rnnoise.dll'
. (Join-Path $PSScriptRoot 'vs.ps1')
if ((Get-BuildArch) -eq 'arm64') {
    # под ARM колеса нет, собираем тот же коммит RNNoise с той же моделью, что внутри pyrnnoise 0.4.5
    $commit = '70f1d256acd4b34a572f999a05c87bf00b67730d'
    $model = '0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37'
    $work = Join-Path ([IO.Path]::GetTempPath()) ('rnnoise-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $work | Out-Null
    try {
        $source = Join-Path $work 'source.tar.gz'
        Invoke-WebRequest "https://codeload.github.com/xiph/rnnoise/tar.gz/$commit" -OutFile $source
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne 'F61EE0B3F4C4CD337303E003D333357C5EAF25EF5D75A742109EE59E9A0A3932') { throw 'RNNoise source checksum does not match' }
        $data = Join-Path $work 'model.tar.gz'
        # имя модели и есть её SHA-256; xiph бывает очень медленным, тогда берём зеркало, откуда качает pyrnnoise
        foreach ($url in "https://media.xiph.org/rnnoise/models/rnnoise_data-$model.tar.gz", "https://modelscope.cn/models/pengzhendong/rnnoise/resolve/master/rnnoise_data-$model.tar.gz") {
            try { Invoke-WebRequest $url -OutFile $data -TimeoutSec 300; break } catch { Write-Warning "$url failed: $_" }
        }
        if (-not (Test-Path -LiteralPath $data) -or (Get-FileHash -LiteralPath $data -Algorithm SHA256).Hash -ne $model) { throw 'RNNoise model checksum does not match' }
        tar -xzf $source -C $work
        if ($LASTEXITCODE -ne 0) { throw 'RNNoise source unpack failed' }
        $root = Join-Path $work "rnnoise-$commit"
        tar -xzf $data -C $root
        if ($LASTEXITCODE -ne 0) { throw 'RNNoise model unpack failed' }
        $files = 'celt_lpc', 'denoise', 'kiss_fft', 'parse_lpcnet_weights', 'pitch', 'rnn', 'rnnoise_data', 'rnnoise_tables', 'nnet', 'nnet_default' | ForEach-Object { "src\$_.c" }
        New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
        $vcvars = Join-Path (Get-VisualStudioPath) 'VC\Auxiliary\Build\vcvarsall.bat'
        cmd /c "call `"$vcvars`" arm64 >nul && cd /d `"$root`" && cl /nologo /O2 /MD /LD /DWIN32 /DDLL_EXPORT /DRNNOISE_BUILD /Iinclude /Isrc $($files -join ' ') /Fe:rnnoise.dll"
        if ($LASTEXITCODE -ne 0) { throw 'RNNoise build failed' }
        Copy-Item -LiteralPath (Join-Path $root 'rnnoise.dll') -Destination $target -Force
    }
    finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Output 'Built build/rnnoise.dll for arm64'
    return
}
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
