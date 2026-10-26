$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
# .NET из .local у разработчика, иначе установленный в системе, как на машинах GitHub
$dotnet = Join-Path $repoPath '.local/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (!$dotnet) { throw 'Install the .NET 10 SDK in .local/dotnet or system wide' }
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$output = Join-Path $repoPath 'dist/app'
$output = [IO.Path]::GetFullPath($output)
if (!$output.StartsWith([IO.Path]::GetFullPath($repoPath) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish output is outside the repository' }
for ($directory = New-Object IO.DirectoryInfo($output); $directory -ne $null; $directory = $directory.Parent) {
    if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Publish output must not contain reparse points' }
    if ($directory.FullName -eq $repoPath) { break }
}
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
& $dotnet publish (Join-Path $repoPath 'app-winui/NoiseReduction.csproj') -c Release -p:Platform=x64 --packages (Join-Path $repoPath '.local/nuget') -o $output
if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed' }
$compiledResources = Join-Path $repoPath 'app-winui/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64'
foreach ($resource in @('NoiseReduction.pri', 'App.xbf', 'MainWindow.xbf', 'TrayFlyout.xbf')) {
    Copy-Item -LiteralPath (Join-Path $compiledResources $resource) -Destination $output -Force
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$engineSources = @('app-engine/Program.cs', 'app/Core.cs', 'app/SecureControl.cs', 'app/Comparison.cs', 'app/Localization.cs', 'platform/Integration.cs') | ForEach-Object { Join-Path $repoPath $_ }
$engineSources += @(Get-ChildItem -LiteralPath (Join-Path $repoPath 'app/i18n') -Filter '*.cs' | ForEach-Object FullName)
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$(Join-Path $repoPath 'app/assets/NoiseReduction.ico')" "/out:$output/NoiseReduction.Engine.exe" /r:System.Runtime.Serialization.dll /r:System.ServiceProcess.dll /r:System.Windows.Forms.dll $engineSources
if ($LASTEXITCODE -ne 0) { throw 'Engine helper compilation failed' }
Copy-Item -LiteralPath (Join-Path $repoPath 'app/NoiseReduction.exe.config') -Destination "$output/NoiseReduction.Engine.exe.config" -Force
foreach ($library in @('NoiseReduction.Apo.dll', 'rnnoise.dll', 'nr_deepfilter.dll')) {
    Copy-Item -LiteralPath (Join-Path $repoPath "build/$library") -Destination $output -Force
}
Write-Output "Built $output/NoiseReduction.exe"
