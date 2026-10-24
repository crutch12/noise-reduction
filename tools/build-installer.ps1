$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$cscPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$buildPath = Join-Path $repoPath 'build'
$distPath = Join-Path $repoPath 'dist/setup'
New-Item -ItemType Directory -Force $buildPath, $distPath | Out-Null
$appPath = Join-Path $repoPath 'dist/app'
$payload = @{}
foreach ($inputFile in (Get-ChildItem -LiteralPath $appPath -File -Recurse)) {
    $relative = $inputFile.FullName.Substring($appPath.Length + 1).Replace('\', '/')
    if (($inputFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse-point package input: $relative" }
    for ($parent = $inputFile.Directory; $parent -and $parent.FullName.Length -ge $appPath.Length; $parent = $parent.Parent) {
        if (($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse-point package directory: $($parent.FullName)" }
    }
    $extension = $inputFile.Extension.ToLowerInvariant()
    if ($extension -in @('.wav', '.mp3', '.flac', '.ogg', '.m4a', '.aac', '.pcm', '.cs', '.cpp', '.h', '.ps1', '.pdb', '.py', '.zip') -or $inputFile.Name -match '(?i)(^|[._-])(test|tests|settings|recording)([._-]|$)') { throw "Private, source or test file in publish output: $relative" }
    if ($extension -eq '.json' -and $relative -notin @('NoiseReduction.deps.json', 'NoiseReduction.runtimeconfig.json', 'workloads.json', 'workloads.365.json', 'workloads.j32.json', 'workloads.lnl.json', 'workloads.qnn.json', 'workloads.stx.json')) { throw "Unexpected JSON in publish output: $relative" }
    if ($relative -in @('NoiseReduction.Setup.exe', 'installed-files.txt', 'install-transaction.txt', 'install-transaction.txt.pending', 'payload-sha256.txt') -or $relative.EndsWith('.nr-backup', [StringComparison]::OrdinalIgnoreCase)) { throw "Reserved installer input: $relative" }
    $payload[$relative] = $inputFile.FullName
}
foreach ($required in @('NoiseReduction.exe', 'NoiseReduction.dll', 'NoiseReduction.Engine.exe', 'NoiseReduction.Engine.exe.config', 'NoiseReduction.deps.json', 'NoiseReduction.runtimeconfig.json', 'NoiseReduction.pri', 'NoiseReduction.ico', 'Microphone.png')) {
    if (-not $payload.ContainsKey($required)) { throw "Missing WinUI package input: $required" }
}
$payload['NoiseReduction.Apo.dll'] = Join-Path $buildPath 'NoiseReduction.Apo.dll'
$payload['rnnoise.dll'] = Join-Path $buildPath 'rnnoise.dll'
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
# открытый установщик держит файл, тогда собираем рядом с отметкой времени
$setupPath = Join-Path $distPath 'NoiseReduction.Setup.exe'
try { if (Test-Path -LiteralPath $setupPath) { [IO.File]::Open($setupPath, 'Open', 'ReadWrite', 'None').Dispose() } }
catch { $setupPath = Join-Path $distPath ('NoiseReduction.Setup.' + (Get-Date -Format 'HHmmss') + '.exe'); Write-Warning "NoiseReduction.Setup.exe is in use, building $setupPath" }
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warnaserror+', '/r:System.Windows.Forms.dll', '/r:System.ServiceProcess.dll', '/r:Microsoft.CSharp.dll', "/win32icon:$(Join-Path $repoPath 'app/assets/NoiseReduction.ico')", "/win32manifest:$(Join-Path $repoPath 'installer/Setup.manifest')", "/out:$setupPath")
foreach ($name in ($payload.Keys | Sort-Object)) { $arguments += "/resource:$($payload[$name]),payload.$name" }
$arguments += Join-Path $repoPath 'platform/Integration.cs'
$arguments += Join-Path $repoPath 'installer/Setup.cs'
$responsePath = Join-Path $buildPath 'setup.rsp'
$arguments | ForEach-Object { '"' + $_ + '"' } | Set-Content -LiteralPath $responsePath -Encoding UTF8
& $cscPath "@$responsePath"
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed' }
$selfTestLog = Join-Path $buildPath 'setup-self-test.txt'
$selfTest = Start-Process -FilePath $setupPath -ArgumentList '--self-test', '--silent' -Wait -PassThru -NoNewWindow -RedirectStandardOutput $selfTestLog -RedirectStandardError "$selfTestLog.err"
Get-Content -LiteralPath $selfTestLog, "$selfTestLog.err"
if ($selfTest.ExitCode -ne 0) { throw 'Setup self-test failed' }
Write-Output $setupPath
