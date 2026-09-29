@echo off
setlocal
title First-time setup: SPTUNITYTESTS on this PC
rem Download ONLY this file from GitHub and double-click it. It:
rem   1. installs Git if it is missing (winget),
rem   2. clones the repo to  %USERPROFILE%\Downloads\Claude Current\SPTUNITYTESTS,
rem   3. runs SYNC_TO_MY_PC.bat there.
set "DEST=%USERPROFILE%\Downloads\Claude Current\SPTUNITYTESTS"
set "BRANCH=claude/bold-mayer-11fzxj"
set "GIT=git"
where git >nul 2>nul && goto :have_git
if exist "%ProgramFiles%\Git\cmd\git.exe" (set "GIT=%ProgramFiles%\Git\cmd\git.exe" & goto :have_git)
echo Git is not installed - installing it with winget...
winget install --id Git.Git -e --source winget --accept-package-agreements --accept-source-agreements
if not exist "%ProgramFiles%\Git\cmd\git.exe" (
    echo Could not install Git. Install it from https://git-scm.com/download/win and run this again.
    goto :fail
)
set "GIT=%ProgramFiles%\Git\cmd\git.exe"
:have_git
if exist "%DEST%\.git" (
    echo Already cloned at %DEST%
) else (
    echo Cloning into %DEST% ^(a browser window may ask you to sign in to GitHub^)...
    "%GIT%" clone -b %BRANCH% https://github.com/trappuss/SPTUNITYTESTS.git "%DEST%" || goto :fail
)
"%GIT%" -C "%DEST%" config user.name >nul 2>nul || "%GIT%" -C "%DEST%" config user.name "trappuss"
"%GIT%" -C "%DEST%" config user.email >nul 2>nul || "%GIT%" -C "%DEST%" config user.email "notsomlgally@gmail.com"
echo.
echo From now on use the .bat files in %DEST%
echo   SYNC_TO_MY_PC.bat            - get Claude's latest work onto this PC
echo   SEND_RESULTS_TO_CLAUDE.bat   - send logs / reports / screenshots back
echo.
call "%DEST%\SYNC_TO_MY_PC.bat"
exit /b 0
:fail
echo ==== Setup failed - scroll up for the error ====
pause
exit /b 1
