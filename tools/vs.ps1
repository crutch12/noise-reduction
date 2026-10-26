# Пути Visual Studio через vswhere: у разработчика это Build Tools, на машинах GitHub полная Enterprise
# собираем под архитектуру самой машины: x64 на x64, arm64 на Windows для ARM
function Get-BuildArch {
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { return 'arm64' }
    return 'x64'
}

function Get-VisualStudioPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe is missing, install Visual Studio 2022 Build Tools with C++' }
    $component = if ((Get-BuildArch) -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    $path = & $vswhere -latest -products * -requires $component -property installationPath
    if (-not $path) { throw "Visual Studio with C++ $(Get-BuildArch) tools is not installed" }
    return $path
}

function Get-RedistPath {
    $arch = Get-BuildArch
    $redist = Get-ChildItem (Join-Path (Get-VisualStudioPath) 'VC/Redist/MSVC') -Directory | Where-Object Name -Match '^14\.' | Sort-Object { [version]$_.Name } -Descending | Where-Object { Test-Path (Join-Path $_.FullName "$arch/Microsoft.VC143.CRT/vcruntime140.dll") } | Select-Object -First 1
    if (-not $redist) { throw "MSVC $arch redistributable directory is missing" }
    return Join-Path $redist.FullName "$arch/Microsoft.VC143.CRT"
}

# vcruntime140_1.dll бывает не во всех архитектурах, берём то, что есть
function Get-RedistFiles {
    $redist = Get-RedistPath
    return @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') | Where-Object { Test-Path -LiteralPath (Join-Path $redist $_) }
}
