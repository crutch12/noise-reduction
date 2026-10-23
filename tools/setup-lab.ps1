$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$labPath = Join-Path $repoPath '.local'
$pythonPath = Join-Path $labPath 'venv/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $pythonPath)) {
    uv venv --python 3.11 (Join-Path $labPath 'venv')
    if ($LASTEXITCODE -ne 0) { throw 'Python environment setup failed' }
}
uv pip install --python $pythonPath -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed' }
$binaryPath = Join-Path $labPath 'bin/deep-filter.exe'
$expectedHash = '75e11fa16445f560cb6b021521ddb89e89270d13b83089705d98776f58fd7915'
New-Item -ItemType Directory -Force (Split-Path -Parent $binaryPath) | Out-Null
if (-not (Test-Path -LiteralPath $binaryPath)) {
    Invoke-WebRequest 'https://github.com/Rikorose/DeepFilterNet/releases/download/v0.5.6/deep-filter-0.5.6-x86_64-pc-windows-msvc.exe' -OutFile $binaryPath
}
if ((Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'DeepFilterNet checksum mismatch'
}
Get-Command ffmpeg -ErrorAction Stop | Out-Null
Write-Output 'Audio comparison environment is ready'
