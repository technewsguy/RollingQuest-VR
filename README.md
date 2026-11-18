# Rolling Quest VR

![Rolling Quest VR](art/header.jpg)

A VR mod for **[Rolling Quest](https://krampus1721.itch.io/rollingquest)** by krampus1721, a Kula World–style puzzle game where you roll a ball around floating block levels. Play it in a SteamVR headset in two ways:

- **Follow view:** you ride right behind the ball, the way the game normally plays, in stereo 3D at full headset frame rate.
- **Diorama view:** the level becomes a model on a table in front of you. Push the stick toward where you want the ball to go, and the mod does the turning for you.

Menus and the HUD float on a screen in front of you. The desktop window keeps showing the game, so others can watch.

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/technewsguy)

## Requirements

- Windows 10/11 (64-bit)
- [Rolling Quest](https://krampus1721.itch.io/rollingquest) (Windows version, tested with v0.3.0.4)
- [SteamVR](https://store.steampowered.com/app/250820/SteamVR/) and a SteamVR headset (tested with a Bigscreen Beyond 2 and Valve Index controllers)
- One of these controllers:
  - A gamepad (PS4/PS5, Xbox), or the keyboard
  - VR controllers (Index, Touch or Vive)

## Install

1. Download **`RollingQuest-VR-Setup-v1.1.0.exe`** from **[Releases](https://github.com/technewsguy/RollingQuest-VR/releases)** and double-click it.
2. It finds your Rolling Quest folder (or lets you pick it) and installs the mod there. It also adds **Rolling Quest VR** to Steam, with artwork, listed in SteamVR's library (under *Non-Steam*).
   - If Steam is open, it asks to restart Steam. If you say no, the entry is added the next time Steam restarts.
3. With your headset connected, start **Rolling Quest VR** from Steam or SteamVR.

> The **first start takes a minute or two** while BepInEx (the mod loader) prepares the game. It needs an internet connection that one time, to download Unity's base libraries. Later starts are quick.

The mod is part of the game folder now, so `RollingQuest.exe` always starts in VR, whichever way you launch it.

**Upgrading from 1.0:** just run the new installer. It removes the old launcher, scripts, desktop shortcut and SteamVR entry, and points the existing **Rolling Quest VR** Steam entry straight at the game (your artwork and controller settings stay).

**Linux (Steam + Proton):** run the installer through Proton (add it to Steam as a non-Steam game, force Proton, start it once). It installs the mod and then shows the few Steam settings to make by hand, including the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`.

### With UniGetUI or Scoop

Rolling Quest VR is also in the [technewsguy/scoop-vr-mods](https://github.com/technewsguy/scoop-vr-mods) bucket, so [UniGetUI](https://github.com/marticliment/UniGetUI) (formerly WingetUI) or [Scoop](https://scoop.sh) can install it and keep it up to date:

- **UniGetUI:** Settings > Package managers > Scoop > Buckets. Add `vrmods` with the URL `https://github.com/technewsguy/scoop-vr-mods`, then search for `rollingquest-vr` and install it.
- **Command line:**
  ```powershell
  scoop bucket add vrmods https://github.com/technewsguy/scoop-vr-mods
  scoop install vrmods/rollingquest-vr
  ```

It runs the same installer silently. If it can't find your Rolling Quest folder, it asks you to pick it (or set the `ROLLINGQUEST_DIR` environment variable). Uninstalling the package removes the mod again.

## Controls

The game's own gamepad and keyboard controls work as usual. VR controllers act as a gamepad:

| Index / Touch | Gamepad | Does |
|---|---|---|
| Left thumbstick | Left stick / D-pad | Roll and turn, menus |
| Right A | A / Cross | Jump, accept |
| Right B | B / Circle | Back (menus) |
| Left A (Touch: X) | X / Square | Action |
| Left B (Touch: Y) | Start / Options | Pause |
| Right trigger | R2 | Look up |
| Left trigger | R1 | Look down |
| Left stick click | Share / View (keyboard **F9**) | Switch between follow and diorama view |
| Right stick click | – (keyboard **F8**) | Recenter the view |
| Right thumbstick | Right stick | Diorama: flick left/right to turn the model, up/down to resize |

You can rebind the VR controllers in SteamVR's controller settings for Rolling Quest.

## Diorama view

- **Placement:** the model stays centered on the ball and keeps its direction, so up on the stick always means away from you.
- **Walls:** when the ball rolls up a wall facing you, up and down move it up and down the wall.
- **Turning the model:** flick the right stick left or right.
- **Original controls:** to use the game's turn-then-roll controls in diorama view too, set `PushToSteer = false` in the settings file.

## Settings

Edit `BepInEx\config\rollingquestvr.mod.cfg`, which is created after the first start:

| Setting | Default | Meaning |
|---|---|---|
| `EnableVR` | true | `false` plays the normal flat game |
| `Diorama` | false | Start in diorama view |
| `[Diorama] Size / Distance / Height` | 1.0 / 0.9 / -0.7 | Model width, distance and height, in meters |
| `PushToSteer` | true | Diorama steering (see above) |
| `[UI] Width / Distance / VerticalOffset` | 1.6 / 1.4 / -0.1 | Size and position of the floating menu screen |
| `ShowOnDesktop` | true | Also show menus and HUD in the desktop window |
| `WorldScale` | 1 | Follow view: your size relative to the level |

## Uninstall

Double-click **`Rolling Quest VR Uninstall.exe`** in the game folder. It removes the mod, BepInEx (unless other mods use it) and the Steam entry (Steam restarts, if you agree). Your saves are kept.

## How it works

Rolling Quest is a Unity 2023 game built with IL2CPP. The mod is a [BepInEx 6](https://github.com/BepInEx/BepInEx) plugin that doesn't use Unity's XR at all:

- **Rendering:** two eye cameras follow the game's camera (or the diorama rig) and render into textures. The mod submits them to SteamVR through [OpenVR](https://github.com/ValveSoftware/openvr).
- **Menus and HUD:** the game's screen-space canvases are redirected into a texture, which floats on a quad in front of you.
- **VR controllers:** they're read through SteamVR Input and fed to the game's Input System as a virtual gamepad.
- **Diorama steering:** a [Harmony](https://github.com/BepInEx/HarmonyX) patch on the game's ball controller turns stick directions into the game's turn and forward inputs.
- **Installer:** one exe with BepInEx and the mod inside. It adds the Steam entry by editing Steam's `shortcuts.vdf` while Steam is closed (with `OpenVR` set, which puts it in the VR library).

### Building

```powershell
.\fetch-deps.ps1                                   # BepInEx 6 be.793 IL2CPP + openvr_api.dll into .\deps
.\build.ps1 -GameDir "C:\path\to\Rolling Quest"    # needs BepInEx installed there and the game started once
.\make-release.ps1 -GameDir "C:\path\to\Rolling Quest"  # dist\RollingQuest-VR-Setup-v<version>.exe + RollingQuest-VR-v<version>.zip
```

## Credits

- **Rolling Quest** © krampus1721. This is an unofficial fan mod. The artwork in `art/` is made from the game's own images (its itch.io page and in-game logo) and belongs to its developer. Please support the game: <https://krampus1721.itch.io/rollingquest>
- [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1), bundled in the installer, with [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) and [HarmonyX](https://github.com/BepInEx/HarmonyX)
- [OpenVR](https://github.com/ValveSoftware/openvr) by Valve (BSD-3-Clause)

The mod's own code is under the [MIT License](LICENSE).
