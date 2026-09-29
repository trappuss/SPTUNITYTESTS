@echo off
setlocal EnableExtensions
title COD2EFT - copy a converted character into the WTT-SDK Unity project
rem =====================================================================================
rem  Drag a converted <name>_EFT.fbx (from an EFT_Converted folder) onto this file.
rem  It copies into your WTT-SDK Unity project:
rem     <name>_EFT.fbx and <name>_*.png   ->  Assets\COD2EFT\<name>\
rem  Materials, prefabs and bundles are then made by the EFT Auto Prefabber in the SDK (it owns
rem  the Unity side - see unity\COD2EFT_TEXTURE_SPEC.md).  The project folder is remembered in
rem  wtt_path.txt.
rem =====================================================================================
if "%~1"=="" (
    echo Drag a ^<name^>_EFT.fbx onto COD2EFT_To_Unity.bat
    goto :fail
)
set "FBX=%~f1"
if /I not "%~x1"==".fbx" (
    echo ERROR: "%~nx1" is not an .fbx - drag the ^<name^>_EFT.fbx file.
    goto :fail
)
set "SRC=%~dp1"
set "NAME=%~n1"
if /I "%NAME:~-4%"=="_EFT" set "NAME=%NAME:~0,-4%"

set "WTT="
if exist "%~dp0wtt_path.txt" set /p WTT=<"%~dp0wtt_path.txt"
if defined WTT if exist "%WTT%\Assets" goto :have_wtt
set "WTT="
set /p "WTT=Drag your WTT-SDK Unity project folder (the one with Assets inside) here and press Enter: "
if not defined WTT goto :fail
set WTT=%WTT:"=%
if not exist "%WTT%\Assets" (
    echo ERROR: "%WTT%\Assets" not found - that is not a Unity project folder.
    goto :fail
)
> "%~dp0wtt_path.txt" echo %WTT%
:have_wtt
echo Unity project: %WTT%

set "DST=%WTT%\Assets\COD2EFT\%NAME%"
if not exist "%DST%" mkdir "%DST%"
copy /Y "%FBX%" "%DST%\" >nul || goto :fail
set "N=0"
for %%P in ("%SRC%%NAME%_*.png") do (
    copy /Y "%%~fP" "%DST%\" >nul
    set /a N+=1
)
echo.
echo Copied %NAME%_EFT.fbx and %N% texture(s) to %DST%
echo.
echo ==== In Unity: set up materials and prefabs with the EFT Auto Prefabber ^(its material fixer^) ====
pause
exit /b 0
:fail
echo.
echo ==== Nothing copied ====
pause
exit /b 1
