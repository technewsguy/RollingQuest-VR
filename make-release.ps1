# Builds the mod and packages the release:
#   dist\RollingQuest-VR-Setup-v<version>.exe   the installer on its own (what most people download)
#   dist\RollingQuest-VR-v<version>.zip         the same installer + README and license (used by the Scoop bucket)
# Run fetch-deps.ps1 first. -GameDir: a Rolling Quest folder with BepInEx 6 that has been started once (see build.ps1).
param([string]$GameDir = "S:\Games\rollingcube-windows")
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& (Join-Path $root 'build.ps1') -GameDir $GameDir
$ver = ([regex]'const string Version = "([^"]+)"').Match((Get-Content (Join-Path $root 'src\Plugin.cs') -Raw)).Groups[1].Value
$name = "RollingQuest-VR-v$ver"
$stage = Join-Path $root "dist\$name"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $root 'build\RollingQuest-VR-Setup.exe'), (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
$zip = Join-Path $root "dist\$name.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force
$exe = Join-Path $root "dist\RollingQuest-VR-Setup-v$ver.exe"
Copy-Item (Join-Path $root 'build\RollingQuest-VR-Setup.exe') $exe -Force
foreach ($f in $exe, $zip) {
    Write-Host ("Release file: {0}  ({1} MB)  SHA256 {2}" -f $f, [math]::Round((Get-Item $f).Length / 1MB, 2), (Get-FileHash $f).Hash.ToLower())
}
