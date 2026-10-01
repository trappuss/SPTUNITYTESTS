@echo off
title Push this PC's commits to GitHub
rem Pushes commits made in this repo folder on this PC (e.g. by a Cowork session, which has no GitHub login)
rem to GitHub with your own Git login. No questions asked. The result is also written to _xfer\push_result.txt.
cd /d "%~dp0"
if not exist _xfer mkdir _xfer
git push origin claude/bold-mayer-11fzxj > _xfer\push_result.txt 2>&1
echo exit code %errorlevel% >> _xfer\push_result.txt
type _xfer\push_result.txt
timeout /t 15
