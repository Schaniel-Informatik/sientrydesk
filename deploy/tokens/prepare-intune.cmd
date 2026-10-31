@echo off
rem Doppelklick: bereitet das Intune-Paket vor. Startet prepare-intune.ps1 nur fuer diesen Aufruf ohne
rem Ausfuehrungsrichtlinie, aendert keine Einstellung am PC. Der Modulpfad wird geleert, weil ein aus
rem PowerShell 7 geerbter Pfad Windows PowerShell die falschen Module laden laesst.
setlocal
set "PSModulePath="
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-intune.ps1" %*
echo.
pause
