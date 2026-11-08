# Builds Rolling Quest VR into .\build:
#   RollingQuestVR.dll (the BepInEx plugin), RollingQuestVR-launch.exe (launcher), SteamShortcut.exe (adds the Steam entry)
# The plugin compiles against the game's BepInEx interop assemblies, so -GameDir must point at a copy of Rolling Quest
# with BepInEx 6 installed that has been started once (that generates BepInEx\interop).
param([Parameter(Mandatory = $true)][string]$GameDir, [switch]$Art)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = "$root\build"
New-Item -ItemType Directory -Force $build | Out-Null
if (-not (Test-Path "$GameDir\BepInEx\interop\Assembly-CSharp.dll")) { throw "No BepInEx interop assemblies in $GameDir. Install BepInEx 6 IL2CPP there and start the game once." }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
& $dotnet build "$root\src\RollingQuestVR.csproj" -c Release -nologo -v q "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }
Copy-Item "$root\src\bin\Release\net6.0\RollingQuestVR.dll" $build -Force

# The small Windows tools use the C# compiler that comes with Windows (.NET Framework 4).
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /optimize+ "/out:$build\RollingQuestVR-launch.exe" "$root\launcher\RqvrLaunch.cs" "$root\src\openvr_api.cs"
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed." }
& $csc /nologo /optimize+ "/out:$build\SteamShortcut.exe" "$root\tools\SteamShortcut.cs"
if ($LASTEXITCODE -ne 0) { throw "SteamShortcut build failed." }

if ($Art) {
    # Regenerates art\*.jpg/png from art\source (the game's itch.io images + its in-game logo; not in the repository).
    & $csc /nologo /r:System.Drawing.dll "/out:$build\MakeArt.exe" "$root\tools\MakeArt.cs"
    & "$build\MakeArt.exe" "$root\art\source" "$root\art"
}
Write-Host "Built into $build"
