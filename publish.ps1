# Builds the self-contained single-file MeshCore programs into .\dist — precompiled (ReadyToRun) and uncompressed so they
# start quickly (bigger files, faster starts).
param([string[]]$Runtimes = @("win-arm64", "win-x64"))
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
New-Item -ItemType Directory -Force -Path "$root\dist" | Out-Null
foreach ($rid in $Runtimes) {
    $out = "$root\artifacts\$rid"
    dotnet publish "$root\src\MC1.Windows\MC1.Windows.csproj" -c Release -f net10.0-windows10.0.19041.0 -r $rid --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=false -p:DebugType=none -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
    $arch = $rid.Replace("win-", "")
    Copy-Item "$out\MeshCoreOne.exe" "$root\dist\MeshCore-$arch.exe" -Force
    Write-Host "dist\MeshCore-$arch.exe"
}
