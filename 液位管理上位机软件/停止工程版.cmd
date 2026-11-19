@echo off
pwsh -NoProfile -File "%~dp0tools\stop-engineering.ps1"
if errorlevel 1 pause
