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
echo Probe local Qwen at 127.0.0.1:18135
echo.
"%PYEXE%" "%~dp0tools\local_qwen.py"
echo.
pause
