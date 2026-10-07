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
echo Re-fill Unity dump / unmatched via Local AI (GalTransl)
echo Open Local AI first. Do not start MiniMax H3.
echo Drag the same Unity game folder onto this bat. First-time use 点我汉化Unity游戏.bat.
echo.
"%PYEXE%" "%~dp0tools\one_click_unity.py" --fill %*
echo.
pause
