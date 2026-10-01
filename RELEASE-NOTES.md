# MeshCore for Windows 1.0.0

Windows desktop client for MeshCore companion radios, with Bluetooth LE, USB serial and WiFi connections. Includes chats, channels, contacts, maps, radio tools, backups and a demo radio.

## Downloads

- **MeshCore-1.0.0-win-x64.zip** — Intel/AMD Windows PCs.
- **MeshCore-1.0.0-win-arm64.zip** — Windows on ARM.
- **MeshCore-1.0.0-source.zip** — matching Windows source code.
- **SHA256SUMS.txt** — download checksums.

Extract the complete app ZIP and run its executable. Keep the included `LockScreenWidget` folder alongside it to use the optional Windows 11 widget. No separate .NET runtime or source-code folder is needed. Desktop executables are unsigned; the widget uses a self-signed certificate whose installation requires approval.

Windows 10 version 2004 or newer is required; the optional widget needs compatible Windows 11. App data stays in `%APPDATA%\MeshCore` when upgrading.

Based on **MeshCore One by Avi0n and contributors**. Desktop adaptation by **YndreW (Yandres Rivera / WP4TNP)**. Distributed under GPL-3.0 with upstream and third-party notices included. No Apple application source or developer node database is included.

## Validation and limitations

Both architectures built successfully. Protocol tests: 77/77 passed; application tests: 21/22 passed. The remaining test could not protect a saved password through Windows DPAPI in the restricted build environment. Real hardware connections, ARM64 execution and widget installation still need manual verification. See `docs/VALIDATION.md` in the source for details. Publish as a prerelease until these checks pass.
