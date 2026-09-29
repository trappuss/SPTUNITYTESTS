@echo off
rem ---- shared helper: sets BLENDER to blender.exe (remembered in blender_path.txt) ----
set "BLENDER="
if exist "%~dp0blender_path.txt" set /p BLENDER=<"%~dp0blender_path.txt"
if defined BLENDER if exist "%BLENDER%" goto :eof
set "BLENDER="
for /d %%D in ("%ProgramFiles%\Blender Foundation\Blender*") do if exist "%%~D\blender.exe" set "BLENDER=%%~D\blender.exe"
if not defined BLENDER if exist "%ProgramFiles(x86)%\Steam\steamapps\common\Blender\blender.exe" set "BLENDER=%ProgramFiles(x86)%\Steam\steamapps\common\Blender\blender.exe"
if not defined BLENDER for /f "delims=" %%B in ('where blender.exe 2^>nul') do if not defined BLENDER set "BLENDER=%%B"
if not defined BLENDER (
    echo Could not find blender.exe automatically.
    set /p "BLENDER=Drag blender.exe into this window and press Enter: "
)
if not defined BLENDER goto :eof
set BLENDER=%BLENDER:"=%
if not exist "%BLENDER%" (
    echo ERROR: "%BLENDER%" does not exist.
    set "BLENDER="
    goto :eof
)
> "%~dp0blender_path.txt" echo %BLENDER%
echo Using Blender: %BLENDER%
echo   ^(to change it, edit or delete blender_path.txt^)
goto :eof
