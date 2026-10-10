@echo off
setlocal EnableExtensions
chcp 65001 >nul
set "TMH_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "TMH_PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%TMH_PS%" (
  echo Windows PowerShell 5.1 is required.
  pause
  exit /b 3
)
"%TMH_PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Run.ps1" -Traces %*
set "TMH_EXIT=%ERRORLEVEL%"
if not "%TMH_EXIT%"=="0" (
  echo.
  echo TeamsMessageHistory stopped with exit code %TMH_EXIT%.
  pause
)
endlocal & exit /b %TMH_EXIT%
