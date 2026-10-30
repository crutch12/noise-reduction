# Noise Reduction

System-wide AI noise suppression for microphones on Windows, macOS and Linux

Noise Reduction removes background noise from your microphone in every program: Teams, Zoom, Discord, Telegram, browsers, recorders and games. Audio is processed locally on your computer

- **Windows:** noise suppression runs inside the Windows audio engine for the microphone itself, as an audio processing object (APO). There is no virtual device to pick in each app
- **macOS and Linux (preview):** the app adds a virtual microphone called Noise Reduction. Pick it in your apps or make it the default microphone

## Features

| | Windows | macOS | Linux |
|---|---|---|---|
| Fast (RNNoise) and Precise (DeepFilterNet 3) local models | Yes | Yes | Yes |
| Strength presets and a custom slider, low cut filter | Yes | Yes | Yes |
| Tray or menu bar icon | Quick panel | Quick panel | Menu |
| 20 interface languages, start at sign in | Yes | Yes | Yes |
| Settings for each microphone | Yes | Yes | Yes |
| Microphones processed at the same time | All turned on | One source | One source |
| Test with your own voice, changing settings while listening | Yes | Yes | No |
| Switch to the Fast model on battery | Yes | Yes | No |
| Window and tray themes | Yes | Yes | No |
| Shortcut to pause and resume | Ctrl+Alt+N | Control+Option+N | No |
| Icons of the apps using the microphone | Yes | macOS 14.2 or newer | No |
| Works without picking a virtual microphone | Yes | No | No |

Windows also has an economy mode for multichannel microphone arrays and a notification when a new microphone is connected

If noise reduction fails, your microphone keeps working: Windows and the virtual microphone on macOS and Linux pass the original sound through, and the app shows an error

## Requirements

**Windows**

- Windows 10 version 2004 or newer, or Windows 11, x64
- Windows 11 on ARM64 (Snapdragon and other ARM processors)
- Administrator rights for installation and for turning noise reduction on for a microphone

On Windows 11 24H2 and newer, microphones that Windows manages through audio effect packs (most USB and Bluetooth microphones) get Noise Reduction as an effect pack. On older systems the effect is added to the microphone's effect chain directly

**macOS**

- macOS 13 or newer, Apple Silicon or Intel
- Administrator password to install the virtual microphone driver

**Linux**

- x64 or ARM64 with glibc 2.31 or newer, for example Ubuntu 20.04 or Debian 11
- PulseAudio or PipeWire with `pactl` (package `pulseaudio-utils` on Debian and Ubuntu)

## Download

