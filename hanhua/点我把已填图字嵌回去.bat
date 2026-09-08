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
echo Typeset filled translations.json with manga-image-translator.
echo Stop Local AI first. This uses the GPU for inpaint.
echo Drag the work folder (has translations.json and typeset_in or in).
echo.
if "%~1"=="" (
  echo usage: drag the work folder onto this bat
  pause
  exit /b 2
)
"%PYEXE%" "%~dp0tools\typeset_ocr_local.py" %*
echo.
pause
