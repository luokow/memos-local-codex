@echo off
cd /d "%~dp0"
set PYTHONUTF8=1
set "PYEXE=C:\Users\kow\AppData\Local\Programs\Python\Python312\python.exe"
echo Restore MTool custom AI to the saved cloud engine.
echo Bing is not touched. Quit MTool completely after this.
echo.
"%PYEXE%" "%~dp0tools\switch_mtool_engine.py" cloud
echo.
pause
