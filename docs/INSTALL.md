# Installing MeshCore for Windows 1.0.0

1. Choose `MeshCore-1.0.0-win-x64.zip` for Intel/AMD PCs, or `MeshCore-1.0.0-win-arm64.zip` for Windows on ARM.
2. Extract the entire ZIP to a folder you control.
3. Open `MeshCore-x64.exe` or `MeshCore-arm64.exe`. The app is self-contained; you do not need the source files or a separate .NET runtime.

The desktop executables are unsigned. Windows may show an unknown-publisher or SmartScreen warning. Download only from the repository's release page; SHA256SUMS.txt lets you verify that your download matches the published files.

Windows 10 version 2004 or newer is required. Bluetooth needs a compatible Bluetooth LE adapter. A MeshCore companion radio is needed for real mesh use; the built-in Demo option works without hardware.

## Optional lock screen widget

Keep `LockScreenWidget` beside the executable. On a compatible Windows 11 system, open Settings → Notifications → Lock screen in the app and choose Install. Installation asks for administrator permission to trust the included self-signed public certificate in Trusted People and install the widget/runtime packages. This is a Windows widget, not an Apple widget.

Then choose MeshCore under Windows Settings → Personalization → Lock screen, or add it on the Widgets board. MeshCore must remain running to supply status. The app's Remove action uninstalls the widget; a certificate trusted during installation may remain in the Windows certificate store.

## Data and updates

The app creates its own settings, contacts, messages and logs under `%APPDATA%\MeshCore`. Downloads do not contain the developer's node database, saved contacts, messages, passwords or private radio keys. Demo contacts are fictional.

When updating, close the app from its tray menu, extract the new ZIP into a separate folder, and run the new executable. Your existing AppData is retained. Back up your data before changing versions. There is no automatic updater.

Source: https://github.com/Yandi9/Meshcore-App-for-Desktop
Issues: https://github.com/Yandi9/Meshcore-App-for-Desktop/issues
