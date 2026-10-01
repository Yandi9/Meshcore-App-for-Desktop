# MeshCore for Windows

Windows desktop adaptation of [MeshCore One](https://github.com/Avi0n/MeshCoreOne), maintained by **YndreW**.

Connect a MeshCore companion radio over Bluetooth LE, USB serial, or WiFi. Includes messaging, channels, contacts, maps, radio tools, backups, a demo radio, and an optional Windows lock screen widget.

## Download — version 1.0.0

Download the ZIP matching your PC. These links become available when release `v1.0.0` is published:

| Computer | Download |
|---|---|
| Intel / AMD Windows PC | [Windows x64](https://github.com/Yandi9/Meshcore-App-for-Desktop/releases/download/v1.0.0/MeshCore-1.0.0-win-x64.zip) |
| Windows on ARM | [Windows ARM64](https://github.com/Yandi9/Meshcore-App-for-Desktop/releases/download/v1.0.0/MeshCore-1.0.0-win-arm64.zip) |

Extract the entire ZIP, then run the matching MeshCore executable. Keep `LockScreenWidget` beside it. The app needs Windows 10 version 2004 or newer; the optional widget needs a compatible Windows 11 installation. No separate .NET runtime is needed for self-contained builds.

The widget installer asks for permission to trust its self-signed certificate and install its Windows runtime. Install it from Settings → Notifications → Lock screen. MeshCore must remain running to supply widget status.

See the [Windows guide](docs/WINDOWS-GUIDE.md) for connections, features, and setup. See [validation results](docs/VALIDATION.md) for the build and test results. Real-radio behavior and widget installation require hardware testing.

## Build and test

Install the .NET 10 SDK. Run these commands from the repository root:

```powershell
dotnet test tests/MeshCore.Tests -c Release
dotnet test tests/MC1.Core.Tests -c Release
./publish.ps1
```

`publish.ps1` builds both Windows architectures into `dist`. Widget packaging is separate: run `packaging/widget/build.sh` in WSL/Linux with .NET 10, Python 3, openssl, osslsigncode, curl and unzip. Keep the signing private key outside the repository. Preserve the existing widget package version, 1.2.0.0, for upgrade compatibility; the desktop release version is 1.0.0.

The manual GitHub Actions workflow tests and builds the desktop app. It does not publish a release or sign/rebuild the widget.

## Source and versions

[Source code](https://github.com/Yandi9/Meshcore-App-for-Desktop) · [Releases](https://github.com/Yandi9/Meshcore-App-for-Desktop/releases) · [Issues](https://github.com/Yandi9/Meshcore-App-for-Desktop/issues)

Use a Git tag and GitHub Release for each public version (first tag: `v1.0.0`). Local preparation folders keep each version's source and download packages separately. Upload the contents of the source folder to the repository; upload large download ZIPs as release assets.

## Credits and license

Based on **MeshCore One by Avi0n and contributors**, distributed under GPL-3.0. This Windows adaptation retains that license; see [LICENSE](LICENSE). The upstream Swift MeshCore library is credited for protocol work, with its supplied MIT notice preserved in [licenses](licenses). No Swift source, Xcode projects, iPhone app, or Apple widget code is included.

Thanks to [MeshCore and its contributors](https://github.com/meshcore-dev/MeshCore), [meshcore_py](https://github.com/meshcore-dev/meshcore_py), Avalonia and the other libraries listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Original names and attribution remain to acknowledge the app's history. This is an independent desktop adaptation.
