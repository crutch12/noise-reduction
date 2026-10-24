$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$cscPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$buildPath = Join-Path $repoPath 'build'
$distPath = Join-Path $repoPath 'dist/setup'
New-Item -ItemType Directory -Force $buildPath, $distPath | Out-Null
$payload = @{
    'NoiseReduction.exe' = Join-Path $repoPath 'dist/app/NoiseReduction.exe'
    'NoiseReduction.exe.config' = Join-Path $repoPath 'dist/app/NoiseReduction.exe.config'
    'NoiseReduction.Apo.dll' = Join-Path $buildPath 'NoiseReduction.Apo.dll'
    'rnnoise.dll' = Join-Path $buildPath 'rnnoise.dll'
}
if ((Get-FileHash -LiteralPath $payload['rnnoise.dll'] -Algorithm SHA256).Hash.ToLowerInvariant() -ne '06770aeb8855ef942b5dcdb436f84dfdd5867b716ba4b4c8a2c66ade03197f31') { throw 'RNNoise DLL differs from the licensed pyrnnoise 0.4.5 payload; update provenance and notices before packaging' }
$redistPath = Get-ChildItem 'C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/VC/Redist/MSVC' -Directory | Where-Object Name -Match '^14\.' | Sort-Object Name -Descending | Select-Object -First 1
if (-not $redistPath) { throw 'MSVC x64 redistributable directory is missing' }
foreach ($name in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    $payload[$name] = Join-Path $redistPath.FullName "x64/Microsoft.VC143.CRT/$name"
}
Get-ChildItem (Join-Path $repoPath 'installer/licenses') -File | ForEach-Object { $payload[$_.Name] = $_.FullName }
$payload['THIRD-PARTY-NOTICES.txt'] = Join-Path $repoPath 'installer/THIRD-PARTY-NOTICES.txt'
foreach ($path in $payload.Values) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing package input: $path" } }
$hashLines = $payload.Keys | Sort-Object | ForEach-Object { "$_|$((Get-FileHash -LiteralPath $payload[$_] -Algorithm SHA256).Hash.ToLowerInvariant())" }
$hashPath = Join-Path $buildPath 'payload-sha256.txt'
$hashLines | Set-Content -LiteralPath $hashPath -Encoding Ascii
$payload['payload-sha256.txt'] = $hashPath
$arguments = @('/nologo', '/target:exe', '/platform:x64', '/optimize+', '/warnaserror+', '/r:System.Windows.Forms.dll', '/r:System.ServiceProcess.dll', '/r:Microsoft.CSharp.dll', "/win32icon:$(Join-Path $repoPath 'app/assets/NoiseReduction.ico')", "/win32manifest:$(Join-Path $repoPath 'installer/Setup.manifest')", "/out:$(Join-Path $distPath 'NoiseReduction.Setup.exe')")
foreach ($name in ($payload.Keys | Sort-Object)) { $arguments += "/resource:$($payload[$name]),payload.$name" }
$arguments += Join-Path $repoPath 'platform/Integration.cs'
$arguments += Join-Path $repoPath 'installer/Setup.cs'
& $cscPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed' }
& (Join-Path $distPath 'NoiseReduction.Setup.exe') --self-test --silent
if ($LASTEXITCODE -ne 0) { throw 'Setup self-test failed' }
Write-Output (Join-Path $distPath 'NoiseReduction.Setup.exe')
