# Testing with an empty profile

MeshCore stores user data outside the download folder:

`%APPDATA%\\MeshCore`

Older MeshCore One installations may use:

`%APPDATA%\\MeshCoreOne`

All copies of the Windows executable on the same Windows account use those folders. That means opening the ARM64 or x64 executable from a second folder can show the same messages, contacts and settings already saved on the computer. The data is not being read from the EXE, source ZIP or release ZIP.

To test a clean profile safely:

1. Close MeshCore completely from its notification-area menu.
2. In File Explorer, paste `%APPDATA%` into the address bar.
3. Rename `MeshCore` to `MeshCore-backup`.
4. If it exists, rename `MeshCoreOne` to `MeshCoreOne-backup`.
5. Start the test executable. It will create a new empty MeshCore profile.

Renaming preserves the old data. Restore it by closing the app, deleting the newly created `MeshCore` folder, and renaming `MeshCore-backup` back to `MeshCore`. Restore `MeshCoreOne` only if you need the legacy profile.

Do not include either AppData folder in GitHub uploads. The release packages contain source, program files, licenses and the optional Windows widget only.
