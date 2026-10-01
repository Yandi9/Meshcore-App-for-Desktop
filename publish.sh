#!/usr/bin/env sh
# Cross-builds the Windows programs from Linux/macOS into ./dist. Precompiled (ReadyToRun) and uncompressed, so they
# start quickly: nothing to unpack and little to compile at start-up (bigger files, faster starts).
set -e
cd "$(dirname "$0")"
mkdir -p dist
for rid in win-arm64 win-x64; do
  dotnet publish src/MC1.Windows/MC1.Windows.csproj -c Release -f net10.0-windows10.0.19041.0 -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=false -p:DebugType=none -o "artifacts/$rid"
  cp "artifacts/$rid/MeshCoreOne.exe" "dist/MeshCore-${rid#win-}.exe"
done
# The lock screen widget (dist/LockScreenWidget) needs openssl and osslsigncode; skipped when they're missing.
if command -v osslsigncode >/dev/null 2>&1 && command -v openssl >/dev/null 2>&1; then
  ./packaging/widget/build.sh
else
  echo "osslsigncode/openssl not found: dist/LockScreenWidget not rebuilt"
fi
