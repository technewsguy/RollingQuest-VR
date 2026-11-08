@echo off
rem Adds "Rolling Quest VR" to your Steam library (and SteamVR's library) with artwork, plus a desktop shortcut.
rem Steam is closed and restarted while this runs.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BepInEx\plugins\RollingQuestVR\add-to-steam.ps1"
pause
