$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
Push-Location $repoPath
try {
    $pytestTemp = Join-Path '.local' ('pytest-' + [guid]::NewGuid().ToString('N'))
    & ./.local/venv/Scripts/python.exe -m pytest tools/tests -q -p no:cacheprovider --basetemp $pytestTemp
    if ($LASTEXITCODE -ne 0) { throw 'Python tests failed' }
    cmd /c native\build.cmd
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
    Copy-Item -LiteralPath '.local/venv/Lib/site-packages/pyrnnoise/rnnoise.dll' -Destination 'build/rnnoise.dll'
    & ./tools/build-deepfilter.ps1
    & ./build/processor_test.exe (Resolve-Path 'build/rnnoise.dll').Path
    if ($LASTEXITCODE -ne 0) { throw 'Native processing tests failed' }
    & ./build/apo_test.exe (Resolve-Path 'build/NoiseReduction.Apo.dll').Path
    if ($LASTEXITCODE -ne 0) { throw 'APO tests failed' }
    & ./build/control_test.exe
    if ($LASTEXITCODE -ne 0) { throw 'Shared control tests failed' }
    & ./build/windows_control_test.exe (Resolve-Path 'build/NoiseReduction.Apo.dll').Path
    if ($LASTEXITCODE -ne 0) { throw 'Windows initialization tests failed' }
    foreach ($fault in 'lock', 'process') {
        & ./build/fault_test.exe (Resolve-Path 'build/fault/NoiseReduction.Apo.dll').Path $fault
        if ($LASTEXITCODE -ne 0) { throw "Fault test $fault failed" }
    }
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    & $compiler /nologo /platform:x64 /warnaserror+ /r:System.ServiceProcess.dll /out:build\IntegrationTests.exe platform\Integration.cs platform\IntegrationTests.cs
    if ($LASTEXITCODE -ne 0) { throw 'Integration test compilation failed' }
    & ./build/IntegrationTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Registry integration tests failed' }
    & $compiler /nologo /platform:x64 /warnaserror+ /out:build\CaptureSessionsTests.exe platform\CaptureSessions.cs platform\CaptureSessionsTests.cs
    if ($LASTEXITCODE -ne 0) { throw 'Capture session test compilation failed' }
    & ./build/CaptureSessionsTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Capture session tests failed' }
    & ./tools/test-app.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Desktop tests failed' }
    & ./tools/build-installer.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Installer verification failed' }
    & ./tools/build-audition.ps1
    $check = Start-Process -FilePath (Resolve-Path 'dist/comparison/NoiseReduction.Compare.exe').Path -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($check.ExitCode -ne 0) { throw "Audio playback self-test failed: $($check.ExitCode)" }
    Write-Output 'PASS: audio comparison player opens and seeks all variants'
}
finally {
    Pop-Location
}
