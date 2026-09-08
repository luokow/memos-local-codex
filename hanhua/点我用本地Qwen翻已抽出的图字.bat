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
echo Fill already-extracted OCR strings via local Qwen.
echo Does not run OCR / inpaint / typeset.
echo Drag translations.json or a work folder onto this bat.
echo Open Local AI first. Do not start MiniMax H3 / ComfyUI.
echo.
if "%~1"=="" (
  echo usage: drag translations.json or the work folder onto this bat
  pause
  exit /b 2
)
"%PYEXE%" "%~dp0tools\fill_ocr_local.py" %*
echo.
pause
