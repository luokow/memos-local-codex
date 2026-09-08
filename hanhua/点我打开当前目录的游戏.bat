@echo off
cd /d "%~dp0"
if exist "Game.exe" (
  start "" "%~dp0Game.exe"
  goto :eof
)
echo Game.exe not found in this folder.
echo Copy this bat next to Game.exe, then double-click it.
pause
