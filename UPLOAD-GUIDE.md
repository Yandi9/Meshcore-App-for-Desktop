# Uploading version 1.0.0 to GitHub

Repository: https://github.com/Yandi9/Meshcore-App-for-Desktop

## 1. Upload the source

Upload the CONTENTS of `01-GitHub-Source` to the repository root on `main`. Replace the placeholder README and retain LICENSE. Make sure hidden `.github` and `.gitignore` are included. Do not upload the entire release-preparation folder or the original MeshCoreOne-main folder. Do not upload EXE/MSIX files to the source repository.

GitHub's browser uploader limits each batch to 100 files. This source includes dependency notices, so use GitHub Desktop or Git for a full upload, or upload folders in smaller batches. With GitHub Desktop: clone the existing repository, copy the contents of `01-GitHub-Source` into that clone (including hidden files), review the changes, commit with “Prepare Windows release 1.0.0”, then push.

## 2. Verify before making a stable release

In the repository's Actions tab, run “Validate Windows release”. It builds and tests the source but does not publish anything. Confirm the saved-password test passes in a normal Windows context and complete the hardware/widget checks in `docs/VALIDATION.md`. Until then, keep the release as a draft or mark it as a prerelease. Do not describe it as fully tested.

## 3. Create the release

Open the repository's Releases page, choose “Draft a new release”, create tag `v1.0.0` from the commit containing this source, and use title **MeshCore for Windows 1.0.0**. Paste the contents of RELEASE-NOTES.md into the description. Do not mark a prerelease as the latest stable release.

Attach ALL FOUR files from `02-Release-Uploads`:

1. MeshCore-1.0.0-win-x64.zip
2. MeshCore-1.0.0-win-arm64.zip
3. MeshCore-1.0.0-source.zip
4. SHA256SUMS.txt

The ZIP filenames match the README download URLs. Use the ZIPs rather than uploading only the EXEs: the ZIPs also contain the widget, instructions, licenses and credits. The EXEs are over GitHub's ordinary source-file size limit and belong inside release assets.

Review the tag, source, files and description; save the draft or publish the prerelease. After validation, edit the release description to record the new results before marking it stable. The source and executables must always match; rebuild and regenerate checksums if source behavior changes.

## 4. Check the published download

Download each asset back from GitHub and compare its SHA256 with SHA256SUMS.txt. Confirm the README's download links open the correct release assets. On your computer, PowerShell `Get-FileHash -Algorithm SHA256 <file>` calculates a file's checksum.
