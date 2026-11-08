# Adds "Rolling Quest VR" to the Steam library as a non-Steam game with "Include in VR Library" on, plus its artwork
# (cover, header, background, logo, icon). It then shows in Steam's library and in SteamVR's library.
# Lives in <game>\BepInEx\plugins\RollingQuestVR. Steam has to be closed while its shortcut list is edited, so this
# script closes Steam and starts it again.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$gameDir = (Resolve-Path "$here\..\..\..").Path
$launcher = "$here\RollingQuestVR-launch.exe"
if (-not (Test-Path "$gameDir\RollingQuest.exe")) { throw "RollingQuest.exe not found in $gameDir. Unzip Rolling Quest VR into the game folder first." }
# Files from a downloaded zip are marked as "from the internet"; clear that so Windows doesn't block the launcher.
Get-ChildItem $here -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

$reg = Get-ItemProperty HKCU:\Software\Valve\Steam -ErrorAction SilentlyContinue
if (-not $reg) { throw "Steam doesn't seem to be installed." }
$steam = $reg.SteamPath; $steamExe = $reg.SteamExe
$user = (Get-ItemProperty HKCU:\Software\Valve\Steam\ActiveProcess -ErrorAction SilentlyContinue).ActiveUser
if (-not $user -or $user -eq 0) {
    # Steam not running: use the most recently used account folder
    $user = (Get-ChildItem "$steam\userdata" -Directory | Where-Object Name -match '^\d+$' | Where-Object Name -ne '0' |
             Sort-Object { (Get-Item "$($_.FullName)\config" -ErrorAction SilentlyContinue).LastWriteTime } -Descending | Select-Object -First 1).Name
}
if (-not $user) { throw "Couldn't find your Steam account folder (sign in to Steam once first)." }
$cfg = "$steam\userdata\$user\config"
New-Item -ItemType Directory -Force "$cfg\grid" | Out-Null

if (Get-Process steam -ErrorAction SilentlyContinue) {
    Write-Host "Closing Steam (it can't be running while its shortcut list is changed)..."
    Start-Process $steamExe -ArgumentList "-shutdown"
    $t = 0; while ((Get-Process steam -ErrorAction SilentlyContinue) -and $t -lt 60) { Start-Sleep 1; $t++ }
    if (Get-Process steam -ErrorAction SilentlyContinue) { throw "Steam didn't close. Close it yourself and run this again." }
}
if ((Test-Path "$cfg\shortcuts.vdf") -and -not (Test-Path "$cfg\shortcuts.vdf.rqvr-backup")) { Copy-Item "$cfg\shortcuts.vdf" "$cfg\shortcuts.vdf.rqvr-backup" }

$icon = if (Test-Path "$here\art\icon.png") { "$here\art\icon.png" } else { "$gameDir\RollingQuest.exe" }
# "-" = no launch options. Last argument 1 = include in the VR library.
$id = & "$here\SteamShortcut.exe" "$cfg\shortcuts.vdf" "Rolling Quest VR" $launcher $here $icon "-" 1
if ($LASTEXITCODE -ne 0 -or -not $id) { throw "Couldn't update Steam's shortcut list." }
$gameId = ([uint64]$id -shl 32) -bor 0x02000000
Set-Content "$here\steam_gameid.txt" $gameId

# Artwork file names Steam looks for: <id>.jpg header, <id>p.jpg cover, <id>_hero.jpg background, <id>_logo.png, <id>_icon.png
$pairs = [ordered]@{ "header.jpg" = "$id.jpg"; "capsule.jpg" = "${id}p.jpg"; "hero.jpg" = "${id}_hero.jpg"; "logo.png" = "${id}_logo.png"; "icon.png" = "${id}_icon.png" }
foreach ($k in $pairs.Keys) { if (Test-Path "$here\art\$k") { Copy-Item "$here\art\$k" "$cfg\grid\$($pairs[$k])" -Force } }

# Desktop shortcut too (it starts the same Steam entry).
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) "Rolling Quest VR.lnk"))
$lnk.TargetPath = $launcher; $lnk.WorkingDirectory = $here; $lnk.IconLocation = "$gameDir\RollingQuest.exe,0"; $lnk.Description = "Rolling Quest in VR"
$lnk.Save()

Write-Host "Added 'Rolling Quest VR' to Steam (shortcut id $id) and to your desktop. Starting Steam..."
Start-Process $steamExe
