@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -DataDirectory "%~dp0.local-demo" %*
if errorlevel 1 pause
