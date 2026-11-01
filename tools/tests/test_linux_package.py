import hashlib
import os
import platform
import shutil
import subprocess
import sys
from pathlib import Path

import pytest


pytestmark = pytest.mark.skipif(sys.platform != 'linux', reason='Linux installer scripts')
ROOT = Path(__file__).resolve().parents[2]
OWNER = 'noise-reduction-user-install-v1'


@pytest.fixture
def environment(tmp_path):
    home = tmp_path / 'home with spaces'
    home.mkdir()
    values = dict(os.environ, HOME=str(home), XDG_DATA_HOME=str(home / 'custom data'),
                  XDG_CONFIG_HOME=str(home / 'custom config'))
    return values


def package(tmp_path, version='1.2.3'):
    folder = tmp_path / ('payload ' + version)
    folder.mkdir()
    for source, target in [('install-linux.sh', 'install.sh'), ('uninstall-linux.sh', 'uninstall.sh')]:
        shutil.copyfile(ROOT / 'tools' / source, folder / target)
    (folder / 'NoiseReduction').write_text('#!/bin/sh\n[ "${1:-}" = "quit" ] && exit 0\n[ "$#" -eq 0 ] || exit 64\nprintf "%s\\n" "' + version + '"\n')
    (folder / 'NoiseReduction').chmod(0o755)
    (folder / 'VERSION').write_text(version + '\n')
    (folder / 'ARCHITECTURE').write_text(platform.machine() + '\n')
    (folder / 'NoiseReduction.png').write_bytes(b'owned icon')
    seal(folder)
    return folder


def seal(folder):
    names = sorted(str(path.relative_to(folder)) for path in folder.rglob('*')
                   if path.is_file() and path.name not in ('MANIFEST', 'SHA256SUMS'))
    (folder / 'MANIFEST').write_text(''.join(name + '\n' for name in names))
    (folder / 'SHA256SUMS').write_text(''.join(
        hashlib.sha256((folder / name).read_bytes()).hexdigest() + '  ' + name + '\n'
        for name in names + ['MANIFEST']))


def run(script, environment, *args):
    return subprocess.run(['sh', str(script), *args], env=environment, text=True,
                          capture_output=True, timeout=20)


def installed(environment):
    return Path(environment['XDG_DATA_HOME']) / 'noise-reduction'


def launcher(environment):
    return Path(environment['HOME']) / '.local/bin/noise-reduction'


def require_install(payload, environment):
    result = run(payload / 'install.sh', environment)
    assert result.returncode == 0, result.stderr + result.stdout
    return installed(environment)


def test_install_produces_stable_launcher_desktop_icon_and_owned_manifest(tmp_path, environment):
    root = require_install(package(tmp_path), environment)
    assert launcher(environment).is_symlink()
    assert (root / 'current').is_symlink()
    assert (root / 'current/.installed').read_text().strip() == OWNER
    assert (root / '.owned-files').is_file()
    assert subprocess.check_output([str(launcher(environment))], env=environment, text=True).strip() == '1.2.3'
    desktop = Path(environment['XDG_DATA_HOME']) / 'applications/noise-reduction.desktop'
    assert 'Exec="' + str(launcher(environment)) + '"' in desktop.read_text()
    assert 'X-NoiseReduction-Owner=' + OWNER in desktop.read_text()
    assert (Path(environment['XDG_DATA_HOME']) / 'icons/hicolor/256x256/apps/noise-reduction.png').read_bytes() == b'owned icon'


def test_update_switches_current_and_bad_hash_keeps_previous_version_launchable(tmp_path, environment):
    root = require_install(package(tmp_path), environment)
    require_install(package(tmp_path, '1.2.4'), environment)
    current = (root / 'current').readlink()
    broken = package(tmp_path, '1.2.5')
    (broken / 'NoiseReduction').write_text('tampered')
    assert run(broken / 'install.sh', environment).returncode != 0
    assert (root / 'current').readlink() == current
    assert subprocess.check_output([str(launcher(environment))], env=environment, text=True).strip() == '1.2.4'


def test_foreign_launcher_is_not_overwritten(tmp_path, environment):
    target = launcher(environment)
    target.parent.mkdir(parents=True)
    target.write_text('foreign launcher')
    assert run(package(tmp_path) / 'install.sh', environment).returncode != 0
    assert target.read_text() == 'foreign launcher'


@pytest.mark.parametrize('target', ['current', 'versions'])
def test_foreign_symlink_inside_install_root_is_rejected(tmp_path, environment, target):
    root = installed(environment)
    root.mkdir(parents=True)
    (root / '.installed').write_text(OWNER + '\n')
    foreign = tmp_path / 'foreign'
    foreign.mkdir()
    (foreign / 'keep.txt').write_text('keep')
    (root / target).symlink_to(foreign, target_is_directory=True)
    assert run(package(tmp_path) / 'install.sh', environment).returncode != 0
    assert (foreign / 'keep.txt').read_text() == 'keep'


def test_manifest_parent_traversal_is_rejected_before_any_install(tmp_path, environment):
    payload = package(tmp_path)
    (payload / 'MANIFEST').write_text('../outside\n')
    (tmp_path / 'outside').write_text('foreign')
    (payload / 'SHA256SUMS').write_text(hashlib.sha256(b'foreign').hexdigest() + '  ../outside\n' +
        hashlib.sha256((payload / 'MANIFEST').read_bytes()).hexdigest() + '  MANIFEST\n')
    assert run(payload / 'install.sh', environment).returncode != 0
    assert not launcher(environment).exists()
    assert (tmp_path / 'outside').read_text() == 'foreign'


