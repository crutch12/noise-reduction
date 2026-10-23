$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
Push-Location $repoPath
try {
    & ./.local/venv/Scripts/python.exe -m pytest tools/tests -q
    if ($LASTEXITCODE -ne 0) { throw 'Python tests failed' }
    cmd /c native\build.cmd
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
    Copy-Item -LiteralPath '.local/venv/Lib/site-packages/pyrnnoise/rnnoise.dll' -Destination 'build/rnnoise.dll'
    & ./build/processor_test.exe (Resolve-Path 'build/rnnoise.dll').Path
    if ($LASTEXITCODE -ne 0) { throw 'Native processing tests failed' }
    & ./build/apo_test.exe (Resolve-Path 'build/NoiseReduction.Apo.dll').Path
    if ($LASTEXITCODE -ne 0) { throw 'APO tests failed' }
    & ./tools/build-audition.ps1
    $check = Start-Process -FilePath (Resolve-Path 'dist/comparison/NoiseReduction.Compare.exe').Path -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($check.ExitCode -ne 0) { throw "Audio playback self-test failed: $($check.ExitCode)" }
    Write-Output 'PASS: audio comparison player opens and seeks all variants'
}
finally {
    Pop-Location
}
