# Noise Reduction for Windows

System-wide AI noise suppression for real Windows microphones, without a virtual microphone

Noise Reduction removes background noise from your microphone in every program: Teams, Zoom, Discord, Telegram, browsers, recorders and games. It does not add a virtual device you have to pick in each app. Instead it installs a Windows audio processing object (APO), so the noise suppression runs inside the Windows audio engine for the microphone itself

## Features

- Works in all apps at once, including Chrome and other Chromium based apps
- Per microphone settings: on or off, model, strength, low cut filter, economy mode for multichannel arrays
- Two local models: Fast (RNNoise, default) and Precise (DeepFilterNet 3)
- Optional switch to the Fast model while a laptop runs on battery
- Strength presets tuned per model, plus a custom slider
- Test with your own voice: record a phrase, listen to the original and the processed version, change the model or strength while listening
- Tray icon with a quick panel, Ctrl+Alt+N to pause and resume noise reduction
- Notification when a new microphone is connected
- USB, built in and Bluetooth microphones, sample rates from 8 to 48 kHz
- 20 interface languages
- Clean uninstall: every change to Windows audio settings is journaled and restored

## Requirements

- Windows 10 version 2004 or newer, or Windows 11, x64
- Windows 11 on ARM64 (Snapdragon and other ARM processors)
- Administrator rights for installation and for turning noise reduction on for a microphone

On Windows 11 24H2 and newer, microphones that Windows manages through audio effect packs (most USB and Bluetooth microphones) get Noise Reduction as an effect pack. On older systems the effect is added to the microphone's effect chain directly

## Download

### winget

```
winget install crutch12.NoiseReduction
```

winget picks the right installer for your processor. To update later run `winget upgrade crutch12.NoiseReduction`, to remove `winget uninstall crutch12.NoiseReduction`

### Installer

