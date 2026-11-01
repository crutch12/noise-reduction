#!/bin/sh
set -eu

fail() { printf '%s\n' "$*" >&2; exit 1; }
owner=noise-reduction-user-install-v1
carriage_return=$(printf '\r')
data_home=${XDG_DATA_HOME:-"$HOME/.local/share"}
config_home=${XDG_CONFIG_HOME:-"$HOME/.config"}
root="$data_home/noise-reduction"
launcher="$HOME/.local/bin/noise-reduction"
desktop="$data_home/applications/noise-reduction.desktop"
icon="$data_home/icons/hicolor/256x256/apps/noise-reduction.png"
remove_data=no
wait_pid=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --remove-data) remove_data=yes; shift ;;
        --wait-pid) [ "$#" -ge 2 ] || fail "--wait-pid requires a PID"; wait_pid=$2; shift 2 ;;
        *) fail "Usage: sh uninstall.sh [--wait-pid PID] [--remove-data]" ;;
    esac
done
if [ "$remove_data" = yes ]; then
    for directory in "$data_home/NoiseReduction" "$config_home/noise-reduction"; do
        [ ! -L "$directory" ] || fail "Settings directory is a symlink: $directory"
        if [ -e "$directory" ]; then [ -d "$directory" ] || fail "Settings path is not a directory: $directory"; fi
    done
fi

safe_path() {
    case "$1" in ''|/*|.|..|../*|*/../*|*/..|./*|*/./*|*/.|*//*|*\\*) return 1 ;; esac
    case "$1" in *'
'*|*"$carriage_return"*) return 1 ;; esac
    return 0
}
remove_external() {
    if [ -f "$1" ] && [ ! -L "$1" ] && [ -f "$2" ] && [ ! -L "$2" ]; then
        actual=$(sha256sum -- "$1" | cut -c1-64)
        if [ "$actual" = "$(cat "$2")" ]; then rm -f -- "$1"; fi
    fi
}

for directory in "$HOME" "$data_home" "$config_home"; do
    case "$directory" in /*) ;; *) fail "Installation paths must be absolute" ;; esac
    [ "$directory" != / ] || fail "Installation paths cannot be the filesystem root"
done
[ ! -L "$root" ] && [ -f "$root/.installed" ] && [ ! -L "$root/.installed" ] && [ "$(cat "$root/.installed")" = "$owner" ] || fail "No owned Noise Reduction installation found"
[ ! -L "$root/versions" ] && [ -f "$root/.owned-files" ] && [ ! -L "$root/.owned-files" ] || fail "Invalid installation ownership manifest"
while IFS= read -r target; do
    case "$target" in versions/*) safe_path "$target" || fail "Unsafe version path" ;; *) fail "Unsafe version path" ;; esac
    case "${target#versions/}" in ''|*/*) fail "Invalid version directory" ;; esac
    directory="$root/$target"
    [ ! -L "$directory" ] && [ -f "$directory/.installed" ] && [ "$(cat "$directory/.installed")" = "$owner" ] || fail "Version directory is not owned: $directory"
    [ -z "$(find "$directory" -type l -print -quit)" ] || fail "Version directory contains a symlink: $directory"
    [ -f "$directory/MANIFEST" ] && [ -f "$directory/SHA256SUMS" ] || fail "Version manifest is missing"
    while IFS= read -r file; do safe_path "$file" || fail "Unsafe installed file path"; done < "$directory/MANIFEST"
done < "$root/.owned-files"
if [ -e "$root/current" ] || [ -L "$root/current" ]; then
    [ -L "$root/current" ] || fail "Current installation is not an owned symlink"
    current=$(readlink "$root/current")
    grep -Fxq "$current" "$root/.owned-files" || fail "Current installation points outside the owned versions"
fi

if [ -n "$wait_pid" ]; then
    case "$wait_pid" in *[!0-9]*|'') fail "Invalid process ID" ;; esac
    if kill -0 "$wait_pid" 2>/dev/null; then
        executable=$(readlink "/proc/$wait_pid/exe" 2>/dev/null || true)
        case "$executable" in "$root"/versions/*/NoiseReduction) ;; *) fail "The process is not part of this installation" ;; esac
        attempts=0
        while kill -0 "$wait_pid" 2>/dev/null; do
            attempts=$((attempts + 1))
            [ "$attempts" -le 350 ] || fail "Noise Reduction did not exit; close it and retry"
            sleep 0.1
        done
    fi
fi
if [ -L "$root/current" ] && [ -x "$root/current/NoiseReduction" ]; then
    timeout 35 "$root/current/NoiseReduction" quit || fail "Could not stop Noise Reduction; close it and retry"
fi

remove_external "$desktop" "$root/.desktop.sha256"
remove_external "$icon" "$root/.icon.sha256"
if [ -L "$launcher" ] && [ "$(readlink "$launcher")" = "$root/current/NoiseReduction" ]; then rm -f -- "$launcher"; fi
if [ -L "$root/current" ]; then
    current=$(readlink "$root/current")
    if grep -Fxq "$current" "$root/.owned-files"; then rm -f -- "$root/current"; fi
fi
autostart="$config_home/autostart/noise-reduction.desktop"
if [ -f "$autostart" ] && [ ! -L "$autostart" ] && grep -Fxq "X-NoiseReduction-Owner=$owner" "$autostart"; then rm -f -- "$autostart"; fi
while IFS= read -r target; do
    directory="$root/$target"
    while IFS= read -r file; do
        expected=$(awk -v name="$file" 'substr($0,67)==name { print substr($0,1,64); exit }' "$directory/SHA256SUMS")
        if [ -f "$directory/$file" ] && [ ! -L "$directory/$file" ] && [ "$(sha256sum -- "$directory/$file" | cut -c1-64)" = "$expected" ]; then rm -f -- "$directory/$file"; fi
    done < "$directory/MANIFEST"
    rm -f -- "$directory/.installed" "$directory/MANIFEST" "$directory/SHA256SUMS"
    find "$directory" -depth -type d -exec rmdir -- {} \; 2>/dev/null || true
done < "$root/.owned-files"
rm -f -- "$root/.owned-files" "$root/.installed" "$root/.desktop.sha256" "$root/.icon.sha256"
rmdir -- "$root/versions" "$root" 2>/dev/null || true
if [ "$remove_data" = yes ]; then
    printf '%s\n' "Removing settings from $data_home/NoiseReduction and $config_home/noise-reduction"
    rm -f -- "$data_home/NoiseReduction/settings.json" "$data_home/NoiseReduction/linux.json" "$config_home/noise-reduction/window.json"
    rmdir -- "$data_home/NoiseReduction" "$config_home/noise-reduction" 2>/dev/null || true
fi
printf '%s\n' "Noise Reduction was uninstalled"
