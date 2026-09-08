@echo off
cd /d "%~dp0"
set "PYEXE=C:\Users\kow\AppData\Local\Programs\Python\Python312\python.exe"
set "PROXY=%~dp0tools\qwen_mt_proxy.py"
if not exist "%PYEXE%" (
  echo [ERR] Python 3.12 not found:
  echo %PYEXE%
  pause
  exit /b 1
)
if not exist "%PROXY%" (
  echo [ERR] proxy script not found:
  echo %PROXY%
  pause
  exit /b 1
)
echo Starting Qwen adapter: http://127.0.0.1:18765
echo Do not close this window.
echo Next: double-click the LinguaGacha bat.
echo.
"%PYEXE%" "%PROXY%"
echo.
echo Adapter stopped.
pause
