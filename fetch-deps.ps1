# Downloads the third-party pieces Rolling Quest VR needs into .\deps (they are not stored in this repository):
#   - BepInEx 6.0.0-be.793 for Unity IL2CPP, 64-bit Windows (mod loader, LGPL-2.1)  https://github.com/BepInEx/BepInEx
#   - openvr_api.dll 1.14.15 for 64-bit Windows (Valve, BSD-3)                        https://github.com/ValveSoftware/openvr
# src\openvr_api.cs (Valve's C# bindings, same version) is included in the repository.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$deps = Join-Path $PSScriptRoot "deps"
New-Item -ItemType Directory -Force $deps, "$deps\openvr" | Out-Null

$bep = "$deps\BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793.zip"
if (-not (Test-Path $bep)) {
    Write-Host "Downloading BepInEx 6.0.0-be.793 (IL2CPP)..."
    Invoke-WebRequest "https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip" -OutFile $bep -UseBasicParsing
}
if (-not (Test-Path "$deps\openvr\openvr_api.dll")) {
    Write-Host "Downloading openvr_api.dll 1.14.15..."
    Invoke-WebRequest "https://raw.githubusercontent.com/ValveSoftware/openvr/v1.14.15/bin/win64/openvr_api.dll" -OutFile "$deps\openvr\openvr_api.dll" -UseBasicParsing
}
Write-Host "Dependencies ready in $deps"
