@echo off
pwsh -NoProfile -File "%~dp0tools\start-engineering.ps1"
if errorlevel 1 pause