def test_payload_symlinks_and_wrong_architecture_are_rejected(tmp_path, environment):
    payload = package(tmp_path)
    (payload / 'NoiseReduction.png').unlink()
    (payload / 'NoiseReduction.png').symlink_to(tmp_path / 'outside')
    assert run(payload / 'install.sh', environment).returncode != 0
    (payload / 'NoiseReduction.png').unlink()
    (payload / 'NoiseReduction.png').write_bytes(b'icon')
    (payload / 'ARCHITECTURE').write_text('unsupported\n')
    seal(payload)
    assert run(payload / 'install.sh', environment).returncode != 0


def test_uninstall_removes_only_owned_files_and_keeps_settings(tmp_path, environment):
    root = require_install(package(tmp_path), environment)
    config = Path(environment['XDG_CONFIG_HOME']) / 'noise-reduction'
    config.mkdir(parents=True)
    (config / 'window.json').write_text('{"width":760}')
    foreign = root / 'current/keep.txt'
    foreign.write_text('foreign file')
    foreign_real = foreign.resolve()
    result = run(root / 'current/uninstall.sh', environment)
    assert result.returncode == 0, result.stderr + result.stdout
    assert not launcher(environment).exists()
    assert not (root / 'current').exists()
    assert foreign_real.read_text() == 'foreign file'
    assert (config / 'window.json').exists()


def test_uninstall_remove_data_is_explicit_and_preserves_foreign_config(tmp_path, environment):
    root = require_install(package(tmp_path), environment)
    config = Path(environment['XDG_CONFIG_HOME']) / 'noise-reduction'
    config.mkdir(parents=True)
    (config / 'window.json').write_text('{}')
    (config / 'keep.txt').write_text('foreign')
    preferences = Path(environment['XDG_DATA_HOME']) / 'NoiseReduction'
    preferences.mkdir()
    (preferences / 'settings.json').write_text('{}')
    (preferences / 'linux.json').write_text('{}')
    result = run(root / 'current/uninstall.sh', environment, '--remove-data')
    assert result.returncode == 0, result.stderr + result.stdout
    assert not (config / 'window.json').exists()
    assert not (preferences / 'settings.json').exists()
    assert not (preferences / 'linux.json').exists()
    assert (config / 'keep.txt').read_text() == 'foreign'


def test_uninstall_preserves_replaced_desktop_and_icon(tmp_path, environment):
    root = require_install(package(tmp_path), environment)
    desktop = Path(environment['XDG_DATA_HOME']) / 'applications/noise-reduction.desktop'
    icon = Path(environment['XDG_DATA_HOME']) / 'icons/hicolor/256x256/apps/noise-reduction.png'
    desktop.write_text('foreign desktop')
    icon.write_bytes(b'foreign icon')
    result = run(root / 'current/uninstall.sh', environment)
    assert result.returncode == 0, result.stderr + result.stdout
    assert desktop.read_text() == 'foreign desktop'
    assert icon.read_bytes() == b'foreign icon'


def test_failed_desktop_conflict_leaves_previous_version_current(tmp_path, environment):
    root = require_install(package(tmp_path), environment)
    previous = (root / 'current').readlink()
    desktop = Path(environment['XDG_DATA_HOME']) / 'applications/noise-reduction.desktop'
    desktop.write_text('foreign desktop')
    result = run(package(tmp_path, '1.2.4') / 'install.sh', environment)
    assert result.returncode != 0
    assert (root / 'current').readlink() == previous
    assert subprocess.check_output([str(launcher(environment))], env=environment, text=True).strip() == '1.2.3'


def test_desktop_quotes_special_home_paths_and_escapes_percent_field_codes(tmp_path, environment):
    special = tmp_path / 'home % " $'
    special.mkdir()
    environment = dict(environment, HOME=str(special), XDG_DATA_HOME=str(special / 'data % " $'),
                       XDG_CONFIG_HOME=str(special / 'config'))
    require_install(package(tmp_path), environment)
    executable = str(launcher(environment))
    quoted = executable.replace('\\', '\\\\').replace('"', '\\"').replace('`', '\\`').replace('$', '\\$')
    quoted = quoted.replace('\\', '\\\\').replace('%', '%%')
    desktop = Path(environment['XDG_DATA_HOME']) / 'applications/noise-reduction.desktop'
    assert 'Exec="' + quoted + '"' in desktop.read_text()
    assert subprocess.check_output([str(launcher(environment))], env=environment, text=True).strip() == '1.2.3'


def test_remove_data_rejects_settings_symlink_before_removing_installation(tmp_path, environment):
    root = require_install(package(tmp_path), environment)
    config = Path(environment['XDG_CONFIG_HOME'])
    config.mkdir()
    foreign = tmp_path / 'foreign config'
    foreign.mkdir()
    (foreign / 'window.json').write_text('keep')
    (config / 'noise-reduction').symlink_to(foreign, target_is_directory=True)
    result = run(root / 'current/uninstall.sh', environment, '--remove-data')
    assert result.returncode != 0
    assert launcher(environment).exists()
    assert (root / 'current/NoiseReduction').exists()
    assert (foreign / 'window.json').read_text() == 'keep'


def test_wait_pid_uninstall_still_stops_the_running_coordinator(tmp_path, environment):
    payload = package(tmp_path)
    (payload / 'NoiseReduction').write_text('#!/bin/sh\n[ "${1:-}" = "quit" ] || exit 64\nprintf stopped > "$HOME/quit-called"\n')
    (payload / 'NoiseReduction').chmod(0o755)
    seal(payload)
    root = require_install(payload, environment)
    result = run(root / 'current/uninstall.sh', environment, '--wait-pid', '99999999')
    assert result.returncode == 0, result.stderr + result.stdout
    assert (Path(environment['HOME']) / 'quit-called').read_text() == 'stopped'
