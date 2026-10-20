@echo off
cd /d "%~dp0"
if exist "%~dp0python\python.exe" (
  "%~dp0python\python.exe" croc.py
) else (
  python croc.py
)
if errorlevel 1 pause
