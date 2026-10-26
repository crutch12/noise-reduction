# Пути Visual Studio через vswhere: у разработчика это Build Tools, на машинах GitHub полная Enterprise
function Get-VisualStudioPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe is missing, install Visual Studio 2022 Build Tools with C++' }
    $path = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $path) { throw 'Visual Studio with C++ x64 tools is not installed' }
    return $path
}

function Get-RedistPath {
    $redist = Get-ChildItem (Join-Path (Get-VisualStudioPath) 'VC/Redist/MSVC') -Directory | Where-Object Name -Match '^14\.' | Sort-Object { [version]$_.Name } -Descending | Where-Object { Test-Path (Join-Path $_.FullName 'x64/Microsoft.VC143.CRT/vcruntime140.dll') } | Select-Object -First 1
    if (-not $redist) { throw 'MSVC x64 redistributable directory is missing' }
    return Join-Path $redist.FullName 'x64/Microsoft.VC143.CRT'
}
