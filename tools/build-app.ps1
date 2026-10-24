param([switch]$Test, [switch]$UiTest, [switch]$NativeTest)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output = Join-Path $repoRoot 'dist/app'
New-Item -ItemType Directory -Force $output | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'app') -Filter '*.cs' | ForEach-Object FullName)
$integration = Join-Path $repoRoot 'platform/Integration.cs'
$icon = Join-Path $repoRoot 'app/assets/NoiseReduction.ico'
if (!(Test-Path -LiteralPath $integration)) { throw 'platform/Integration.cs is required' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$icon" "/out:$output/NoiseReduction.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll /r:System.ServiceProcess.dll $sources $integration
if ($LASTEXITCODE -ne 0) { throw 'App compilation failed' }
foreach ($library in @('NoiseReduction.Apo.dll', 'rnnoise.dll')) {
    $librarySource = Join-Path $repoRoot "build/$library"
    if (Test-Path -LiteralPath $librarySource) { Copy-Item -LiteralPath $librarySource -Destination $output -Force }
}
if ($Test) {
    $testOutput = Join-Path $repoRoot '.local'
    New-Item -ItemType Directory -Force $testOutput | Out-Null
    & $compiler /nologo /target:exe /platform:x64 "/out:$testOutput/CoreTests.exe" /r:System.Runtime.Serialization.dll (Join-Path $repoRoot 'app/Core.cs') (Join-Path $repoRoot 'app/SecureControl.cs') (Join-Path $repoRoot 'app/Comparison.cs') (Join-Path $repoRoot 'app/tests/CoreTests.cs') (Join-Path $repoRoot 'app/tests/SecurityTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
    & "$testOutput/CoreTests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'App tests failed' }
}
Write-Output "Built $output/NoiseReduction.exe"
if ($UiTest) {
    $testOutput = Join-Path $repoRoot '.local'
    New-Item -ItemType Directory -Force $testOutput | Out-Null
    & $compiler /nologo /target:exe /platform:x64 /main:UiTests "/win32icon:$icon" "/out:$testOutput/UiTests.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll /r:System.ServiceProcess.dll $sources $integration (Join-Path $repoRoot 'app/tests/UiTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed' }
    & "$testOutput/UiTests.exe" "$output/NoiseReduction.exe"
    if ($LASTEXITCODE -ne 0) { throw 'UI smoke test failed' }
}
if ($NativeTest) {
    & $compiler /nologo /target:exe /platform:x64 "/out:$output/NativeTests.exe" (Join-Path $repoRoot 'app/Comparison.cs') (Join-Path $repoRoot 'app/tests/NativeTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Native test compilation failed' }
    & "$output/NativeTests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Native comparison test failed' }
}
