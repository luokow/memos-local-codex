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
echo Unity one-click: install + dump + GalTransl fill
echo Open Local AI first. Do not start MiniMax H3 / ComfyUI.
echo Drag a Unity game folder onto this bat, or pick a folder in the next window.
echo Plugins go into the game directory. Original assets are not copied.
echo.
"%PYEXE%" "%~dp0tools\one_click_unity.py" --fill %*
echo.
pause
