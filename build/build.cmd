@echo off
rem Build the solution. Usage: build.cmd [Debug|Release]
rem Wrapper exists so users do not need to change the PowerShell execution policy.
setlocal
set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Configuration %CONFIG%
exit /b %ERRORLEVEL%
