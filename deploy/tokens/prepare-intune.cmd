@echo off
rem Double-click: prepares the Intune package. Runs prepare-intune.ps1 without execution policy for this call only,
rem changes no setting on the PC. The module path is cleared, because a path inherited from PowerShell 7 makes
rem Windows PowerShell load the wrong modules.
setlocal
set "PSModulePath="
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-intune.ps1" %*
echo.
pause
