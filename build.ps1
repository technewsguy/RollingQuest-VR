# Builds Rolling Quest VR into .\build:
#   RollingQuestVR.dll (the BepInEx plugin) and RollingQuest-VR-Setup.exe (the installer, with BepInEx and the mod embedded)
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

$version = [regex]::Match((Get-Content "$root\src\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
# The installer uses the C# compiler that comes with Windows (.NET Framework 4).
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# Installer: BepInEx 6 (embedded as its zip) + everything for <game>\BepInEx\plugins\RollingQuestVR as "payload/<path>".
$payload = "$build\payload"
Remove-Item $payload -Recurse -Force -ErrorAction SilentlyContinue
$pd = "$payload\BepInEx\plugins\RollingQuestVR"
New-Item -ItemType Directory -Force "$pd\input", "$pd\art" | Out-Null
Copy-Item "$build\RollingQuestVR.dll", "$root\deps\openvr\openvr_api.dll" $pd
Copy-Item "$root\input\*.json" "$pd\input"
Copy-Item "$root\art\*.jpg", "$root\art\*.png" "$pd\art"
Copy-Item "$root\LICENSE" "$pd\LICENSE.txt"
$resArgs = @("/resource:$root\deps\BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793.zip,bepinex.zip")
$resArgs += Get-ChildItem $payload -Recurse -File | ForEach-Object { "/resource:$($_.FullName),payload/" + $_.FullName.Substring($payload.Length + 1).Replace('\', '/') }
"static partial class RqvrSetup { internal const string ModVersion = `"$version`"; }" | Set-Content "$build\SetupVersion.cs" -Encoding ASCII
@"
using System.Reflection;
[assembly: AssemblyTitle("Rolling Quest VR $version Setup")]
[assembly: AssemblyProduct("Rolling Quest VR")]
[assembly: AssemblyDescription("Installs the Rolling Quest VR mod")]
[assembly: AssemblyFileVersion("$version.0")]
"@ | Set-Content "$build\SetupAssemblyInfo.cs" -Encoding ASCII
& $csc /nologo /target:winexe /optimize+ /main:RqvrSetup /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "/out:$build\RollingQuest-VR-Setup.exe" "$root\installer\Setup.cs" "$root\installer\SteamShortcuts.cs" "$build\SetupVersion.cs" "$build\SetupAssemblyInfo.cs" $resArgs
if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
Write-Host "Built $build\RollingQuest-VR-Setup.exe ($([math]::Round((Get-Item "$build\RollingQuest-VR-Setup.exe").Length / 1MB, 1)) MB)"

if ($Art) {
    # Regenerates art\*.jpg/png from art\source (the game's itch.io images + its in-game logo; not in the repository).
    & $csc /nologo /r:System.Drawing.dll "/out:$build\MakeArt.exe" "$root\tools\MakeArt.cs"
    & "$build\MakeArt.exe" "$root\art\source" "$root\art"
}
Write-Host "Built into $build"
