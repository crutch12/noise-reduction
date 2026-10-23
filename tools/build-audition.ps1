$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $repoPath 'dist/comparison'
$audioPath = Join-Path $outputPath 'audio'
New-Item -ItemType Directory -Force $audioPath | Out-Null
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$exePath = Join-Path $outputPath 'NoiseReduction.Compare.exe'
$sourcePath = Join-Path $PSScriptRoot 'Audition.cs'
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$exePath" $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'Comparison tool compilation failed' }
$names = @('original.wav', 'rnnoise-24db.wav', 'rnnoise-full.wav', 'deepfilter-24db.wav', 'deepfilter-100db.wav')
foreach ($name in $names) {
    Copy-Item -LiteralPath (Join-Path $repoPath ".local/comparison/$name") -Destination $audioPath
}
Copy-Item -LiteralPath (Join-Path $repoPath '.local/comparison/report.json') -Destination $outputPath
Write-Output "$outputPath/NoiseReduction.Compare.exe"
