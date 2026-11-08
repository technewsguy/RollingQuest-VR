@echo off
rem Removes Rolling Quest VR and BepInEx from this folder (your saves are kept).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BepInEx\plugins\RollingQuestVR\uninstall.ps1"
pause
