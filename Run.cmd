@echo off
setlocal
rem Let Windows PowerShell select its own modules when called from PowerShell 7.
set "PSModulePath="
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start.ps1" %*
exit /b %errorlevel%