Download the latest installer from [GitHub Releases](https://github.com/crutch12/noise-reduction/releases): `x64-setup.exe` for Intel and AMD processors, `arm64-setup.exe` for ARM

The installer is not code signed yet, so Windows may show a SmartScreen warning when you run a file downloaded in the browser. Click More info, then Run anyway. winget installs without this warning

### macOS (preview)

macOS 11 or newer. Download `macos-arm64.zip` for Apple Silicon or `macos-x64.zip` for Intel, unzip and move Noise Reduction to Applications

The app is not signed by Apple, so the first launch is blocked. Open System Settings > Privacy & Security and click Open Anyway, or run:

```
xattr -dr com.apple.quarantine "/Applications/Noise Reduction.app"
```

On the first launch click Install driver and enter your password. This adds the Noise Reduction microphone to the system. Then choose your real microphone in the app and the Noise Reduction microphone in Zoom, Discord or any other app

To remove the driver:

```
sudo rm -rf /Library/Audio/Plug-Ins/HAL/NoiseReduction.driver && sudo killall coreaudiod
```

### Linux (preview)

Needs PulseAudio or PipeWire with `pactl` (package `pulseaudio-utils` on Debian and Ubuntu). Download `linux-x64.tar.gz` or `linux-arm64.tar.gz` and run:

```
tar -xzf NoiseReduction-*-linux-x64.tar.gz
./noise-reduction/NoiseReduction
```

The app adds the Noise Reduction microphone while it runs. Choose it in Zoom, Discord or any other app. On GNOME the tray icon needs the AppIndicator extension

### Verifying a download

Installers are built by GitHub Actions from this repository ([build.yml](.github/workflows/build.yml)). Starting with the release after 1.1.2 each one also has a build attestation. To check that a file you downloaded is exactly what the build produced, run with [GitHub CLI](https://cli.github.com):

```
gh attestation verify NoiseReduction-<version>-x64-setup.exe --repo crutch12/noise-reduction
```

The release also lists SHA256 checksums in `SHA256SUMS.txt`

## How to use

1. Run the installer and confirm the installation
2. Open Noise Reduction from the tray or the Start menu
3. Turn on noise reduction for your microphone. Windows asks for administrator rights once per microphone
4. Optionally pick the model and strength, and check the result with "Test with your voice"

To uninstall, use "Uninstall the app" in the settings window or Windows Settings, Apps. Uninstalling restores the original microphone effects

## How it works

- `native/` is the APO, a COM DLL loaded by the Windows audio engine (`audiodg.exe`). It runs the model on 48 kHz audio, resamples other rates, mixes multichannel arrays when needed and reads its settings from a small shared memory file per microphone
- `native/deepfilter/` is a Rust wrapper around DeepFilterNet with a C interface that never lets a panic reach the audio engine
- `platform/` attaches and detaches the effect: registry based effect chains, Windows 11 effect packs, a journal that can roll everything back after a crash
- `app-winui/` is the WinUI 3 settings window and tray app
- `app-engine/` is a small elevated helper that applies and verifies changes for one microphone
- `installer/` is the setup program and the effect pack driver package

The engine verifies every change: after attaching the effect it records a short capture and checks that the model actually processed audio, otherwise the change is rolled back

## Building

Requirements:

- Visual Studio 2022 or Build Tools with C++ x64 or ARM64 tools, ATL and the Windows SDK 10.0.26100
- .NET 10 SDK
- Rust stable (MSVC toolchain)
- Python 3, only for the translation check

```powershell
./tools/fetch-rnnoise.ps1         # rnnoise.dll from pyrnnoise 0.4.5, verified by SHA-256
./tools/build-deepfilter.ps1      # nr_deepfilter.dll
cmd /c native\build.cmd           # APO and native tests
./tools/build-winui.ps1           # app, output in dist/app
./tools/build-installer.ps1       # installer, output in dist/setup
```

Local builds sign the effect pack catalog with a temporary self-signed certificate. Release builds in GitHub Actions are signed through SignPath

Tests:

```powershell
./build/resampler_test.exe
./build/processor_test.exe (Resolve-Path build/rnnoise.dll).Path
./build/apo_test.exe (Resolve-Path build/NoiseReduction.Apo.dll).Path
./build/control_test.exe
./build/windows_control_test.exe (Resolve-Path build/NoiseReduction.Apo.dll).Path
python tools/check-translations.py
```

macOS and Linux need a C++ compiler, Rust, .NET 10 SDK and, on macOS, CMake:

```sh
sh tools/build-native.sh test     # bridge, models and native tests
sh tools/package-macos.sh 1.2.0   # app with the driver, output in dist/macos
sh tools/package-linux.sh 1.2.0   # archive, output in dist/linux
```

Linux release builds run in the `rust:1.98-bullseye` image so they work on glibc 2.31 and newer

The version comes from the git tag `vX.Y.Z`. Pushing such a tag builds a signed draft release

## Translations

Interface strings live in `app/Localization.cs` (Russian source and English) and `app/i18n/*.cs` (other languages). `tools/check-translations.py` checks that every language has every string

## Limitations

- Apps that open the microphone in exclusive mode or through ASIO bypass Windows audio effects
- Turning noise reduction on for a microphone also makes apps that ask for raw unprocessed audio receive processed audio, as the microphone stops advertising raw mode while noise reduction is on
- Precise model uses about twice the CPU of the Fast model

## Credits

- [RNNoise](https://github.com/xiph/rnnoise) by Xiph.Org, BSD 3-Clause
- [pyrnnoise](https://github.com/pengzhendong/pyrnnoise) builds of RNNoise, Apache 2.0
- [DeepFilterNet](https://github.com/Rikorose/DeepFilterNet), MIT or Apache 2.0
- [Windows audio processing objects](https://learn.microsoft.com/windows-hardware/drivers/audio/windows-audio-processing-objects)

## Privacy

This program will not transfer any information to other networked systems. Microphone audio is processed locally and test recordings stay in a temporary folder until the app closes

## License

[MIT](LICENSE). Third party components keep their own licenses, see [installer/THIRD-PARTY-NOTICES.txt](installer/THIRD-PARTY-NOTICES.txt)
