@echo off
setlocal
title COD2EFT - install Blender add-on
rem Installs + enables the COD2EFT add-on (N-panel "COD2EFT" in the 3D view) in your Blender.
rem It also rebuilds COD2EFT_Blender_Addon.zip (the same zip you can install by hand via
rem Edit > Preferences > Add-ons > Install from Disk). Re-run after updating any COD2EFT file.
rem Close Blender first so it doesn't overwrite the saved preferences.
call "%~dp0find_blender.bat"
if not defined BLENDER goto :fail
"%BLENDER%" --background --python-exit-code 1 --python "%~dp0install_addon.py"
if errorlevel 1 goto :fail
echo.
echo ==== Installed. Start Blender, press N in the 3D view and open the COD2EFT tab ====
pause
exit /b 0
:fail
echo ==== Install failed - scroll up for errors ====
pause
exit /b 1
