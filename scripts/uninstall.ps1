# Removes Rolling Quest VR from the game folder: the mod, BepInEx (the mod loader it came with), the desktop shortcut and
# the Steam artwork. The "Rolling Quest VR" entry in Steam is removed by hand: right-click it > Manage > Remove
# non-Steam game from your library. Your Rolling Quest saves are not touched.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$gameDir = (Resolve-Path "$here\..\..\..").Path
if (-not (Test-Path "$gameDir\RollingQuest.exe")) { throw "This doesn't look like the Rolling Quest folder: $gameDir" }
if (Get-Process RollingQuest -ErrorAction SilentlyContinue) { throw "Close Rolling Quest first." }

$others = Get-ChildItem "$gameDir\BepInEx\plugins" -Directory -ErrorAction SilentlyContinue | Where-Object Name -ne "RollingQuestVR"
Write-Host "This removes Rolling Quest VR and BepInEx from $gameDir"
if ($others) { Write-Host "Note: other BepInEx mods were found and would be removed too: $($others.Name -join ', ')" }
$ok = Read-Host "Continue? (y/n)"
if ($ok -notmatch '^[yY]') { Write-Host "Nothing changed."; return }

# Steam artwork for the shortcut
$gid = if (Test-Path "$here\steam_gameid.txt") { [uint64](Get-Content "$here\steam_gameid.txt") } else { 0 }
if ($gid) {
    $id = $gid -shr 32
    $steam = (Get-ItemProperty HKCU:\Software\Valve\Steam -ErrorAction SilentlyContinue).SteamPath
    if ($steam) { Get-ChildItem "$steam\userdata\*\config\grid\$id*" -ErrorAction SilentlyContinue | Remove-Item -Force }
}
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) "Rolling Quest VR.lnk") -ErrorAction SilentlyContinue

# PowerShell has already read this whole script, so its own folder (inside BepInEx) can be deleted.
Set-Location $gameDir
foreach ($f in "BepInEx", "dotnet", "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt",
               "Rolling Quest VR - Add to Steam.bat", "Rolling Quest VR - Uninstall.bat", "Rolling Quest VR - README.txt") {
    if (Test-Path "$gameDir\$f") { Remove-Item "$gameDir\$f" -Recurse -Force }
}
Write-Host "Rolling Quest VR is removed. Remove the 'Rolling Quest VR' entry from Steam: right-click it > Manage > Remove non-Steam game from your library."
