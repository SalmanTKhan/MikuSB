@echo off
cd /d "%~dp0"
set MIKUSB_OFFLINE=1
dotnet run --project MikuSB --no-launch-profile
pause
