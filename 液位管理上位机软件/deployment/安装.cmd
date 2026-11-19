@echo off
set "LEVEL_SETUP_SCRIPT=%~dp0Install.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$a='-NoProfile -ExecutionPolicy Bypass -File '+[char]34+$env:LEVEL_SETUP_SCRIPT+[char]34; Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $a -WindowStyle Hidden -Wait"
