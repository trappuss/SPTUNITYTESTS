@echo off
title Build + install COD2EFT Inspector (SPT client plugin)
rem Compiles spt_mod\COD2EFTInspector against YOUR SPT game's own DLLs and copies the plugin to
rem <SPT game>\BepInEx\plugins\COD2EFTInspector\. Installs the .NET SDK with winget if it is missing.
rem The SPT game folder is asked once and kept in pc\config.local.txt (SPT_GAME).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pc\build_inspector.ps1"
echo.
pause
