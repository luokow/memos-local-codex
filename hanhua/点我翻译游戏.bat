@echo off
cd /d "%~dp0"
set PYTHONUTF8=1
set PYTHONIOENCODING=utf-8
set "PYEXE=C:\Users\kow\AppData\Local\Programs\Python\Python312\python.exe"
if not exist "%PYEXE%" (
  echo [ERR] Python 3.12 not found:
  echo %PYEXE%
  pause
  exit /b 1
)
echo RPG Maker MV/MZ one-click embed
echo Drag a game folder onto this bat, or pick a folder in the next window.
echo Original files are not modified. A *-cn copy is created here.
echo.
"%PYEXE%" "%~dp0tools\one_click_rm.py" %*
echo.
pause
