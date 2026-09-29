@echo off
setlocal
title COD2EFT - Call of Duty to Tarkov converter
rem =====================================================================================
rem  (Everything here can also be done inside Blender: COD2EFT tab > Batch Convert.)
rem  Drag COD model files (.fbx or .cast), a character folder, or a whole tree of folders
rem  onto this file. Characters are found automatically (body+head, Cold War parts ...);
rem  first-person models and alternative heads are skipped.
rem  Output: <dropped folder>\EFT_Converted\<name>_EFT.blend / .fbx / _report.txt
rem          plus _COD2EFT_summary.txt listing everything converted or skipped
rem
rem  SETTINGS (edit here):
rem    OPTIONS  --export-fbx   also write <model>_EFT.fbx ready for Unity / WTT-SDK
rem             --cast         prefer .cast over .fbx when a folder has both
rem             --joints-only  old joint-on-joint fit (no body-volume / floor / face matching)
rem             --no-lengths   aim only, don't stretch limbs onto their targets
rem             --no-tweaks    don't apply your saved pose tweaks (cod2eft_pose_tweaks.json)
rem             --head-height off / limited / full   bring the eyes to EFT's eye height
rem                            (default limited: spine + neck at most +-6 %)
rem             --face-landmark nose   line the head up by the nose (default: eyes)
rem             --no-fingertips        don't match the knuckles and fingertips to EFT's
rem             --fp-hands             also make <name>_Hands for EFT's first-person hands
rem                            (from the COD first-person arms when exported)
rem             --fp-hands-from third  make them from the third-person arms instead
rem             --neck-lean 35 most the neck may lean forward (degrees)
rem             --head-forward 0.5  share of the leftover face gap the upper spine takes up (0-1,
rem                            default 0.5)
rem             --fit-scale    best-fit scale instead of real-world size
rem             --no-join      keep COD sub-meshes separate
rem             --split-materials   one object per COD material (vests, hats ... separate)
rem             --max-influences N   (default 4)
rem           Textures (one EFT texture set per part, PNG, next to the .blend):
rem             --no-textures        keep the COD materials, write no textures
rem             --texture-size 2048  atlas size per part: 1024, 2048 or 4096
rem             --texture-layout whole   put each COD texture in whole (default: islands =
rem                                  only the parts the model uses, sharper)
rem             --normal-style directx   flip the normal map green channel (default opengl =
rem                                  what EFT's own normal maps use)
rem             --spec-strength 1    specular (shine) multiplier, colour map alpha (1 = COD)
rem             --metal-colour 0.7   0-1, share of a metal's colour kept (0 = black metal)
rem             --ao-strength 1      0-1, ambient occlusion multiplied into the colour
rem             --no-ao-spec         don't multiply the occlusion into the specular too
set "OPTIONS=--export-fbx"
rem    OUTDIR   empty = "EFT_Converted" next to the first input
set "OUTDIR="
rem =====================================================================================

set "INPUT="
if not "%~1"=="" goto :have_input
echo Drag COD .fbx / .cast files or a folder onto COD2EFT_Convert.bat
echo.
set /p "INPUT=...or paste a folder / file path here and press Enter: "
if not defined INPUT goto :fail
:have_input

call "%~dp0find_blender.bat"
if not defined BLENDER goto :fail

rem ---- EFT template (remembered in template_path.txt) ----
set "TEMPLATE="
if exist "%~dp0template_path.txt" set /p TEMPLATE=<"%~dp0template_path.txt"
if defined TEMPLATE if exist "%TEMPLATE%" goto :have_template
set "TEMPLATE=%~dp0..\Testing\CUSTOM\EFT BASIC [Template].blend"
if exist "%TEMPLATE%" goto :save_template
echo EFT template .blend not found.
set "TEMPLATE="
set /p "TEMPLATE=Drag your EFT template .blend into this window and press Enter: "
if not defined TEMPLATE goto :fail
set TEMPLATE=%TEMPLATE:"=%
if not exist "%TEMPLATE%" (
    echo ERROR: "%TEMPLATE%" does not exist.
    goto :fail
)
:save_template
for %%T in ("%TEMPLATE%") do set "TEMPLATE=%%~fT"
> "%~dp0template_path.txt" echo %TEMPLATE%
:have_template
echo Using template: %TEMPLATE%
echo   ^(to change it, edit or delete template_path.txt^)
echo.

set "OUTARG="
if defined OUTDIR set OUTARG=--out "%OUTDIR%"

if defined INPUT goto :run_typed
"%BLENDER%" --background --factory-startup --python-exit-code 1 --python "%~dp0cod2eft_batch.py" -- --template "%TEMPLATE%" %OUTARG% %OPTIONS% %*
goto :after_run
:run_typed
set INPUT=%INPUT:"=%
"%BLENDER%" --background --factory-startup --python-exit-code 1 --python "%~dp0cod2eft_batch.py" -- --template "%TEMPLATE%" %OUTARG% %OPTIONS% "%INPUT%"
:after_run
if errorlevel 1 goto :fail
echo.
echo ==== Finished OK - open the *_EFT.blend (and read *_EFT_report.txt) in the EFT_Converted folder ====
pause
exit /b 0

:fail
echo.
echo ==== Something failed - scroll up for the [COD2EFT] messages ====
pause
exit /b 1
