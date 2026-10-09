@echo off
rem Build and run all automated tests. Usage: test.cmd [Debug|Release]
setlocal
set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-tests.ps1" -Configuration %CONFIG%
exit /b %ERRORLEVEL%
