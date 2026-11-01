#!/bin/sh
set -eu

fail() { printf '%s\n' "$*" >&2; exit 1; }
owner=noise-reduction-user-install-v1
carriage_return=$(printf '\r')
payload=$(CDPATH= cd "$(dirname "$0")" && pwd -P)
data_home=${XDG_DATA_HOME:-"$HOME/.local/share"}
config_home=${XDG_CONFIG_HOME:-"$HOME/.config"}
root="$data_home/noise-reduction"
launcher="$HOME/.local/bin/noise-reduction"
desktop="$data_home/applications/noise-reduction.desktop"
icon="$data_home/icons/hicolor/256x256/apps/noise-reduction.png"
stage=
desktop_tmp=
icon_tmp=
current_tmp=
owned_tmp=
created_launcher=no
committed=no
prepared=no

cleanup() {
    if [ -n "$stage" ] && [ -d "$stage" ] && [ ! -L "$stage" ]; then rm -rf -- "$stage"; fi
    [ "$prepared" = yes ] || return 0
    for file in "$current_tmp" "$owned_tmp" "$desktop_tmp" "$icon_tmp"; do [ -z "$file" ] || rm -f -- "$file"; done
    if [ "$committed" = no ] && [ "$created_launcher" = yes ] && [ "$(readlink "$launcher" 2>/dev/null || true)" = "$root/current/NoiseReduction" ]; then
        rm -f -- "$launcher"
    fi
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

safe_path() {
    case "$1" in ''|/*|.|..|../*|*/../*|*/..|./*|*/./*|*/.|*//*|*\\*) return 1 ;; esac
    case "$1" in *'
'*|*"$carriage_return"*) return 1 ;; esac
    return 0
}

check_external() {
    if [ -e "$1" ] || [ -L "$1" ]; then
        [ ! -L "$1" ] && [ -f "$1" ] && [ -f "$2" ] && [ ! -L "$2" ] || fail "A foreign file already exists: $1"
        actual=$(sha256sum -- "$1" | cut -c1-64)
        [ "$actual" = "$(cat "$2")" ] || fail "A foreign file already exists: $1"
    fi
}

[ "$#" -eq 0 ] || fail "Usage: sh install.sh"
case "$HOME:$data_home:$config_home" in *'
'*|*"$carriage_return"*) fail "Installation paths cannot contain newlines" ;; esac
for directory in "$HOME" "$data_home" "$config_home"; do
    case "$directory" in /*) ;; *) fail "Installation paths must be absolute" ;; esac
    [ "$directory" != / ] || fail "Installation paths cannot be the filesystem root"
done
case "$launcher" in *=*) fail "The launcher path cannot contain = in a desktop entry" ;; esac
command -v sha256sum >/dev/null 2>&1 || fail "Install coreutils to provide sha256sum"
[ -f "$payload/MANIFEST" ] && [ -f "$payload/SHA256SUMS" ] || fail "Package manifest is missing"
[ -z "$(find "$payload" -type l -print -quit)" ] || fail "Package symlinks are not allowed"
[ -s "$payload/MANIFEST" ] || fail "Package manifest is empty"
while IFS= read -r file; do
    safe_path "$file" || fail "Unsafe package path: $file"
    [ -f "$payload/$file" ] || fail "Package file is missing: $file"
done < "$payload/MANIFEST"
[ "$(sort "$payload/MANIFEST" | uniq -d | wc -l)" -eq 0 ] || fail "Duplicate package paths"
for file in NoiseReduction VERSION ARCHITECTURE NoiseReduction.png install.sh uninstall.sh; do
    grep -Fxq "$file" "$payload/MANIFEST" || fail "Required package file is not owned: $file"
done
LC_ALL=C grep -Eqv '^[0-9a-f]{64}  .+$' "$payload/SHA256SUMS" && fail "Invalid package checksums"
expected=$(cat "$payload/MANIFEST"; printf '%s\n' MANIFEST)
actual=$(cut -c67- "$payload/SHA256SUMS")
[ "$expected" = "$actual" ] || fail "Checksums do not match the package manifest"
(cd "$payload" && sha256sum --check --strict SHA256SUMS >/dev/null) || fail "Package checksum verification failed"
version=$(cat "$payload/VERSION")
case "$version" in ''|*[!A-Za-z0-9._+-]*|*..*) fail "Invalid package version" ;; esac
architecture=$(cat "$payload/ARCHITECTURE")
[ "$architecture" = "$(uname -m)" ] || fail "This package is for $architecture, this machine is $(uname -m)"
case "$architecture" in x86_64|aarch64) ;; *) fail "Unsupported Linux architecture: $architecture" ;; esac

[ ! -L "$root" ] || fail "Install directory is a symlink: $root"
if [ -e "$root" ]; then
    [ -d "$root" ] && [ -f "$root/.installed" ] && [ ! -L "$root/.installed" ] && [ "$(cat "$root/.installed")" = "$owner" ] || fail "Install directory is not owned: $root"
fi
[ ! -L "$root/versions" ] || fail "Version directory is a symlink"
for file in .owned-files .desktop.sha256 .icon.sha256; do
    [ ! -L "$root/$file" ] || fail "Installation metadata is a symlink: $file"
    if [ -e "$root/$file" ]; then [ -f "$root/$file" ] || fail "Invalid installation metadata: $file"; fi
done
if [ -e "$root/current" ] || [ -L "$root/current" ]; then
    [ -L "$root/current" ] || fail "Current installation is not an owned symlink"
    current=$(readlink "$root/current")
    case "$current" in versions/*) safe_path "$current" || fail "Unsafe current installation" ;; *) fail "Current installation points outside the owned versions" ;; esac
    case "${current#versions/}" in ''|*/*) fail "Invalid current version directory" ;; esac
    [ ! -L "$root/$current" ] && [ -f "$root/$current/.installed" ] && [ "$(cat "$root/$current/.installed")" = "$owner" ] || fail "Current version is not owned"
