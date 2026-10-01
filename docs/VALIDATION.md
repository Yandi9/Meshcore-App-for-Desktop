# Version 1.0.0 validation

Prepared 2026-10-01 from the cleaned Windows source with .NET SDK 10.0.401 / .NET 10.0.12.

## Results

- Self-contained Release build, Windows x64: succeeded.
- Self-contained Release build, Windows ARM64: succeeded (cross-compiled; not executed on ARM64 hardware).
- Protocol tests: 77 passed, 0 failed.
- Application integration tests: 21 passed, 1 failed.
- The failing test is `RepeaterAdminFlow`: Windows DPAPI rejected the password-encryption operation with a user-profile/security-context error in the restricted build environment. Password protection was NOT disabled or modified. Re-run this test in a normal Windows user session before publishing a stable release.
- Existing compiler warning: direct access to a generated observable field in the welcome guide; builds succeeded.
- JSON/XML source files parsed and project references checked.
- Source scan excluded saved-data file types, private signing keys, Apple project/source files, and identified personal demo node names/coordinates.
- Dependency metadata and available package license/notice files included.
- Release ZIP integrity and SHA256 checksums verified during packaging.

## Manual checks before stable publication

Run the app on a normal Windows PC, verify the About source link, back up/restore fictional data, and confirm saved repeater passwords work. Test the matching x64/ARM64 builds on their actual hardware, Bluetooth/USB/WiFi with a radio, and widget installation/update/removal on compatible Windows 11. These hardware and installation checks have not been performed here.

The widget MSIX packages are the supplied Windows packages (version 1.2.0.0), retained with their original signatures and matching public certificate. The desktop app version is 1.0.0. No signing private key is distributed. Widget runtime packages are Microsoft Windows App Runtime 2.5.1.0. Widget archive structure/block hashes are checked separately; that does not establish installed operation or certificate trust.
