@echo off
setlocal
if /I "%~1"=="-Help" goto :help
if /I "%~1"=="--help" goto :help
if "%~1"=="-?" goto :help
if "%~1"=="/?" goto :help
rem Let Windows PowerShell select its own modules when called from PowerShell 7.
set "PSModulePath="
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start.ps1" %*
exit /b %errorlevel%

:help
echo Usage: Run.cmd [-ProjectPath "C:\Projects\project file.L5X"] [-ShowErrorDialog]
echo Starts the installed shared service and desktop GUI.
echo Project paths containing spaces must be quoted.
exit /b 0