fi
if [ -e "$launcher" ] || [ -L "$launcher" ]; then
    [ -L "$launcher" ] && [ "$(readlink "$launcher")" = "$root/current/NoiseReduction" ] || fail "A foreign launcher already exists: $launcher"
fi
check_external "$desktop" "$root/.desktop.sha256"
check_external "$icon" "$root/.icon.sha256"

mkdir -p "$root/versions" "$(dirname "$launcher")" "$(dirname "$desktop")" "$(dirname "$icon")"
prepared=yes
printf '%s\n' "$owner" > "$root/.installed"
identifier="$version-$architecture-$(sha256sum "$payload/SHA256SUMS" | cut -c1-12)"
target="versions/$identifier"
stage=$(mktemp -d "$root/versions/.stage.XXXXXX")
while IFS= read -r file; do
    mkdir -p "$stage/$(dirname "$file")"
    cp -p -- "$payload/$file" "$stage/$file"
done < "$payload/MANIFEST"
cp -- "$payload/MANIFEST" "$payload/SHA256SUMS" "$stage/"
chmod 755 "$stage/NoiseReduction" "$stage/install.sh" "$stage/uninstall.sh"
printf '%s\n' "$owner" > "$stage/.installed"
if [ -e "$root/$target" ] || [ -L "$root/$target" ]; then
    [ ! -L "$root/$target" ] && [ -f "$root/$target/.installed" ] && [ "$(cat "$root/$target/.installed")" = "$owner" ] || fail "Target version is not owned"
    [ -z "$(find "$root/$target" -type l -print -quit)" ] || fail "The existing version contains a symlink"
    (cd "$root/$target" && sha256sum --check --strict SHA256SUMS >/dev/null) || fail "The existing version has changed"
    rm -rf -- "$stage"
else
    mv -- "$stage" "$root/$target"
fi
stage=

escaped_launcher=$(printf '%s' "$launcher" | sed 's/\\/\\\\\\\\/g; s/"/\\\\"/g; s/`/\\\\`/g; s/\$/\\\\$/g; s/%/%%/g')
desktop_tmp=$(mktemp "$(dirname "$desktop")/.noise-reduction.desktop.XXXXXX")
icon_tmp=$(mktemp "$(dirname "$icon")/.noise-reduction.png.XXXXXX")
cat > "$desktop_tmp" <<EOF
[Desktop Entry]
Type=Application
Name=Noise Reduction
Comment=Local microphone noise reduction
Exec="$escaped_launcher"
Icon=noise-reduction
Terminal=false
Categories=AudioVideo;Audio;Settings;
X-NoiseReduction-Owner=$owner
EOF
cp -- "$payload/NoiseReduction.png" "$icon_tmp"
mv -f -- "$desktop_tmp" "$desktop"
desktop_tmp=
mv -f -- "$icon_tmp" "$icon"
icon_tmp=
sha256sum -- "$desktop" | cut -c1-64 > "$root/.desktop.sha256"
sha256sum -- "$icon" | cut -c1-64 > "$root/.icon.sha256"
if [ ! -L "$launcher" ]; then ln -s "$root/current/NoiseReduction" "$launcher"; created_launcher=yes; fi
owned_tmp=$(mktemp "$root/.owned-files.XXXXXX")
if [ -f "$root/.owned-files" ]; then cat "$root/.owned-files" > "$owned_tmp"; fi
printf '%s\n' "$target" >> "$owned_tmp"
sort -u "$owned_tmp" -o "$owned_tmp"
mv -f -- "$owned_tmp" "$root/.owned-files"
owned_tmp=
current_candidate="$root/.current-$$"
[ ! -e "$current_candidate" ] && [ ! -L "$current_candidate" ] || fail "Temporary current link already exists"
ln -s "$target" "$current_candidate"
current_tmp="$current_candidate"
mv -Tf -- "$current_tmp" "$root/current"
current_tmp=
committed=yes
printf '%s\n' "Installed Noise Reduction $version" "Launcher: $launcher"
