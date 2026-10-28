$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$testOutput = Join-Path $repoRoot '.local'
New-Item -ItemType Directory -Force $testOutput | Out-Null
& $compiler /nologo /target:exe /platform:x64 "/out:$testOutput/CoreTests.exe" /r:System.Runtime.Serialization.dll (Join-Path $repoRoot 'app/Core.cs') (Join-Path $repoRoot 'app/SecureControl.cs') (Join-Path $repoRoot 'app/Comparison.cs') (Join-Path $repoRoot 'app/Localization.cs') @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'app/i18n') -Filter '*.cs' | ForEach-Object FullName) (Join-Path $repoRoot 'app/tests/CoreTests.cs') (Join-Path $repoRoot 'app/tests/SecurityTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& "$testOutput/CoreTests.exe"
if ($LASTEXITCODE -ne 0) { throw 'App tests failed' }
& $compiler /nologo /target:exe /platform:x64 "/out:$testOutput/NativeTests.exe" (Join-Path $repoRoot 'app/Comparison.cs') (Join-Path $repoRoot 'app/tests/NativeTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native test compilation failed' }
foreach ($library in @('NoiseReduction.Apo.dll', 'rnnoise.dll', 'nr_deepfilter.dll')) {
    $librarySource = Join-Path $repoRoot "build/$library"
    if (Test-Path -LiteralPath $librarySource) { Copy-Item -LiteralPath $librarySource -Destination $testOutput -Force }
}
& "$testOutput/NativeTests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Native comparison test failed' }
