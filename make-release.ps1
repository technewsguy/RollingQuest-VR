# Packages dist\RollingQuest-VR-v<version>.zip, to be unzipped into the Rolling Quest folder:
#   BepInEx 6 IL2CPP (winhttp.dll, doorstop_config.ini, dotnet\, BepInEx\core ...) + BepInEx\plugins\RollingQuestVR\
#   (plugin, openvr_api.dll, launcher, SteamShortcut.exe, scripts, input bindings, artwork) + the two .bat files + README.
# Run fetch-deps.ps1 and build.ps1 first.
param([string]$Version = "1.0.0")
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$stage = "$root\dist\stage"
$zip = "$root\dist\RollingQuest-VR-v$Version.zip"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Expand-Archive "$root\deps\BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793.zip" -DestinationPath $stage -Force
$pd = "$stage\BepInEx\plugins\RollingQuestVR"
New-Item -ItemType Directory -Force $pd, "$pd\input", "$pd\art" | Out-Null
Copy-Item "$root\build\RollingQuestVR.dll", "$root\build\RollingQuestVR-launch.exe", "$root\build\SteamShortcut.exe" $pd
Copy-Item "$root\deps\openvr\openvr_api.dll" $pd
Copy-Item "$root\scripts\add-to-steam.ps1", "$root\scripts\uninstall.ps1" $pd
Copy-Item "$root\input\*.json" "$pd\input"
Copy-Item "$root\art\*.jpg", "$root\art\*.png" "$pd\art"
Copy-Item "$root\scripts\Rolling Quest VR - Add to Steam.bat", "$root\scripts\Rolling Quest VR - Uninstall.bat" $stage
(Get-Content "$root\scripts\README.txt" -Raw).Replace("{VERSION}", "v$Version") | Set-Content "$stage\Rolling Quest VR - README.txt" -Encoding UTF8
Copy-Item "$root\LICENSE" "$pd\LICENSE.txt"

if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
# Entries are written one by one so their paths use "/" (Windows PowerShell's CreateFromDirectory writes "\").
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File -Force | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal)
    }
    # keep the empty BepInEx folders the loader expects
    foreach ($d in "BepInEx/patchers/") { [void]$archive.CreateEntry($d) }
} finally { $archive.Dispose() }
Remove-Item $stage -Recurse -Force
Write-Host "Release package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
