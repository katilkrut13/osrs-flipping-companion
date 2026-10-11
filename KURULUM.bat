@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Kurulum.ps1"
if errorlevel 1 (
  echo.
  echo Kurulum tamamlanamadi. Yukaridaki mesaji kontrol edin.
  pause
)
