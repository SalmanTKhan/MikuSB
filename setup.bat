@echo off
cd /d "%~dp0"
py -3 tools\setup.py %*
if errorlevel 1 pause
