# Версия сборки: из тега vX.Y.Z в CI (NR_VERSION), иначе из последнего тега в git, иначе 1.1.0
function Get-BuildVersion {
    $value = $env:NR_VERSION
    if (-not $value) { $value = (& git -C (Split-Path -Parent $PSScriptRoot) describe --tags --abbrev=0 --match 'v[0-9]*' 2>$null) }
    if (-not $value) { $value = '1.1.0' }
    $value = $value.Trim().TrimStart('v')
    if ($value -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3, got '$value'" }
    return $value
}
