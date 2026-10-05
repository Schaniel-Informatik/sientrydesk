@echo off
rem Doppelklick: bereitet das Intune-Paket vor. Startet prepare-intune.ps1 nur fuer diesen Aufruf ohne
rem Ausfuehrungsrichtlinie, aendert keine Einstellung am PC.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-intune.ps1" %*
echo.
pause
