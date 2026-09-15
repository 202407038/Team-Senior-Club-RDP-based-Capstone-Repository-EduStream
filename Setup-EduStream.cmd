@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Publish-EduStreamDesktop.ps1" -CreateDesktopShortcuts
if errorlevel 1 (
  echo Preparation failed. Existing launchers were not intentionally replaced before publishing completed.
  pause
  exit /b 1
)
echo Ready. Use the EduStream desktop shortcuts from now on.
pause
