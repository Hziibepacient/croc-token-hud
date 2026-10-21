@echo off
cd /d "%~dp0"
if exist "python\pythonw.exe" (
  start "" "python\pythonw.exe" croc_desktop.py
) else (
  where pythonw >nul 2>nul
  if errorlevel 1 (
    echo Python 3.10 or later is required.
    pause
  ) else (
    start "" pythonw croc_desktop.py
  )
)