All files are on [GitHub Releases](https://github.com/crutch12/noise-reduction/releases)

### Windows

With winget:

```
winget install crutch12.NoiseReduction
```

winget picks the right installer for your processor. To update later run `winget upgrade crutch12.NoiseReduction`, to remove `winget uninstall crutch12.NoiseReduction`

Or download the installer: `x64-setup.exe` for Intel and AMD processors, `arm64-setup.exe` for ARM

The installer is not code signed yet, so Windows may show a SmartScreen warning when you run a file downloaded in the browser. Click More info, then Run anyway. winget installs without this warning

### macOS (preview)

Download `macos-arm64.zip` for Apple Silicon or `macos-x64.zip` for Intel, unzip and move Noise Reduction to Applications

The app is not signed by Apple, so the first launch is blocked. Open System Settings > Privacy & Security and click Open Anyway, or run:

```
xattr -dr com.apple.quarantine "/Applications/Noise Reduction.app"
```

### Linux (preview)

Download `linux-x64.tar.gz` or `linux-arm64.tar.gz` and run:

```
tar -xzf NoiseReduction-*-linux-x64.tar.gz
./noise-reduction/NoiseReduction
```

On GNOME the tray icon needs the AppIndicator extension

### Verifying a download

Release files are built by GitHub Actions from this repository ([build.yml](.github/workflows/build.yml)). Windows installers have a build attestation starting with the release after 1.1.2, macOS and Linux files starting with 1.1.7. To check that a file you downloaded is exactly what the build produced, run with [GitHub CLI](https://cli.github.com):

```
gh attestation verify <file> --repo crutch12/noise-reduction
```

The release also lists SHA256 checksums in `SHA256SUMS.txt`

## How to use

### Windows

1. Run the installer and confirm the installation
2. Open Noise Reduction from the tray or the Start menu
3. Turn on noise reduction for your microphone. Windows asks for administrator rights once per microphone
4. Optionally pick the model and strength, and check the result with "Test with your voice"

To uninstall, use "Uninstall the app" in the settings window or Windows Settings, Apps. Uninstalling restores the original microphone effects

### macOS

1. Open Noise Reduction, click "Install driver" and enter your password. This adds the Noise Reduction microphone and restarts system audio for a moment
2. Click "Make it the default microphone", or choose Noise Reduction in Zoom, Discord or any other app
3. Allow microphone access when macOS asks
4. Optionally pick the source microphone, model and strength, and check the result with "Test with your voice"

Your real microphone is only opened while an app uses the Noise Reduction microphone

To uninstall, use "Uninstall the app" in the settings window. It removes the driver and moves the app to the Trash

### Linux

1. Run the app. It adds the Noise Reduction microphone through `pactl`
2. Choose Noise Reduction in Zoom, Discord or any other app
3. Optionally pick the source microphone, model and strength

Your real microphone is only opened while an app uses the Noise Reduction microphone. To uninstall, delete the folder and turn off "Start when I sign in" first if you turned it on

## How it works

Shared by all platforms:

- `native/processor.cpp` and the model backends run the noise suppression on 48 kHz audio, `native/deepfilter/` is a Rust wrapper around DeepFilterNet with a C interface that never lets a panic reach the audio engine
- `app/` holds the settings format and the translations

Windows:

- `native/` is the APO, a COM DLL loaded by the Windows audio engine (`audiodg.exe`). It runs the model on 48 kHz audio, resamples other rates, mixes multichannel arrays when needed and reads its settings from a small shared memory file per microphone
- `platform/` attaches and detaches the effect: registry based effect chains, Windows 11 effect packs, a journal that can roll everything back after a crash
- `app-winui/` is the WinUI 3 settings window and tray app
- `app-engine/` is a small elevated helper that applies and verifies changes for one microphone
- `installer/` is the setup program and the effect pack driver package

The engine verifies every change: after attaching the effect it records a short capture and checks that the model actually processed audio, otherwise the change is rolled back

macOS and Linux:

- `native/bridge.cpp` captures the real microphone with miniaudio, processes it and writes it to the virtual device. It only runs while an app uses the virtual microphone
- `native/macos/driver/` is the macOS audio driver on libASPL: a visible Noise Reduction microphone and a hidden output the bridge writes to
- `app-macos/` is the native macOS app on SwiftUI
- `app-avalonia/` is the Linux app on Avalonia. It creates the virtual microphone with `module-null-sink` and `module-remap-source`

## Building

### Windows

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

Local builds sign the effect pack catalog with a temporary self-signed certificate. Release builds are not code signed yet

Tests:

```powershell
./build/resampler_test.exe
./build/processor_test.exe (Resolve-Path build/rnnoise.dll).Path
./build/apo_test.exe (Resolve-Path build/NoiseReduction.Apo.dll).Path
./build/control_test.exe
./build/windows_control_test.exe (Resolve-Path build/NoiseReduction.Apo.dll).Path
python tools/check-translations.py
```

### macOS and Linux

macOS needs Xcode 26, CMake, Rust and Python 3. Liquid Glass only appears in builds made with the macOS 26 SDK. Linux needs a C++ compiler, Rust and the .NET 10 SDK

```sh
sh tools/build-native.sh test     # bridge, models and native tests
sh tools/package-macos.sh 1.2.0   # app with the driver, output in dist/macos
sh tools/package-linux.sh 1.2.0   # archive, output in dist/linux
```

Linux release builds run in the `rust:1.98-bullseye` image so they work on glibc 2.31 and newer

### Releases

The version comes from the git tag `vX.Y.Z`. Pushing such a tag builds all platforms and publishes a release

## Translations

Interface strings live in `app/Localization.cs` (Russian source and English) and `app/i18n/*.cs` (other languages). `tools/check-translations.py` checks that every language has every string

## Limitations

Windows:

- Apps that open the microphone in exclusive mode or through ASIO bypass Windows audio effects
- Turning noise reduction on for a microphone also makes apps that ask for raw unprocessed audio receive processed audio, as the microphone stops advertising raw mode while noise reduction is on

macOS and Linux (preview):

- Only one source microphone is processed at a time, because there is one virtual microphone. Each microphone keeps its own settings for when it becomes the source
- The virtual microphone adds a small delay of about 30 ms on top of the model
- macOS: the app and the driver are not signed by Apple. Without that signature the driver installation cannot verify who built the driver, so install it only from this repository's releases
- Linux: tested on PulseAudio. PipeWire and the tray on real desktops are not fully tested yet. After the app is killed, the virtual devices stay until the audio server restarts and are reused on the next start

All platforms:

- Precise model uses about twice the CPU of the Fast model

## Credits

- [RNNoise](https://github.com/xiph/rnnoise) by Xiph.Org, BSD 3-Clause
- [pyrnnoise](https://github.com/pengzhendong/pyrnnoise) builds of RNNoise, Apache 2.0
- [DeepFilterNet](https://github.com/Rikorose/DeepFilterNet), MIT or Apache 2.0
- [miniaudio](https://github.com/mackron/miniaudio), MIT-0
- [libASPL](https://github.com/gavv/libASPL), MIT
- [Avalonia](https://github.com/AvaloniaUI/Avalonia), MIT
- [Windows audio processing objects](https://learn.microsoft.com/windows-hardware/drivers/audio/windows-audio-processing-objects)

## Privacy

This program will not transfer any information to other networked systems. Microphone audio is processed locally. On Windows test recordings stay in a temporary folder until the app closes, on macOS they stay in memory until the settings window closes

## License

[MIT](LICENSE). Third party components keep their own licenses, see [installer/THIRD-PARTY-NOTICES.txt](installer/THIRD-PARTY-NOTICES.txt) and Credits above
