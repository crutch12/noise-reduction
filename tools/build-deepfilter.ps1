$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$labPath = Join-Path $repoPath '.local'
# Rust ставится в .local через rustup-init, как и остальные инструменты сборки
$env:RUSTUP_HOME = Join-Path $labPath 'rustup'
$env:CARGO_HOME = Join-Path $labPath 'cargo'
$cargo = Join-Path $env:CARGO_HOME 'bin/cargo.exe'
if (-not (Test-Path -LiteralPath $cargo)) { throw 'Install Rust into .local with rustup-init -y --profile minimal --no-modify-path' }
$crate = Join-Path $repoPath 'native/deepfilter'
& $cargo build --release --locked --manifest-path (Join-Path $crate 'Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'DeepFilterNet build failed' }
New-Item -ItemType Directory -Force (Join-Path $repoPath 'build') | Out-Null
Copy-Item -LiteralPath (Join-Path $crate 'target/release/nr_deepfilter.dll') -Destination (Join-Path $repoPath 'build/nr_deepfilter.dll') -Force
Write-Output 'Built build/nr_deepfilter.dll'
