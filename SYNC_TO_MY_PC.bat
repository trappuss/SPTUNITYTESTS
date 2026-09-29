@echo off
title Sync COD2EFT + EFT Tools from GitHub to this PC
rem Pulls the latest from GitHub and copies it into your COD2EFT folder and your WTT-SDK
rem Unity project (Assets\Editor\EFTAutoPrefabber). Replaced files are backed up to
rem pc\_backup first; nothing is ever deleted. Folders are asked once, then kept in
rem pc\config.local.txt.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pc\sync.ps1"
echo.
pause
