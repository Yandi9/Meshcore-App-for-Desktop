#!/usr/bin/env sh
# Builds dist/LockScreenWidget: the signed MeshCore widget packages (arm64 and x64), the Windows App Runtime
# framework they need, and MeshCore.cer (the certificate MeshCore asks Windows to trust when you click Install).
# Needs: .NET 10 SDK, python3, openssl, osslsigncode, curl, unzip.
set -e
cd "$(dirname "$0")/../.."
VERSION=${WIDGET_VERSION:-1.2.0.0}
RUNTIME_VERSION=2.5.1
KEYDIR=${MESHCORE_SIGNING_DIR:-$HOME/.meshcore-signing}
OUT=dist/LockScreenWidget
mkdir -p "$OUT" artifacts "$KEYDIR"

# The signing certificate is made once and kept outside the source tree (never share key.pem).
if [ ! -f "$KEYDIR/key.pem" ]; then
  openssl req -x509 -newkey rsa:3072 -sha256 -days 7300 -nodes -subj "/CN=MeshCore" \
    -addext "keyUsage=critical,digitalSignature" -addext "extendedKeyUsage=codeSigning" \
    -addext "basicConstraints=critical,CA:FALSE" -keyout "$KEYDIR/key.pem" -out "$KEYDIR/cert.pem"
  chmod 600 "$KEYDIR/key.pem"
fi
openssl x509 -in "$KEYDIR/cert.pem" -outform DER -out "$OUT/MeshCore.cer"

# Windows App Runtime (the widget API lives there), from Microsoft's NuGet package.
NUPKG=artifacts/microsoft.windowsappsdk.runtime.$RUNTIME_VERSION.nupkg
if [ ! -f "$NUPKG" ]; then
  curl -fL -o "$NUPKG.part" "https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.runtime/$RUNTIME_VERSION/microsoft.windowsappsdk.runtime.$RUNTIME_VERSION.nupkg"
  mv "$NUPKG.part" "$NUPKG"
fi

for arch in arm64 x64; do
  pub=artifacts/widget-$arch
  dotnet publish src/MC1.Widget/MC1.Widget.csproj -c Release -r "win-$arch" --self-contained -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version="${VERSION%.*}" -o "$pub"
  layout=artifacts/widget-layout-$arch
  rm -rf "$layout" && mkdir -p "$layout/Assets"
  cp "$pub/MeshCoreWidget.exe" "$layout/"
  cp packaging/widget/Assets/*.png "$layout/Assets/"
  sed -e "s/{VERSION}/$VERSION/" -e "s/{ARCH}/$arch/" packaging/widget/AppxManifest.xml > "$layout/AppxManifest.xml"
  python3 packaging/widget/pack_msix.py "$layout" "artifacts/MeshCoreWidget_$arch.unsigned.msix"
  rm -f "$OUT/MeshCoreWidget_$arch.msix"
  osslsigncode sign -certs "$KEYDIR/cert.pem" -key "$KEYDIR/key.pem" -h sha256 \
    -in "artifacts/MeshCoreWidget_$arch.unsigned.msix" -out "$OUT/MeshCoreWidget_$arch.msix"
  osslsigncode verify -CAfile "$KEYDIR/cert.pem" -in "$OUT/MeshCoreWidget_$arch.msix" | tail -n 3
  python3 packaging/widget/check_msix.py "$OUT/MeshCoreWidget_$arch.msix"
  unzip -p "$NUPKG" "tools/MSIX/win10-$arch/Microsoft.WindowsAppRuntime.2.msix" > "$OUT/Microsoft.WindowsAppRuntime.2_$arch.msix"
done
ls -l "$OUT"
