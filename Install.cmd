@echo off
setlocal
if /I "%~1"=="-Help" goto :help
if /I "%~1"=="--help" goto :help
if "%~1"=="-?" goto :help
if "%~1"=="/?" goto :help
rem Let Windows PowerShell select its own modules when called from PowerShell 7.
set "PSModulePath="
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install.ps1" %*
exit /b %errorlevel%

:help
echo Usage: Install.cmd [-WithSdk] [-WithCip] [-SkipBuild] [-NoShortcut]
echo                    [-CodexIntegration Standalone^|None] [-ReconnectIdleMcpClients]
echo Builds and installs RockwellMCPServer and its app-owned plugin by default.
echo Use -CodexIntegration None to skip Codex configuration changes.
echo Existing jobs and clients must be stopped before replacing a build.
exit /b 0
