@echo off
title Send results to Claude (via GitHub)
rem Double-click, or drag files / folders onto this (reports, screenshots, logs).
rem Collects the Unity Editor.log, installed versions, tool files that were changed on this
rem PC, and your files into from_pc\<time>\, then pushes them to GitHub for Claude to read.
rem .blend files and files over 90 MB are skipped.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pc\send.ps1" %*
echo.
pause
