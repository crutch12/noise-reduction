$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$labPath = Join-Path $repoPath '.local'
# Rust ставится в .local через rustup-init, как и остальные инструменты сборки
$cargo = Join-Path $labPath 'cargo/bin/cargo.exe'
if (Test-Path -LiteralPath $cargo) { $env:RUSTUP_HOME = Join-Path $labPath 'rustup'; $env:CARGO_HOME = Join-Path $labPath 'cargo' }
else { $cargo = (Get-Command cargo -ErrorAction SilentlyContinue).Source }
if (-not $cargo) { throw 'Install Rust into .local with rustup-init -y --profile minimal --no-modify-path, or system wide' }
$crate = Join-Path $repoPath 'native/deepfilter'
. (Join-Path $PSScriptRoot 'vs.ps1')
$options = @('--locked')
if ((Get-BuildArch) -eq 'arm64') {
    # tract-linalg 0.19 под Windows ARM64 пытается собрать GNU-ассемблер через MASM;
    # в апстриме tract это исправлено так же: без MASM и через clang (sonos/tract 9f6e40611e)
    & $cargo fetch --locked --manifest-path (Join-Path $crate 'Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw 'Cargo fetch failed' }
    $home_ = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $env:USERPROFILE '.cargo' }
    $original = Get-ChildItem (Join-Path $home_ 'registry/src/*/tract-linalg-0.19.16') -Directory | Select-Object -First 1
    if (-not $original) { throw 'tract-linalg 0.19.16 sources are missing' }
    $patched = Join-Path $crate 'target/tract-linalg-arm64'
    Remove-Item -LiteralPath $patched -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath $original.FullName -Destination $patched -Recurse
    $script = Join-Path $patched 'build.rs'
    $find = 'env::var("CARGO_CFG_TARGET_ENV") == Ok("msvc".to_string()) && var("HOST").contains("-windows-")'
    $text = Get-Content -LiteralPath $script -Raw
    if (-not $text.Contains($find)) { throw 'Unexpected tract-linalg build.rs' }
    [IO.File]::WriteAllText($script, $text.Replace($find, $find + ' && env::var("CARGO_CFG_TARGET_ARCH") != Ok("aarch64".to_string())'))
    $clang = Get-Command clang -ErrorAction SilentlyContinue
    if (-not $clang) { $clang = Get-Item (Join-Path (Get-VisualStudioPath) 'VC/Tools/Llvm/ARM64/bin/clang.exe') -ErrorAction SilentlyContinue }
    if (-not $clang) { throw 'clang is required to build DeepFilterNet for arm64' }
    $env:CC_aarch64_pc_windows_msvc = if ($clang.Source) { $clang.Source } else { $clang.FullName }
    # подмена исходника меняет Cargo.lock, поэтому без --locked; версии всё равно берутся из него
    $options = @('--config', ("patch.crates-io.tract-linalg.path='" + $patched.Replace('\', '/') + "'"))
}
& $cargo build --release @options --manifest-path (Join-Path $crate 'Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'DeepFilterNet build failed' }
New-Item -ItemType Directory -Force (Join-Path $repoPath 'build') | Out-Null
Copy-Item -LiteralPath (Join-Path $crate 'target/release/nr_deepfilter.dll') -Destination (Join-Path $repoPath 'build/nr_deepfilter.dll') -Force
Write-Output 'Built build/nr_deepfilter.dll'
